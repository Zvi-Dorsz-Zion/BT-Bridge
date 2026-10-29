package com.btbridge.app

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothServerSocket
import android.bluetooth.BluetoothSocket
import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.os.StatFs
import android.os.storage.StorageManager
import com.btbridge.app.core.AskPassword
import com.btbridge.app.core.Handshake
import com.btbridge.app.core.HandshakeResult
import com.btbridge.app.core.FileService
import com.btbridge.app.core.Link
import com.btbridge.app.core.Lockout
import com.btbridge.app.core.Log
import com.btbridge.app.core.PasswordAnswer
import com.btbridge.app.core.RequestHandler
import com.btbridge.app.core.RemoteException
import com.btbridge.app.core.Root
import com.btbridge.app.core.ScreenSource
import com.btbridge.app.core.str
import com.btbridge.app.core.Session
import com.btbridge.app.core.Transfer
import com.btbridge.app.core.WifiLinks
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import org.json.JSONObject
import java.io.File
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.util.UUID
import java.util.concurrent.CancellationException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class PasswordRequest(val peerName: String, val wrongBefore: Boolean, val answer: CompletableFuture<PasswordAnswer?>)

/** The app's core on Android: Bluetooth listener, outgoing connections, sessions, transfers. */
class Bridge(private val ctx: Context) : RequestHandler {
    companion object {
        val SERVICE_UUID: UUID = UUID.fromString("7f3c2a10-5b8e-4d21-9c47-2e6b1a9d0f53")
        fun key(address: String) = address.filter { it.isLetterOrDigit() }.uppercase()

        /** Errors that mean "the connection went away", not "this file can't be transferred". */
        private val linkErrors = listOf("החיבור נותק", "החיבור נסגר", "ההעברה נתקעה", "אין תשובה מהמכשיר השני")
    }

    val store = AndroidStore(ctx)
    val me = store.identity()
    private val files = FileService({ store.deviceName }, "android", Environment.getExternalStorageDirectory(), ::roots,
        { store.downloadDir }, ::onReceived, ::mediaScan)
    val hotspot = DirectHotspot(ctx)

    /**
     * Tells Android about files that were added, renamed or deleted, so they show up right away in "Downloads",
     * the gallery and file managers (those read the media index, not the disk). The app's own folders are skipped.
     */
    fun mediaScan(files: List<File>) {
        val own = listOf(ctx.cacheDir.path, ctx.filesDir.path, ctx.getExternalFilesDir(null)?.path).filterNotNull()
        val paths = files.map { it.path }.filter { p -> own.none { p.startsWith(it) } }
        if (paths.isEmpty()) return
        android.media.MediaScannerConnection.scanFile(ctx, paths.toTypedArray(), null, null)
    }
    val wifi = WifiLinks({ sessions.value }, ::localIps, ::connectTcp)
    private val lockout = Lockout()
    private val list = CopyOnWriteArrayList<Session>()

    val sessions = MutableStateFlow<List<Session>>(emptyList())
    val listenStatus = MutableStateFlow("מתחיל…")
    val passwordRequest = MutableStateFlow<PasswordRequest?>(null)
    val transfers = MutableStateFlow<List<Transfer>>(emptyList())
    /** Bumped while transfers run, so the UI redraws progress. */
    val tick = MutableStateFlow(0L)
    val notices = MutableSharedFlow<String>(extraBufferCapacity = 8)

    private val bg = Executors.newCachedThreadPool { r -> Thread(r, "bridge-bg").apply { isDaemon = true } }
    private val transferExec = Executors.newSingleThreadExecutor { r -> Thread(r, "transfers").apply { isDaemon = true } }
    private val main = Handler(Looper.getMainLooper())
    private var started = false

    @Synchronized
    fun start() {
        if (started) return
        started = true
        wifi.start()
        Thread(::listenLoop, "bt-listen").apply { isDaemon = true }.start()
        val timer = Executors.newSingleThreadScheduledExecutor()
        timer.scheduleAtFixedRate({
            val active = transfers.value.filter { it.active }
            if (active.isNotEmpty()) {
                active.forEach { it.tick() }
                tick.value++
            }
        }, 300, 300, TimeUnit.MILLISECONDS)
        timer.scheduleAtFixedRate({ runCatching { closeIdleHotspot() } }, 15, 15, TimeUnit.SECONDS)
        timer.scheduleAtFixedRate({ runCatching { cleanViewFiles() } }, 10, 15 * 60, TimeUnit.SECONDS)
        timer.scheduleAtFixedRate({ runCatching { holdWifiWhileBusy() } }, 2, 2, TimeUnit.SECONDS)
        watchBattery()
    }

    /** Deletes files that were opened only for viewing, after the time chosen in the settings. */
    private fun cleanViewFiles() {
        val root = File(ctx.cacheDir, "צפייה")
        val before = System.currentTimeMillis() - maxOf(1, store.viewCleanupHours) * 3_600_000L
        root.listFiles()?.forEach { d ->
            val created = d.name.toLongOrNull() ?: d.lastModified()
            if (created < before) d.deleteRecursively()
        }
    }

    fun displayName(s: Session) = store.nameFor(s.peerFp, s.peerName)

    fun sessionFor(fp: String): Session? = sessions.value.firstOrNull { it.peerFp == fp && !it.closed }

    // ------------------------------------------------------------------ fast link

    /**
     * Before a big transfer or screen sharing: when there is no Wi-Fi link, first tries the shared network again
     * (the link may just have dropped), and only then opens the direct network (PROTOCOL.md section 8). Blocking; never throws.
     */
    fun ensureFastLink(s: Session) {
        if (s.hasWifi) return
        wifi.refresh(s)
        repeat(12) { if (!s.hasWifi && !s.closed) Thread.sleep(250) }
        if (s.hasWifi || store.directMode == "off" || s.peerOs != "windows") return
        try {
            openDirect(s)
            repeat(50) { if (!s.hasWifi && !s.closed) Thread.sleep(500) }
        } catch (e: Exception) {
            Log.w("direct: ${e.message}")
            notices.tryEmit("רשת ישירה לא נפתחה: ${com.btbridge.app.core.Heb.of(e)}. ממשיך בבלוטוס.")
        }
    }

    private fun openDirect(s: Session) {
        val c = hotspot.start()
        s.sendEvent(JSONObject().put("ev", "direct").put("ssid", c.ssid).put("pass", c.pass))
        // Our new address on the hotspot.
        Thread.sleep(500)
        wifi.announce(s)
    }

    /** 90 seconds without transfers or screen sharing: close the hotspot, and tell the computers to go back. */
    private fun closeIdleHotspot() {
        if (!hotspot.active) return
        val now = System.currentTimeMillis()
        val busy = sessions.value.any { it.hasActiveStreams || now - it.lastDataActivity < 90_000 } ||
            transfers.value.any { it.active }
        if (busy) return
        hotspot.stop()
        for (s in sessions.value) if (s.peerOs == "windows") runCatching { s.sendEvent(JSONObject().put("ev", "direct-end")) }
    }

    /**
     * Phones put Wi-Fi to sleep between packets, which slows screen sharing and transfers and can drop the link.
     * While something is moving, keep Wi-Fi fully awake; otherwise let it rest.
     */
    private var wifiLock: WifiManager.WifiLock? = null

    @Suppress("DEPRECATION")
    private fun holdWifiWhileBusy() {
        val busy = sessions.value.any { it.hasWifi && it.hasActiveStreams } || transfers.value.any { it.active }
        val lock = wifiLock ?: ctx.applicationContext.getSystemService(WifiManager::class.java)
            ?.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "btbridge")?.apply { setReferenceCounted(false) }
            ?.also { wifiLock = it } ?: return
        if (busy && !lock.isHeld) lock.acquire() else if (!busy && lock.isHeld) lock.release()
    }

    // ------------------------------------------------------------------ received files

    private fun onReceived(s: Session, f: File, update: Boolean) {
        val t = Transfer(f.name, upload = false).apply {
            total = f.length()
            done.set(f.length())
            status = if (update) "עדכון לגשר בלוטוס מ-${displayName(s)}. הקש \"פתח\" כדי להתקין." else "התקבל מ-${displayName(s)}"
            resultPath = f.path
            active = false
        }
        transfers.value = listOf(t) + transfers.value
        received.tryEmit(Received(f, displayName(s), update))
    }

    class Received(val file: File, val from: String, val update: Boolean)

    /** Files that arrived through "send to device" (the service shows a notification). */
    val received = MutableSharedFlow<Received>(extraBufferCapacity = 16)

    // ------------------------------------------------------------------ clipboard, notifications, battery

    /** The last text we put on the clipboard from the computer, so it isn't sent straight back. */
    @Volatile private var lastClip: String? = null

    /**
     * Sends copied text to the connected computers (PROTOCOL.md section 5, "clip"). Returns true when it went out.
     * Without [force], text that came from the computer (or was already sent) isn't sent again.
     */
    fun shareClipboard(text: String, force: Boolean = false): Boolean {
        if (!store.shareClipboard || text.isEmpty() || text.length > 200_000 || (!force && text == lastClip)) return false
        lastClip = text
        var sent = false
        for (s in sessions.value) if (s.has("clip")) runCatching { s.sendEvent(JSONObject().put("ev", "clip").put("text", text)); sent = true }
        return sent
    }

    private fun receiveClipboard(text: String) {
        if (!store.shareClipboard || text.isEmpty() || text.length > 200_000) return
        lastClip = text
        main.post {
            runCatching {
                ctx.getSystemService(ClipboardManager::class.java)?.setPrimaryClip(ClipData.newPlainText("גשר בלוטוס", text))
            }.onFailure { Log.w("clipboard: ${it.message}") }
        }
    }

    /** A notification that appeared on this phone, for the computers (PROTOCOL.md section 5, "notif"). */
    fun forwardNotification(app: String, title: String, text: String) {
        if (!store.forwardNotifications) return
        val ev = JSONObject().put("ev", "notif").put("app", app).put("title", title).put("text", text.take(2000))
        for (s in sessions.value) if (s.has("notif")) runCatching { s.sendEvent(ev) }
    }

    @Volatile private var battery = -1
    @Volatile private var charging = false

    private fun watchBattery() {
        val r = object : BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) {
                val level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
                val scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, 100).coerceAtLeast(1)
                val pct = if (level < 0) -1 else level * 100 / scale
                val plugged = i.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0
                if (pct == battery && plugged == charging) return
                battery = pct
                charging = plugged
                bg.execute { for (s in sessions.value) sendBattery(s) }
            }
        }
        runCatching { ctx.applicationContext.registerReceiver(r, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) }
    }

    private fun sendBattery(s: Session) {
        if (battery < 0 || !s.has("battery")) return
        runCatching { s.sendEvent(JSONObject().put("ev", "battery").put("level", battery).put("charging", charging)) }
    }

    // ------------------------------------------------------------------ screen sharing

    private fun openScreen(s: Session, id: Int, req: JSONObject): JSONObject? {
        if (!store.allowControl) throw RemoteException("denied", "השליטה בטלפון כבויה בהגדרות של גשר בלוטוס בטלפון")
        val mp = Projection.obtain(ctx) ?: throw RemoteException("denied", "שיתוף המסך לא אושר בטלפון")
        val grab = ProjectionGrabber(ctx, mp)
        val src = ScreenSource(s, id, grab, AccessibilityInput, AndroidTileCodec, req)
        ScreenSource.listeners += object : (ScreenSource, Boolean) -> Unit {
            override fun invoke(x: ScreenSource, on: Boolean) {
                if (x === src && !on) { grab.release(); ScreenSource.listeners.remove(this) }
            }
        }
        s.reply(id, JSONObject().put("ok", true).put("kind", "android").put("monitors", org.json.JSONArray())
            .put("input", BridgeAccessibility.instance != null))
        src.start()
        notices.tryEmit("${displayName(s)} צופה במסך הטלפון")
        return null
    }

    fun adapter(): BluetoothAdapter? = ctx.getSystemService(BluetoothManager::class.java)?.adapter

    fun hasBtPermission(): Boolean =
        Build.VERSION.SDK_INT < 31 ||
            ctx.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED

    // ------------------------------------------------------------------ incoming

    @SuppressLint("MissingPermission")
    private fun listenLoop() {
        while (true) {
            var ss: BluetoothServerSocket? = null
            try {
                val a = adapter()
                if (a == null || !a.isEnabled || !hasBtPermission()) {
                    listenStatus.value = when {
                        a == null -> "אין בלוטוס במכשיר"
                        !hasBtPermission() -> "חסרה הרשאת בלוטוס"
                        else -> "הבלוטוס כבוי"
                    }
                    Thread.sleep(3000)
                    continue
                }
                ss = a.listenUsingRfcommWithServiceRecord("גשר בלוטוס", SERVICE_UUID)
                listenStatus.value = "מוכן לחיבורים בבלוטוס"
                while (true) {
                    val sock = ss.accept()
                    bg.execute { incoming(sock) }
                }
            } catch (e: Exception) {
                Log.w("listen: ${e.message}")
                runCatching { ss?.close() }
                listenStatus.value = "הבלוטוס לא זמין. מנסה שוב…"
                Thread.sleep(3000)
            }
        }
    }

    private fun incoming(sock: BluetoothSocket) {
        val addr = key(sock.remoteDevice.address ?: "")
        val done = java.util.concurrent.atomic.AtomicBoolean(false)
        bg.execute {
            Thread.sleep(180_000)
            if (!done.get()) runCatching { sock.close() }
        }
        try {
            val hr = Handshake.server(sock.inputStream, sock.outputStream, me, store, "android", addr, lockout)
            done.set(true)
            addSession(hr, addr, false, Link(false, sock.inputStream, sock.outputStream, sock, hr.codec))
            notices.tryEmit("${hr.peerName} התחבר")
        } catch (e: Exception) {
            done.set(true)
            Log.w("incoming from $addr failed: ${e.message}")
            runCatching { sock.close() }
        }
    }

    // ------------------------------------------------------------------ outgoing

    fun sessionForAddress(address: String): Session? = sessions.value.firstOrNull { it.peerAddress == address }

    /** Blocking; call from a background thread. */
    @SuppressLint("MissingPermission")
    fun connect(device: BluetoothDevice): Session {
        val addr = key(device.address)
        sessionForAddress(addr)?.let { return it }
        runCatching { adapter()?.cancelDiscovery() }
        val sock = device.createRfcommSocketToServiceRecord(SERVICE_UUID)
        try {
            sock.connect()
            val hr = Handshake.client(sock.inputStream, sock.outputStream, me, store, "android", addr,
                AskPassword { name, wrong -> askPassword(name, wrong) })
            return addSession(hr, addr, true, Link(false, sock.inputStream, sock.outputStream, sock, hr.codec))
        } catch (e: Exception) {
            runCatching { sock.close() }
            throw e
        }
    }

    private fun askPassword(peerName: String, wrong: Boolean): PasswordAnswer? {
        val f = CompletableFuture<PasswordAnswer?>()
        passwordRequest.value = PasswordRequest(peerName, wrong, f)
        return try { f.get(3, TimeUnit.MINUTES) } catch (e: Exception) { null } finally { passwordRequest.value = null }
    }

    private fun addSession(hr: HandshakeResult, addr: String, initiator: Boolean, link: Link): Session {
        val s = Session(hr, addr, initiator, this)
        val older = list.filter { it.peerFp == hr.peerFp }
        list.removeAll(older.toSet())
        list.add(s)
        older.forEach { it.close() }
        s.onClosed = { list.remove(it); publish() }
        s.onChanged = { publish() }
        // A dropped Wi-Fi link comes back by itself within seconds, not only on the 30-second announcement.
        s.onWifiLost = {
            bg.execute {
                for (wait in listOf(2000L, 8000L)) {
                    Thread.sleep(wait)
                    if (s.closed || s.hasWifi) break
                    wifi.refresh(s)
                }
            }
        }
        s.attach(link)
        wifi.announce(s)
        publish()
        bg.execute {
            sendBattery(s)
            resumeInterrupted(s.peerFp)
        }
        return s
    }

    private fun publish() { sessions.value = list.filter { !it.closed } }

    // ------------------------------------------------------------------ RequestHandler

    override fun handle(s: Session, id: Int, req: JSONObject): JSONObject? = when (req.str("op")) {
        "screen-open" -> openScreen(s, id, req)
        "direct" -> {
            if (store.directMode == "off") throw RemoteException("denied", "רשת ישירה כבויה בהגדרות של הטלפון")
            try { openDirect(s) } catch (e: IllegalStateException) { throw RemoteException("io", e.message ?: "הרשת הישירה לא נפתחה") }
            JSONObject().put("ok", true)
        }
        "ring" -> {
            Ring.start(ctx, displayName(s))
            JSONObject().put("ok", true)
        }
        "log" -> JSONObject().put("ok", true).put("text", Log.tail(64 * 1024))
        else -> files.handle(s, id, req)
    }

    override fun onEvent(s: Session, ev: JSONObject) {
        if (ScreenSource.onEvent(s, ev)) return
        when (ev.str("ev")) {
            "clip" -> receiveClipboard(ev.str("text") ?: "")
            else -> wifi.onEvent(s, ev)
        }
    }

    // ------------------------------------------------------------------ transfers

    /**
     * Runs transfers one at a time, each on its device's current session. When the connection drops in the middle,
     * the transfer waits and continues from where it stopped once the device is back.
     * [after] gets true when it finally succeeded.
     */
    fun queue(t: Transfer, after: ((Boolean) -> Unit)? = null) {
        if (after != null) t.after = after
        if (transfers.value.none { it === t }) transfers.value = listOf(t) + transfers.value
        transferExec.execute { runTransfer(t) }
    }

    private fun runTransfer(t: Transfer) {
        var ok = false
        if (t.cancel.isCancelled) {
            t.status = "בוטל"
        } else {
            val s = sessionFor(t.peerFp)
            if (s == null) {
                t.interrupted = true
                t.status = "ממתין לחיבור. ימשיך כשהמכשיר יתחבר שוב."
            } else {
                t.status = "מעביר…"
                try {
                    t.work!!(s, t)
                    t.status = "הושלם ✓"
                    ok = true
                } catch (e: Exception) {
                    val why = com.btbridge.app.core.Heb.of(e)
                    when {
                        t.cancel.isCancelled -> t.status = "בוטל"
                        s.closed -> {
                            t.interrupted = true
                            t.status = "נקטע. ימשיך כשהמכשיר יתחבר שוב."
                        }
                        (e is CancellationException || linkErrors.any { why.contains(it) }) && t.attempts < 5 -> {
                            // Only the Wi-Fi link dropped; Bluetooth is still up. Go on right away from where it stopped.
                            t.status = "החיבור התחלף. ממשיך…"
                            t.active = false
                            t.restart()
                            transferExec.execute { runTransfer(t) }
                            return
                        }
                        else -> {
                            t.status = "שגיאה: $why"
                            Log.w("transfer ${t.name}: $e")
                        }
                    }
                }
            }
        }
        t.active = false
        tick.value++
        if (!t.interrupted) runCatching { t.after?.invoke(ok) }
    }

    /** The user pressed "continue", or the device came back. */
    fun resume(t: Transfer) {
        if (t.active || t.work == null) return
        t.restart()
        queue(t)
    }

    private fun resumeInterrupted(fp: String) {
        transfers.value.filter { it.interrupted && !it.active && it.peerFp == fp }.reversed().forEach { resume(it) }
    }

    fun clearFinished() { transfers.value = transfers.value.filter { it.active || it.interrupted } }

    fun runInBackground(f: () -> Unit) = bg.execute(f)

    // ------------------------------------------------------------------ storage and network

    fun roots(): List<Root> {
        val out = mutableListOf<Root>()
        val ext = Environment.getExternalStorageDirectory()
        for ((name, rel) in listOf(
            "הורדות" to Environment.DIRECTORY_DOWNLOADS,
            "מצלמה" to "DCIM/Camera",
            "תמונות" to Environment.DIRECTORY_PICTURES,
            "מסמכים" to Environment.DIRECTORY_DOCUMENTS,
        )) {
            val d = File(ext, rel)
            if (d.isDirectory) out += Root(name, d)
        }
        val sm = ctx.getSystemService(StorageManager::class.java)
        var primaryAdded = false
        if (Build.VERSION.SDK_INT >= 30 && sm != null) {
            for (v in sm.storageVolumes) {
                val dir = v.directory ?: continue
                val st = runCatching { StatFs(dir.path) }.getOrNull()
                val name = if (v.isPrimary) "זיכרון פנימי" else (v.getDescription(ctx) ?: "כרטיס זיכרון")
                out += Root(name, dir, st?.totalBytes ?: 0, st?.availableBytes ?: 0)
                if (v.isPrimary) primaryAdded = true
            }
        }
        if (!primaryAdded) out += Root("זיכרון פנימי", ext)
        return out
    }

    private fun localIps(): List<String> {
        val out = mutableListOf<Pair<Int, String>>()
        for (ni in NetworkInterface.getNetworkInterfaces()) {
            if (!ni.isUp || ni.isLoopback) continue
            val n = ni.name.lowercase()
            if (n.startsWith("rmnet") || n.startsWith("ccmni") || n.startsWith("dummy") || n.startsWith("tun")) continue
            val rank = if (n.startsWith("wlan") || n.startsWith("ap") || n.startsWith("swlan")) 0 else 1
            for (a in ni.inetAddresses) if (a is Inet4Address && !a.isLoopbackAddress) out += rank to a.hostAddress!!
        }
        return out.sortedBy { it.first }.map { it.second }.distinct()
    }

    /** Opens a TCP socket on the network that actually contains [ip] (Wi-Fi without internet is not the default). */
    private fun connectTcp(ip: String, port: Int, timeoutMs: Int): Socket {
        val target = InetAddress.getByName(ip)
        val cm = ctx.getSystemService(ConnectivityManager::class.java)
        @Suppress("DEPRECATION")
        val net = cm?.allNetworks?.firstOrNull { n ->
            cm.getLinkProperties(n)?.linkAddresses?.any { la -> samePrefix(la.address, target, la.prefixLength) } == true
        }
        val sock = net?.socketFactory?.createSocket() ?: Socket()
        sock.connect(InetSocketAddress(target, port), timeoutMs)
        return sock
    }

    private fun samePrefix(a: InetAddress, b: InetAddress, bits: Int): Boolean {
        val x = a.address
        val y = b.address
        if (x.size != y.size || bits <= 0) return false
        var left = bits
        for (i in x.indices) {
            if (left <= 0) return true
            val mask = if (left >= 8) 0xff else (0xff shl (8 - left)) and 0xff
            if ((x[i].toInt() and mask) != (y[i].toInt() and mask)) return false
            left -= 8
        }
        return true
    }
}

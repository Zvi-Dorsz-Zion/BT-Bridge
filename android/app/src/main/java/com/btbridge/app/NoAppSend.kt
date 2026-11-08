package com.btbridge.app

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.view.WindowManager
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import com.btbridge.app.core.Heb
import com.btbridge.app.core.ShareItem
import com.btbridge.app.core.Transfer
import com.btbridge.app.core.WebShare
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.common.BitMatrix
import com.google.zxing.qrcode.QRCodeWriter
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.Inet4Address
import java.net.NetworkInterface
import java.util.concurrent.atomic.AtomicInteger
import kotlin.math.floor

/** A picked file, with the address Android's Bluetooth sender needs. */
private class Chosen(val uri: Uri, val file: PickedFile)

private const val NOT_ENCRYPTED = "החיבור הזה לא מוצפן. השתמשו בו רק ברשת שאתם סומכים עליה."

/** "Send to a device that doesn't have our app": pick files, then a link for a browser or Android's own Bluetooth sender. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NoAppSendScreen(onBack: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var files by remember { mutableStateOf<List<Chosen>>(emptyList()) }
    var loading by remember { mutableStateOf(false) }
    var viaWifi by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    BackHandler(onBack = onBack)

    // The read permission Android gives with each picked file lasts as long as the activity, longer than this screen.
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isNotEmpty()) {
            loading = true
            val app = ctx.applicationContext
            scope.launch {
                val got = withContext(Dispatchers.IO) {
                    uris.mapNotNull { u -> runCatching { Chosen(u, LocalFiles.fromUri(app, u)) }.getOrNull() }
                }
                files = got
                loading = false
                if (got.size < uris.size) message = "חלק מהקבצים לא נפתחו, ולא ייכללו בשליחה."
            }
        }
    }

    Scaffold(topBar = {
        TopAppBar(title = { Text("שלח למכשיר בלי התוכנה") },
            navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "חזור") } })
    }) { pad ->
        Column(
            Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            if (viaWifi) WifiSend(files, onDone = onBack)
            else {
                Button(onClick = { picker.launch(arrayOf("*/*")) }, enabled = !loading) { Text(if (loading) "קורא…" else "בחר קבצים") }
                if (files.isEmpty()) Text("בוחרים קודם את הקבצים לשליחה.", style = MaterialTheme.typography.bodySmall)
                else {
                    val total = files.sumOf { it.file.size }
                    Text((if (files.size == 1) "קובץ אחד" else "${files.size} קבצים") + " · סה״כ ${Transfer.formatSize(total)}", fontWeight = FontWeight.SemiBold)
                    HorizontalDivider()
                    Button(onClick = { viaWifi = true }, Modifier.fillMaxWidth()) { Text("דרך הוויפי (קישור לדפדפן)") }
                    OutlinedButton(onClick = { message = sendViaBluetooth(ctx, files.map { it.uri }) }, Modifier.fillMaxWidth()) { Text("דרך הבלוטוס") }
                    Text("המכשיר השני יתבקש לאשר כל קובץ. השליחה איטית יותר מוויפי. לאייפון אי אפשר לשלוח כך.",
                        style = MaterialTheme.typography.bodySmall)
                }
            }
        }
    }
    message?.let { MessageDialog(it) { message = null } }
}

/** Android's own Bluetooth sender; on phones where it isn't there under this name, the usual share list. Returns an error text. */
private fun sendViaBluetooth(ctx: Context, uris: List<Uri>): String? {
    val send = if (uris.size == 1) Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM, uris[0])
        else Intent(Intent.ACTION_SEND_MULTIPLE).putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(uris))
    send.type = "*/*"
    send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    return try {
        ctx.startActivity(Intent(send).setPackage("com.android.bluetooth"))
        null
    } catch (e: Exception) {
        try {
            ctx.startActivity(Intent.createChooser(send, "שליחה בבלוטוס"))
            null
        } catch (e2: Exception) {
            "לא נמצאה בטלפון אפליקציה ששולחת בבלוטוס."
        }
    }
}

/** The phone's local IPv4 addresses, Wi-Fi and hotspot first (the same rules as the fast link in Bridge). */
private fun phoneIps(): List<String> {
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

private fun wifiJoinText(ssid: String, pass: String): String {
    fun esc(s: String) = buildString {
        for (c in s) {
            if (c in "\\;,:\"") append('\\')
            append(c)
        }
    }
    return "WIFI:T:WPA;S:${esc(ssid)};P:${esc(pass)};;"
}

@Composable
private fun WifiSend(files: List<Chosen>, onDone: () -> Unit) {
    val b = App.bridge
    val ctx = LocalContext.current
    val clipboard = LocalClipboardManager.current
    var share by remember { mutableStateOf<WebShare?>(null) }
    var closedIdle by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf<String?>(null) }
    var allowUpload by remember { mutableStateOf(false) }
    var ips by remember { mutableStateOf<List<String>?>(null) }
    var pickedIp by remember { mutableStateOf<String?>(null) }
    var creds by remember { mutableStateOf<DirectHotspot.Credentials?>(null) }
    var opening by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    var sentBytes by remember { mutableLongStateOf(0L) }
    var receivedShown by remember { mutableIntStateOf(0) }
    val receivedCount = remember { AtomicInteger() }
    val hotspotUse = remember { HotspotUse() }

    // The server lives exactly as long as this part of the screen.
    DisposableEffect(Unit) {
        val dir = b.store.downloadDir
        val ws = WebShare(files.map { ShareItem(it.file.name, it.file.size, it.file.open) }, dir,
            onUploaded = { f ->
                receivedCount.incrementAndGet()
                b.mediaScan(listOf(f))
            },
            onStopped = { closedIdle = true })
        b.runInBackground {
            try {
                dir.mkdirs()
                ws.start()
                share = ws
            } catch (e: Exception) {
                failed = "לא הצלחתי לפתוח את הקישור: ${Heb.of(e)}"
            }
        }
        onDispose { b.runInBackground { runCatching { ws.stop() } } }
    }
    LaunchedEffect(share, allowUpload) { share?.allowUpload = allowUpload }

    val running = share != null && !closedIdle && failed == null
    DisposableEffect(running) {
        val win = (ctx as? Activity)?.window
        if (running) win?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        onDispose { win?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) }
    }

    DisposableEffect(Unit) {
        onDispose {
            hotspotUse.left = true
            b.hotspotKeptOpen = false
            if (hotspotUse.started) b.runInBackground { runCatching { b.hotspot.stop() } }
        }
    }

    // While the screen is visible: addresses come and go, and the hotspot can be closed from elsewhere.
    val owner = ctx as? LifecycleOwner
    LaunchedEffect(owner) {
        owner?.repeatOnLifecycle(Lifecycle.State.STARTED) {
            while (isActive) {
                ips = runCatching { withContext(Dispatchers.IO) { phoneIps() } }.getOrDefault(emptyList())
                share?.let { sentBytes = it.bytesSent }
                receivedShown = receivedCount.get()
                if (creds != null && !b.hotspot.active) {
                    creds = null
                    hotspotUse.started = false
                    message = "הנקודה החמה נסגרה. אפשר לפתוח אותה שוב."
                }
                delay(2000)
            }
        }
    }

    fun openHotspot() {
        if (opening) return
        if (Build.VERSION.SDK_INT < 33 && !locationOn(ctx)) {
            message = "כדי לפתוח נקודה חמה צריך להדליק את המיקום בטלפון."
            return
        }
        opening = true
        // Without this the idle check closes it within seconds: nothing else is using it here.
        b.hotspotKeptOpen = true
        val wasOpen = b.hotspot.active
        b.runInBackground {
            try {
                val c = b.hotspot.start()
                if (!wasOpen) hotspotUse.started = true
                if (hotspotUse.left) {
                    if (hotspotUse.started) b.hotspot.stop()
                } else creds = c
            } catch (e: Exception) {
                message = Heb.of(e)
            } finally {
                opening = false
            }
        }
    }

    val perms = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        if (nearbyGranted(ctx)) openHotspot() else message = "בלי ההרשאה אי אפשר לפתוח נקודה חמה."
    }

    val ws = share
    val known = ips
    val ip = pickedIp?.takeIf { known?.contains(it) == true } ?: known?.firstOrNull()
    val url = if (ws != null && ip != null && !closedIdle) "http://$ip:${ws.port}${ws.path}" else null

    failed?.let { Text(it, color = MaterialTheme.colorScheme.error) }
    if (closedIdle) Text("הקישור נסגר אחרי 15 דקות בלי פעילות.", color = MaterialTheme.colorScheme.error)

    if (known != null && known.isEmpty() && creds == null && !closedIdle) {
        Text("הטלפון לא מחובר לשום רשת.", fontWeight = FontWeight.SemiBold)
        if (DirectHotspot.supported) {
            Text("אפשר לפתוח נקודה חמה זמנית, ולחבר אליה את המכשיר השני.")
            Button(enabled = !opening, onClick = {
                if (nearbyGranted(ctx)) openHotspot() else perms.launch(nearbyPermissions())
            }) { Text(if (opening) "פותח…" else "פתח נקודה חמה") }
        } else Text("חברו את הטלפון לרשת וויפי, והכתובת תופיע כאן.")
    }

    creds?.let { c ->
        Text("חברו את המכשיר השני לנקודה החמה", fontWeight = FontWeight.SemiBold)
        SelectionContainer {
            Column {
                Text("שם הרשת: ${c.ssid}")
                Text("סיסמה: ${c.pass}")
            }
        }
        QrCode(wifiJoinText(c.ssid, c.pass))
        if (url == null && !closedIdle) Text("מחכה לכתובת של הטלפון ברשת…", style = MaterialTheme.typography.bodySmall)
    }

    if (url != null) {
        HorizontalDivider()
        Text("פתחו את הכתובת בדפדפן של המכשיר השני. שני המכשירים צריכים להיות באותה רשת. אין צורך באינטרנט.")
        SelectionContainer(Modifier.fillMaxWidth()) {
            Text(url, Modifier.fillMaxWidth(), fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center)
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center) {
            OutlinedButton(onClick = { clipboard.setText(AnnotatedString(url)) }) { Text("העתק כתובת") }
        }
        QrCode(url)
        if (known != null && known.size > 1) {
            Text("אם הכתובת לא נפתחת, נסו כתובת אחרת של הטלפון:", style = MaterialTheme.typography.bodySmall)
            known.forEach { option ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(option == ip, { pickedIp = option })
                    Text(option)
                }
            }
        }
    }

    if (running) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Checkbox(allowUpload, { allowUpload = it })
            Text("לאפשר גם לקבל קבצים מהמכשיר")
        }
        if (sentBytes > 0) Text("נשלחו ${Transfer.formatSize(sentBytes)}", style = MaterialTheme.typography.bodySmall)
        if (receivedShown > 0) Text((if (receivedShown == 1) "התקבל קובץ אחד" else "התקבלו $receivedShown קבצים") + ". נשמר ב: ${b.store.downloadLabel}", style = MaterialTheme.typography.bodySmall)
        Text(NOT_ENCRYPTED, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    Button(onClick = onDone, Modifier.fillMaxWidth()) { Text("סיום") }

    message?.let { MessageDialog(it) { message = null } }
}

/** Whether this screen opened the hotspot, and whether the screen is gone. Read from background threads too. */
private class HotspotUse {
    @Volatile var started = false
    @Volatile var left = false
}

/** Black on white in both themes, with the quiet zone that the encoder puts in the matrix: scanners need both. */
@Composable
private fun QrCode(text: String) {
    val matrix = remember(text) { runCatching { qrMatrix(text) }.getOrNull() } ?: return
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.Center) {
        Canvas(Modifier.size(220.dp).background(Color.White)) {
            val n = matrix.width
            // Whole pixels per module, or thin lines appear between them.
            val cell = maxOf(1f, floor(size.minDimension / n))
            val left = floor((size.width - n * cell) / 2)
            val top = floor((size.height - n * cell) / 2)
            for (y in 0 until n) for (x in 0 until n) {
                if (matrix.get(x, y)) drawRect(Color.Black, Offset(left + x * cell, top + y * cell), Size(cell, cell))
            }
        }
    }
}

/** Size 0 gives the smallest matrix, one cell per module. UTF-8 is declared only when needed: some scanners dislike the marker. */
private fun qrMatrix(text: String): BitMatrix {
    val hints = if (text.any { it.code > 127 }) mapOf(EncodeHintType.CHARACTER_SET to "UTF-8") else emptyMap()
    return QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, hints)
}

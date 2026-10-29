package com.btbridge.app.core

import com.btbridge.app.core.Crypto.concat
import com.btbridge.app.core.Crypto.expand
import com.btbridge.app.core.Crypto.hmac
import com.btbridge.app.core.Crypto.same
import com.btbridge.app.core.Crypto.utf8
import org.json.JSONArray
import org.json.JSONObject
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.Collections
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * Extra encrypted TCP links over a shared network (PROTOCOL.md section 6).
 * [connect] opens a TCP socket to an address; on Android it picks the right network for it.
 */
class WifiLinks(
    private val sessions: () -> List<Session>,
    private val localIps: () -> List<String>,
    private val connect: (ip: String, port: Int, timeoutMs: Int) -> Socket,
) {
    private var server: ServerSocket? = null
    private var port = 0
    private val trying = Collections.synchronizedSet(HashSet<Session>())
    private val pool = Executors.newCachedThreadPool { r -> Thread(r, "wifi").apply { isDaemon = true } }
    private val timer = Executors.newSingleThreadScheduledExecutor { r -> Thread(r, "wifi-timer").apply { isDaemon = true } }

    fun start() {
        try {
            val ss = ServerSocket(0)
            server = ss
            port = ss.localPort
            Thread({ acceptLoop(ss) }, "wifi-accept").apply { isDaemon = true }.start()
        } catch (e: Exception) {
            Log.w("tcp listen: ${e.message}")
        }
        timer.scheduleAtFixedRate({ announceAll() }, 30, 30, TimeUnit.SECONDS)
    }

    fun announceAll() {
        for (s in sessions()) if (!s.hasWifi) announce(s)
    }

    fun announce(s: Session) {
        val ips = runCatching { localIps() }.getOrDefault(emptyList())
        runCatching { s.sendEvent(JSONObject().put("ev", "net").put("ips", JSONArray(ips)).put("port", port)) }
    }

    fun onEvent(s: Session, ev: JSONObject) {
        when (ev.str("ev")) {
            "net" -> {
                val arr = ev.optJSONArray("ips")
                s.peerIps = if (arr == null) emptyList() else (0 until arr.length()).map { arr.getString(it) }
                s.peerPort = ev.optInt("port")
                if (s.initiator && !s.hasWifi) pool.execute { tryConnect(s, notifyFail = true) }
            }
            "net-fail" -> if (!s.initiator && !s.hasWifi) pool.execute { tryConnect(s, notifyFail = false) }
        }
    }

    private fun tryConnect(s: Session, notifyFail: Boolean) {
        val ips = s.peerIps
        val port = s.peerPort
        if (ips.isEmpty() || port == 0 || !trying.add(s)) return
        try {
            // Try all addresses at once; the first that answers wins.
            val tasks = ips.map { ip -> pool.submit<Socket?> { runCatching { connect(ip, port, 2500) }.getOrNull() } }
            var won: Socket? = null
            for (t in tasks) {
                val sock = runCatching { t.get(3, TimeUnit.SECONDS) }.getOrNull() ?: continue
                if (won == null) won = sock else runCatching { sock.close() }
            }
            if (won != null && linkHandshake(s, won)) return
            runCatching { won?.close() }
            if (notifyFail) runCatching { s.sendEvent(JSONObject().put("ev", "net-fail")) }
        } catch (e: Exception) {
            Log.w("wifi connect: ${e.message}")
        } finally {
            trying.remove(s)
        }
    }

    /**
     * Brings a dropped Wi-Fi link back. Only the side that opened Bluetooth connects, so the two sides never race
     * each other into two links; the other side just announces itself, which makes the opener try.
     */
    fun refresh(s: Session) {
        announce(s)
        if (s.initiator && !s.hasWifi) pool.execute { tryConnect(s, notifyFail = true) }
    }

    /** After joining a new network: try the other side's addresses right away, whichever side we are. */
    fun kick(s: Session) {
        announce(s)
        if (!s.hasWifi) pool.execute { tryConnect(s, notifyFail = true) }
    }

    private fun linkHandshake(s: Session, sock: Socket): Boolean = try {
        sock.tcpNoDelay = true
        sock.soTimeout = 5000
        val linkId = Crypto.random(16)
        Wire.write(sock.getOutputStream(), concat(linkId, hmac(s.linkKey, utf8("btb-wifi"), linkId)))
        val r = Wire.read(sock.getInputStream())
        if (r.size != 48) throw ProtocolException("wifi ack")
        val srvNonce = r.copyOfRange(0, 16)
        if (!same(r.copyOfRange(16, 48), hmac(s.linkKey, utf8("btb-wifi-ok"), linkId, srvNonce)))
            throw ProtocolException("wifi ack")
        sock.soTimeout = 0
        val (up, down) = linkKeys(s.linkKey, linkId, srvNonce)
        s.attach(Link(true, sock.getInputStream(), sock.getOutputStream(), sock, RecordCodec(up, down)))
        true
    } catch (e: Exception) {
        Log.w("wifi handshake: ${e.message}")
        false
    }

    private fun acceptLoop(ss: ServerSocket) {
        while (true) {
            val c = try { ss.accept() } catch (e: Exception) { Log.w("tcp accept: ${e.message}"); return }
            pool.execute { accept(c) }
        }
    }

    private fun accept(c: Socket) {
        try {
            c.tcpNoDelay = true
            c.soTimeout = 5000
            val f = Wire.read(c.getInputStream())
            if (f.size != 48) throw ProtocolException("wifi hello")
            val linkId = f.copyOfRange(0, 16)
            val mac = f.copyOfRange(16, 48)
            val s = sessions().firstOrNull { same(mac, hmac(it.linkKey, utf8("btb-wifi"), linkId)) }
                ?: throw ProtocolException("unknown wifi link")
            if (!s.usedLinkIds.add(hex(linkId))) throw ProtocolException("wifi link id replayed")
            val srvNonce = Crypto.random(16)
            Wire.write(c.getOutputStream(), concat(srvNonce, hmac(s.linkKey, utf8("btb-wifi-ok"), linkId, srvNonce)))
            c.soTimeout = 0
            val (up, down) = linkKeys(s.linkKey, linkId, srvNonce)
            s.attach(Link(true, c.getInputStream(), c.getOutputStream(), c, RecordCodec(down, up)))
        } catch (e: Exception) {
            Log.w("wifi accept: ${e.message}")
            runCatching { c.close() }
        }
    }

    companion object {
        fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it.toInt() and 0xff) }

        /**
         * PROTOCOL.md section 6. Both sides add fresh randomness to the link keys, and the accepting side refuses a link id
         * it has seen, so a recorded hello can neither take over the link nor make us reuse an AES-GCM nonce.
         */
        fun linkKeys(linkKey: ByteArray, linkId: ByteArray, srvNonce: ByteArray): Pair<ByteArray, ByteArray> {
            val tag = hex(linkId) + hex(srvNonce)
            return expand(linkKey, "up $tag") to expand(linkKey, "down $tag")
        }

        /** Plain TCP connect, for JVM tests and as a fallback. */
        fun plainConnect(ip: String, port: Int, timeoutMs: Int): Socket =
            Socket().apply { connect(InetSocketAddress(ip, port), timeoutMs) }
    }
}

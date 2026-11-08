package com.btbridge.app.core

import java.io.BufferedInputStream
import java.io.Closeable
import java.io.EOFException
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Locale
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Semaphore
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import kotlin.concurrent.thread

/** A file offered for download. [size] 0 means empty or unknown: it is then sent without a length, and without ranges. */
class ShareItem(val name: String, val size: Long, val open: () -> InputStream)

/**
 * "Send to a device that doesn't have our app": a tiny HTTP/1.1 server for a browser on the same network.
 * Everything lives under a long random token; only the items given here are reachable, and uploads go into
 * [uploadDir] only (never when it is null, and only while [allowUpload] is on). Plain JVM code, so the unit tests run it.
 *
 * [onStopped] is called once, when the server closes by itself after [idleLimitMs] without activity (not after [stop]).
 */
class WebShare(
    items: List<ShareItem>,
    private val uploadDir: File?,
    private val onUploaded: (File) -> Unit = {},
    private val onStopped: () -> Unit = {},
    private val idleLimitMs: Long = 15 * 60_000L,
) {
    private companion object {
        const val MAX_HEAD = 16 * 1024
        const val HEAD_TIMEOUT = 15_000
        const val TRANSFER_TIMEOUT = 60_000
        const val BUFFER = 64 * 1024
        const val MAX_CONNECTIONS = 16
        const val WRONG_DELAY_MS = 200L
        const val PART = ".btpart"
        val UNSATISFIABLE = LongArray(0)
        const val ABC = "abcdefghijklmnopqrstuvwxyz0123456789"
        const val NOTE = "החיבור הזה לא מוצפן. השתמשו בו רק ברשת שאתם סומכים עליה."
    }

    private val items = items.toList()
    val token: String = SecureRandom().let { r -> String(CharArray(32) { ABC[r.nextInt(ABC.length)] }) }
    val path: String get() = "/$token/"

    @Volatile var port = 0
        private set

    /** Uploads are refused while this is off. */
    @Volatile var allowUpload = false

    private val sent = AtomicLong()
    private val received = AtomicLong()
    val bytesSent: Long get() = sent.get()
    val bytesReceived: Long get() = received.get()

    /** One accepted connection; the watch thread closes it after [deadline]. Socket timeouts don't cover writes. */
    private class Conn(val sock: Socket) {
        @Volatile var deadline = System.currentTimeMillis() + HEAD_TIMEOUT
    }

    private class Request(val method: String, val target: String, val headers: Map<String, String>)

    private val life = Any()
    private var server: ServerSocket? = null
    private val stopped = AtomicBoolean(false)
    private val notified = AtomicBoolean(false)
    private val conns = CopyOnWriteArrayList<Conn>()
    private val slots = Semaphore(MAX_CONNECTIONS)
    private val refusals = Semaphore(MAX_CONNECTIONS)
    @Volatile private var lastActivity = System.currentTimeMillis()

    val running: Boolean get() = port != 0 && !stopped.get()

    /** Opens the port (blocking, throws when it can't). After [stop] it does nothing. */
    fun start() {
        synchronized(life) {
            if (stopped.get() || server != null) return
            val s = ServerSocket(0, 64)
            server = s
            port = s.localPort
            lastActivity = System.currentTimeMillis()
            thread(name = "web-accept", isDaemon = true) { acceptLoop(s) }
            thread(name = "web-watch", isDaemon = true) { watchLoop() }
        }
    }

    fun stop() {
        synchronized(life) {
            if (!stopped.compareAndSet(false, true)) return
            closeQuiet(server)
            for (c in conns) closeQuiet(c.sock)
        }
    }

    private fun closeQuiet(c: Closeable?) {
        try { c?.close() } catch (_: Exception) { }
    }

    private fun acceptLoop(s: ServerSocket) {
        while (!stopped.get()) {
            try {
                val sock = s.accept()
                when {
                    slots.tryAcquire() -> {
                        val c = Conn(sock)
                        conns.add(c)
                        handOff(sock, slots, { conns.remove(c) }) { connection(c) }
                    }
                    refusals.tryAcquire() -> handOff(sock, refusals, {}) { refuse(sock) }
                    else -> closeQuiet(sock)
                }
            } catch (t: Throwable) {
                if (stopped.get()) break
                Log.w("web accept: $t")
                try { Thread.sleep(100) } catch (_: InterruptedException) { }
            }
        }
    }

    /** Runs [work] in its own daemon thread; the socket and [slot] are always given back. */
    private fun handOff(sock: Socket, slot: Semaphore, cleanup: () -> Unit, work: () -> Unit) {
        val done = {
            closeQuiet(sock)
            cleanup()
            slot.release()
        }
        try {
            thread(name = "web-conn", isDaemon = true) {
                try {
                    work()
                } catch (_: IOException) {
                    // The other side went away, a timeout, or stop(): nothing to tell anyone.
                } catch (t: Throwable) {
                    Log.w("web connection: $t")
                } finally {
                    done()
                }
            }
        } catch (t: Throwable) {
            done()
        }
    }

    /** Idle stop, and connections past their deadline. */
    private fun watchLoop() {
        val tick = minOf(1000L, maxOf(10L, idleLimitMs / 4))
        while (!stopped.get()) {
            try {
                Thread.sleep(tick)
                val now = System.currentTimeMillis()
                if (now - lastActivity >= idleLimitMs) {
                    stop()
                    if (notified.compareAndSet(false, true)) onStopped()
                    return
                }
                for (c in conns) if (now > c.deadline) closeQuiet(c.sock)
            } catch (t: Throwable) {
                Log.w("web watch: $t")
            }
        }
    }

    /** Called for bytes that really moved: keeps the server and this connection alive. */
    private fun progress(c: Conn) {
        val now = System.currentTimeMillis()
        lastActivity = now
        c.deadline = now + TRANSFER_TIMEOUT
    }

    // ------------------------------------------------------------------ request

    private fun connection(c: Conn) {
        val sock = c.sock
        if (stopped.get()) return
        sock.soTimeout = HEAD_TIMEOUT
        sock.tcpNoDelay = true
        val input = BufferedInputStream(sock.getInputStream(), 8192)
        val out = sock.getOutputStream()
        val r = readRequest(input, out) ?: return
        route(r, input, out, c)
    }

    private fun refuse(sock: Socket) {
        sock.soTimeout = 500
        val input = sock.getInputStream()
        send(sock.getOutputStream(), 503, "Service Unavailable", "text/plain; charset=utf-8", "עסוק".toByteArray(Charsets.UTF_8))
        drain(sock, input)
    }

    private fun readRequest(input: InputStream, out: OutputStream): Request? {
        val buf = ByteArray(MAX_HEAD)
        var n = 0
        while (true) {
            val b = input.read()
            if (b < 0) return null
            if (n == MAX_HEAD) {
                send(out, 431, "Request Header Fields Too Large", "text/plain; charset=utf-8", ByteArray(0))
                return null
            }
            buf[n++] = b.toByte()
            if (n >= 4 && buf[n - 4] == 13.toByte() && buf[n - 3] == 10.toByte() && buf[n - 2] == 13.toByte() && buf[n - 1] == 10.toByte()) break
        }
        val lines = String(buf, 0, n - 4, Charsets.ISO_8859_1).split("\r\n")
        val first = lines[0].split(' ')
        if (first.size != 3 || !first[2].startsWith("HTTP/1.")) {
            send(out, 400, "Bad Request", "text/plain; charset=utf-8", ByteArray(0))
            return null
        }
        val headers = HashMap<String, String>()
        for (i in 1 until lines.size) {
            val colon = lines[i].indexOf(':')
            if (colon <= 0) continue
            val name = lines[i].substring(0, colon).trim().lowercase(Locale.ROOT)
            val value = lines[i].substring(colon + 1).trim()
            // A repeated header no longer parses as one value: a doubled length or range is then refused or ignored.
            headers[name] = headers[name]?.let { "$it, $value" } ?: value
        }
        return Request(first[0], first[1], headers)
    }

    private fun route(r: Request, input: InputStream, out: OutputStream, c: Conn) {
        val q = r.target.indexOf('?')
        val path = if (q >= 0) r.target.substring(0, q) else r.target
        val query = if (q >= 0) r.target.substring(q + 1) else ""
        val inner = path.removePrefix("/")
        val slash = inner.indexOf('/')
        if (!path.startsWith("/") || slash <= 0 || !tokenMatches(inner.substring(0, slash))) return notFound(r, input, out, c)
        // Only here: probing with a wrong token must not keep the server open.
        progress(c)
        val rest = inner.substring(slash + 1)
        when {
            rest.isEmpty() && r.method == "GET" ->
                send(out, 200, "OK", "text/html; charset=utf-8", page().toByteArray(Charsets.UTF_8))
            rest == "upload" && r.method == "POST" -> upload(r, query, input, out, c)
            rest.startsWith("f/") && (r.method == "GET" || r.method == "HEAD") -> download(r, rest, input, out, c)
            else -> notFound(r, input, out, c)
        }
    }

    private fun tokenMatches(s: String) =
        MessageDigest.isEqual(s.toByteArray(Charsets.ISO_8859_1), token.toByteArray(Charsets.ISO_8859_1))

    private fun notFound(r: Request, input: InputStream, out: OutputStream, c: Conn) {
        Thread.sleep(WRONG_DELAY_MS)
        send(out, 404, "Not Found", "text/plain; charset=utf-8", "לא נמצא".toByteArray(Charsets.UTF_8), r.method == "HEAD")
        drain(c.sock, input)
    }

    // ------------------------------------------------------------------ response

    private fun headerBytes(code: Int, reason: String, extra: List<String>): ByteArray {
        val sb = StringBuilder("HTTP/1.1 ").append(code).append(' ').append(reason).append("\r\n")
        sb.append("Connection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n")
        for (h in extra) sb.append(h).append("\r\n")
        sb.append("\r\n")
        return sb.toString().toByteArray(Charsets.ISO_8859_1)
    }

    private fun send(out: OutputStream, code: Int, reason: String, type: String, body: ByteArray, headOnly: Boolean = false) {
        out.write(headerBytes(code, reason, listOf("Content-Type: $type", "Content-Length: ${body.size}")))
        if (!headOnly) out.write(body)
        out.flush()
    }

    /**
     * After answering without reading a request's body: closing a socket with unread data resets it, and the browser
     * would then lose the answer. Reads a limited amount so that the status arrives.
     */
    private fun drain(sock: Socket, input: InputStream) {
        try {
            sock.shutdownOutput()
            sock.soTimeout = 500
            val buf = ByteArray(8192)
            val until = System.currentTimeMillis() + 2000
            var left = 256 * 1024
            while (left > 0 && System.currentTimeMillis() < until) {
                val n = input.read(buf, 0, minOf(buf.size, left))
                if (n < 0) break
                left -= n
            }
        } catch (_: IOException) {
        }
    }

    // ------------------------------------------------------------------ download

    private fun download(r: Request, rest: String, input: InputStream, out: OutputStream, c: Conn) {
        val parts = rest.split('/')
        val item = if (parts.size == 3) parts[1].toIntOrNull()?.let { items.getOrNull(it) } else null
        if (item == null) return notFound(r, input, out, c)
        val size = item.size
        val known = size > 0
        val range = if (known) parseRange(r.headers["range"], size) else null
        if (range === UNSATISFIABLE) {
            out.write(headerBytes(416, "Range Not Satisfiable", listOf("Content-Range: bytes */$size", "Content-Length: 0")))
            out.flush()
            return
        }
        val from = range?.get(0) ?: 0L
        val to = range?.get(1) ?: (size - 1)

        // Opened and positioned before the status line, so that a file that can't be read is an error and not a broken 200.
        var stream: InputStream? = null
        try {
            if (r.method == "GET") {
                try {
                    stream = item.open()
                    skipFully(stream, from, c)
                } catch (e: Exception) {
                    Log.w("web open: $e")
                    send(out, 500, "Internal Server Error", "text/plain; charset=utf-8", "הקובץ לא נפתח".toByteArray(Charsets.UTF_8))
                    return
                }
            }
            val extra = mutableListOf(
                "Content-Type: application/octet-stream",
                "Content-Disposition: attachment; filename*=UTF-8''" + pct(item.name),
                "Accept-Ranges: bytes",
            )
            if (known) extra += "Content-Length: ${to - from + 1}"
            if (range != null) extra += "Content-Range: bytes $from-$to/$size"
            out.write(if (range != null) headerBytes(206, "Partial Content", extra) else headerBytes(200, "OK", extra))
            out.flush()
            progress(c)
            if (stream != null) copyOut(stream, out, if (known) to - from + 1 else Long.MAX_VALUE, c)
        } finally {
            closeQuiet(stream)
        }
    }

    /** "bytes=a-" or "bytes=a-b" → {from, to}; anything else is ignored (null), a start past the end is [UNSATISFIABLE]. */
    private fun parseRange(h: String?, size: Long): LongArray? {
        if (h == null || !h.startsWith("bytes=") || ',' in h) return null
        val v = h.substring(6).trim()
        val dash = v.indexOf('-')
        if (dash <= 0) return null
        val a = v.substring(0, dash).trim().toLongOrNull() ?: return null
        val end = v.substring(dash + 1).trim()
        val b = if (end.isEmpty()) null else end.toLongOrNull() ?: return null
        if (a < 0 || (b != null && b < a)) return null
        if (a >= size) return UNSATISFIABLE
        return longArrayOf(a, minOf(b ?: (size - 1), size - 1))
    }

    private fun skipFully(s: InputStream, count: Long, c: Conn) {
        var left = count
        while (left > 0) {
            val n = s.skip(left)
            if (n > 0) left -= n
            else if (s.read() < 0) throw EOFException()
            else left--
            progress(c)
        }
    }

    /** A source shorter than promised ends the connection early, and the browser sees a broken download. */
    private fun copyOut(s: InputStream, out: OutputStream, count: Long, c: Conn) {
        val buf = ByteArray(BUFFER)
        var left = count
        while (left > 0) {
            val n = s.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
            if (n < 0) break
            out.write(buf, 0, n)
            left -= n
            sent.addAndGet(n.toLong())
            progress(c)
        }
        out.flush()
    }

    // ------------------------------------------------------------------ upload

    private fun refuseUpload(c: Conn, input: InputStream, out: OutputStream, code: Int, reason: String) {
        send(out, code, reason, "application/json; charset=utf-8", "{\"ok\":false}".toByteArray(Charsets.UTF_8))
        drain(c.sock, input)
    }

    private fun upload(r: Request, query: String, input: InputStream, out: OutputStream, c: Conn) {
        val dir = uploadDir
        if (dir == null || !allowUpload) return refuseUpload(c, input, out, 403, "Forbidden")
        val name = queryName(query)?.let { safeName(it) } ?: return refuseUpload(c, input, out, 400, "Bad Request")
        val lengthText = r.headers["content-length"] ?: return refuseUpload(c, input, out, 411, "Length Required")
        val length = lengthText.toLongOrNull()?.takeIf { it >= 0 } ?: return refuseUpload(c, input, out, 400, "Bad Request")
        if (!dir.isDirectory && !dir.mkdirs() && !dir.isDirectory) return refuseUpload(c, input, out, 500, "Internal Server Error")
        if (dir.usableSpace < length) return refuseUpload(c, input, out, 507, "Insufficient Storage")

        c.sock.soTimeout = TRANSFER_TIMEOUT
        val target = reserve(dir, name)
        val part = partOf(target)
        val dest: File
        var ok = false
        try {
            FileOutputStream(part).use { f ->
                val buf = ByteArray(BUFFER)
                var left = length
                while (left > 0) {
                    if (!allowUpload || stopped.get()) throw IOException("upload turned off")
                    val n = input.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
                    if (n < 0) throw EOFException()
                    f.write(buf, 0, n)
                    left -= n
                    received.addAndGet(n.toLong())
                    progress(c)
                }
            }
            dest = synchronized(life) {
                // renameTo replaces an existing file without asking, and something else may have created this name meanwhile.
                val free = FileService.uniqueFile(target)
                if (!part.renameTo(free)) throw IOException("rename failed")
                free
            }
            ok = true
        } finally {
            if (!ok) part.delete()
        }
        try { onUploaded(dest) } catch (t: Throwable) { Log.w("web onUploaded: $t") }
        val body = "{\"ok\":true,\"name\":\"" + jsonEscape(dest.name) + "\"}"
        send(out, 200, "OK", "application/json; charset=utf-8", body.toByteArray(Charsets.UTF_8))
    }

    private fun partOf(target: File) = File(target.parentFile, target.name + PART)

    /** Picks a free name and claims it by creating its partial file, so two uploads of one name can't collide. */
    private fun reserve(dir: File, name: String): File {
        val dot = name.lastIndexOf('.')
        val base = if (dot > 0) name.substring(0, dot) else name
        val ext = if (dot > 0) name.substring(dot) else ""
        var i = 0
        while (i < 100_000) {
            val target = File(dir, if (i == 0) name else "$base ($i)$ext")
            if (!target.exists() && partOf(target).createNewFile()) return target
            i++
        }
        throw IOException("no free name")
    }

    private fun queryName(query: String): String? {
        val raw = query.split('&').firstOrNull { it.startsWith("name=") }?.substring(5) ?: return null
        return try { URLDecoder.decode(raw, "UTF-8") } catch (_: IllegalArgumentException) { null }
    }

    /** The last path segment, with what a file name can't hold replaced; null for a name that can't be used. */
    fun safeName(raw: String): String? {
        val last = raw.substring(maxOf(raw.lastIndexOf('/'), raw.lastIndexOf('\\')) + 1)
        val clean = buildString {
            for (ch in last) append(if (Character.isISOControl(ch) || ch in "\\/:*?\"<>|") '_' else ch)
        }.trim()
        return clean.takeUnless { it.isEmpty() || it == "." || it == ".." }
    }

    private fun jsonEscape(s: String) = buildString {
        for (ch in s) when {
            ch == '"' -> append("\\\"")
            ch == '\\' -> append("\\\\")
            ch < ' ' -> append("\\u").append(ch.code.toString(16).padStart(4, '0'))
            else -> append(ch)
        }
    }

    /** Percent-encodes everything but the unreserved characters, as UTF-8 (safe in a URL path and in filename*). */
    private fun pct(s: String) = buildString {
        for (b in s.toByteArray(Charsets.UTF_8)) {
            val v = b.toInt() and 0xff
            val ch = v.toChar()
            if (ch in 'a'..'z' || ch in 'A'..'Z' || ch in '0'..'9' || ch == '-' || ch == '.' || ch == '_' || ch == '~') append(ch)
            else append('%').append("0123456789ABCDEF"[v shr 4]).append("0123456789ABCDEF"[v and 15])
        }
    }

    // ------------------------------------------------------------------ page

    private fun html(s: String) = buildString {
        for (ch in s) when (ch) {
            '&' -> append("&amp;")
            '<' -> append("&lt;")
            '>' -> append("&gt;")
            '"' -> append("&quot;")
            '\'' -> append("&#39;")
            else -> append(ch)
        }
    }

    private fun page(): String {
        val sb = StringBuilder(4096)
        sb.append("<!doctype html><html dir=\"rtl\" lang=\"he\"><head><meta charset=\"utf-8\">")
        sb.append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>גשר בלוטוס</title><style>")
        sb.append(CSS)
        sb.append("</style></head><body><h1>גשר בלוטוס</h1><h2>קבצים להורדה</h2><ul>")
        items.forEachIndexed { i, it ->
            val size = if (it.size > 0) Transfer.formatSize(it.size) else "גודל לא ידוע"
            sb.append("<li><div class=\"n\"><bdi>").append(html(it.name)).append("</bdi></div><div class=\"r\"><span>")
                .append(html(size)).append("</span><a href=\"f/").append(i).append('/').append(pct(it.name)).append("\">הורד</a></div></li>")
        }
        sb.append("</ul>")
        if (uploadDir != null && allowUpload) {
            sb.append("<h2>שליחת קבצים לטלפון</h2><label class=\"b\">בחר קבצים<input id=\"p\" type=\"file\" multiple></label>")
            sb.append("<div id=\"s\"></div><div id=\"l\"></div><script>").append(SCRIPT).append("</script>")
        }
        sb.append("<p class=\"note\">").append(NOTE).append("</p></body></html>")
        return sb.toString()
    }
}

private const val CSS = """
body{font-family:sans-serif;margin:0 auto;padding:16px;max-width:640px;background:#fff;color:#1f2937}
h1{font-size:1.4em;margin:0 0 12px}
h2{font-size:1.1em;margin:20px 0 8px}
ul{list-style:none;margin:0;padding:0}
li{border-bottom:1px solid #e5e7eb;padding:10px 0}
.n{font-weight:bold;word-break:break-all}
.r{display:flex;justify-content:space-between;align-items:center;margin-top:6px;color:#6b7280}
a,.b{display:inline-block;background:#2563eb;color:#fff;padding:8px 18px;border-radius:6px;text-decoration:none}
.b input{display:none}
#s{margin-top:10px}
#l div{margin-top:4px;word-break:break-all}
.note{margin-top:24px;font-size:.85em;color:#6b7280}
"""

/** One file at a time. Messages go in with textContent, so a file name can't inject anything. */
private const val SCRIPT = """
(function(){
var p=document.getElementById('p'),s=document.getElementById('s'),l=document.getElementById('l'),q=[],busy=false;
function line(t){var d=document.createElement('div');d.textContent=t;l.appendChild(d);}
function why(c){return c==403?'הטלפון לא מאפשר כרגע לקבל קבצים':c==507?'אין מספיק מקום פנוי בטלפון':'השליחה נכשלה';}
function next(){
if(busy)return;
var f=q.shift();
if(!f){s.textContent='';return;}
busy=true;
s.textContent='שולח: '+f.name;
var x=new XMLHttpRequest();
x.open('POST','upload?name='+encodeURIComponent(f.name));
x.upload.onprogress=function(e){if(e.lengthComputable)s.textContent='שולח: '+f.name+' ('+Math.floor(e.loaded*100/e.total)+'%)';};
x.onload=function(){busy=false;line(x.status==200?'נשלח: '+f.name:why(x.status)+': '+f.name);next();};
x.onerror=function(){busy=false;line('השליחה נכשלה: '+f.name);next();};
x.send(f);
}
p.onchange=function(){for(var i=0;i<p.files.length;i++)q.push(p.files[i]);p.value='';next();};
})();
"""

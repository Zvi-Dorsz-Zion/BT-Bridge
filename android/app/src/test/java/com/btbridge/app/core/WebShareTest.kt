package com.btbridge.app.core

import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.ByteArrayInputStream
import java.io.File
import java.net.HttpURLConnection
import java.net.Proxy
import java.net.Socket
import java.net.URL
import java.net.URLEncoder
import java.util.concurrent.atomic.AtomicInteger

class WebShareTest {
    @get:Rule val tmp = TemporaryFolder()

    private val running = mutableListOf<WebShare>()
    private val data = ByteArray(200_000) { (it * 31 % 251).toByte() }
    private val hebrew = "דוח שנתי (סופי).txt"

    @After fun stopAll() = running.forEach { it.stop() }

    private fun item(name: String, bytes: ByteArray = data) = ShareItem(name, bytes.size.toLong()) { ByteArrayInputStream(bytes) }

    private fun share(items: List<ShareItem> = listOf(item("a.bin")), dir: File? = null, onStopped: () -> Unit = {}, idle: Long = 60_000): WebShare =
        WebShare(items, dir, onStopped = onStopped, idleLimitMs = idle).also { it.start(); running += it }

    private class Reply(val code: Int, val body: ByteArray, val c: HttpURLConnection) {
        val text get() = String(body, Charsets.UTF_8)
        fun header(n: String): String? = c.getHeaderField(n)
    }

    private fun call(w: WebShare, rest: String, method: String = "GET", headers: Map<String, String> = emptyMap(), upload: ByteArray? = null): Reply {
        val c = URL("http://127.0.0.1:${w.port}$rest").openConnection(Proxy.NO_PROXY) as HttpURLConnection
        c.requestMethod = method
        c.connectTimeout = 5000
        c.readTimeout = 20000
        headers.forEach { (k, v) -> c.setRequestProperty(k, v) }
        if (upload != null) {
            c.doOutput = true
            c.outputStream.use { it.write(upload) }
        }
        val code = c.responseCode
        val body = (if (code >= 400) c.errorStream else c.inputStream)?.use { it.readBytes() } ?: ByteArray(0)
        return Reply(code, body, c)
    }

    private fun send(w: WebShare, name: String, bytes: ByteArray) =
        call(w, w.path + "upload?name=" + URLEncoder.encode(name, "UTF-8"), "POST", upload = bytes)

    private fun pct(s: String) = URLEncoder.encode(s, "UTF-8").replace("+", "%20")

    @Test
    fun tokenIsLongAndRandom() {
        val a = share()
        assertTrue(a.token, a.token.matches(Regex("[a-z0-9]{32}")))
        assertEquals("/${a.token}/", a.path)
        assertNotEquals(a.token, share().token)
    }

    @Test
    fun pageListsFiles() {
        val w = share(listOf(item("a.bin"), item("<b>x.txt")))
        val r = call(w, w.path)
        assertEquals(200, r.code)
        assertTrue(r.text.contains("dir=\"rtl\""))
        assertTrue(r.text.contains("a.bin"))
        assertTrue(r.text.contains("&lt;b&gt;x.txt"))
        assertFalse(r.text.contains("<b>x.txt"))
        assertEquals("close", r.header("Connection"))
        assertEquals("no-store", r.header("Cache-Control"))
        assertEquals("no-referrer", r.header("Referrer-Policy"))
        assertEquals("nosniff", r.header("X-Content-Type-Options"))
    }

    @Test
    fun uploadFormOnlyWhenAllowed() {
        val w = share(dir = tmp.newFolder())
        assertFalse(call(w, w.path).text.contains("type=\"file\""))
        w.allowUpload = true
        assertTrue(call(w, w.path).text.contains("type=\"file\""))
    }

    @Test
    fun downloadsWholeFile() {
        val w = share()
        val r = call(w, w.path + "f/0/a.bin")
        assertEquals(200, r.code)
        assertArrayEquals(data, r.body)
        assertEquals("application/octet-stream", r.header("Content-Type"))
        assertEquals("bytes", r.header("Accept-Ranges"))
        assertEquals(data.size.toString(), r.header("Content-Length"))
        assertEquals(data.size.toLong(), w.bytesSent)
    }

    @Test
    fun headHasNoBody() {
        val w = share()
        val r = call(w, w.path + "f/0/a.bin", "HEAD")
        assertEquals(200, r.code)
        assertEquals(data.size.toString(), r.header("Content-Length"))
        assertEquals(0, r.body.size)
        assertEquals(0L, w.bytesSent)
    }

    @Test
    fun rangeGivesThePart() {
        val w = share()
        val r = call(w, w.path + "f/0/a.bin", headers = mapOf("Range" to "bytes=1000-1999"))
        assertEquals(206, r.code)
        assertEquals("bytes 1000-1999/${data.size}", r.header("Content-Range"))
        assertArrayEquals(data.copyOfRange(1000, 2000), r.body)

        val open = call(w, w.path + "f/0/a.bin", headers = mapOf("Range" to "bytes=199990-"))
        assertEquals(206, open.code)
        assertArrayEquals(data.copyOfRange(199990, data.size), open.body)

        val past = call(w, w.path + "f/0/a.bin", headers = mapOf("Range" to "bytes=199990-999999"))
        assertEquals("bytes 199990-199999/${data.size}", past.header("Content-Range"))
    }

    @Test
    fun rangePastTheEndIs416() {
        val w = share()
        val r = call(w, w.path + "f/0/a.bin", headers = mapOf("Range" to "bytes=${data.size}-"))
        assertEquals(416, r.code)
        assertEquals("bytes */${data.size}", r.header("Content-Range"))
    }

    @Test
    fun unknownSizeIsSentToTheEnd() {
        val w = share(listOf(ShareItem("u.bin", 0) { ByteArrayInputStream(data) }))
        val r = call(w, w.path + "f/0/u.bin", headers = mapOf("Range" to "bytes=5-"))
        assertEquals(200, r.code)
        assertEquals(null, r.header("Content-Length"))
        assertArrayEquals(data, r.body)
    }

    @Test
    fun wrongTokenAndOtherPathsAre404() {
        val w = share()
        val started = System.currentTimeMillis()
        assertEquals(404, call(w, "/" + "x".repeat(32) + "/").code)
        assertTrue(System.currentTimeMillis() - started >= 150)
        assertEquals(404, call(w, "/").code)
        assertEquals(404, call(w, "/" + w.token).code)
        assertEquals(404, call(w, "/" + "x".repeat(32) + "/f/0/a.bin").code)
        assertEquals(404, call(w, w.path + "f/9/a.bin").code)
        assertEquals(404, call(w, w.path + "f/x/a.bin").code)
        assertEquals(404, call(w, w.path + "../a.bin").code)
        assertEquals(0L, w.bytesSent)
    }

    @Test
    fun hebrewName() {
        val w = share(listOf(item(hebrew)))
        val page = call(w, w.path).text
        assertTrue(page.contains(hebrew))
        val r = call(w, w.path + "f/0/" + pct(hebrew))
        assertEquals(200, r.code)
        assertArrayEquals(data, r.body)
        assertEquals("attachment; filename*=UTF-8''" + pct(hebrew), r.header("Content-Disposition"))
    }

    @Test
    fun uploadRefusedWhenOff() {
        val dir = tmp.newFolder()
        val w = share(dir = dir)
        assertEquals(403, send(w, "x.txt", byteArrayOf(1, 2, 3)).code)
        assertEquals(0, dir.list()!!.size)
    }

    @Test
    fun uploadRefusedWithoutFolder() {
        val w = share()
        w.allowUpload = true
        assertEquals(403, send(w, "x.txt", byteArrayOf(1, 2, 3)).code)
    }

    @Test
    fun uploadIsSaved() {
        val dir = tmp.newFolder()
        val seen = mutableListOf<File>()
        val w = WebShare(listOf(item("a.bin")), dir, onUploaded = { synchronized(seen) { seen += it } }).also { it.start(); running += it }
        w.allowUpload = true
        val r = send(w, "שלום.bin", data)
        assertEquals(200, r.code)
        assertEquals("{\"ok\":true,\"name\":\"שלום.bin\"}", r.text)
        assertArrayEquals(data, File(dir, "שלום.bin").readBytes())
        assertEquals(listOf("שלום.bin"), dir.list()!!.toList())
        assertEquals(listOf(File(dir, "שלום.bin")), synchronized(seen) { seen.toList() })
    }

    @Test
    fun traversalStaysInside() {
        val parent = tmp.newFolder()
        val dir = File(parent, "in").apply { mkdirs() }
        val w = share(dir = dir)
        w.allowUpload = true
        assertEquals(200, send(w, "../../evil.txt", byteArrayOf(9)).code)
        assertArrayEquals(byteArrayOf(9), File(dir, "evil.txt").readBytes())
        assertEquals(listOf("in"), parent.list()!!.toList())
        assertEquals(200, send(w, "..\\..\\back.txt", byteArrayOf(8)).code)
        assertTrue(File(dir, "back.txt").exists())
        assertEquals(400, send(w, "..", byteArrayOf(1)).code)
        assertEquals(400, send(w, "a/", byteArrayOf(1)).code)
        assertEquals(400, send(w, "", byteArrayOf(1)).code)
    }

    @Test
    fun badCharactersAreReplaced() {
        val dir = tmp.newFolder()
        val w = share(dir = dir)
        w.allowUpload = true
        assertEquals("{\"ok\":true,\"name\":\"a_b_c_.txt\"}", send(w, "a:b*c?.txt", byteArrayOf(1)).text)
        assertTrue(File(dir, "a_b_c_.txt").exists())
    }

    @Test
    fun duplicateGetsNumber() {
        val dir = tmp.newFolder()
        val w = share(dir = dir)
        w.allowUpload = true
        assertEquals(200, send(w, "x.txt", byteArrayOf(1)).code)
        val r = send(w, "x.txt", byteArrayOf(2))
        assertEquals("{\"ok\":true,\"name\":\"x (1).txt\"}", r.text)
        assertArrayEquals(byteArrayOf(1), File(dir, "x.txt").readBytes())
        assertArrayEquals(byteArrayOf(2), File(dir, "x (1).txt").readBytes())
    }

    /** Writes a raw request and reads the whole answer. */
    private fun raw(w: WebShare, head: String, body: ByteArray = ByteArray(0)): String {
        Socket("127.0.0.1", w.port).use { s ->
            s.soTimeout = 10000
            s.getOutputStream().write(head.toByteArray(Charsets.ISO_8859_1))
            s.getOutputStream().write(body)
            s.getOutputStream().flush()
            s.shutdownOutput()
            return String(s.getInputStream().readBytes(), Charsets.UTF_8)
        }
    }

    @Test
    fun uploadNeedsLength() {
        val w = share(dir = tmp.newFolder())
        w.allowUpload = true
        assertTrue(raw(w, "POST ${w.path}upload?name=a HTTP/1.1\r\nHost: x\r\n\r\n").startsWith("HTTP/1.1 411"))
    }

    @Test
    fun shortUploadLeavesNothing() {
        val dir = tmp.newFolder()
        val w = share(dir = dir)
        w.allowUpload = true
        raw(w, "POST ${w.path}upload?name=a.bin HTTP/1.1\r\nHost: x\r\nContent-Length: 10\r\n\r\n", byteArrayOf(1, 2, 3))
        // The server may still be finishing when the socket closes.
        repeat(100) { if (dir.list()!!.isNotEmpty()) Thread.sleep(20) }
        assertEquals(emptyList<String>(), dir.list()!!.toList())
    }

    @Test
    fun tooBigHeaderIsRefused() {
        val w = share()
        assertTrue(raw(w, "GET ${w.path} HTTP/1.1\r\nX-A: " + "a".repeat(20_000) + "\r\n\r\n").startsWith("HTTP/1.1 431"))
    }

    @Test
    fun seventeenthConnectionIsBusy() {
        val w = share()
        val held = List(16) { Socket("127.0.0.1", w.port) }
        try {
            Thread.sleep(200)
            assertTrue(raw(w, "GET ${w.path} HTTP/1.1\r\nHost: x\r\n\r\n").startsWith("HTTP/1.1 503"))
        } finally {
            held.forEach { it.close() }
        }
        // The slots come back.
        var answer = ""
        repeat(50) {
            answer = raw(w, "GET ${w.path} HTTP/1.1\r\nHost: x\r\n\r\n")
            if (!answer.startsWith("HTTP/1.1 200")) Thread.sleep(50)
        }
        assertTrue(answer.startsWith("HTTP/1.1 200"))
    }

    @Test
    fun stopsWhenIdleAndTellsOnce() {
        val calls = AtomicInteger()
        val w = share(onStopped = { calls.incrementAndGet() }, idle = 150)
        repeat(100) { if (w.running) Thread.sleep(20) }
        assertFalse(w.running)
        w.stop()
        Thread.sleep(50)
        assertEquals(1, calls.get())
    }

    @Test
    fun stopClosesThePort() {
        val calls = AtomicInteger()
        val w = share(onStopped = { calls.incrementAndGet() })
        val port = w.port
        w.stop()
        assertFalse(runCatching { Socket("127.0.0.1", port).close() }.isSuccess)
        assertEquals(0, calls.get())
    }
}

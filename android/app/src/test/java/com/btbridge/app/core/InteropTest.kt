package com.btbridge.app.core

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.nio.file.Files
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

/**
 * Runs the real Kotlin core against the real Windows core (windows/InteropTest) over TCP:
 * handshake with a Hebrew password, a wrong password first, Wi-Fi link, file operations both ways,
 * and a second connection that must not ask for a password.
 * Needs INTEROP_EXE = path of InteropTest.exe; skipped otherwise.
 */
class InteropTest {
    private val password = "סיסמה-123"
    private val exe = System.getenv("INTEROP_EXE")

    private class MemStore(override val deviceName: String) : Store {
        private val salt = Crypto.random(16)
        private val key = Crypto.pbkdf2("סיסמה-123", salt, 50_000)
        private val trusted = CopyOnWriteArrayList<TrustedDevice>()
        override val pwSalt = Crypto.b64(salt)
        override val pwIter = 50_000
        override val hasPassword = true
        override fun passwordKey() = key
        override fun findByFp(fp: String) = trusted.firstOrNull { it.fp == fp }
        override fun findByAddress(address: String) = trusted.firstOrNull { it.address == address }
        override fun trust(fp: String, pub: ByteArray, name: String, address: String, os: String) {
            trusted.removeAll { it.fp == fp || it.address == address }
            trusted += TrustedDevice(fp, Crypto.b64(pub), name, address, os)
        }
        override fun touch(fp: String, name: String, address: String) {}
        fun forgetAll() = trusted.clear()
    }

    /**
     * Unit tests have no image library, so tiles are 24-bit BMP here (the Windows side decodes any GDI+ format).
     * The real app uses JPEG through Bitmap.compress.
     */
    private object JvmCodec : TileCodec {
        override fun encode(px: IntArray, stride: Int, x: Int, y: Int, w: Int, h: Int, quality: Int): ByteArray {
            val row = (w * 3 + 3) and 3.inv()
            val b = java.nio.ByteBuffer.allocate(54 + row * h).order(java.nio.ByteOrder.LITTLE_ENDIAN)
            b.put('B'.code.toByte()).put('M'.code.toByte()).putInt(54 + row * h).putInt(0).putInt(54)
            b.putInt(40).putInt(w).putInt(h).putShort(1).putShort(24).putInt(0).putInt(row * h).putInt(2835).putInt(2835).putInt(0).putInt(0)
            for (r in h - 1 downTo 0) {
                val start = b.position()
                for (c in 0 until w) {
                    val p = px[(y + r) * stride + x + c]
                    b.put((p and 0xff).toByte()).put((p shr 8 and 0xff).toByte()).put((p shr 16 and 0xff).toByte())
                }
                b.position(start + row)
            }
            return b.array()
        }

        /** Width and height from a JPEG's SOF marker. */
        fun decodeSize(j: ByteArray): Pair<Int, Int> {
            var i = 2
            while (i + 9 < j.size) {
                if (j[i] != 0xFF.toByte()) { i++; continue }
                val m = j[i + 1].toInt() and 0xff
                val len = ((j[i + 2].toInt() and 0xff) shl 8) or (j[i + 3].toInt() and 0xff)
                if (m in 0xC0..0xC3) {
                    val h = ((j[i + 5].toInt() and 0xff) shl 8) or (j[i + 6].toInt() and 0xff)
                    val w = ((j[i + 7].toInt() and 0xff) shl 8) or (j[i + 8].toInt() and 0xff)
                    return w to h
                }
                i += 2 + len
            }
            error("no SOF in JPEG")
        }
    }

    /** A 1080×2340 "phone screen" with a moving bar. */
    private class FakePhoneScreen : ScreenGrabber {
        override val realW = 1080
        override val realH = 2340
        private var n = 0
        override fun grab(maxW: Int): Frame {
            val (w, h) = ScreenProto.fit(realW, realH, maxW)
            val px = IntArray(w * h) { 0x103050 + (it % w) }
            val y0 = (n++ * 50) % (h - 40)
            for (y in y0 until y0 + 40) for (x in 0 until w) px[y * w + x] = 0x20FF20
            return Frame(w, h, px)
        }
    }

    /** A minimal Bridge: sessions + file service + Wi-Fi links + screen sharing, without Bluetooth. */
    private class Node : RequestHandler {
        val store = MemStore("טלפון-בדיקה")
        val me = Identity(Crypto.newKeyPair())
        val sessions = CopyOnWriteArrayList<Session>()
        val inbox: File = Files.createTempDirectory("btb-inbox").toFile()
        val received = CopyOnWriteArrayList<File>()
        val inputs = CopyOnWriteArrayList<String>()
        val clips = CopyOnWriteArrayList<String>()
        val files = FileService({ store.deviceName }, "android", File("."), { emptyList() }, { inbox }, { _, f, _ -> received += f })
        val wifi = WifiLinks({ sessions.filter { !it.closed } }, ::ips, WifiLinks::plainConnect).also { it.start() }

        fun add(hr: HandshakeResult, initiator: Boolean, sock: Socket): Session {
            val s = Session(hr, "AABBCCDDEE09", initiator, this)
            sessions += s
            // Like the app: a dropped Wi-Fi link comes back by itself.
            s.onWifiLost = { x -> Thread { Thread.sleep(500); wifi.refresh(x) }.start() }
            s.attach(Link(false, sock.getInputStream(), sock.getOutputStream(), sock, hr.codec))
            wifi.announce(s)
            return s
        }

        override fun handle(s: Session, id: Int, req: JSONObject): JSONObject? {
            if (req.str("op") == "log") return JSONObject().put("ok", true).put("text", "יומן בדיקה")
            if (req.str("op") != "screen-open") return files.handle(s, id, req)
            val src = ScreenSource(s, id, FakePhoneScreen(), { ev, x, y, x2, y2 -> inputs += "${ev.str("t")} $x $y $x2 $y2" }, JvmCodec, req)
            s.reply(id, JSONObject().put("ok", true).put("kind", "android").put("monitors", org.json.JSONArray()))
            src.start()
            return null
        }

        override fun onEvent(s: Session, ev: JSONObject) {
            if (ev.str("ev") == "clip") { clips += ev.str("text") ?: ""; return }
            if (!ScreenSource.onEvent(s, ev)) wifi.onEvent(s, ev)
        }

        private fun ips(): List<String> = NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.toList() }
            .filterIsInstance<Inet4Address>().map { it.hostAddress!! }
    }

    private fun pattern(n: Int) = ByteArray(n) { ((it * 31 + 7) and 0xff).toByte() }

    init {
        // The Windows side checks that our version and features arrive.
        AppInfo.version = "9.9.9"
    }

    /** Sends the same Wi-Fi hello twice on raw connections: the first must be answered, the second refused (PROTOCOL.md section 6). */
    private fun replayRefused(s: Session): Boolean {
        val linkId = Crypto.random(16)
        val hello = Crypto.concat(linkId, Crypto.hmac(s.linkKey, Crypto.utf8("btb-wifi"), linkId))
        fun attempt(): ByteArray? = Socket(s.peerIps.first(), s.peerPort).use { c ->
            c.soTimeout = 3000
            Wire.write(c.getOutputStream(), hello)
            runCatching { Wire.read(c.getInputStream()) }.getOrNull()
        }
        val first = attempt()
        val second = attempt()
        println("REPLAY first=${first?.size} second=${second?.size}")
        return first?.size == 48 && second == null
    }

    @Test(timeout = 300_000)
    fun kotlinServer_windowsClient() {
        assumeTrue("INTEROP_EXE not set", exe != null)
        val node = Node()
        val dir = Files.createTempDirectory("btb-kt").toFile()
        File(dir, "from-kotlin.bin").writeBytes(pattern(3_000_000))
        File(dir, "sub").mkdirs()
        File(dir, "sub/deep-findme.txt").writeText("x")
        val ss = ServerSocket(0, 5, InetAddress.getLoopbackAddress())
        val lockout = Lockout()
        Thread {
            while (true) {
                val c = runCatching { ss.accept() }.getOrNull() ?: break
                Thread {
                    try {
                        val hr = Handshake.server(c.getInputStream(), c.getOutputStream(), node.me, node.store, "android", "AABBCCDDEE02", lockout)
                        node.add(hr, false, c)
                    } catch (e: Exception) {
                        println("serve failed: $e")
                        c.close()
                    }
                }.start()
            }
        }.apply { isDaemon = true }.start()

        val state = Files.createTempDirectory("btb-cs").toFile()
        val p = ProcessBuilder(exe, "client", ss.localPort.toString(), state.path, dir.path).redirectErrorStream(true).start()
        val out = p.inputStream.bufferedReader(Charsets.UTF_8).readText()
        p.waitFor(120, TimeUnit.SECONDS)
        println(out)
        ss.close()
        assertTrue("windows client failed:\n$out", out.contains("OK") && p.exitValue() == 0)
        assertTrue("wifi link", out.contains("WIFI True"))
        val up = File(dir, "from-windows.bin")
        assertTrue(up.exists())
        assertTrue(up.readBytes().contentEquals(pattern(2_500_000)))
        assertTrue(File(dir, "שם-חדש").isDirectory)
        assertTrue(!File(dir, "למחיקה").exists())
        assertTrue(dir.listFiles()!!.none { it.name.endsWith(".btpart") })
        assertTrue("replay test", out.contains("REPLAY refused"))
        assertTrue("resumed download", out.contains("RESUME-DOWNLOAD ok"))
        assertTrue("search from windows", out.contains("SEARCH ok"))
        assertTrue("clip from windows: ${node.clips}", node.clips.contains("מהמחשב"))

        // "Send to device" from Windows landed in the phone's inbox, and "../" was refused.
        val got = File(File(node.inbox, "תת"), "מחשב.bin")
        assertTrue("inbox file", got.exists() && got.readBytes().contentEquals(pattern(300_000)))
        assertTrue(node.received.any { it.path == got.path })
        assertTrue(!File(node.inbox.parentFile, "escape.bin").exists())

        // Screen: frames went out, and the Windows viewer's input arrived mapped to the real 1080×2340 screen.
        assertTrue("screen frames", out.contains("SCREEN frames="))
        val (fw, fh) = Regex("size=(\\d+)x(\\d+)").find(out)!!.destructured
        val tap = node.inputs.first { it.startsWith("tap") }.split(" ")
        assertEquals(540, tap[1].toInt())
        assertEquals(2340 / 4, tap[2].toInt(), 3)
        assertTrue("frame scaled from 1080 wide: ${fw}x$fh", fw.toInt() == 1080)
        val swipe = node.inputs.first { it.startsWith("swipe") }.split(" ")
        assertEquals(30 * 1080 / fw.toInt(), swipe[3].toInt())
    }

    private fun assertEquals(expected: Int, actual: Int, tolerance: Int) =
        assertTrue("expected $expected±$tolerance, got $actual", kotlin.math.abs(expected - actual) <= tolerance)

    @Test(timeout = 300_000)
    fun windowsServer_kotlinClient() {
        assumeTrue("INTEROP_EXE not set", exe != null)
        val node = Node()
        val state = Files.createTempDirectory("btb-cs").toFile()
        val dir = Files.createTempDirectory("btb-srv").toFile()
        File(dir, "from-windows.bin").writeBytes(pattern(3_000_000))
        File(dir, "sub").mkdirs()
        File(dir, "sub/deep-findme.txt").writeText("x")
        val p = ProcessBuilder(exe, "server", state.path).redirectErrorStream(true).start()
        val winOut = CopyOnWriteArrayList<String>()
        try {
            val reader = p.inputStream.bufferedReader(Charsets.UTF_8)
            val port = reader.readLine().removePrefix("PORT ").trim().toInt()
            val win = p.outputStream.bufferedWriter(Charsets.UTF_8)
            Thread { reader.forEachLine { println("[win] $it"); winOut += it } }.apply { isDaemon = true }.start()

            var asked = 0
            fun connect(ask: AskPassword): Session {
                val sock = Socket(InetAddress.getLoopbackAddress(), port)
                val hr = Handshake.client(sock.getInputStream(), sock.getOutputStream(), node.me, node.store, "android", "AABBCCDDEE01", ask)
                return node.add(hr, true, sock)
            }

            val s = connect { _, wrong ->
                asked++
                PasswordAnswer(if (wrong) password else "טעות", true)
            }
            assertEquals(2, asked)
            assertEquals("מחשב-בדיקה", s.peerName)
            assertEquals("windows", s.peerOs)
            repeat(50) { if (!s.hasWifi) Thread.sleep(100) }
            assertTrue("wifi link", s.hasWifi)
            assertTrue("caps ${s.peerCaps}", s.has("resume") && s.has("clip") && s.peerApp.isNotEmpty())
            assertTrue("windows saw our version: $winOut", winOut.any { it.contains("app=9.9.9 clip=True") })

            // A recorded Wi-Fi hello must not be accepted twice; the real link comes back afterwards.
            assertTrue("replay refused", replayRefused(s))
            repeat(80) { if (!s.hasWifi) Thread.sleep(100) }
            assertTrue("wifi back after replay test", s.hasWifi)

            val listing = RemoteFs.list(s, dir.path)
            assertTrue(listing.items.any { it.name == "from-windows.bin" && it.size == 3_000_000L })
            val root = RemoteFs.list(s, "")
            assertTrue("drives in root", root.items.isNotEmpty())

            val got = File(state, "got.bin")
            RemoteFs.download(s, RemoteFs.join(s, dir.path, "from-windows.bin"), got, Transfer("got", false))
            assertTrue(got.readBytes().contentEquals(pattern(3_000_000)))

            // Resume a download: part of it is here from an interrupted try.
            val item = listing.items.first { it.name == "from-windows.bin" }
            val resumed = File(state, "resumed.bin")
            File(resumed.path + ".btpart").writeBytes(pattern(3_000_000).copyOfRange(0, 1_100_000))
            RemoteFs.download(s, item.path, resumed, Transfer("resumed", false).apply { attempts = 1 }, item.size, item.mtime)
            assertTrue("resumed download", resumed.readBytes().contentEquals(pattern(3_000_000)))

            // Search on the computer: a name inside a subfolder, in any letter case, with its folder.
            val found = RemoteFs.search(s, dir.path, "FINDME")
            assertTrue("search: ${found.items.map { it.path }}", found.items.any { it.name == "deep-findme.txt" && it.folder.endsWith("sub") })

            val upSrc = pattern(2_500_000)
            val t = Transfer("up", true)
            val landed = RemoteFs.upload(s, { upSrc.inputStream() }, upSrc.size.toLong(), System.currentTimeMillis(),
                RemoteFs.join(s, dir.path, "from-kotlin.bin"), t)
            assertTrue(File(landed).readBytes().contentEquals(upSrc))
            // Same name again: the other side keeps both.
            val landed2 = RemoteFs.upload(s, { upSrc.inputStream() }, upSrc.size.toLong(), 0,
                RemoteFs.join(s, dir.path, "from-kotlin.bin"), Transfer("up2", true))
            assertTrue(landed2.endsWith("from-kotlin (1).bin"))

            // Resume an upload: the other side keeps the part that arrived before the interruption.
            val full = pattern(2_000_000)
            File(dir, "resume-up.bin.btpart").writeBytes(full.copyOfRange(0, 700_000))
            val tu = Transfer("resume-up", true).apply { attempts = 1 }
            val landed3 = RemoteFs.upload(s, { full.inputStream() }, full.size.toLong(), 0, RemoteFs.join(s, dir.path, "resume-up.bin"), tu)
            assertTrue("resumed upload", File(landed3).readBytes().contentEquals(full))

            // Shared clipboard, both ways, and the other side's log.
            s.sendEvent(JSONObject().put("ev", "clip").put("text", "מהטלפון"))
            win.write("CLIP hello-from-windows\n"); win.flush()
            repeat(50) { if (node.clips.isEmpty()) Thread.sleep(100) }
            assertTrue("clip from windows: ${node.clips}", node.clips.contains("hello-from-windows"))
            assertTrue("log", s.request(JSONObject().put("op", "log")).has("text"))

            // Cancel in the middle of a download: the stream stops and nothing is left behind.
            val big = File(dir, "big.bin").apply { writeBytes(ByteArray(100_000_000)) }
            val tc = Transfer("big", false)
            val dst = File(state, "big.bin")
            Thread { while (tc.done.get() == 0L) Thread.sleep(1); tc.cancel.cancel() }.start()
            val failed = runCatching { RemoteFs.download(s, big.path, dst, tc) }.isFailure
            assertTrue("cancel", failed && !dst.exists() && !File(dst.path + ".btpart").exists())

            RemoteFs.mkdir(s, RemoteFs.join(s, dir.path, "תיקייה"))
            RemoteFs.rename(s, RemoteFs.join(s, dir.path, "תיקייה"), RemoteFs.join(s, dir.path, "שם-חדש"))
            assertTrue(File(dir, "שם-חדש").isDirectory)
            RemoteFs.delete(s, listOf(RemoteFs.join(s, dir.path, "שם-חדש")))
            assertTrue(!File(dir, "שם-חדש").exists())

            // "Send to device" into the computer's downloads folder.
            val inboxed = RemoteFs.upload(s, { upSrc.inputStream() }, upSrc.size.toLong(), 0, "מהטלפון/צילום.jpg", Transfer("inbox", true), inbox = true)
            assertTrue(inboxed.startsWith(File(state, "inbox").path))
            assertTrue(File(inboxed).readBytes().contentEquals(upSrc))

            // Screen sharing from the (fake) computer screen, then input back to it.
            val sc = ScreenClient(s)
            val frames = java.util.concurrent.CountDownLatch(4)
            var tiles = 0
            sc.onFrame = { _, _, _, list ->
                for (t in list) {
                    val (tw, th) = JvmCodec.decodeSize(t.jpeg)
                    assertTrue("tile ${tw}x$th vs ${t.w}x${t.h}", tw == t.w && th == t.h)
                    tiles++
                }
                frames.countDown()
            }
            sc.open(0, "auto", false)
            assertEquals("windows", sc.kind)
            assertEquals(1, sc.monitors.size)
            assertTrue("frames", frames.await(20, TimeUnit.SECONDS))
            assertTrue("tiles", tiles > 0)
            // Frame is 1280 wide on Wi-Fi (sharp), so frame coordinates = real coordinates here.
            sc.input("click", 640.0, 360.0) { put("b", 0).put("n", 1) }
            sc.input("text", extra = { put("s", "שלום") })
            sc.input("key", extra = { put("k", "Enter") })
            Thread.sleep(800)
            sc.close()
            Thread.sleep(300)

            // Session still healthy after all that.
            assertTrue(RemoteFs.list(s, dir.path).items.isNotEmpty())
            s.close()
            Thread.sleep(300)

            // Second connection: trusted both ways, no password.
            val s2 = connect { _, _ -> throw AssertionError("password asked on a trusted connection") }
            assertTrue(RemoteFs.list(s2, dir.path).items.any { it.name == "from-kotlin.bin" })
            s2.close()
            Thread.sleep(300)

            // Only the computer forgot us: the password is asked once, then both trust each other again.
            win.write("FORGET\n"); win.flush()
            repeat(50) { if (winOut.none { it == "FORGOT" }) Thread.sleep(100) }
            var asked3 = 0
            connect { _, _ -> asked3++; PasswordAnswer(password, true) }.close()
            assertEquals(1, asked3)
            Thread.sleep(300)
            connect { _, _ -> throw AssertionError("password asked after trust was restored") }.close()

            // What the Windows side reported.
            assertTrue("input click: $winOut", winOut.any { it.startsWith("INPUT click 640 360") })
            assertTrue("input text", winOut.any { it.startsWith("INPUT text") && it.contains("שלום") })
            assertTrue("input key", winOut.any { it.startsWith("INPUT key") && it.contains("Enter") })
            assertTrue("received", winOut.any { it.startsWith("RECEIVED") && it.contains("צילום.jpg") })
            assertTrue("clip reached windows", winOut.any { it == "CLIP מהטלפון" })
        } finally {
            runCatching { p.outputStream.close() }
            p.waitFor(5, TimeUnit.SECONDS)
            p.destroy()
        }
    }
}

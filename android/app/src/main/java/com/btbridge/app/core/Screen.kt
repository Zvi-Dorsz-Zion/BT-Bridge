package com.btbridge.app.core

import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

class Frame(val w: Int, val h: Int, val px: IntArray)

/** Captures this device's screen. Android: MediaProjection. Tests: synthetic frames. */
interface ScreenGrabber {
    /** Real screen size, for mapping input coordinates. */
    val realW: Int
    val realH: Int
    /** The newest frame scaled to at most maxW wide, or null when none is ready. */
    fun grab(maxW: Int): Frame?
    fun release() {}
}

/** Performs remote input (PROTOCOL.md section 7). x/y (and x2/y2) are already in real screen pixels. */
fun interface InputSink {
    fun input(ev: JSONObject, x: Int, y: Int, x2: Int, y2: Int)
}

fun interface TileCodec {
    fun encode(px: IntArray, stride: Int, x: Int, y: Int, w: Int, h: Int, quality: Int): ByteArray
}

class ScreenProfile(val maxW: Int, val quality: Int, val fps: Int) {
    companion object {
        val SHARP = ScreenProfile(1920, 70, 15)
        val FAST = ScreenProfile(960, 35, 4)
        fun of(profile: String, wifi: Boolean) = when (profile) {
            "sharp" -> SHARP
            "fast" -> FAST
            else -> if (wifi) SHARP else FAST
        }
    }
}

class ScreenTile(val x: Int, val y: Int, val w: Int, val h: Int, val jpeg: ByteArray)

/** Frame message layout and change detection (PROTOCOL.md section 7). */
object ScreenProto {
    const val TILE = 128
    const val FIRST = 1
    const val LAST = 2

    fun build(seq: Long, w: Int, h: Int, tiles: List<ScreenTile>): ByteArray {
        val b = ByteBuffer.allocate(11 + tiles.sumOf { 12 + it.jpeg.size })
        b.put(1).putInt(seq.toInt()).putShort(w.toShort()).putShort(h.toShort()).putShort(tiles.size.toShort())
        for (t in tiles) {
            b.putShort(t.x.toShort()).putShort(t.y.toShort()).putShort(t.w.toShort()).putShort(t.h.toShort())
                .putInt(t.jpeg.size).put(t.jpeg)
        }
        return b.array()
    }

    class Parsed(val seq: Long, val w: Int, val h: Int, val tiles: List<ScreenTile>)

    fun parse(m: ByteArray): Parsed {
        val b = ByteBuffer.wrap(m)
        if (m.size < 11 || b.get().toInt() != 1) throw ProtocolException("פריים לא תקין")
        val seq = b.int.toLong() and 0xffffffffL
        val w = b.short.toInt() and 0xffff
        val h = b.short.toInt() and 0xffff
        val n = b.short.toInt() and 0xffff
        val tiles = ArrayList<ScreenTile>(n)
        repeat(n) {
            val x = b.short.toInt() and 0xffff
            val y = b.short.toInt() and 0xffff
            val tw = b.short.toInt() and 0xffff
            val th = b.short.toInt() and 0xffff
            val len = b.int
            if (len < 0 || len > b.remaining()) throw ProtocolException("פריים קטוע")
            val j = ByteArray(len)
            b.get(j)
            tiles += ScreenTile(x, y, tw, th, j)
        }
        return Parsed(seq, w, h, tiles)
    }

    /** Rectangles that changed since prev (whole frame when prev is null). Neighbouring changed tiles in a row are merged. */
    fun changed(cur: IntArray, prev: IntArray?, w: Int, h: Int): List<IntArray> {
        val out = mutableListOf<IntArray>()
        var ty = 0
        while (ty < h) {
            val th = minOf(TILE, h - ty)
            var runStart = -1
            var tx = 0
            while (true) {
                val inside = tx < w
                val changed = inside && (prev == null || differs(cur, prev, w, tx, ty, minOf(TILE, w - tx), th))
                if (runStart >= 0 && (!changed || tx - runStart >= 4 * TILE)) {
                    out += intArrayOf(runStart, ty, minOf(tx, w) - runStart, th)
                    runStart = -1
                }
                if (changed && runStart < 0) runStart = tx
                if (!inside) break
                tx += TILE
            }
            ty += TILE
        }
        return out
    }

    private fun differs(a: IntArray, b: IntArray, stride: Int, x: Int, y: Int, w: Int, h: Int): Boolean {
        for (r in 0 until h) {
            val at = (y + r) * stride + x
            for (i in at until at + w) if (a[i] != b[i]) return true
        }
        return false
    }

    fun fit(w: Int, h: Int, maxW: Int): Pair<Int, Int> {
        if (w <= maxW) return (w and 1.inv()) to (h and 1.inv())
        val k = maxW.toDouble() / w
        return ((w * k).toInt() and 1.inv()) to ((h * k).toInt() and 1.inv())
    }
}

/** The sharing side of one screen stream: captures, sends changed tiles, applies remote input. */
class ScreenSource(
    val session: Session,
    val id: Int,
    private val grab: ScreenGrabber,
    private val inputSink: InputSink,
    private val codec: TileCodec,
    req: JSONObject,
) {
    @Volatile private var profile = req.str("profile") ?: "auto"
    @Volatile private var needKey = true
    private val acked = AtomicLong(0)
    private val ackLock = Object()
    private val stopped = AtomicBoolean(false)

    fun start() {
        active[key(session, id)] = this
        session.expectCancel(id) { stop() }
        listeners.forEach { it(this, true) }
        Thread(::loop, "screen-source").apply { isDaemon = true }.start()
    }

    fun stop() {
        if (!stopped.compareAndSet(false, true)) return
        synchronized(ackLock) { ackLock.notifyAll() }
        active.remove(key(session, id))
        session.forgetCancel(id)
        listeners.forEach { it(this, false) }
    }

    private fun applyInput(ev: JSONObject) {
        val fw = maxOf(1, ev.optInt("fw", grab.realW))
        val fh = maxOf(1, ev.optInt("fh", grab.realH))
        fun mx(v: Double) = (v * grab.realW / fw).toInt()
        fun my(v: Double) = (v * grab.realH / fh).toInt()
        try {
            inputSink.input(ev, mx(ev.optDouble("x", 0.0)), my(ev.optDouble("y", 0.0)),
                mx(ev.optDouble("x2", 0.0)), my(ev.optDouble("y2", 0.0)))
        } catch (e: Exception) {
            Log.w("input: ${e.message}")
        }
    }

    private fun loop() {
        var seq = 0L
        var prev: IntArray? = null
        var pw = 0
        var ph = 0
        var lastSent = 0L
        try {
            while (!stopped.get() && !session.closed) {
                val start = System.currentTimeMillis()
                val link = session.dataLink
                val prof = ScreenProfile.of(profile, link.isWifi)
                val f = grab.grab(prof.maxW)
                if (f == null) { Thread.sleep(100); continue }
                val full = needKey || prev == null || f.w != pw || f.h != ph
                needKey = false
                val rects = ScreenProto.changed(f.px, if (full) null else prev, f.w, f.h)
                // An empty frame every few seconds keeps the stream (and its watchdog) alive.
                if (rects.isNotEmpty() || start - lastSent > 5000) {
                    val tiles = rects.map { r -> ScreenTile(r[0], r[1], r[2], r[3], codec.encode(f.px, f.w, r[0], r[1], r[2], r[3], prof.quality)) }
                    sendFrame(link, ScreenProto.build(++seq, f.w, f.h, tiles))
                    lastSent = start
                    prev = f.px; pw = f.w; ph = f.h
                    // At most 2 frames in flight.
                    synchronized(ackLock) {
                        val until = System.currentTimeMillis() + 3000
                        while (!stopped.get() && seq - acked.get() > 2 && System.currentTimeMillis() < until) ackLock.wait(200)
                    }
                }
                val wait = 1000 / prof.fps - (System.currentTimeMillis() - start)
                if (wait > 0) Thread.sleep(wait)
            }
        } catch (e: Exception) {
            if (!stopped.get()) Log.w("screen source: ${e.message}")
        }
        stop()
        runCatching { session.dataLink.sendData(Rec.json(Rec.END, id, JSONObject().put("ok", true))) }
    }

    private fun sendFrame(link: Link, msg: ByteArray) {
        val chunk = link.chunkSize - 1
        var at = 0
        while (at < msg.size) {
            val n = minOf(chunk, msg.size - at)
            val body = ByteArray(n + 1)
            body[0] = ((if (at == 0) ScreenProto.FIRST else 0) or (if (at + n >= msg.size) ScreenProto.LAST else 0)).toByte()
            System.arraycopy(msg, at, body, 1, n)
            link.sendData(Rec.make(Rec.DATA, id, body))
            at += n
        }
        session.touchData()
    }

    companion object {
        private val active = ConcurrentHashMap<String, ScreenSource>()
        val listeners = java.util.concurrent.CopyOnWriteArrayList<(ScreenSource, Boolean) -> Unit>()
        private fun key(s: Session, id: Int) = "${System.identityHashCode(s)}:$id"
        val count: Int get() = active.size

        fun stopAll(s: Session? = null) = active.values.filter { s == null || it.session === s }.forEach { it.stop() }

        /** Routes screen-ack / screen-cfg / input. Returns false when the event is not for a screen stream. */
        fun onEvent(s: Session, ev: JSONObject): Boolean {
            val name = ev.str("ev")
            if (name != "screen-ack" && name != "screen-cfg" && name != "input") return false
            val src = active[key(s, ev.optInt("id"))] ?: return true
            when (name) {
                "screen-ack" -> {
                    src.acked.set(ev.optLong("seq"))
                    synchronized(src.ackLock) { src.ackLock.notifyAll() }
                }
                "screen-cfg" -> {
                    ev.str("profile")?.let { src.profile = it }
                    src.needKey = true
                }
                "input" -> src.applyInput(ev)
            }
            return true
        }
    }
}

/** The viewing side of one screen stream. Frames arrive on the link's reader thread. */
class ScreenClient(private val s: Session) : StreamSink {
    private val buf = ByteArrayOutputStream()
    private var inFrame = false
    var id = 0
        private set
    @Volatile var frameW = 0
        private set
    @Volatile var frameH = 0
        private set
    var kind = ""
        private set
    var monitors: List<JSONObject> = emptyList()
        private set

    var onFrame: ((seq: Long, w: Int, h: Int, tiles: List<ScreenTile>) -> Unit)? = null
    var onClosed: ((String) -> Unit)? = null

    /** Blocking. */
    fun open(monitor: Int, profile: String, restoreCursor: Boolean) {
        id = s.newId()
        s.expectStream(id, this, sticky = false)
        try {
            val res = s.request(JSONObject().put("op", "screen-open").put("monitor", monitor)
                .put("profile", profile).put("restoreCursor", restoreCursor), id = id, timeoutSec = 100)
            kind = res.str("kind") ?: ""
            val arr = res.optJSONArray("monitors") ?: JSONArray()
            monitors = (0 until arr.length()).map { arr.getJSONObject(it) }
        } catch (e: Exception) {
            s.forgetStream(id)
            throw e
        }
    }

    fun configure(profile: String? = null, monitor: Int? = null, key: Boolean = false) {
        val o = JSONObject().put("ev", "screen-cfg").put("id", id).put("key", key)
        profile?.let { o.put("profile", it) }
        monitor?.let { o.put("monitor", it) }
        runCatching { s.sendEvent(o) }
    }

    /** Sends an input event; x/y are in frame pixels. */
    fun input(type: String, x: Double = 0.0, y: Double = 0.0, extra: (JSONObject.() -> Unit)? = null) {
        val o = JSONObject().put("ev", "input").put("id", id).put("t", type).put("x", x).put("y", y)
            .put("fw", frameW).put("fh", frameH)
        extra?.invoke(o)
        runCatching { s.sendEvent(o) }
    }

    fun close() = s.cancel(id)

    override fun onData(buf: ByteArray, off: Int, len: Int) {
        if (len == 0) return
        val flags = buf[off].toInt()
        if (flags and ScreenProto.FIRST != 0) { this.buf.reset(); inFrame = true }
        if (!inFrame) return
        this.buf.write(buf, off + 1, len - 1)
        if (flags and ScreenProto.LAST == 0) return
        inFrame = false
        val f = ScreenProto.parse(this.buf.toByteArray())
        frameW = f.w
        frameH = f.h
        try { onFrame?.invoke(f.seq, f.w, f.h, f.tiles) } catch (e: Exception) { Log.w("frame: ${e.message}") }
        runCatching { s.sendEvent(JSONObject().put("ev", "screen-ack").put("id", id).put("seq", f.seq)) }
    }

    override fun onEnd(end: JSONObject) { onClosed?.invoke("השיתוף הסתיים") }
    override fun onFail(why: String) { onClosed?.invoke(why) }
}

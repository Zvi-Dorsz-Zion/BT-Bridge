package com.btbridge.app.core

import org.json.JSONObject
import java.io.FileNotFoundException
import java.io.IOException
import java.io.InputStream
import java.util.concurrent.CancellationException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ExecutionException
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import java.util.concurrent.atomic.AtomicLong

interface RequestHandler {
    /** Returns the response, or null when the handler replied itself (streams). Runs on a worker thread. */
    fun handle(s: Session, id: Int, req: JSONObject): JSONObject?
    fun onEvent(s: Session, ev: JSONObject)
}

/** Consumer of an incoming DATA…END stream. */
interface StreamSink {
    fun onData(buf: ByteArray, off: Int, len: Int)
    fun onEnd(end: JSONObject)
    fun onFail(why: String)
}

/** One authenticated connection to another device, over one or two links. */
class Session(hr: HandshakeResult, val peerAddress: String, val initiator: Boolean, private val handler: RequestHandler) {
    val peerName = hr.peerName
    val peerOs = hr.peerOs
    val peerFp = hr.peerFp
    val linkKey = hr.linkKey
    /** The other side's program version ("0.3.0") and optional features. */
    val peerApp = hr.peerApp
    val peerCaps = hr.peerCaps
    fun has(cap: String) = cap in peerCaps
    @Volatile var closed = false
        private set

    /** Wi-Fi addresses the other side announced (PROTOCOL.md section 5). */
    @Volatile var peerIps: List<String> = emptyList()
    @Volatile var peerPort = 0

    /** Wi-Fi link ids already accepted on this session: a recorded hello can't be replayed (PROTOCOL.md section 6). */
    val usedLinkIds: MutableSet<String> = java.util.Collections.synchronizedSet(HashSet())

    var onChanged: ((Session) -> Unit)? = null
    var onClosed: ((Session) -> Unit)? = null
    /** The Wi-Fi link dropped while Bluetooth is still up. */
    var onWifiLost: ((Session) -> Unit)? = null

    private val gate = Any()
    private var bt: Link? = null
    private var wifi: Link? = null
    private val nextId = AtomicLong(if (initiator) -1 else 0)
    private val pending = ConcurrentHashMap<Int, CompletableFuture<JSONObject>>()
    private val done = ConcurrentHashMap<Int, CompletableFuture<JSONObject>>()
    private val sinks = ConcurrentHashMap<Int, SinkEntry>()
    private val outgoing = ConcurrentHashMap<Int, CancelToken>()

    private val cancelHandlers = ConcurrentHashMap<Int, () -> Unit>()

    /** Last time a file or screen stream moved data (for closing an idle direct network). */
    @Volatile var lastDataActivity = System.currentTimeMillis()
        private set
    val hasActiveStreams: Boolean get() = sinks.isNotEmpty() || outgoing.isNotEmpty() || cancelHandlers.isNotEmpty()

    /** Sticky streams (files) fail when their link drops. Screen streams survive a link change. */
    private class SinkEntry(val sink: StreamSink, val sticky: Boolean) {
        @Volatile var link: Link? = null
        @Volatile var lastActivity = System.currentTimeMillis()
    }

    private val watchdog = timer.scheduleAtFixedRate({
        val now = System.currentTimeMillis()
        for ((id, e) in sinks) if (now - e.lastActivity > 90_000 && sinks.remove(id) != null) e.sink.onFail("ההעברה נתקעה")
    }, 15, 15, TimeUnit.SECONDS)

    val hasWifi: Boolean get() = synchronized(gate) { wifi?.alive == true }
    val linkText: String get() = if (hasWifi) "וויפי + בלוטוס" else "בלוטוס"

    private fun control(): Link = synchronized(gate) { bt ?: wifi } ?: throw IOException("החיבור נסגר")

    val dataLink: Link
        get() = synchronized(gate) { wifi?.takeIf { it.alive } ?: bt } ?: throw IOException("החיבור נסגר")

    fun attach(link: Link) {
        val old = synchronized(gate) {
            if (link.isWifi) wifi.also { wifi = link } else bt.also { bt = link }
        }
        link.onRecord = ::dispatch
        link.onClosed = ::linkClosed
        link.start()
        old?.close("הוחלף")
        Log.w("session $peerName: ${link.kind} link up")
        onChanged?.invoke(this)
    }

    private fun linkClosed(link: Link) {
        var wifiLost = false
        val dead = synchronized(gate) {
            if (bt === link) bt = null
            if (wifi === link) { wifi = null; wifiLost = true }
            bt == null && wifi == null
        }
        for ((id, e) in sinks) if (dead || (e.sticky && e.link === link)) sinks.remove(id)?.sink?.onFail("החיבור נותק")
        if (dead) close()
        else {
            onChanged?.invoke(this)
            if (wifiLost) onWifiLost?.invoke(this)
        }
    }

    fun close() {
        val links = synchronized(gate) {
            if (closed) return
            closed = true
            listOfNotNull(bt, wifi)
        }
        watchdog.cancel(false)
        links.forEach { it.close() }
        pending.values.forEach { it.completeExceptionally(IOException("החיבור נסגר")) }
        done.values.forEach { it.completeExceptionally(IOException("החיבור נסגר")) }
        outgoing.values.forEach { it.cancel() }
        sinks.values.forEach { it.sink.onFail("החיבור נסגר") }
        sinks.clear()
        Log.w("session $peerName closed")
        onClosed?.invoke(this)
    }

    // ------------------------------------------------------------------ outgoing

    fun newId(): Int = nextId.addAndGet(2).toInt()

    fun request(req: JSONObject, id: Int? = null, timeoutSec: Long = 60, cancel: CancelToken? = null): JSONObject {
        val rid = id ?: newId()
        val f = CompletableFuture<JSONObject>()
        pending[rid] = f
        try {
            control().send(Rec.json(Rec.REQ, rid, req))
            cancel?.onCancel { f.completeExceptionally(CancellationException("בוטל")) }
            val res = try { f.get(timeoutSec, TimeUnit.SECONDS) }
                catch (e: TimeoutException) { throw IOException("אין תשובה מהמכשיר השני") }
                catch (e: ExecutionException) { throw e.cause ?: e }
            throwIfError(res)
            return res
        } finally {
            pending.remove(rid)
        }
    }

    fun reply(id: Int, o: JSONObject) = control().send(Rec.json(Rec.RES, id, o))
    fun sendEvent(o: JSONObject) = control().send(Rec.json(Rec.EVT, 0, o))
    fun sendDone(id: Int, o: JSONObject) = control().send(Rec.json(Rec.DONE, id, o))

    fun cancel(id: Int) {
        outgoing[id]?.cancel()
        sinks.remove(id)?.sink?.onFail("בוטל")
        cancelHandlers.remove(id)?.invoke()
        runCatching { control().send(Rec.make(Rec.CANCEL, id)) }
    }

    fun forgetStream(id: Int) { sinks.remove(id) }

    fun expectStream(id: Int, sink: StreamSink, sticky: Boolean = true) { sinks[id] = SinkEntry(sink, sticky) }

    /** Runs when the other side cancels stream id (long-running sources such as screen sharing). */
    fun expectCancel(id: Int, onCancel: () -> Unit) { cancelHandlers[id] = onCancel }
    fun forgetCancel(id: Int) { cancelHandlers.remove(id) }
    fun touchData() { lastDataActivity = System.currentTimeMillis() }

    fun expectDone(id: Int): CompletableFuture<JSONObject> = CompletableFuture<JSONObject>().also { done[id] = it }

    fun forgetDone(id: Int) { done.remove(id) }

    /** Sends src as DATA records on the data link, then END on the same link. Blocking. */
    fun streamOut(id: Int, src: InputStream, progress: ((Int) -> Unit)?, cancel: CancelToken?) {
        val link = dataLink
        val token = CancelToken()
        cancel?.onCancel { token.cancel() }
        outgoing[id] = token
        try {
            val buf = ByteArray(link.chunkSize)
            while (true) {
                token.throwIfCancelled()
                val n = src.read(buf)
                if (n < 0) break
                if (n == 0) continue
                link.sendData(Rec.make(Rec.DATA, id, buf, 0, n), token)
                progress?.invoke(n)
                touchData()
            }
            link.sendData(Rec.json(Rec.END, id, JSONObject().put("ok", true)))
        } catch (e: CancellationException) {
            runCatching { link.sendData(Rec.json(Rec.END, id, JSONObject().put("err", "cancelled"))) }
            throw e
        } catch (e: Exception) {
            // A local read error: tell the other side, so it doesn't wait for the rest.
            if (link.alive) runCatching { link.sendData(Rec.json(Rec.END, id, JSONObject().put("err", Heb.of(e)))) }
            throw e
        } finally {
            outgoing.remove(id)
        }
    }

    // ------------------------------------------------------------------ incoming

    private fun dispatch(link: Link, p: ByteArray) {
        val id = Rec.id(p)
        when (Rec.type(p)) {
            Rec.REQ -> {
                val req = Rec.parseJson(p)
                workers.execute { handleRequest(id, req) }
            }
            Rec.RES -> pending.remove(id)?.complete(Rec.parseJson(p))
            Rec.DONE -> done.remove(id)?.complete(Rec.parseJson(p))
            Rec.DATA -> sinks[id]?.also { touchData() }?.let { e ->
                if (e.link == null) e.link = link
                e.lastActivity = System.currentTimeMillis()
                try { e.sink.onData(p, 5, p.size - 5) }
                catch (ex: Exception) {
                    sinks.remove(id)
                    e.sink.onFail(Heb.of(ex))
                    runCatching { control().send(Rec.make(Rec.CANCEL, id)) }
                }
            }
            Rec.END -> sinks.remove(id)?.let { e ->
                val o = Rec.parseJson(p)
                val err = o.str("err")
                if (err != null) e.sink.onFail(if (err == "cancelled") "בוטל" else err) else e.sink.onEnd(o)
            }
            Rec.CANCEL -> {
                outgoing[id]?.cancel()
                sinks.remove(id)?.sink?.onFail("בוטל בצד השני")
                cancelHandlers.remove(id)?.invoke()
            }
            Rec.EVT -> try { handler.onEvent(this, Rec.parseJson(p)) } catch (e: Exception) { Log.w("event: ${e.message}") }
        }
    }

    private fun handleRequest(id: Int, req: JSONObject) {
        val res: JSONObject? = try {
            handler.handle(this, id, req)
        } catch (e: RemoteException) {
            JSONObject().put("err", e.code).put("msg", Heb.of(e))
        } catch (e: FileNotFoundException) {
            JSONObject().put("err", "notfound").put("msg", Heb.of(e))
        } catch (e: SecurityException) {
            JSONObject().put("err", "denied").put("msg", Heb.of(e))
        } catch (e: CancellationException) {
            null
        } catch (e: Exception) {
            JSONObject().put("err", "io").put("msg", Heb.of(e))
        }
        if (res != null) runCatching { reply(id, res) }
    }

    companion object {
        private val workers = Executors.newCachedThreadPool { r -> Thread(r, "bridge-worker").apply { isDaemon = true } }
        private val timer = Executors.newSingleThreadScheduledExecutor { r -> Thread(r, "session-timer").apply { isDaemon = true } }

        fun throwIfError(o: JSONObject) {
            val err = o.str("err") ?: return
            throw RemoteException(err, o.str("msg") ?: err)
        }
    }
}

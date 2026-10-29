package com.btbridge.app.core

import java.io.Closeable
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.util.concurrent.CancellationException
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** Cancellation flag with callbacks (plain threads, no coroutines, so the core runs in JVM tests too). */
class CancelToken {
    @Volatile var isCancelled = false
        private set
    private val listeners = CopyOnWriteArrayList<() -> Unit>()

    fun cancel() {
        if (isCancelled) return
        isCancelled = true
        listeners.forEach { runCatching(it) }
    }

    fun onCancel(f: () -> Unit) {
        listeners += f
        if (isCancelled) runCatching(f)
    }

    fun throwIfCancelled() { if (isCancelled) throw CancellationException("בוטל") }
}

/**
 * One encrypted transport (Bluetooth or Wi-Fi). Control records always go before data records,
 * so a big transfer never blocks requests.
 */
class Link(
    val isWifi: Boolean,
    private val input: InputStream,
    private val output: OutputStream,
    private val owner: Closeable,
    private val codec: RecordCodec,
) {
    val kind: String get() = if (isWifi) "וויפי" else "בלוטוס"
    val chunkSize: Int get() = if (isWifi) 256 * 1024 else 16 * 1024
    val alive: Boolean get() = !closed.get()

    var onRecord: ((Link, ByteArray) -> Unit)? = null
    var onClosed: ((Link) -> Unit)? = null

    private val lock = Object()
    private val high = ArrayDeque<ByteArray>()
    private val low = ArrayDeque<ByteArray>()
    private val closed = AtomicBoolean(false)
    @Volatile private var lastRx = System.currentTimeMillis()

    fun start() {
        Thread(::readLoop, "$kind-read").apply { isDaemon = true }.start()
        Thread(::writeLoop, "$kind-write").apply { isDaemon = true }.start()
        pinger.scheduleAtFixedRate({
            if (!alive) return@scheduleAtFixedRate
            if (System.currentTimeMillis() - lastRx > 45_000) close("אין תגובה")
            else runCatching { send(Rec.make(Rec.PING, 0)) }
        }, 15, 15, TimeUnit.SECONDS).also { pingTask = it }
    }

    private var pingTask: java.util.concurrent.ScheduledFuture<*>? = null

    /** Control record. Never blocks. */
    fun send(plain: ByteArray) = synchronized(lock) {
        if (closed.get()) throw IOException("החיבור נסגר")
        high.addLast(plain)
        lock.notifyAll()
    }

    /** Data record. Blocks while the queue is full (back-pressure). */
    fun sendData(plain: ByteArray, cancel: CancelToken? = null) = synchronized(lock) {
        while (low.size >= 8 && !closed.get() && cancel?.isCancelled != true) lock.wait(200)
        if (closed.get()) throw IOException("החיבור נסגר")
        cancel?.throwIfCancelled()
        low.addLast(plain)
        lock.notifyAll()
    }

    private fun writeLoop() {
        try {
            while (true) {
                val item = synchronized(lock) {
                    while (high.isEmpty() && low.isEmpty() && !closed.get()) lock.wait()
                    if (closed.get()) return
                    (high.removeFirstOrNull() ?: low.removeFirst()).also { lock.notifyAll() }
                }
                Wire.write(output, codec.seal(item))
            }
        } catch (e: Exception) {
            close(e.message)
        }
    }

    private fun readLoop() {
        try {
            while (true) {
                val p = Rec.check(codec.open(Wire.read(input)))
                lastRx = System.currentTimeMillis()
                when (Rec.type(p)) {
                    Rec.PING -> send(Rec.make(Rec.PONG, 0))
                    Rec.PONG -> {}
                    else -> onRecord?.invoke(this, p)
                }
            }
        } catch (e: Exception) {
            close(e.message)
        }
    }

    fun close(why: String? = null) {
        if (!closed.compareAndSet(false, true)) return
        if (why != null) Log.w("$kind link closed: $why")
        synchronized(lock) { lock.notifyAll() }
        pingTask?.cancel(false)
        runCatching { input.close() }
        runCatching { output.close() }
        runCatching { owner.close() }
        onClosed?.invoke(this)
    }

    companion object {
        private val pinger = Executors.newSingleThreadScheduledExecutor { r -> Thread(r, "pinger").apply { isDaemon = true } }
    }
}

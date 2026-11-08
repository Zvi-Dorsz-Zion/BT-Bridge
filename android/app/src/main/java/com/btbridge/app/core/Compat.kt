package com.btbridge.app.core

import java.util.concurrent.CancellationException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException

/**
 * Standard Base64 with padding, the same as java.util.Base64's basic encoder and decoder.
 * Written out because java.util.Base64 exists on Android only from 8.0, and the core must stay plain JVM code
 * (the unit tests run it), so android.util.Base64 can't be used here.
 */
object B64 {
    private const val ABC = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
    private val REV = IntArray(128) { -1 }.also { r -> ABC.forEachIndexed { i, c -> r[c.code] = i } }

    fun encode(b: ByteArray): String {
        val out = StringBuilder((b.size + 2) / 3 * 4)
        var i = 0
        while (i + 2 < b.size) {
            val v = ((b[i].toInt() and 0xff) shl 16) or ((b[i + 1].toInt() and 0xff) shl 8) or (b[i + 2].toInt() and 0xff)
            out.append(ABC[v ushr 18]).append(ABC[(v ushr 12) and 63]).append(ABC[(v ushr 6) and 63]).append(ABC[v and 63])
            i += 3
        }
        when (b.size - i) {
            1 -> {
                val v = (b[i].toInt() and 0xff) shl 16
                out.append(ABC[v ushr 18]).append(ABC[(v ushr 12) and 63]).append("==")
            }
            2 -> {
                val v = ((b[i].toInt() and 0xff) shl 16) or ((b[i + 1].toInt() and 0xff) shl 8)
                out.append(ABC[v ushr 18]).append(ABC[(v ushr 12) and 63]).append(ABC[(v ushr 6) and 63]).append('=')
            }
        }
        return out.toString()
    }

    /** Like java.util.Base64.getDecoder(): padding is optional, anything outside the alphabet is an error. */
    fun decode(s: String): ByteArray {
        val out = java.io.ByteArrayOutputStream(s.length * 3 / 4)
        var acc = 0
        var bits = 0
        var n = 0
        var padded = false
        for (c in s) {
            if (c == '=') { padded = true; break }
            val v = if (c.code < 128) REV[c.code] else -1
            if (v < 0) throw IllegalArgumentException("תו לא חוקי בבסיס 64")
            acc = (acc shl 6) or v
            bits += 6
            n++
            if (bits >= 8) {
                bits -= 8
                out.write((acc ushr bits) and 0xff)
            }
        }
        if (n % 4 == 1) throw IllegalArgumentException("אורך לא חוקי בבסיס 64")
        if (padded) {
            // Only the right amount of '=' may follow, and nothing after it.
            val pad = s.length - n
            if (s.substring(n).any { it != '=' } || (n + pad) % 4 != 0 || pad > 2) throw IllegalArgumentException("ריפוד לא חוקי בבסיס 64")
        }
        return out.toByteArray()
    }
}

/**
 * A result that arrives later from another thread: the few parts of CompletableFuture that the app uses
 * (CompletableFuture exists on Android only from 7.0). get() behaves the same: a CancellationException is thrown
 * as it is, any other failure arrives wrapped in an ExecutionException, and a timeout throws TimeoutException.
 */
class Promise<T> {
    private val latch = CountDownLatch(1)
    private val lock = Any()
    @Volatile private var finished = false
    private var value: T? = null
    private var error: Throwable? = null

    val isDone: Boolean get() = finished

    fun complete(v: T): Boolean = finish(v, null)
    fun completeExceptionally(e: Throwable): Boolean = finish(null, e)

    private fun finish(v: T?, e: Throwable?): Boolean {
        synchronized(lock) {
            if (finished) return false
            value = v
            error = e
            finished = true
        }
        latch.countDown()
        return true
    }

    fun get(): T {
        latch.await()
        return result()
    }

    fun get(timeout: Long, unit: TimeUnit): T {
        if (!latch.await(timeout, unit)) throw TimeoutException()
        return result()
    }

    @Suppress("UNCHECKED_CAST")
    private fun result(): T {
        val e = synchronized(lock) { error }
        if (e != null) {
            if (e is CancellationException) throw e
            throw ExecutionException(e)
        }
        return synchronized(lock) { value } as T
    }
}

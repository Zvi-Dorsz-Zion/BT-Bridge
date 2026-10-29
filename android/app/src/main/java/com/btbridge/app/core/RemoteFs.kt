package com.btbridge.app.core

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.io.InputStream
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

data class RemoteItem(val name: String, val path: String, val isDir: Boolean, val size: Long, val mtime: Long)

class RemoteListing(val items: List<RemoteItem>, val parent: String?)

/**
 * One file transfer, shown in the transfers list. Progress fields are read by the UI.
 * [work] runs it on a session; a retry after a dropped connection runs it again on the device's new session,
 * and resumes from the partial file.
 */
class Transfer(val name: String, val upload: Boolean, val peerFp: String = "", val work: ((Session, Transfer) -> Unit)? = null) {
    @Volatile var cancel = CancelToken()
        private set
    val done = AtomicLong()
    /** How many times this transfer started. A retry (more than 0) resumes from the partial file. */
    @Volatile var attempts = 0
    /** The connection dropped in the middle: it continues by itself when the device connects again. */
    @Volatile var interrupted = false
    val canResume: Boolean get() = !active && work != null && (interrupted || status.startsWith("שגיאה"))

    /** Runs once, when the transfer finally finished (true = succeeded). */
    @Volatile var after: ((Boolean) -> Unit)? = null

    /** Ready for another try. */
    fun restart() {
        cancel = CancelToken()
        interrupted = false
        active = true
        status = "ממתין"
    }

    /** Progress restarts here (0, or the part that arrived before an interruption). */
    fun setDone(v: Long) { done.set(v); lastDone = v }
    @Volatile var total = 0L
    @Volatile var status = "ממתין"
    @Volatile var linkKind = ""
    @Volatile var active = true
    @Volatile var resultPath: String? = null
    @Volatile var speed = 0L
    private var lastDone = 0L
    private var lastTime = System.currentTimeMillis()

    fun add(n: Int) { done.addAndGet(n.toLong()) }

    val percent: Float get() = if (total > 0) (done.get().toFloat() / total).coerceIn(0f, 1f) else 0f

    /** Called about once a second to update [speed] (bytes/second). */
    fun tick() {
        val now = System.currentTimeMillis()
        val dt = now - lastTime
        if (dt < 900) return
        val d = done.get()
        speed = ((d - lastDone) * 1000 / dt)
        lastDone = d
        lastTime = now
    }

    companion object {
        fun formatSize(b: Long): String = when {
            b < 1024 -> "$b בתים"
            b < 1024 * 1024 -> "%.1f ק״ב".format(b / 1024.0)
            b < 1024L * 1024 * 1024 -> "%.1f מ״ב".format(b / 1024.0 / 1024)
            else -> "%.2f ג״ב".format(b / 1024.0 / 1024 / 1024)
        }
    }
}

/** Client side of the file operations (PROTOCOL.md section 4). All calls block. */
object RemoteFs {
    fun list(s: Session, path: String): RemoteListing {
        val items = mutableListOf<RemoteItem>()
        var parent: String?
        while (true) {
            val res = s.request(JSONObject().put("op", "list").put("path", path).put("offset", items.size))
            parent = res.str("parent")
            val arr = res.getJSONArray("items")
            for (i in 0 until arr.length()) {
                val o = arr.getJSONObject(i)
                items += RemoteItem(o.getString("n"), o.getString("p"), o.getBoolean("d"), o.long("s"), o.long("m"))
            }
            if (res.bool("more") != true) break
        }
        return RemoteListing(items, parent)
    }

    fun mkdir(s: Session, path: String) { s.request(JSONObject().put("op", "mkdir").put("path", path)) }

    fun rename(s: Session, from: String, to: String) {
        s.request(JSONObject().put("op", "rename").put("from", from).put("to", to))
    }

    fun delete(s: Session, paths: List<String>) {
        s.request(JSONObject().put("op", "delete").put("paths", JSONArray(paths)), timeoutSec = 300)
    }

    fun join(s: Session, dir: String, name: String): String {
        val sep = if (s.peerOs == "windows") '\\' else '/'
        return dir.trimEnd(sep) + sep + name
    }

    private class FileChanged : IOException("הקובץ השתנה")

    /**
     * Brings a remote file here. When [t] was interrupted before (a dropped connection), the partial file is kept
     * and only the rest is asked for (PROTOCOL.md section 4, "read" with "offset").
     */
    fun download(s: Session, remotePath: String, local: File, t: Transfer, expectSize: Long = -1, expectMtime: Long = -1) {
        local.parentFile?.mkdirs()
        val part = File(local.path + ".btpart")
        var offset = if (t.attempts > 0 && expectSize > 0 && part.isFile) part.length() else 0L
        if (offset >= expectSize) offset = 0
        t.attempts++
        t.setDone(offset)
        val out = FileOutputStream(part, offset > 0)
        val end = CompletableFuture<Unit>()
        val id = s.newId()
        s.expectStream(id, FileSink(out, t::add, { end.complete(Unit) }, { end.completeExceptionally(IOException(it)) }))
        t.cancel.onCancel { s.cancel(id) }
        try {
            t.linkKind = s.dataLink.kind
            val res = s.request(JSONObject().put("op", "read").put("path", remotePath).put("offset", offset), id = id, cancel = t.cancel)
            t.total = res.long("size")
            val m = res.long("m")
            if (offset > 0 && (t.total != expectSize || (expectMtime > 0 && m != expectMtime))) throw FileChanged()
            try { end.get() } catch (e: ExecutionException) { throw e.cause ?: e }
            local.delete()
            if (!part.renameTo(local)) throw IOException("שמירת הקובץ נכשלה")
            if (m > 0) local.setLastModified(m)
            t.resultPath = local.path
            return
        } catch (e: Exception) {
            s.cancel(id)
            runCatching { end.get(10, TimeUnit.SECONDS) }
            // Keep what arrived for resuming, unless the user cancelled, the file changed, or nothing came.
            if (e is FileChanged || t.cancel.isCancelled || part.length() == 0L) part.delete()
            if (e !is FileChanged) throw e
        }
        // The file changed since the first try: start over.
        t.attempts = 0
        download(s, remotePath, local, t)
    }

    /**
     * Returns the path the file got on the other side (renamed if the name was taken).
     * A retry of an interrupted upload asks the other side to keep its partial file and sends only the rest.
     */
    fun upload(s: Session, open: () -> InputStream, size: Long, mtime: Long, remotePath: String, t: Transfer, inbox: Boolean = false): String {
        t.total = size
        val resume = t.attempts > 0 && s.has("resume")
        t.attempts++
        t.setDone(0)
        val id = s.newId()
        val done = s.expectDone(id)
        try {
            val res = s.request(JSONObject().put("op", "write").put("path", remotePath).put("size", size)
                .put("mtime", mtime).put("overwrite", false).put("inbox", inbox).put("resume", resume), id = id, cancel = t.cancel)
            val have = res.long("have")
            if (have < 0 || have > size) {
                // Start the next try from scratch.
                s.cancel(id)
                t.attempts = 0
                throw IOException("החלק שכבר הגיע לא מתאים לקובץ. נסה שוב.")
            }
            t.linkKind = s.dataLink.kind
            open().use { input ->
                if (have > 0) { skipFully(input, have); t.setDone(have) }
                s.streamOut(id, input, t::add, t.cancel)
            }
            val r = try { done.get(5, TimeUnit.MINUTES) } catch (e: ExecutionException) { throw e.cause ?: e }
            Session.throwIfError(r)
            return (r.str("path") ?: remotePath).also { t.resultPath = it }
        } finally {
            s.forgetDone(id)
        }
    }

    private fun skipFully(input: InputStream, n: Long) {
        var left = n
        val buf = ByteArray(64 * 1024)
        while (left > 0) {
            val k = input.skip(left)
            if (k > 0) { left -= k; continue }
            val r = input.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
            if (r < 0) throw IOException("הקובץ קצר ממה שציפינו")
            left -= r
        }
    }
}

/**
 * Files already brought here for viewing. Copying or downloading the same file again (same size and date)
 * takes the local copy instead of sending it over the air a second time.
 */
object ViewCache {
    private val map = java.util.concurrent.ConcurrentHashMap<String, File>()
    private fun key(fp: String, i: RemoteItem) = "$fp|${i.path}|${i.size}|${i.mtime}"

    fun add(fp: String, i: RemoteItem, local: File) { map[key(fp, i)] = local }

    /** The local copy, or null when there is none (or it was cleaned up or changed). */
    fun find(fp: String, i: RemoteItem): File? {
        val f = map[key(fp, i)] ?: return null
        if (f.isFile && f.length() == i.size) return f
        map.remove(key(fp, i))
        return null
    }
}

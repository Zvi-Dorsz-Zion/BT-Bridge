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

/** One file transfer, shown in the transfers list. Progress fields are read by the UI. */
class Transfer(val name: String, val upload: Boolean) {
    val cancel = CancelToken()
    val done = AtomicLong()
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

    fun download(s: Session, remotePath: String, local: File, t: Transfer) {
        local.parentFile?.mkdirs()
        val part = File(local.path + ".btpart")
        val out = FileOutputStream(part)
        val end = CompletableFuture<Unit>()
        val id = s.newId()
        s.expectStream(id, FileSink(out, t::add, { end.complete(Unit) }, { end.completeExceptionally(IOException(it)) }))
        t.cancel.onCancel { s.cancel(id) }
        try {
            t.linkKind = s.dataLink.kind
            val res = s.request(JSONObject().put("op", "read").put("path", remotePath).put("offset", 0), id = id, cancel = t.cancel)
            t.total = res.long("size")
            try { end.get() } catch (e: ExecutionException) { throw e.cause ?: e }
            out.close()
            local.delete()
            if (!part.renameTo(local)) throw IOException("שמירת הקובץ נכשלה")
            val m = res.long("m")
            if (m > 0) local.setLastModified(m)
            t.resultPath = local.path
        } catch (e: Exception) {
            s.cancel(id)
            runCatching { out.close() }
            part.delete()
            throw e
        }
    }

    /** Returns the path the file got on the other side (renamed if the name was taken). */
    fun upload(s: Session, open: () -> InputStream, size: Long, mtime: Long, remotePath: String, t: Transfer, inbox: Boolean = false): String {
        t.total = size
        val id = s.newId()
        val done = s.expectDone(id)
        try {
            s.request(JSONObject().put("op", "write").put("path", remotePath).put("size", size)
                .put("mtime", mtime).put("overwrite", false).put("inbox", inbox), id = id, cancel = t.cancel)
            t.linkKind = s.dataLink.kind
            open().use { s.streamOut(id, it, t::add, t.cancel) }
            val r = try { done.get(5, TimeUnit.MINUTES) } catch (e: ExecutionException) { throw e.cause ?: e }
            Session.throwIfError(r)
            return (r.str("path") ?: remotePath).also { t.resultPath = it }
        } finally {
            s.forgetDone(id)
        }
    }
}

package com.btbridge.app.core

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream

/**
 * Writes an incoming DATA…END stream into a file on its own thread, so a slow disk never holds up the link
 * (pings, requests and other streams keep flowing). Up to 64 chunks wait in memory; beyond that the link waits.
 * [whenEnd] and [whenFail] run on the writer thread after everything before them was written and the file was closed.
 * (Named apart from onEnd/onFail on purpose: a call to those inside this class would reach the methods below, not the callbacks.)
 */
class FileSink(
    private val out: FileOutputStream,
    private val progress: ((Int) -> Unit)?,
    private val whenEnd: (JSONObject) -> Unit,
    private val whenFail: (String) -> Unit,
) : StreamSink {
    private val q = java.util.concurrent.ArrayBlockingQueue<Any>(64)
    @Volatile private var error: Exception? = null
    @Volatile private var finished = false

    private class EndMark(val end: JSONObject)
    private class FailMark(val why: String)

    init {
        // A lambda, not "::run": that name also matches Kotlin's own run {} and the thread would do nothing.
        Thread({ writeLoop() }, "file-writer").apply { isDaemon = true }.start()
    }

    override fun onData(buf: ByteArray, off: Int, len: Int) {
        error?.let { throw it }
        put(buf.copyOfRange(off, off + len))
    }

    override fun onEnd(end: JSONObject) = put(EndMark(end))
    override fun onFail(why: String) = put(FailMark(why))

    /** After the end or a failure nothing more is accepted (a late record is dropped instead of waiting forever). */
    private fun put(item: Any) {
        while (!finished) if (q.offer(item, 200, java.util.concurrent.TimeUnit.MILLISECONDS)) return
    }

    private fun writeLoop() {
        try {
            while (true) {
                when (val item = q.take()) {
                    is ByteArray -> if (error == null) {
                        try { out.write(item); progress?.invoke(item.size) } catch (e: Exception) { error = e }
                    }
                    is EndMark -> {
                        finish()
                        val err = error
                        try { if (err != null) whenFail(Heb.of(err)) else whenEnd(item.end) } catch (e: Exception) { Log.w("file sink end: ${e.message}") }
                        return
                    }
                    is FailMark -> {
                        finish()
                        try { whenFail(item.why) } catch (e: Exception) { Log.w("file sink fail: ${e.message}") }
                        return
                    }
                }
            }
        } catch (e: Throwable) {
            // Never leave the waiting side hanging.
            Log.w("file writer: $e")
            finish()
            runCatching { whenFail("שגיאה בכתיבת הקובץ") }
        }
    }

    private fun finish() {
        finished = true
        q.clear()
        try { out.close() } catch (e: Exception) { if (error == null) error = e }
    }

    companion object {
        /** A failure the user asked for (cancel). Anything else (a dropped connection) keeps the partial file for resuming. */
        fun isCancel(why: String) = why.startsWith("בוטל")
    }
}

class Root(val name: String, val dir: File, val total: Long = 0, val free: Long = 0)

/**
 * Serves this phone's files to the other device (PROTOCOL.md section 4).
 * [roots] lists storage volumes and quick folders; on Android it comes from StorageManager.
 */
class FileService(
    private val deviceName: () -> String,
    private val os: String,
    private val home: File,
    private val roots: () -> List<Root>,
    private val inboxDir: () -> File = { home },
    /** A file arrived through "send to device"; the flag says it is an update of this app (PROTOCOL.md section 4). */
    private val onReceived: ((Session, File, Boolean) -> Unit)? = null,
) {
    fun handle(s: Session, id: Int, req: JSONObject): JSONObject? = when (val op = req.str("op")) {
        "info" -> JSONObject().put("name", deviceName()).put("os", os).put("sep", "/").put("home", home.path)
        "list" -> list(path(req, "path"), req.optInt("offset", 0))
        "mkdir" -> {
            val f = File(path(req, "path"))
            if (f.exists()) throw RemoteException("exists", "כבר קיים: ${f.path}")
            if (!f.mkdirs()) throw RemoteException("io", "לא הצלחתי ליצור את התיקייה")
            ok()
        }
        "rename" -> {
            val from = File(path(req, "from"))
            val to = File(path(req, "to"))
            if (!from.exists()) throw RemoteException("notfound", "לא נמצא: ${from.path}")
            if (to.exists()) throw RemoteException("exists", "כבר קיים: ${to.path}")
            if (!from.renameTo(to)) throw RemoteException("io", "שינוי השם נכשל")
            ok()
        }
        "delete" -> {
            val arr = req.getJSONArray("paths")
            val failed = mutableListOf<String>()
            for (i in 0 until arr.length()) {
                val f = File(arr.getString(i))
                if (f.exists() && !f.deleteRecursively()) failed += f.name
            }
            if (failed.isNotEmpty()) throw RemoteException("io", "לא נמחק: " + failed.joinToString(", "))
            ok().put("recycled", false)
        }
        "read" -> { read(s, id, req); null }
        "write" -> write(s, id, req)
        "search" -> search(path(req, "path"), path(req, "q"), req.optInt("max", 500))
        else -> throw RemoteException("unknown", "פעולה לא מוכרת: $op")
    }

    private fun ok() = JSONObject().put("ok", true)

    // ------------------------------------------------------------------ search

    /**
     * PROTOCOL.md section 4, "search": names containing [q] (any case) under a folder and all its subfolders.
     * Hidden folders (starting with ".") are skipped. Stops at [max] results or after 20 seconds ("more": true).
     */
    private fun search(path: String, q: String, max: Int): JSONObject {
        val query = q.trim()
        if (query.isEmpty()) throw RemoteException("bad", "הזן מה לחפש")
        val root = if (path.isEmpty()) home else File(path)
        if (!root.isDirectory) throw RemoteException("notfound", "התיקייה לא קיימת: ${root.path}")
        val limit = max.coerceIn(1, 2000)
        val until = System.currentTimeMillis() + 20_000
        // Several threads read folders at the same time; only matching names become results.
        val dirs = java.util.concurrent.ConcurrentLinkedQueue<File>().apply { add(root) }
        val found = java.util.concurrent.ConcurrentLinkedQueue<File>()
        val busy = java.util.concurrent.atomic.AtomicInteger()
        val count = java.util.concurrent.atomic.AtomicInteger()
        val stop = java.util.concurrent.atomic.AtomicBoolean()
        val more = java.util.concurrent.atomic.AtomicBoolean()
        val workers = (1..4).map {
            Thread {
                while (!stop.get()) {
                    // Counted as busy before taking a folder, so "queue empty and nobody busy" really means done.
                    busy.incrementAndGet()
                    val dir = dirs.poll()
                    if (dir == null) {
                        if (busy.decrementAndGet() == 0 && dirs.isEmpty()) break
                        Thread.sleep(1)
                        continue
                    }
                    try {
                        for (f in dir.listFiles() ?: emptyArray()) {
                            if (stop.get()) break
                            if (f.name.startsWith(".")) continue
                            if (f.isDirectory) dirs.add(f)
                            if (!f.name.contains(query, ignoreCase = true)) continue
                            if (count.incrementAndGet() > limit) { more.set(true); stop.set(true); break }
                            found.add(f)
                        }
                        if (System.currentTimeMillis() > until) { more.set(true); stop.set(true) }
                    } catch (_: Exception) {
                        // A folder that can't be read is skipped.
                    } finally {
                        busy.decrementAndGet()
                    }
                }
            }.apply { isDaemon = true; start() }
        }
        workers.forEach { it.join() }
        // Nearer results first: by how deep they are, then by name.
        val items = JSONArray()
        found.sortedWith(compareBy<File>({ it.path.count { c -> c == '/' } }, { it.name.lowercase() })).forEach { items.put(item(it)) }
        return JSONObject().put("items", items).put("more", more.get()).put("root", root.path)
    }

    private fun item(f: File) = JSONObject().put("n", f.name).put("p", f.path).put("d", f.isDirectory)
        .put("s", if (f.isFile) f.length() else 0).put("m", f.lastModified())

    private fun path(o: JSONObject, key: String): String = o.str(key) ?: throw RemoteException("bad", "חסר שדה: $key")

    // ------------------------------------------------------------------ list

    private fun list(path: String, offset: Int): JSONObject {
        val items = JSONArray()
        val rootList = roots()
        if (path.isEmpty()) {
            for (r in rootList) {
                items.put(JSONObject().put("n", r.name).put("p", r.dir.path).put("d", true)
                    .put("s", r.total).put("free", r.free).put("m", 0))
            }
            return JSONObject().put("items", items).put("parent", JSONObject.NULL).put("more", false)
        }
        val dir = File(path)
        if (!dir.isDirectory) throw RemoteException("notfound", "התיקייה לא קיימת: $path")
        val files = dir.listFiles()
            ?: throw RemoteException("denied", "אין גישה לתיקייה. אשר בטלפון \"גישה לכל הקבצים\" לגשר בלוטוס.")
        val sorted = files.filter { !it.name.startsWith(".") }
            .sortedWith(compareBy<File>({ !it.isDirectory }, { it.name.lowercase() }))
        val page = sorted.drop(offset).take(PAGE)
        for (f in page) {
            items.put(JSONObject().put("n", f.name).put("p", f.path).put("d", f.isDirectory)
                .put("s", if (f.isFile) f.length() else 0).put("m", f.lastModified()))
        }
        val parent = if (rootList.any { it.dir.path == dir.path }) "" else dir.parent ?: ""
        return JSONObject().put("items", items).put("parent", parent).put("more", sorted.size > offset + PAGE)
    }

    // ------------------------------------------------------------------ read / write

    private fun read(s: Session, id: Int, req: JSONObject) {
        val f = File(path(req, "path"))
        if (f.isDirectory) throw RemoteException("isdir", "זו תיקייה: ${f.path}")
        FileInputStream(f).use { input ->
            val offset = req.long("offset")
            if (offset > 0) input.channel.position(offset)
            s.reply(id, JSONObject().put("ok", true).put("size", f.length()).put("m", f.lastModified()))
            s.streamOut(id, input, null, null)
        }
    }

    private fun write(s: Session, id: Int, req: JSONObject): JSONObject {
        val inbox = req.bool("inbox") == true
        var target = if (!inbox) File(path(req, "path")) else {
            // "Send to device": a relative name inside this side's downloads folder, never outside it.
            val parts = path(req, "path").split('/', '\\').filter { it.isNotEmpty() }
            if (parts.isEmpty() || parts.any { it == "." || it == ".." || it.any { c -> c in "\u0000:*?\"<>|" } })
                throw RemoteException("bad", "שם קובץ לא תקין")
            parts.fold(inboxDir()) { dir, p -> File(dir, p) }
        }
        val overwrite = req.bool("overwrite") == true
        if (target.exists() && !overwrite) target = uniqueFile(target)
        if (target.isDirectory) throw RemoteException("exists", "קיימת תיקייה בשם הזה: ${target.path}")
        target.parentFile?.mkdirs()
        val part = File(target.path + ".btpart")
        // Resume (PROTOCOL.md section 4): the target name is chosen the same way again, so an interrupted
        // upload finds its own partial file and only the rest is sent.
        val resume = req.bool("resume") == true && part.isFile
        val have = if (resume) part.length() else 0L
        val out = FileOutputStream(part, resume)
        val mtime = req.long("mtime")
        val finalTarget = target
        s.expectStream(id, FileSink(out, null,
            whenEnd = {
                try {
                    if (overwrite) finalTarget.delete()
                    if (!part.renameTo(finalTarget)) throw RemoteException("io", "שמירת הקובץ נכשלה")
                    if (mtime > 0) finalTarget.setLastModified(mtime)
                    s.sendDone(id, JSONObject().put("ok", true).put("path", finalTarget.path))
                    if (inbox) onReceived?.invoke(s, finalTarget, req.bool("update") == true)
                } catch (e: Exception) {
                    part.delete()
                    runCatching { s.sendDone(id, JSONObject().put("err", "io").put("msg", Heb.of(e))) }
                }
            },
            whenFail = { why ->
                if (FileSink.isCancel(why)) part.delete()
                runCatching { s.sendDone(id, JSONObject().put("err", "cancelled").put("msg", why)) }
            }))
        return ok().put("path", target.path).put("have", have)
    }

    companion object {
        private const val PAGE = 1000

        fun uniqueFile(f: File): File {
            if (!f.exists()) return f
            val dot = f.name.lastIndexOf('.')
            val base = if (dot > 0) f.name.substring(0, dot) else f.name
            val ext = if (dot > 0) f.name.substring(dot) else ""
            var i = 1
            while (true) {
                val c = File(f.parentFile, "$base ($i)$ext")
                if (!c.exists()) return c
                i++
            }
        }
    }
}

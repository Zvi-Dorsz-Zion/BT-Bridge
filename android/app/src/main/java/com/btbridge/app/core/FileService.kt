package com.btbridge.app.core

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream

/** Writes an incoming DATA…END stream into a file. */
class FileSink(
    private val out: FileOutputStream,
    private val progress: ((Int) -> Unit)?,
    private val onEnd: (JSONObject) -> Unit,
    private val onFail: (String) -> Unit,
) : StreamSink {
    override fun onData(buf: ByteArray, off: Int, len: Int) {
        out.write(buf, off, len)
        progress?.invoke(len)
    }

    override fun onEnd(end: JSONObject) = onEnd.invoke(end)
    override fun onFail(why: String) = onFail.invoke(why)
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
    private val onReceived: ((Session, File) -> Unit)? = null,
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
        else -> throw RemoteException("unknown", "פעולה לא מוכרת: $op")
    }

    private fun ok() = JSONObject().put("ok", true)

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
        val out = FileOutputStream(part)
        val mtime = req.long("mtime")
        val finalTarget = target
        s.expectStream(id, FileSink(out, null,
            onEnd = {
                try {
                    out.close()
                    if (overwrite) finalTarget.delete()
                    if (!part.renameTo(finalTarget)) throw RemoteException("io", "שמירת הקובץ נכשלה")
                    if (mtime > 0) finalTarget.setLastModified(mtime)
                    s.sendDone(id, JSONObject().put("ok", true).put("path", finalTarget.path))
                    if (inbox) onReceived?.invoke(s, finalTarget)
                } catch (e: Exception) {
                    part.delete()
                    runCatching { s.sendDone(id, JSONObject().put("err", "io").put("msg", Heb.of(e))) }
                }
            },
            onFail = { why ->
                runCatching { out.close() }
                part.delete()
                runCatching { s.sendDone(id, JSONObject().put("err", "cancelled").put("msg", why)) }
            }))
        return ok().put("path", target.path)
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

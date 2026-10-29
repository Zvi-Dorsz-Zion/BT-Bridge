package com.btbridge.app

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import android.widget.Toast
import androidx.core.content.FileProvider
import java.io.File
import java.io.InputStream

/** A local file picked through the system picker (SAF). */
class PickedFile(val name: String, val size: Long, val mtime: Long, val relDir: String, val open: () -> InputStream)

object LocalFiles {
    fun fromUri(ctx: Context, uri: Uri): PickedFile {
        var name = uri.lastPathSegment ?: "file"
        var size = 0L
        ctx.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
            if (c.moveToFirst()) {
                c.getString(0)?.let { name = it }
                if (!c.isNull(1)) size = c.getLong(1)
            }
        }
        val mtime = runCatching {
            ctx.contentResolver.query(uri, arrayOf(DocumentsContract.Document.COLUMN_LAST_MODIFIED), null, null, null)?.use { c ->
                if (c.moveToFirst() && !c.isNull(0)) c.getLong(0) else null
            }
        }.getOrNull() ?: System.currentTimeMillis()
        return PickedFile(name, size, mtime, "") { ctx.contentResolver.openInputStream(uri) ?: error("לא ניתן לפתוח את $name") }
    }

    /** All files under a tree picked with OpenDocumentTree, with their folder paths relative to it. */
    fun walkTree(ctx: Context, tree: Uri): Pair<String, List<PickedFile>> {
        val rootId = DocumentsContract.getTreeDocumentId(tree)
        val rootName = ctx.contentResolver.query(
            DocumentsContract.buildDocumentUriUsingTree(tree, rootId),
            arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getString(0) else null
        } ?: "תיקייה"
        val out = mutableListOf<PickedFile>()
        fun walk(docId: String, rel: String) {
            val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, docId)
            ctx.contentResolver.query(children, arrayOf(
                DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                DocumentsContract.Document.COLUMN_MIME_TYPE,
                DocumentsContract.Document.COLUMN_SIZE,
                DocumentsContract.Document.COLUMN_LAST_MODIFIED,
            ), null, null, null)?.use { c ->
                while (c.moveToNext()) {
                    val id = c.getString(0)
                    val name = c.getString(1) ?: continue
                    if (c.getString(2) == DocumentsContract.Document.MIME_TYPE_DIR) {
                        walk(id, if (rel.isEmpty()) name else "$rel/$name")
                    } else {
                        val uri = DocumentsContract.buildDocumentUriUsingTree(tree, id)
                        val size = if (c.isNull(3)) 0 else c.getLong(3)
                        val mtime = if (c.isNull(4)) System.currentTimeMillis() else c.getLong(4)
                        out += PickedFile(name, size, mtime, rel) { ctx.contentResolver.openInputStream(uri) ?: error("לא ניתן לפתוח את $name") }
                    }
                }
            }
        }
        walk(rootId, "")
        return rootName to out
    }

    fun open(ctx: Context, f: File) {
        if (!f.exists()) {
            Toast.makeText(ctx, "הקובץ כבר נמחק. אפשר לפתוח אותו שוב מהסייר.", Toast.LENGTH_LONG).show()
            return
        }
        val uri = FileProvider.getUriForFile(ctx, ctx.packageName + ".files", f)
        val ext = f.extension.lowercase()
        if (ext == "apk" && !ctx.packageManager.canRequestPackageInstalls()) {
            // Android hands an APK to the installer only after "install unknown apps" was allowed for us.
            Toast.makeText(ctx, "כדי להתקין, אשר \"התקנת אפליקציות לא מוכרות\" לגשר בלוטוס, חזור והקש שוב על הקובץ", Toast.LENGTH_LONG).show()
            ctx.startActivity(Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                android.net.Uri.parse("package:" + ctx.packageName)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            return
        }
        val mime = if (ext == "apk") "application/vnd.android.package-archive"
            else MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "*/*"
        val i = Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        try { ctx.startActivity(i) }
        catch (e: ActivityNotFoundException) { Toast.makeText(ctx, "אין אפליקציה שפותחת את הקובץ הזה", Toast.LENGTH_SHORT).show() }
    }

    fun unique(f: File): File = com.btbridge.app.core.FileService.uniqueFile(f)
}

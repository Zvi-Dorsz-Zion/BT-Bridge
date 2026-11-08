package com.btbridge.app

import android.annotation.SuppressLint
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import com.btbridge.app.core.RemoteFs
import com.btbridge.app.core.Session
import com.btbridge.app.core.Transfer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

/**
 * "Share → גשר בלוטוס": asks which computer, connects if needed, and sends the files to its downloads folder.
 * Stays open until the files went out (Android lets us read shared files only while this screen lives).
 */
class ShareActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        BridgeService.start(this)
        val uris = sharedUris(intent)
        // Shared text (not a file): it goes to the computer's clipboard.
        val text = intent.getStringExtra(Intent.EXTRA_TEXT)
        if (uris.isEmpty() && !text.isNullOrEmpty()) {
            android.widget.Toast.makeText(this, ClipSend.send(text), android.widget.Toast.LENGTH_SHORT).show()
            finish()
            return
        }
        setContent {
            val dark = isSystemInDarkTheme()
            MaterialTheme(colorScheme = if (dark) darkColorScheme(primary = Color(0xFF60A5FA)) else lightColorScheme(primary = Color(0xFF2563EB))) {
                CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Rtl) {
                    Surface { ShareContent(uris) }
                    PasswordPrompt()
                }
            }
        }
    }

    @Suppress("DEPRECATION")
    private fun sharedUris(i: Intent): List<Uri> = when (i.action) {
        Intent.ACTION_SEND -> listOfNotNull(
            if (Build.VERSION.SDK_INT >= 33) i.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java) else i.getParcelableExtra(Intent.EXTRA_STREAM))
        Intent.ACTION_SEND_MULTIPLE ->
            (if (Build.VERSION.SDK_INT >= 33) i.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)
            else i.getParcelableArrayListExtra(Intent.EXTRA_STREAM)).orEmpty()
        else -> emptyList()
    }

    private class Target(val key: String, val name: String, val session: Session?, val device: android.bluetooth.BluetoothDevice?)

    /** A shared file copied into the app's own folder ([dir], deleted once its transfer is done). */
    private class Copied(val dir: File, val file: File, val name: String, val mtime: Long)

    /**
     * Android lets us read shared files only while this screen lives, so each one is copied into the app's own
     * folder first: the queue, a resume after a dropped connection, and closing this screen all keep working.
     * Blocking. Returns the copies, and the names of the files that could not be read.
     */
    private fun copyShared(uris: List<Uri>, made: MutableList<File>, progress: (Int) -> Unit): Pair<List<Copied>, List<String>> {
        val ok = mutableListOf<Copied>()
        val failed = mutableListOf<String>()
        uris.forEachIndexed { idx, uri ->
            progress(idx)
            var name = uri.lastPathSegment ?: "קובץ"
            var dir: File? = null
            try {
                val src = LocalFiles.fromUri(this, uri)
                name = src.name
                val d = App.bridge.newShareDir().also { dir = it; made += it }
                if (src.size > 0 && src.size > d.usableSpace - 50L * 1024 * 1024) throw java.io.IOException("ENOSPC")
                val local = File(d, name.replace('/', '_').take(100).ifBlank { "file" })
                src.open().use { input -> local.outputStream().use { out -> input.copyTo(out, 256 * 1024) } }
                ok += Copied(d, local, name, src.mtime)
            } catch (e: Exception) {
                com.btbridge.app.core.Log.w("share: could not copy $name: $e")
                dir?.let { made -= it; runCatching { it.deleteRecursively() } }
                failed += name
            }
        }
        progress(uris.size)
        return ok to failed
    }

    @SuppressLint("MissingPermission")
    @Composable
    private fun ShareContent(uris: List<Uri>) {
        val b = App.bridge
        val scope = rememberCoroutineScope()
        val sessions by b.sessions.collectAsState()
        val list by b.transfers.collectAsState()
        val tick by b.tick.collectAsState()
        var busy by remember { mutableStateOf<String?>(null) }
        var sent by remember { mutableStateOf<List<Transfer>>(emptyList()) }
        var error by remember { mutableStateOf<String?>(null) }
        /** While copying: how many files were copied so far (-1 = not copying). */
        var copying by remember { mutableIntStateOf(-1) }
        /** The files went to the queue. Kept if this screen is ever created again, so nothing is sent twice. */
        var queued by rememberSaveable { mutableStateOf(false) }
        tick.let { }
        // A touch outside the window must not close it in the middle of copying or sending.
        val locked = busy != null || sent.any { it.active }
        SideEffect { setFinishOnTouchOutside(!locked) }

        val targets = remember(sessions) {
            val bonded = if (b.hasBtPermission()) runCatching { b.adapter()?.bondedDevices?.toList() }.getOrNull().orEmpty() else emptyList()
            val out = mutableListOf<Target>()
            for (s in sessions) out += Target(s.peerAddress, b.displayName(s), s, null)
            for (t in b.store.trusted()) if (out.none { it.key == t.address }) {
                val d = bonded.firstOrNull { Bridge.key(it.address) == t.address }
                if (d != null) out += Target(t.address, b.store.nameFor(t.fp, t.name), null, d)
            }
            out
        }

        Column(Modifier.padding(16.dp)) {
            Text(if (sent.isEmpty()) "לאן לשלוח ${uris.size} קבצים?" else "שולח…", fontWeight = FontWeight.SemiBold,
                style = MaterialTheme.typography.titleMedium)
            Text("הקבצים יישמרו בתיקיית ההורדות של המכשיר שתבחר.", style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.padding(bottom = 8.dp))
            error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            if (uris.isEmpty()) Text("לא התקבלו קבצים לשליחה.")
            if (copying >= 0) Text("מכין את הקבצים לשליחה… (${minOf(copying + 1, uris.size)} מתוך ${uris.size})",
                style = MaterialTheme.typography.bodySmall)
            if (queued && sent.isEmpty()) {
                // This screen was created again after the files already went to the queue.
                Text("הקבצים כבר נשלחו לתור. אפשר לראות אותם במסך ההעברות של גשר בלוטוס.")
                TextButton(onClick = { finish() }) { Text("סגור") }
            } else if (sent.isEmpty()) {
                if (targets.isEmpty()) Text("אין מכשירים מוכרים. התחבר פעם אחת מתוך האפליקציה.")
                LazyColumn {
                    items(targets, key = { it.key }) { t ->
                        ListItem(
                            headlineContent = { Text(t.name) },
                            supportingContent = { Text(if (t.session != null) "מחובר" else if (busy == t.key) "מתחבר…" else "לא מחובר · יתחבר עכשיו") },
                            leadingContent = { Icon(Icons.Filled.Computer, null) },
                            trailingContent = { if (busy == t.key) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) },
                            modifier = Modifier.clickable(enabled = busy == null && !queued && uris.isNotEmpty()) {
                                busy = t.key
                                error = null
                                scope.launch {
                                    val made = mutableListOf<File>()
                                    try {
                                        val s = t.session ?: withContext(Dispatchers.IO) { b.connect(t.device!!) }
                                        copying = 0
                                        val (ok, failed) = withContext(Dispatchers.IO) { copyShared(uris, made) { copying = it } }
                                        copying = -1
                                        if (failed.isNotEmpty()) error = "לא ניתן היה לקרוא " +
                                            (if (failed.size == 1) "את הקובץ ${failed[0]}, והוא לא נשלח." else "את הקבצים האלה, והם לא נשלחו: ${failed.joinToString(", ")}")
                                        if (ok.isEmpty()) return@launch
                                        if (ok.sumOf { it.file.length() } > 5L * 1024 * 1024) withContext(Dispatchers.IO) { b.ensureFastLink(s) }
                                        sent = ok.map { c ->
                                            val size = c.file.length()
                                            val tr = Transfer(c.name, upload = true, s.peerFp) { ss, tr ->
                                                RemoteFs.upload(ss, { c.file.inputStream() }, size, c.mtime, c.name, tr, inbox = true)
                                            }
                                            // The copy stays for "continue" after an error; it goes once sent or cancelled.
                                            b.queue(tr) { done -> if (done || tr.cancel.isCancelled) runCatching { c.dir.deleteRecursively() } }
                                            tr
                                        }
                                        made.clear()
                                        queued = true
                                    } catch (e: Exception) {
                                        if (e !is java.util.concurrent.CancellationException) error = com.btbridge.app.core.Heb.of(e)
                                    } finally {
                                        // Copies that never reached the queue (an error, or this screen closed in the middle).
                                        made.forEach { runCatching { it.deleteRecursively() } }
                                        copying = -1
                                        busy = null
                                    }
                                }
                            },
                        )
                    }
                }
            } else {
                list.let { }
                Column { sent.forEach { TransferRow(it) } }
                // The files were copied to the app, so the sending goes on also after this screen closes.
                if (sent.any { it.active }) Text("אפשר לסגור את המסך הזה. השליחה תמשיך ברקע.", style = MaterialTheme.typography.bodySmall)
                TextButton(onClick = { finish() }) { Text("סגור") }
            }
            if (sent.isEmpty() && !queued) TextButton(onClick = { finish() }, modifier = Modifier.fillMaxWidth()) { Text("ביטול") }
        }
    }
}

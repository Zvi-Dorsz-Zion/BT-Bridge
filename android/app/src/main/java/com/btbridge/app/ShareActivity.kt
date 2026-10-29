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
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
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

/**
 * "Share → גשר בלוטוס": asks which computer, connects if needed, and sends the files to its downloads folder.
 * Stays open until the files went out (Android lets us read shared files only while this screen lives).
 */
class ShareActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        BridgeService.start(this)
        val uris = sharedUris(intent)
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
            if (sent.isEmpty()) {
                if (targets.isEmpty()) Text("אין מכשירים מוכרים. התחבר פעם אחת מתוך האפליקציה.")
                LazyColumn {
                    items(targets, key = { it.key }) { t ->
                        ListItem(
                            headlineContent = { Text(t.name) },
                            supportingContent = { Text(if (t.session != null) "מחובר" else if (busy == t.key) "מתחבר…" else "לא מחובר · יתחבר עכשיו") },
                            leadingContent = { Icon(Icons.Filled.Computer, null) },
                            trailingContent = { if (busy == t.key) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) },
                            modifier = Modifier.clickable(enabled = busy == null && uris.isNotEmpty()) {
                                busy = t.key
                                scope.launch {
                                    try {
                                        val s = t.session ?: withContext(Dispatchers.IO) { b.connect(t.device!!) }
                                        val files = withContext(Dispatchers.IO) { uris.mapNotNull { runCatching { LocalFiles.fromUri(this@ShareActivity, it) }.getOrNull() } }
                                        if (files.sumOf { it.size } > 5L * 1024 * 1024) withContext(Dispatchers.IO) { b.ensureFastLink(s) }
                                        sent = files.map { f ->
                                            Transfer(f.name, upload = true).also { tr ->
                                                b.queue(tr, { RemoteFs.upload(s, f.open, f.size, f.mtime, f.name, tr, inbox = true) })
                                            }
                                        }
                                    } catch (e: Exception) {
                                        error = com.btbridge.app.core.Heb.of(e)
                                    } finally {
                                        busy = null
                                    }
                                }
                            },
                        )
                    }
                }
            } else {
                tick.let { }
                list.let { }
                Column { sent.forEach { TransferRow(it) } }
                if (sent.none { it.active }) {
                    TextButton(onClick = { finish() }) { Text("סגור") }
                } else {
                    Text("אפשר להשאיר את המסך הזה פתוח עד שהשליחה מסתיימת.", style = MaterialTheme.typography.bodySmall)
                }
            }
            if (sent.isEmpty()) TextButton(onClick = { finish() }, modifier = Modifier.fillMaxWidth()) { Text("ביטול") }
        }
    }
}

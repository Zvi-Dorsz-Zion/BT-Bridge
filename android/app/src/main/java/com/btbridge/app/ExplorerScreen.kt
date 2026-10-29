package com.btbridge.app

import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.automirrored.filled.Sort
import androidx.compose.material.icons.filled.CreateNewFolder
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.DesktopWindows
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.DriveFileRenameOutline
import androidx.compose.material.icons.filled.DriveFolderUpload
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.SelectAll
import androidx.compose.material.icons.filled.SwapVert
import androidx.compose.material.icons.filled.Upload
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextDirection
import androidx.compose.ui.unit.dp
import com.btbridge.app.core.RemoteException
import com.btbridge.app.core.RemoteFs
import com.btbridge.app.core.RemoteItem
import com.btbridge.app.core.RemoteListing
import com.btbridge.app.core.Session
import com.btbridge.app.core.Transfer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Above this, ask for a fast link (Wi-Fi or the direct network) before transferring. */
const val BIG = 5L * 1024 * 1024

@OptIn(ExperimentalFoundationApi::class)
fun Modifier.combinedClick(onClick: () -> Unit, onLongClick: () -> Unit): Modifier =
    this.combinedClickable(onClick = onClick, onLongClick = onLongClick)

private val dateFmt = SimpleDateFormat("dd/MM/yyyy HH:mm", Locale.US)

/** Browses the other device's files. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ExplorerScreen(s: Session, onBack: () -> Unit, onTransfers: () -> Unit, onRemote: () -> Unit) {
    val b = App.bridge
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var path by rememberSaveable(s.peerFp) { mutableStateOf("") }
    var listing by remember(s) { mutableStateOf<RemoteListing?>(null) }
    var loading by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    val selected = remember { mutableStateListOf<String>() }
    var menu by remember { mutableStateOf(false) }
    var newFolder by remember { mutableStateOf(false) }
    var renaming by remember { mutableStateOf<RemoteItem?>(null) }
    var confirmDelete by remember { mutableStateOf(false) }
    val transfers by b.transfers.collectAsState()
    val sessions by b.sessions.collectAsState()
    val live = sessions.firstOrNull { it === s }

    /** Folders visited before this one: Back goes exactly one step back. */
    val history = remember(s) { mutableStateListOf<String>() }
    val storeVersion by b.store.version.collectAsState()

    fun load(p: String, remember: Boolean = true) {
        scope.launch {
            loading = true
            try {
                val l = withContext(Dispatchers.IO) { RemoteFs.list(s, p) }
                if (remember && p != path && listing != null) history += path
                listing = l
                path = p
                selected.clear()
            } catch (e: Exception) {
                message = com.btbridge.app.core.Heb.of(e)
            } finally {
                loading = false
            }
        }
    }

    fun refreshAfter(target: String): (Boolean) -> Unit = { _ ->
        scope.launch { if (path == target) load(target, remember = false) }
    }

    LaunchedEffect(s) { load(path, remember = false) }

    /** Phone Back and the arrow at the top do the same: one step back, and only from the first folder to the device list. */
    fun goBack() {
        when {
            selected.isNotEmpty() -> selected.clear()
            history.isNotEmpty() -> load(history.removeAt(history.lastIndex), remember = false)
            else -> onBack()
        }
    }

    BackHandler { goBack() }

    val pickFiles = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isEmpty()) return@rememberLauncherForActivityResult
        val target = path
        scope.launch(Dispatchers.IO) {
            val files = uris.mapNotNull { runCatching { LocalFiles.fromUri(ctx, it) }.getOrNull() }
            uploadAll(s, target, files, refreshAfter(target))
        }
    }
    val pickFolder = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { tree ->
        if (tree == null) return@rememberLauncherForActivityResult
        val target = path
        scope.launch(Dispatchers.IO) {
            try {
                val (name, files) = LocalFiles.walkTree(ctx, tree)
                val root = RemoteFs.join(s, target, name)
                mkdirQuiet(s, root)
                val dirs = files.map { it.relDir }.filter { it.isNotEmpty() }.distinct().sortedBy { it.length }
                for (d in dirs) mkdirQuiet(s, d.split('/').fold(root) { acc, part -> RemoteFs.join(s, acc, part) })
                uploadAll(s, root, files, refreshAfter(target))
            } catch (e: Exception) {
                withContext(Dispatchers.Main) { message = com.btbridge.app.core.Heb.of(e) }
            }
        }
    }

    // "Send to computer": straight into its downloads folder, wherever we are browsing.
    val pickInbox = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isEmpty()) return@rememberLauncherForActivityResult
        scope.launch(Dispatchers.IO) {
            val files = uris.mapNotNull { runCatching { LocalFiles.fromUri(ctx, it) }.getOrNull() }
            if (files.sumOf { it.size } > BIG) b.ensureFastLink(s)
            files.forEach { f ->
                val t = Transfer(f.name, upload = true)
                b.queue(t, { RemoteFs.upload(s, f.open, f.size, f.mtime, f.name, t, inbox = true) })
            }
        }
    }

    val inFolder = path.isNotEmpty()
    storeVersion.let { }
    val (sortBy, sortDesc) = b.store.sortFor(s.peerFp, path) ?: FileOrder.default(path)
    // The virtual root keeps the other side's order (quick folders, then drives).
    val items = listing?.items.orEmpty().let { if (inFolder) FileOrder.sort(it, sortBy, sortDesc) else it }
    var sortMenu by remember { mutableStateOf(false) }

    Scaffold(topBar = {
        TopAppBar(
            title = {
                Column {
                    Text(if (selected.isEmpty()) s.peerName else "נבחרו ${selected.size}", maxLines = 1)
                    Text(live?.linkText ?: "מנותק", style = MaterialTheme.typography.bodySmall)
                }
            },
            navigationIcon = {
                IconButton(onClick = { goBack() }) {
                    Icon(if (selected.isNotEmpty()) Icons.Filled.Close else Icons.AutoMirrored.Filled.ArrowBack, "חזור")
                }
            },
            actions = {
                if (selected.isNotEmpty()) {
                    IconButton(onClick = { download(scope, s, items.filter { it.path in selected }) { message = it }; selected.clear() }) {
                        Icon(Icons.Filled.Download, "הורד")
                    }
                    if (selected.size == 1 && inFolder) IconButton(onClick = { renaming = items.firstOrNull { it.path == selected[0] } }) {
                        Icon(Icons.Filled.DriveFileRenameOutline, "שנה שם")
                    }
                    if (inFolder) IconButton(onClick = { confirmDelete = true }) { Icon(Icons.Filled.Delete, "מחק") }
                    IconButton(onClick = { selected.clear(); selected.addAll(items.map { it.path }) }) { Icon(Icons.Filled.SelectAll, "בחר הכל") }
                } else {
                    if (s.peerOs == "windows") IconButton(onClick = onRemote) { Icon(Icons.Filled.DesktopWindows, "מסך המחשב") }
                    IconButton(onClick = onTransfers) { Icon(Icons.Filled.SwapVert, "העברות") }
                    IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, "עוד") }
                    DropdownMenu(menu, { menu = false }) {
                        DropdownMenuItem(text = { Text("רענן") }, leadingIcon = { Icon(Icons.Filled.Refresh, null) },
                            onClick = { menu = false; load(path, remember = false) })
                        listing?.parent?.let { up ->
                            DropdownMenuItem(text = { Text("לתיקייה שמעל") }, leadingIcon = { Icon(Icons.Filled.ArrowUpward, null) },
                                onClick = { menu = false; load(up) })
                        }
                        DropdownMenuItem(text = { Text("שלח קבצים להורדות של ${b.displayName(s)}") }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.Send, null) },
                            onClick = { menu = false; pickInbox.launch(arrayOf("*/*")) })
                        if (s.peerOs == "windows") DropdownMenuItem(text = { Text("שליטה במסך המחשב") }, leadingIcon = { Icon(Icons.Filled.DesktopWindows, null) },
                            onClick = { menu = false; onRemote() })
                        HorizontalDivider()
                        DropdownMenuItem(text = { Text("העלה קבצים…") }, leadingIcon = { Icon(Icons.Filled.Upload, null) }, enabled = inFolder,
                            onClick = { menu = false; pickFiles.launch(arrayOf("*/*")) })
                        DropdownMenuItem(text = { Text("העלה תיקייה…") }, leadingIcon = { Icon(Icons.Filled.DriveFolderUpload, null) }, enabled = inFolder,
                            onClick = { menu = false; pickFolder.launch(null) })
                        DropdownMenuItem(text = { Text("תיקייה חדשה") }, leadingIcon = { Icon(Icons.Filled.CreateNewFolder, null) }, enabled = inFolder,
                            onClick = { menu = false; newFolder = true })
                    }
                }
            },
        )
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            if (path.isNotEmpty()) Text(path, Modifier.fillMaxWidth().padding(16.dp, 4.dp, 16.dp, 0.dp),
                style = MaterialTheme.typography.bodySmall.copy(textDirection = TextDirection.Ltr), maxLines = 2)
            if (inFolder) Box(Modifier.padding(horizontal = 8.dp)) {
                TextButton(onClick = { sortMenu = true }) {
                    Icon(Icons.AutoMirrored.Filled.Sort, null)
                    Text("  " + FileOrder.label(sortBy, sortDesc), style = MaterialTheme.typography.bodySmall)
                }
                DropdownMenu(sortMenu, { sortMenu = false }) {
                    listOf(
                        Triple("name", false, "שם (א-ת)"), Triple("name", true, "שם (ת-א)"),
                        Triple("date", true, "תאריך, החדשים קודם"), Triple("date", false, "תאריך, הישנים קודם"),
                        Triple("size", true, "גודל, הגדולים קודם"), Triple("size", false, "גודל, הקטנים קודם"),
                        Triple("type", false, "סוג"),
                    ).forEach { (by, desc, label) ->
                        DropdownMenuItem(
                            text = { Text((if (by == sortBy && desc == sortDesc) "✓ " else "") + label) },
                            onClick = { sortMenu = false; b.store.setSort(s.peerFp, path, by, desc) },
                        )
                    }
                }
            }
            if (loading) LinearProgressIndicator(Modifier.fillMaxWidth())
            val active = transfers.firstOrNull { it.active && it.status == "מעביר…" }
            if (active != null) Box(Modifier.clickable(onClick = onTransfers)) { TransferRow(active) }
            if (!loading && items.isEmpty() && listing != null) Text("התיקייה ריקה.", Modifier.padding(16.dp))
            LazyColumn(Modifier.weight(1f)) {
                items(items, key = { it.path }) { i ->
                    val isSel = i.path in selected
                    ListItem(
                        headlineContent = { Text(i.name, maxLines = 2) },
                        supportingContent = {
                            val parts = listOfNotNull(
                                if (!i.isDir) Transfer.formatSize(i.size) else null,
                                if (i.mtime > 0) dateFmt.format(Date(i.mtime)) else null,
                            )
                            if (parts.isNotEmpty()) Text(parts.joinToString(" · "))
                        },
                        leadingContent = {
                            Icon(if (i.isDir) Icons.Filled.Folder else Icons.AutoMirrored.Filled.InsertDriveFile, null,
                                tint = if (i.isDir) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                        },
                        colors = if (isSel) ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.secondaryContainer) else ListItemDefaults.colors(),
                        modifier = Modifier.combinedClick(
                            onClick = {
                                when {
                                    selected.isNotEmpty() -> if (isSel) selected.remove(i.path) else selected.add(i.path)
                                    i.isDir -> load(i.path)
                                    else -> openRemote(s, i)
                                }
                            },
                            onLongClick = { if (!isSel) selected.add(i.path) },
                        ),
                    )
                    HorizontalDivider()
                }
            }
        }
    }

    if (newFolder) NameDialog("תיקייה חדשה", "תיקייה חדשה", onDismiss = { newFolder = false }) { name ->
        newFolder = false
        scope.launch {
            try { withContext(Dispatchers.IO) { RemoteFs.mkdir(s, RemoteFs.join(s, path, name)) }; load(path) }
            catch (e: Exception) { message = com.btbridge.app.core.Heb.of(e) }
        }
    }
    renaming?.let { item ->
        NameDialog("שינוי שם", item.name, onDismiss = { renaming = null }) { name ->
            renaming = null
            if (name == item.name) return@NameDialog
            scope.launch {
                try { withContext(Dispatchers.IO) { RemoteFs.rename(s, item.path, RemoteFs.join(s, path, name)) }; load(path) }
                catch (e: Exception) { message = com.btbridge.app.core.Heb.of(e) }
            }
        }
    }
    if (confirmDelete) {
        val n = selected.size
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("למחוק $n פריטים?") },
            text = { Text(if (s.peerOs == "windows") "הם יעברו לסל המיחזור במחשב." else "המחיקה היא לצמיתות.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmDelete = false
                    val paths = selected.toList()
                    scope.launch {
                        try { withContext(Dispatchers.IO) { RemoteFs.delete(s, paths) } }
                        catch (e: Exception) { message = com.btbridge.app.core.Heb.of(e) }
                        load(path)
                    }
                }) { Text("מחק") }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("ביטול") } },
        )
    }
    message?.let { MessageDialog(it) { message = null } }
}

@Composable
private fun NameDialog(title: String, initial: String, onDismiss: () -> Unit, onOk: (String) -> Unit) {
    var v by remember { mutableStateOf(initial) }
    val bad = v.isBlank() || v.any { it in "\\/:*?\"<>|" }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = { OutlinedTextField(v, { v = it }, singleLine = true) },
        confirmButton = { TextButton(enabled = !bad, onClick = { onOk(v.trim()) }) { Text("אישור") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("ביטול") } },
    )
}

// ---------------------------------------------------------------------- actions

private fun mkdirQuiet(s: Session, path: String) {
    try { RemoteFs.mkdir(s, path) } catch (e: RemoteException) { if (e.code != "exists") throw e }
}

private fun uploadAll(s: Session, remoteDir: String, files: List<PickedFile>, after: (Boolean) -> Unit) {
    val b = App.bridge
    if (files.sumOf { it.size } > BIG) b.ensureFastLink(s)
    files.forEachIndexed { idx, f ->
        val dir = if (f.relDir.isEmpty()) remoteDir else f.relDir.split('/').fold(remoteDir) { acc, p -> RemoteFs.join(s, acc, p) }
        val t = Transfer(f.name, upload = true)
        b.queue(t, { RemoteFs.upload(s, f.open, f.size, f.mtime, RemoteFs.join(s, dir, f.name), t) },
            if (idx == files.lastIndex) after else null)
    }
}

private fun openRemote(s: Session, i: RemoteItem) {
    val b = App.bridge
    // Only to look at it: a temporary folder, cleaned up automatically. "הורד" saves to the downloads folder.
    val local = File(b.store.newViewDir(), i.name)
    val t = Transfer(i.name, upload = false)
    b.queue(t, { if (i.size > BIG) b.ensureFastLink(s); RemoteFs.download(s, i.path, local, t) }) { ok ->
        if (ok) android.os.Handler(android.os.Looper.getMainLooper()).post { LocalFiles.open(App.bridge.store.ctx, local) }
    }
}

private fun download(scope: CoroutineScope, s: Session, items: List<RemoteItem>, onError: (String?) -> Unit) {
    val b = App.bridge
    scope.launch(Dispatchers.IO) {
        try {
            if (items.any { it.isDir } || items.sumOf { it.size } > BIG) b.ensureFastLink(s)
            for (i in items) {
                if (i.isDir) downloadFolder(s, i, LocalFiles.unique(File(b.store.downloadDir, i.name)))
                else {
                    val t = Transfer(i.name, upload = false)
                    val local = LocalFiles.unique(File(b.store.downloadDir, i.name))
                    b.queue(t, { RemoteFs.download(s, i.path, local, t) })
                }
            }
        } catch (e: Exception) {
            withContext(Dispatchers.Main) { onError(com.btbridge.app.core.Heb.of(e)) }
        }
    }
}

private fun downloadFolder(s: Session, dir: RemoteItem, local: File) {
    val b = App.bridge
    local.mkdirs()
    for (i in RemoteFs.list(s, dir.path).items) {
        if (i.isDir) downloadFolder(s, i, File(local, i.name))
        else {
            val t = Transfer(i.name, upload = false)
            val f = File(local, i.name)
            b.queue(t, { RemoteFs.download(s, i.path, f, t) })
        }
    }
}

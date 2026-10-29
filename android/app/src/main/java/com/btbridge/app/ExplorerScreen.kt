package com.btbridge.app

import android.content.Context
import android.content.Intent
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.automirrored.filled.Sort
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Checklist
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.runtime.MutableFloatState
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.input.pointer.pointerInput
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material3.Checkbox
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.ImeAction
import com.btbridge.app.core.SearchResult
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.CreateNewFolder
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.DesktopWindows
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.DriveFileRenameOutline
import androidx.compose.material.icons.filled.DriveFolderUpload
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.SelectAll
import androidx.compose.material.icons.filled.Share
import androidx.compose.material.icons.filled.SwapVert
import androidx.compose.material.icons.filled.Upload
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.BottomAppBar
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDirection
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import com.btbridge.app.core.RemoteException
import com.btbridge.app.core.RemoteFs
import com.btbridge.app.core.RemoteItem
import com.btbridge.app.core.RemoteListing
import com.btbridge.app.core.Session
import com.btbridge.app.core.Transfer
import com.btbridge.app.core.ViewCache
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.atomic.AtomicInteger

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
    var copyTarget by remember { mutableStateOf<List<RemoteItem>?>(null) }
    /** "Mark several" mode: taps tick items instead of opening them, even before the first one is ticked. */
    var selecting by remember { mutableStateOf(false) }
    val inSelect = selecting || selected.isNotEmpty()
    /** Search: the box is open, the text, and the results (null while a folder is shown). */
    var searchOpen by remember { mutableStateOf(false) }
    var query by rememberSaveable { mutableStateOf("") }
    var results by remember(s) { mutableStateOf<SearchResult?>(null) }
    var resultsFor by remember { mutableStateOf("") }
    var savedDialog by remember { mutableStateOf(false) }
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
                results = null
                selected.clear()
            } catch (e: Exception) {
                message = com.btbridge.app.core.Heb.of(e)
            } finally {
                loading = false
            }
        }
    }

    /** Searches this folder and all its subfolders on the other device (PROTOCOL.md section 4, "search"). */
    fun search() {
        val q = query.trim()
        if (q.isEmpty()) return
        scope.launch {
            loading = true
            try {
                results = withContext(Dispatchers.IO) { RemoteFs.search(s, path, q) }
                resultsFor = q
                selected.clear()
            } catch (e: Exception) {
                message = com.btbridge.app.core.Heb.of(e)
            } finally {
                loading = false
            }
        }
    }

    /** After a change: the search results again when searching, otherwise the folder. */
    fun refresh() { if (results != null) search() else load(path, remember = false) }

    fun refreshAfter(target: String): (Boolean) -> Unit = { _ ->
        scope.launch { if (path == target) load(target, remember = false) }
    }

    LaunchedEffect(s) { load(path, remember = false) }

    /** Phone Back and the arrow at the top do the same: one step back, and only from the first folder to the device list. */
    fun goBack() {
        when {
            inSelect -> { selected.clear(); selecting = false }
            results != null -> results = null
            searchOpen -> searchOpen = false
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
                b.queue(Transfer(f.name, upload = true, s.peerFp) { ss, t -> RemoteFs.upload(ss, f.open, f.size, f.mtime, f.name, t, inbox = true) })
            }
        }
    }

    val inFolder = path.isNotEmpty()
    val inResults = results != null
    storeVersion.let { }
    val saved = b.store.savedFor(s.peerFp)
    val (sortBy, sortDesc) = b.store.sortFor(s.peerFp, path) ?: FileOrder.default(path)
    // The virtual root keeps the other side's order (quick folders, then drives).
    val folderItems = listing?.items.orEmpty().let { if (inFolder) FileOrder.sort(it, sortBy, sortDesc) else it }
    // Rows on screen, each with "is it a saved place": search results, or the folder.
    // The list of places starts with the saved places on this device.
    val rows: List<Pair<RemoteItem, Boolean>> = results?.items?.map { it to false }
        ?: if (inFolder) folderItems.map { it to false }
        else saved.map { RemoteItem(it.name, it.path, it.isDir, 0, 0) to true } + folderItems.map { it to false }
    val items = rows.map { it.first }
    val chosen = items.filter { it.path in selected }.distinctBy { it.path }
    // Renaming and deleting need a real folder (or search results), not the list of places.
    val canEdit = inFolder || inResults
    var sortMenu by remember { mutableStateOf(false) }

    fun downloadChosen(dest: File, label: String) {
        val list = chosen
        selected.clear()
        selecting = false
        Toast.makeText(ctx, "יורד אל: $label", Toast.LENGTH_SHORT).show()
        download(scope, s, list, dest) { message = it }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text(when {
                            inSelect -> "נבחרו ${selected.size}"
                            inResults -> "חיפוש: $resultsFor"
                            else -> b.displayName(s)
                        }, maxLines = 1)
                        Text(live?.linkText ?: "מנותק", style = MaterialTheme.typography.bodySmall)
                    }
                },
                navigationIcon = {
                    IconButton(onClick = { goBack() }) {
                        Icon(if (inSelect) Icons.Filled.Close else Icons.AutoMirrored.Filled.ArrowBack, "חזור")
                    }
                },
                actions = {
                    if (inSelect) {
                        IconButton(onClick = {
                            val all = items.map { it.path }.distinct()
                            if (selected.size == all.size) selected.clear() else { selected.clear(); selected.addAll(all) }
                        }) { Icon(Icons.Filled.SelectAll, "בחר הכל") }
                    } else {
                        IconButton(onClick = { searchOpen = !searchOpen }) { Icon(Icons.Filled.Search, "חיפוש") }
                        if (s.peerOs == "windows") IconButton(onClick = onRemote) { Icon(Icons.Filled.DesktopWindows, "מסך המחשב") }
                        IconButton(onClick = onTransfers) { Icon(Icons.Filled.SwapVert, "העברות") }
                        IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, "עוד") }
                        DropdownMenu(menu, { menu = false }) {
                            DropdownMenuItem(text = { Text("רענן") }, leadingIcon = { Icon(Icons.Filled.Refresh, null) },
                                onClick = { menu = false; refresh() })
                            DropdownMenuItem(text = { Text("בחר כמה פריטים") }, leadingIcon = { Icon(Icons.Filled.Checklist, null) },
                                onClick = { menu = false; selecting = true })
                            DropdownMenuItem(text = { Text("שמורים…") }, leadingIcon = { Icon(Icons.Filled.Star, null) },
                                onClick = { menu = false; savedDialog = true })
                            if (inFolder && !inResults) {
                                val here = b.store.isSaved(s.peerFp, path)
                                DropdownMenuItem(
                                    text = { Text(if (here) "הסר את התיקייה הזו מהשמורים" else "שמור את התיקייה הזו") },
                                    leadingIcon = { Icon(if (here) Icons.Filled.StarBorder else Icons.Filled.Star, null) },
                                    onClick = {
                                        menu = false
                                        if (here) b.store.removeSaved(s.peerFp, path)
                                        else b.store.addSaved(s.peerFp, SavedPlace(folderName(path), path, true))
                                    })
                            }
                            listing?.parent?.let { up ->
                                DropdownMenuItem(text = { Text("לתיקייה שמעל") }, leadingIcon = { Icon(Icons.Filled.ArrowUpward, null) },
                                    onClick = { menu = false; load(up) })
                            }
                            DropdownMenuItem(text = { Text("שלח קבצים להורדות של ${b.displayName(s)}") }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.Send, null) },
                                onClick = { menu = false; pickInbox.launch(arrayOf("*/*")) })
                            if (s.peerOs == "windows") DropdownMenuItem(text = { Text("שליטה במסך המחשב") }, leadingIcon = { Icon(Icons.Filled.DesktopWindows, null) },
                                onClick = { menu = false; onRemote() })
                            HorizontalDivider()
                            DropdownMenuItem(text = { Text("העלה קבצים לתיקייה הזו…") }, leadingIcon = { Icon(Icons.Filled.Upload, null) }, enabled = inFolder,
                                onClick = { menu = false; pickFiles.launch(arrayOf("*/*")) })
                            DropdownMenuItem(text = { Text("העלה תיקייה לכאן…") }, leadingIcon = { Icon(Icons.Filled.DriveFolderUpload, null) }, enabled = inFolder,
                                onClick = { menu = false; pickFolder.launch(null) })
                            DropdownMenuItem(text = { Text("תיקייה חדשה") }, leadingIcon = { Icon(Icons.Filled.CreateNewFolder, null) }, enabled = inFolder,
                                onClick = { menu = false; newFolder = true })
                        }
                    }
                },
            )
        },
        bottomBar = {
            // With a selection, the actions are spelled out at the bottom, where the thumb is.
            if (selected.isNotEmpty()) BottomAppBar {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly) {
                    BarAction(Icons.Filled.Download, "הורד") { downloadChosen(b.store.downloadDir, b.store.downloadLabel) }
                    BarAction(Icons.Filled.ContentCopy, "העתק לטלפון") { copyTarget = chosen }
                    if (chosen.none { it.isDir }) BarAction(Icons.Filled.Share, "שתף") {
                        val list = chosen
                        selected.clear()
                        selecting = false
                        share(ctx, s, list) { message = it }
                    }
                    if (chosen.any { !b.store.isSaved(s.peerFp, it.path) }) BarAction(Icons.Filled.Star, "שמור") {
                        chosen.forEach { b.store.addSaved(s.peerFp, SavedPlace(it.name, it.path, it.isDir)) }
                        selected.clear()
                        selecting = false
                        Toast.makeText(ctx, "נשמר. אפשר לחזור אליו מרשימת המקומות או מ\"שמורים\".", Toast.LENGTH_SHORT).show()
                    }
                    if (selected.size == 1 && canEdit) BarAction(Icons.Filled.DriveFileRenameOutline, "שנה שם") {
                        renaming = items.firstOrNull { it.path == selected[0] }
                    }
                    if (canEdit) BarAction(Icons.Filled.Delete, "מחק") { confirmDelete = true }
                }
            }
        },
    ) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            if (path.isNotEmpty()) Text(path, Modifier.fillMaxWidth().padding(16.dp, 4.dp, 16.dp, 0.dp),
                style = MaterialTheme.typography.bodySmall.copy(textDirection = TextDirection.Ltr), maxLines = 2)
            if (searchOpen || inResults) Row(Modifier.fillMaxWidth().padding(12.dp, 4.dp), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(query, { query = it }, Modifier.weight(1f), singleLine = true,
                    placeholder = { Text(if (inFolder) "חיפוש בתיקייה הזו ובתתי-התיקיות" else "חיפוש בכל התיקיות של המשתמש") },
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                    keyboardActions = KeyboardActions(onSearch = { search() }))
                IconButton(onClick = { search() }, enabled = query.isNotBlank()) { Icon(Icons.Filled.Search, "חפש") }
            }
            results?.let { r ->
                Text("נמצאו ${r.items.size}" + (if (r.more) ". מוצגות רק הראשונות. כדאי לחפש בתיקייה מצומצמת יותר." else ".") +
                    " \"חזור\" חוזר לתיקייה.", Modifier.padding(16.dp, 0.dp, 16.dp, 4.dp),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (selecting && selected.isEmpty()) Text("הקש על הפריטים שאתה רוצה לסמן.", Modifier.padding(16.dp, 0.dp, 16.dp, 4.dp),
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
            if (inFolder && !inResults) Box(Modifier.padding(horizontal = 8.dp)) {
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
            if (!inSelect && !inResults && inFolder && items.any { !it.isDir })
                Text("הקשה פותחת. לחיצה ארוכה (או \"בחר כמה פריטים\" בתפריט) מסמנת, ואז אפשר להוריד, להעתיק לתיקייה בטלפון, לשתף או לשמור.",
                    Modifier.padding(16.dp, 0.dp, 16.dp, 4.dp), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (!loading && items.isEmpty() && (listing != null || inResults))
                Text(if (inResults) "לא נמצא שום דבר עם \"$resultsFor\" בשם." else "התיקייה ריקה.", Modifier.padding(16.dp))
            val listState = rememberLazyListState()
            val autoScroll = remember { mutableFloatStateOf(0f) }
            // The drag handler lives across recompositions, so it reads the rows through this, always up to date.
            val rowKeys by rememberUpdatedState(rows.map { it.first.path })
            LaunchedEffect(autoScroll.floatValue) {
                if (autoScroll.floatValue == 0f) return@LaunchedEffect
                while (isActive) { listState.scrollBy(autoScroll.floatValue); delay(16) }
            }
            LazyColumn(
                Modifier.weight(1f).dragToSelect(listState, { rowKeys }, selected, autoScroll),
                state = listState,
            ) {
                items(rows, key = { (i, isSaved) -> (if (isSaved) "★" else "") + i.path }) { (i, isSaved) ->
                    val isSel = i.path in selected
                    ListItem(
                        headlineContent = { Text(i.name, maxLines = 2) },
                        supportingContent = {
                            val parts = listOfNotNull(
                                if (isSaved) "שמור" else null,
                                if (!i.isDir && !isSaved) Transfer.formatSize(i.size) else null,
                                if (i.mtime > 0) dateFmt.format(Date(i.mtime)) else null,
                            )
                            Column {
                                if (parts.isNotEmpty()) Text(parts.joinToString(" · "))
                                // In search results: where it is.
                                if (inResults && i.folder.isNotEmpty()) Text(i.folder, maxLines = 1,
                                    style = MaterialTheme.typography.bodySmall.copy(textDirection = TextDirection.Ltr),
                                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        },
                        leadingContent = {
                            if (inSelect) Checkbox(isSel, { on -> if (on) selected.add(i.path) else selected.remove(i.path) })
                            else Icon(when {
                                isSaved -> Icons.Filled.Star
                                i.isDir -> Icons.Filled.Folder
                                else -> Icons.AutoMirrored.Filled.InsertDriveFile
                            }, null, tint = if (i.isDir || isSaved) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                        },
                        colors = if (isSel) ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.secondaryContainer) else ListItemDefaults.colors(),
                        modifier = Modifier.combinedClick(
                            onClick = {
                                when {
                                    inSelect -> if (isSel) selected.remove(i.path) else selected.add(i.path)
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
                // In search results the item may sit in a subfolder: rename it where it is.
                try { withContext(Dispatchers.IO) { RemoteFs.rename(s, item.path, RemoteFs.join(s, item.folder.ifEmpty { path }, name)) }; refresh() }
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
                        selecting = false
                        refresh()
                    }
                }) { Text("מחק") }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("ביטול") } },
        )
    }
    copyTarget?.let { list ->
        LocalFolderPicker(onDismiss = { copyTarget = null }) { dir ->
            copyTarget = null
            selected.removeAll(list.map { it.path })
            selecting = false
            Toast.makeText(ctx, "מעתיק אל: ${friendlyPath(dir.path)}", Toast.LENGTH_SHORT).show()
            download(scope, s, list, dir) { message = it }
        }
    }
    if (savedDialog) AlertDialog(
        onDismissRequest = { savedDialog = false },
        title = { Text("שמורים ב${b.displayName(s)}") },
        text = {
            if (saved.isEmpty()) Text("עוד אין שמורים. אפשר לשמור את התיקייה הנוכחית מהתפריט, או לסמן פריטים וללחוץ \"שמור\".")
            else LazyColumn(Modifier.heightIn(max = 400.dp)) {
                items(saved, key = { it.path }) { p ->
                    ListItem(
                        headlineContent = { Text(p.name) },
                        supportingContent = { Text(p.path, maxLines = 1, style = MaterialTheme.typography.bodySmall.copy(textDirection = TextDirection.Ltr)) },
                        leadingContent = { Icon(if (p.isDir) Icons.Filled.Folder else Icons.AutoMirrored.Filled.InsertDriveFile, null) },
                        trailingContent = { IconButton(onClick = { b.store.removeSaved(s.peerFp, p.path) }) { Icon(Icons.Filled.Close, "הסר מהשמורים") } },
                        modifier = Modifier.clickable {
                            savedDialog = false
                            if (p.isDir) load(p.path) else openRemote(s, RemoteItem(p.name, p.path, false, 0, 0))
                        },
                    )
                }
            }
        },
        confirmButton = { TextButton(onClick = { savedDialog = false }) { Text("סגור") } },
    )
    message?.let { MessageDialog(it) { message = null } }
}

/** The last part of a path ("C:\\Users\\x\\Documents" → "Documents"). */
private fun folderName(path: String): String {
    val t = path.trimEnd('\\', '/')
    val cut = t.lastIndexOfAny(charArrayOf('\\', '/'))
    return if (cut >= 0 && cut < t.length - 1) t.substring(cut + 1) else t
}

/**
 * Long press, then drag the finger: every row it passes over is selected, as in the phone's gallery.
 * Rows that were selected before stay selected. Near the top or bottom edge the list scrolls by itself.
 */
private fun Modifier.dragToSelect(
    state: LazyListState,
    keys: () -> List<String>,
    selected: SnapshotStateList<String>,
    autoScroll: MutableFloatState,
): Modifier = pointerInput(Unit) {
    fun indexAt(y: Float): Int? = state.layoutInfo.visibleItemsInfo.firstOrNull { y.toInt() in it.offset until it.offset + it.size }?.index
    var anchor: Int? = null
    var last: Int? = null
    var before: Set<String> = emptySet()
    fun apply(a: Int, b: Int) {
        val k = keys()
        val want = before + (minOf(a, b)..maxOf(a, b)).mapNotNull { k.getOrNull(it) }
        selected.removeAll { it !in want }
        want.forEach { if (it !in selected) selected.add(it) }
    }
    val edge = 56.dp.toPx()
    detectDragGesturesAfterLongPress(
        onDragStart = { pos ->
            indexAt(pos.y)?.let { i ->
                before = selected.toSet()
                anchor = i
                last = i
                apply(i, i)
            }
        },
        onDragEnd = { anchor = null; autoScroll.floatValue = 0f },
        onDragCancel = { anchor = null; autoScroll.floatValue = 0f },
        onDrag = { change, _ ->
            val a = anchor ?: return@detectDragGesturesAfterLongPress
            val y = change.position.y
            autoScroll.floatValue = when {
                y > size.height - edge -> (y - (size.height - edge)) / 3f
                y < edge -> -(edge - y) / 3f
                else -> 0f
            }
            indexAt(y)?.let { i -> if (i != last) { last = i; apply(a, i) } }
        },
    )
}

@Composable
private fun BarAction(icon: ImageVector, label: String, onClick: () -> Unit) {
    Column(
        Modifier.clickable(onClick = onClick).padding(horizontal = 6.dp, vertical = 4.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(icon, null)
        Text(label, style = MaterialTheme.typography.labelSmall, textAlign = TextAlign.Center)
    }
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

/**
 * Chooses a folder on this phone (for "copy to phone"). Needs "all files access" to see everything;
 * without it only the app's own downloads folder is offered.
 */
@Composable
private fun LocalFolderPicker(onDismiss: () -> Unit, onPick: (File) -> Unit) {
    val b = App.bridge
    var dir by remember { mutableStateOf<File?>(null) }
    var newName by remember { mutableStateOf<String?>(null) }
    var version by remember { mutableStateOf(0) }
    val roots = remember { if (allFilesGranted()) b.roots().map { it.name to it.dir } else listOf(b.store.downloadLabel to b.store.downloadDir) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("לאיזו תיקייה בטלפון?") },
        text = {
            Column {
                Text(dir?.path ?: "בחר מקום", style = MaterialTheme.typography.bodySmall.copy(textDirection = TextDirection.Ltr), maxLines = 2)
                if (!allFilesGranted()) Text("כדי לבחור כל תיקייה בטלפון, אשר \"גישה לכל הקבצים\" בהגדרות.",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
                version.let { }
                // Each row: its label and where it leads (null = back to the list of places).
                val entries: List<Pair<String, File?>> = dir?.let { d ->
                    val atRoot = roots.any { it.second == d }
                    listOf((if (atRoot) "⬆ לרשימת המקומות" else "⬆ לתיקייה שמעל") to (if (atRoot) null else d.parentFile)) +
                        (d.listFiles()?.filter { it.isDirectory && !it.name.startsWith(".") }?.sortedBy { it.name.lowercase() }
                            ?.map { it.name to it }.orEmpty())
                } ?: roots
                LazyColumn(Modifier.heightIn(max = 360.dp)) {
                    items(entries, key = { it.first + "|" + it.second?.path }) { (name, f) ->
                        ListItem(
                            headlineContent = { Text(name) },
                            leadingContent = { Icon(if (name.startsWith("⬆")) Icons.Filled.ArrowUpward else Icons.Filled.Folder, null,
                                tint = MaterialTheme.colorScheme.primary) },
                            modifier = Modifier.clickable { dir = f },
                        )
                    }
                }
                if (dir != null) TextButton(onClick = { newName = "תיקייה חדשה" }) {
                    Icon(Icons.Filled.CreateNewFolder, null)
                    Text("  תיקייה חדשה כאן")
                }
            }
        },
        confirmButton = { TextButton(enabled = dir != null, onClick = { dir?.let(onPick) }) { Text("העתק לכאן") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("ביטול") } },
    )
    newName?.let { initial ->
        NameDialog("תיקייה חדשה", initial, onDismiss = { newName = null }) { name ->
            newName = null
            val d = File(dir, name)
            if (d.mkdirs() || d.isDirectory) { dir = d; version++ }
        }
    }
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
        val remote = RemoteFs.join(s, dir, f.name)
        b.queue(Transfer(f.name, upload = true, s.peerFp) { ss, t -> RemoteFs.upload(ss, f.open, f.size, f.mtime, remote, t) },
            if (idx == files.lastIndex) after else null)
    }
}

/**
 * Brings one remote file to [local]. When the file was already opened for viewing (same size and date),
 * the local copy is used instead of the network.
 */
private fun fetch(peerFp: String, i: RemoteItem, local: File): Transfer =
    Transfer(i.name, upload = false, peerFp) { ss, t ->
        val cached = ViewCache.find(ss.peerFp, i)
        if (cached != null && cached.path != local.path) {
            t.linkKind = "מהעותק שכבר נפתח"
            t.total = i.size
            local.parentFile?.mkdirs()
            cached.inputStream().use { input ->
                local.outputStream().use { out ->
                    val buf = ByteArray(256 * 1024)
                    while (true) {
                        t.cancel.throwIfCancelled()
                        val n = input.read(buf)
                        if (n < 0) break
                        out.write(buf, 0, n)
                        t.add(n)
                    }
                }
            }
            if (i.mtime > 0) local.setLastModified(i.mtime)
            t.resultPath = local.path
        } else {
            if (i.size > BIG) App.bridge.ensureFastLink(ss)
            RemoteFs.download(ss, i.path, local, t, i.size, i.mtime)
        }
    }

private fun openRemote(s: Session, i: RemoteItem) {
    val b = App.bridge
    ViewCache.find(s.peerFp, i)?.let { LocalFiles.open(b.store.ctx, it); return }
    // Only to look at it: a temporary folder, cleaned up automatically. "הורד" saves to the downloads folder.
    val local = File(b.store.newViewDir(), i.name)
    b.queue(fetch(s.peerFp, i, local)) { ok ->
        if (!ok) return@queue
        ViewCache.add(s.peerFp, i, local)
        android.os.Handler(android.os.Looper.getMainLooper()).post { LocalFiles.open(b.store.ctx, local) }
    }
}

/** Downloads files and folders into [dest] (the downloads folder, or a folder the user chose on the phone). */
private fun download(scope: CoroutineScope, s: Session, items: List<RemoteItem>, dest: File, onError: (String?) -> Unit) {
    val b = App.bridge
    scope.launch(Dispatchers.IO) {
        try {
            if (items.any { it.isDir } || items.sumOf { it.size } > BIG) b.ensureFastLink(s)
            for (i in items) {
                if (i.isDir) downloadFolder(s, i, LocalFiles.unique(File(dest, i.name)))
                else b.queue(fetch(s.peerFp, i, LocalFiles.unique(File(dest, i.name))))
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
        else b.queue(fetch(s.peerFp, i, File(local, i.name)))
    }
}

/** "Share": brings the files (or takes the copies already opened) and hands them to another app. */
private fun share(ctx: Context, s: Session, items: List<RemoteItem>, onError: (String) -> Unit) {
    val b = App.bridge
    val files = arrayOfNulls<File>(items.size)
    val left = AtomicInteger(items.size)
    val failed = AtomicInteger(0)
    fun finished() {
        if (left.decrementAndGet() > 0) return
        android.os.Handler(android.os.Looper.getMainLooper()).post {
            if (failed.get() > 0) { onError("חלק מהקבצים לא הגיעו, ולכן השיתוף בוטל."); return@post }
            val uris = ArrayList(files.filterNotNull().map { FileProvider.getUriForFile(ctx, ctx.packageName + ".files", it) })
            val send = if (uris.size == 1) Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM, uris[0])
            else Intent(Intent.ACTION_SEND_MULTIPLE).putParcelableArrayListExtra(Intent.EXTRA_STREAM, uris)
            send.type = "*/*"
            send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            runCatching { ctx.startActivity(Intent.createChooser(send, "שתף באמצעות").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
                .onFailure { onError("אין אפליקציה לשיתוף") }
        }
    }
    items.forEachIndexed { idx, i ->
        val cached = ViewCache.find(s.peerFp, i)
        if (cached != null) { files[idx] = cached; finished(); return@forEachIndexed }
        val local = File(b.store.newViewDir(), i.name)
        b.queue(fetch(s.peerFp, i, local)) { ok ->
            if (ok) { files[idx] = local; ViewCache.add(s.peerFp, i, local) } else failed.incrementAndGet()
            finished()
        }
    }
}

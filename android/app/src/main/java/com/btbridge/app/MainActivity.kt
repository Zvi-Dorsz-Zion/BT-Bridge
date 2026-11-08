package com.btbridge.app

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.location.LocationManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Bluetooth
import androidx.compose.material.icons.automirrored.filled.BluetoothSearching
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.SwapVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import com.btbridge.app.core.Session
import com.btbridge.app.core.Transfer
import androidx.core.content.ContextCompat
import androidx.core.location.LocationManagerCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {
    /** Bumped in onResume, so screens re-check permissions after returning from system settings. */
    private val resumed = mutableIntStateOf(0)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        BridgeService.start(this)
        setContent {
            val dark = isSystemInDarkTheme()
            MaterialTheme(colorScheme = if (dark) darkColorScheme(primary = Color(0xFF60A5FA)) else lightColorScheme(primary = Color(0xFF2563EB))) {
                CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Rtl) {
                    AppRoot(resumed.intValue)
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        resumed.intValue++
        // Opening the app stops "ring the phone".
        Ring.stop(this)
    }

    /** Android lets the app read the clipboard only while it has focus: send what was copied to the computer now. */
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || !App.bridge.store.shareClipboard) return
        val text = ClipSend.current(this) ?: return
        App.bridge.runInBackground { App.bridge.shareClipboard(text) }
    }
}

private sealed interface Screen {
    data object Home : Screen
    data class Explorer(val fp: String) : Screen
    /** The computer's whole screen, or (with [target]) one of its programs or the separate browser. */
    data class Remote(val fp: String, val target: RemoteTarget? = null) : Screen
    /** "Programs on the computer": pick one program or the separate browser. */
    data class Apps(val fp: String) : Screen
    data object Transfers : Screen
    data object Settings : Screen
    data object Scan : Screen
    data object NoAppSend : Screen
}

@Composable
private fun AppRoot(resumed: Int) {
    val b = App.bridge
    val storeVersion by b.store.version.collectAsState()
    if (storeVersion >= 0 && !b.store.hasPassword) {
        SetPasswordScreen(firstRun = true, onDone = {})
        return
    }
    var screen by remember { mutableStateOf<Screen>(Screen.Home) }
    val sessions by b.sessions.collectAsState()

    when (val s = screen) {
        Screen.Home -> HomeScreen(resumed, onOpen = { screen = Screen.Explorer(it.peerFp) },
            onTransfers = { screen = Screen.Transfers }, onSettings = { screen = Screen.Settings }, onScan = { screen = Screen.Scan },
            onNoApp = { screen = Screen.NoAppSend })
        is Screen.Explorer -> {
            val session = sessions.firstOrNull { it.peerFp == s.fp }
            if (session == null) LaunchedEffect(Unit) { screen = Screen.Home }
            else ExplorerScreen(session, onBack = { screen = Screen.Home }, onTransfers = { screen = Screen.Transfers },
                onRemote = { screen = Screen.Remote(s.fp) }, onApps = { screen = Screen.Apps(s.fp) })
        }
        is Screen.Remote -> {
            val session = sessions.firstOrNull { it.peerFp == s.fp }
            if (session == null) LaunchedEffect(Unit) { screen = Screen.Home }
            else RemoteScreen(session, s.target, onClose = { screen = if (s.target != null) Screen.Apps(s.fp) else Screen.Explorer(s.fp) })
        }
        is Screen.Apps -> {
            val session = sessions.firstOrNull { it.peerFp == s.fp }
            if (session == null) LaunchedEffect(Unit) { screen = Screen.Home }
            else AppsScreen(session, onBack = { screen = Screen.Explorer(s.fp) }, onOpen = { screen = Screen.Remote(s.fp, it) })
        }
        Screen.Transfers -> TransfersScreen(onBack = { screen = Screen.Home })
        Screen.Settings -> SettingsScreen(resumed, onBack = { screen = Screen.Home })
        Screen.Scan -> ScanScreen(onBack = { screen = Screen.Home }, onConnected = { screen = Screen.Explorer(it.peerFp) })
        Screen.NoAppSend -> NoAppSendScreen(onBack = { screen = Screen.Home })
    }
    PasswordPrompt()
}

// ---------------------------------------------------------------------- permissions

/** Before Android 6.0 every permission is given at install, and this says so. */
private fun granted(ctx: Context, p: String) = ContextCompat.checkSelfPermission(ctx, p) == PackageManager.PERMISSION_GRANTED

/** Access to all the phone's files ([AndroidStore.allFiles]). */
fun allFilesGranted() = App.bridge.store.allFiles

/** Asks for [allFilesGranted]: a settings page on Android 11+, the storage permission ([askStorage]) before it. */
fun askAllFiles(ctx: Context, askStorage: () -> Unit) {
    if (Build.VERSION.SDK_INT >= 30) ctx.startActivity(Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:" + ctx.packageName)))
    else askStorage()
}

/**
 * A launcher for one runtime permission. When Android no longer shows its question (refused for good before, so the
 * answer comes back at once), [fallback] opens the settings page where it can still be allowed.
 */
@Composable
private fun rememberPermissionAsk(permission: String, fallback: (Context) -> Unit): () -> Unit {
    val ctx = LocalContext.current
    val askedAt = remember { longArrayOf(0) }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok ->
        if (!ok && System.currentTimeMillis() - askedAt[0] < 400) runCatching { fallback(ctx) }
    }
    return {
        askedAt[0] = System.currentTimeMillis()
        launcher.launch(permission)
    }
}

private fun openAppDetails(ctx: Context) =
    ctx.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + ctx.packageName)))

/** Android 10 and older: the storage permission, asked at runtime from 6.0 (the manifest has it up to 10). */
@Composable
private fun rememberStorageAsk(): () -> Unit = rememberPermissionAsk(Manifest.permission.WRITE_EXTERNAL_STORAGE, ::openAppDetails)

fun locationOn(ctx: Context): Boolean =
    ctx.service<LocationManager>()?.let { LocationManagerCompat.isLocationEnabled(it) } == true

/** Android 6.0+ can stop apps in the background to save battery; before it nothing needs allowing. */
fun batteryUnrestricted(ctx: Context): Boolean =
    Build.VERSION.SDK_INT < 23 || ctx.service<PowerManager>()?.isIgnoringBatteryOptimizations(ctx.packageName) == true

/** Android 10-11 need location for Bluetooth search and for the direct network; 12+ have their own permissions. */
fun nearbyPermissions(): Array<String> = when {
    Build.VERSION.SDK_INT >= 33 -> arrayOf(Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_CONNECT, Manifest.permission.NEARBY_WIFI_DEVICES)
    // Android 12 asks for the precise location only together with the approximate one.
    Build.VERSION.SDK_INT >= 31 -> arrayOf(Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_CONNECT,
        Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION)
    else -> arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)
}

fun nearbyGranted(ctx: Context) = nearbyPermissions().all { granted(ctx, it) }

// ---------------------------------------------------------------------- home

private class DeviceRow(val key: String, val fp: String, val name: String, val device: BluetoothDevice?, val session: Session?, val trusted: Boolean)

@OptIn(ExperimentalMaterial3Api::class)
@SuppressLint("MissingPermission")
@Composable
private fun HomeScreen(resumed: Int, onOpen: (Session) -> Unit, onTransfers: () -> Unit, onSettings: () -> Unit, onScan: () -> Unit, onNoApp: () -> Unit) {
    val b = App.bridge
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val sessions by b.sessions.collectAsState()
    val status by b.listenStatus.collectAsState()
    val storeVersion by b.store.version.collectAsState()
    val connecting = remember { mutableStateListOf<String>() }
    var message by remember { mutableStateOf<String?>(null) }
    var menuFor by remember { mutableStateOf<DeviceRow?>(null) }
    var renaming by remember { mutableStateOf<DeviceRow?>(null) }
    var removing by remember { mutableStateOf<RemoveTarget?>(null) }
    var showOthers by remember { mutableStateOf(false) }

    val btPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { }
    val notifPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    val askStorage = rememberStorageAsk()
    LaunchedEffect(Unit) {
        // Asked once here; later only from the permissions page in the settings.
        if (Build.VERSION.SDK_INT >= 33 && !b.store.notificationsAsked && !granted(ctx, Manifest.permission.POST_NOTIFICATIONS)) {
            b.store.notificationsAsked = true
            notifPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    val (mine, others) = remember(sessions, storeVersion, resumed, status) {
        val mine = mutableListOf<DeviceRow>()
        val seen = HashSet<String>()
        val bonded = if (b.hasBtPermission()) runCatching { b.adapter()?.bondedDevices?.toList() }.getOrNull().orEmpty() else emptyList()
        for (s in sessions) {
            mine += DeviceRow(s.peerAddress, s.peerFp, b.displayName(s), bonded.firstOrNull { Bridge.key(it.address) == s.peerAddress }, s, true)
            seen += s.peerAddress
        }
        for (t in b.store.trusted()) if (seen.add(t.address)) {
            mine += DeviceRow(t.address, t.fp, b.store.nameFor(t.fp, t.name), bonded.firstOrNull { Bridge.key(it.address) == t.address }, null, true)
        }
        val others = bonded.mapNotNull { d ->
            val k = Bridge.key(d.address)
            if (seen.add(k)) DeviceRow(k, "", b.store.nameFor(k, d.name ?: k), d, null, false) else null
        }
        mine to others
    }

    fun connect(r: DeviceRow) {
        if (r.device == null || r.key in connecting) return
        connecting += r.key
        scope.launch {
            try {
                val s = withContext(Dispatchers.IO) { b.connect(r.device) }
                onOpen(s)
            } catch (e: java.util.concurrent.CancellationException) {
            } catch (e: Exception) {
                message = com.btbridge.app.core.Heb.of(e)
            } finally {
                connecting -= r.key
            }
        }
    }

    @Composable
    fun DeviceRowItem(r: DeviceRow) {
        val busy = r.key in connecting
        ListItem(
            headlineContent = { Text(r.name, fontWeight = FontWeight.SemiBold) },
            supportingContent = {
                Text(
                    when {
                        r.session != null -> "מחובר · " + r.session.linkText
                        busy -> "מתחבר…"
                        r.trusted -> "מוכר · לא מחובר"
                        else -> "מזווג · הקש כדי להתחבר"
                    },
                    color = if (r.session != null) Color(0xFF16A34A) else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            },
            leadingContent = { Icon(if (r.trusted) Icons.Filled.Computer else Icons.Filled.Bluetooth, null) },
            trailingContent = { if (busy) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp) },
            modifier = Modifier.combinedClick(
                onClick = { if (r.session != null) onOpen(r.session) else connect(r) },
                onLongClick = { menuFor = r },
            ),
        )
        HorizontalDivider()
    }

    // Optional permissions that are missing, by name. The user can hide the reminder; it comes back only for a new one.
    val missing = remember(resumed, storeVersion) { missingOptional(ctx) }
    val showMissing = missing.isNotEmpty() && !b.store.dismissedPermissions.containsAll(missing)
    var identityNotice by remember { mutableStateOf(b.store.identityWasReset) }

    Scaffold(topBar = {
        TopAppBar(title = { Text("גשר בלוטוס") }, actions = {
            IconButton(onClick = onTransfers) { Icon(Icons.Filled.SwapVert, "העברות") }
            IconButton(onClick = onSettings) { Icon(Icons.Filled.Settings, "הגדרות") }
            var barMenu by remember { mutableStateOf(false) }
            Box {
                IconButton(onClick = { barMenu = true }) { Icon(Icons.Filled.MoreVert, "עוד") }
                androidx.compose.material3.DropdownMenu(barMenu, { barMenu = false }) {
                    androidx.compose.material3.DropdownMenuItem(text = { Text("שלח למכשיר בלי התוכנה") }, onClick = { barMenu = false; onNoApp() })
                }
            }
        })
    }) { pad ->
        LazyColumn(Modifier.padding(pad).fillMaxSize()) {
            item { Text(status, Modifier.padding(16.dp, 4.dp), color = MaterialTheme.colorScheme.onSurfaceVariant) }
            if (!b.hasBtPermission()) item {
                Notice("צריך לאשר הרשאת בלוטוס.", "אשר") { btPermission.launch(arrayOf(Manifest.permission.BLUETOOTH_CONNECT)) }
            }
            if (b.hasBtPermission() && b.adapter()?.isEnabled == false) item {
                Notice("הבלוטוס כבוי.", "הדלק") { ctx.startActivity(Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE)) }
            }
            if (!allFilesGranted()) item {
                Notice("כדי שהמחשב יראה את כל הקבצים בטלפון, ושההורדות יישמרו בתיקיית ההורדות, צריך לאשר \"גישה לכל הקבצים\".", "אשר") {
                    askAllFiles(ctx, askStorage)
                }
            }
            if (showMissing) item {
                Notice("עוד לא אושר: ${missing.joinToString(", ")}.", "פרטים", secondary = "אל תזכיר",
                    onSecondary = { b.store.dismissedPermissions = missing.toSet() }, onClick = onSettings)
            }
            item { Text(if (mine.isEmpty()) "עוד לא התחברת לאף מכשיר" else "המכשירים שלי", Modifier.padding(16.dp, 12.dp, 16.dp, 4.dp), fontWeight = FontWeight.SemiBold) }
            items(mine, key = { "m" + it.key }) { DeviceRowItem(it) }
            if (others.isNotEmpty()) {
                val show = showOthers || mine.isEmpty()
                item {
                    Row(Modifier.fillMaxWidth().padding(16.dp, 12.dp, 8.dp, 0.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text("מזווגים נוספים (${others.size})", Modifier.weight(1f), fontWeight = FontWeight.SemiBold,
                            color = MaterialTheme.colorScheme.onSurfaceVariant)
                        if (mine.isNotEmpty()) TextButton(onClick = { showOthers = !showOthers }) { Text(if (show) "הסתר" else "הצג") }
                    }
                }
                if (show) items(others, key = { "o" + it.key }) { DeviceRowItem(it) }
            }
            item {
                OutlinedButton(onClick = onScan, Modifier.padding(16.dp)) {
                    Icon(Icons.AutoMirrored.Filled.BluetoothSearching, null)
                    Spacer(Modifier.width(6.dp))
                    Text("חפש וצמד מכשיר חדש")
                }
            }
        }
    }

    menuFor?.let { r ->
        AlertDialog(
            onDismissRequest = { menuFor = null },
            title = { Text(r.name) },
            text = {
                Column {
                    if (r.session == null && r.device != null) TextButton(onClick = { menuFor = null; connect(r) }) { Text("התחבר") }
                    if (r.session != null) TextButton(onClick = { r.session.close(); menuFor = null }) { Text("נתק") }
                    TextButton(onClick = { renaming = r; menuFor = null }) { Text("שנה כינוי") }
                    if (r.trusted) TextButton(onClick = {
                        r.session?.close()
                        b.store.forget(r.fp)
                        menuFor = null
                    }) { Text("שכח מכשיר (בפעם הבאה תידרש סיסמה)") }
                    TextButton(onClick = {
                        removing = RemoveTarget(r.fp, r.key, r.name, r.device != null)
                        menuFor = null
                    }) { Text("הסר מכשיר מהרשימה…") }
                }
            },
            confirmButton = { TextButton(onClick = { menuFor = null }) { Text("סגור") } },
        )
    }
    renaming?.let { r ->
        var v by remember(r) { mutableStateOf(r.name) }
        AlertDialog(
            onDismissRequest = { renaming = null },
            title = { Text("כינוי למכשיר") },
            text = {
                Column {
                    Text("שם שיוצג רק אצלך. ריק = השם המקורי.", style = MaterialTheme.typography.bodySmall)
                    OutlinedTextField(v, { v = it }, singleLine = true)
                }
            },
            confirmButton = { TextButton(onClick = { b.store.setNickname(r.fp.ifEmpty { r.key }, v); renaming = null }) { Text("שמור") } },
            dismissButton = { TextButton(onClick = { renaming = null }) { Text("ביטול") } },
        )
    }
    removing?.let { RemoveDeviceDialogs(it) { removing = null } }
    message?.let { MessageDialog(it) { message = null } }
    if (identityNotice) MessageDialog("המפתח של הטלפון לא נטען, ולכן נוצר מפתח חדש. המכשירים המוכרים יבקשו את הסיסמה פעם אחת.") {
        identityNotice = false
        b.store.identityWasReset = false
    }
}

/** Optional permissions that are missing, in words for the user. */
private fun missingOptional(ctx: Context): List<String> {
    val b = App.bridge
    val out = mutableListOf<String>()
    // Control from the computer needs Android 7.0, the direct network 8.0: on older phones there is nothing to allow.
    if (BridgeAccessibility.gesturesSupported && !BridgeAccessibility.isEnabled(ctx)) out += "שליטה מהמחשב"
    if (DirectHotspot.supported && b.store.directMode != "off" && !nearbyGranted(ctx)) out += "מיקום (לרשת ישירה)"
    if (!batteryUnrestricted(ctx)) out += "פעולה ברקע"
    if (b.store.forwardNotifications && !BridgeNotifications.isEnabled(ctx)) out += "התראות הטלפון במחשב"
    return out
}

@Composable
private fun Notice(text: String, action: String, secondary: String? = null, onSecondary: () -> Unit = {}, onClick: () -> Unit) {
    Card(Modifier.fillMaxWidth().padding(16.dp, 6.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer)) {
        Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(text, Modifier.weight(1f))
            Spacer(Modifier.width(8.dp))
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Button(onClick = onClick) { Text(action) }
                if (secondary != null) TextButton(onClick = onSecondary) { Text(secondary) }
            }
        }
    }
}

@Composable
fun MessageDialog(text: String, onDismiss: () -> Unit) {
    AlertDialog(onDismissRequest = onDismiss, text = { Text(text) },
        confirmButton = { TextButton(onClick = onDismiss) { Text("אישור") } })
}

// ---------------------------------------------------------------------- remove device

/** A device to remove: [fp] is empty for a paired device that never connected; [key] is its Bluetooth address key. */
private class RemoveTarget(val fp: String, val key: String, val name: String, val paired: Boolean)

/** "Remove device": asks, removes it ([Bridge.removeDevice]), then offers to cancel the Bluetooth pairing too. */
@Composable
private fun RemoveDeviceDialogs(t: RemoveTarget, onDone: () -> Unit) {
    val ctx = LocalContext.current
    var askUnpair by remember(t) { mutableStateOf(false) }
    if (!askUnpair) AlertDialog(
        onDismissRequest = onDone,
        title = { Text("להסיר את ${t.name} מהרשימה?") },
        text = {
            Text(if (t.fp.isNotEmpty()) "החיבור אליו ייסגר, והכינוי, השמורים והמיון שלו יימחקו. בפעם הבאה שיתחבר תידרש סיסמה."
                else "הכינוי שלו יימחק.")
        },
        confirmButton = {
            TextButton(onClick = {
                val b = App.bridge
                b.runInBackground { runCatching { b.removeDevice(t.fp, t.key) } }
                if (t.paired) askUnpair = true else onDone()
            }) { Text("הסר") }
        },
        dismissButton = { TextButton(onClick = onDone) { Text("ביטול") } },
    ) else AlertDialog(
        onDismissRequest = onDone,
        title = { Text("לבטל גם את הצימוד בבלוטוס?") },
        text = { Text("אחרי ביטול הצימוד המכשיר ייעלם גם מ\"מזווגים נוספים\". כדי להתחבר אליו שוב יהיה צריך לצמד מחדש.") },
        confirmButton = { TextButton(onClick = { onDone(); unpair(ctx, t.key) }) { Text("בטל צימוד") } },
        dismissButton = { TextButton(onClick = onDone) { Text("השאר צימוד") } },
    )
}

/**
 * Cancels the Bluetooth pairing (BluetoothDevice.removeBond is hidden, so through reflection). When Android refuses,
 * opens the Bluetooth settings, where the user can do it.
 */
@SuppressLint("MissingPermission", "DiscouragedPrivateApi")
private fun unpair(ctx: Context, key: String) {
    val b = App.bridge
    val app = ctx.applicationContext
    val main = android.os.Handler(android.os.Looper.getMainLooper())
    b.runInBackground {
        val ok = try {
            if (!b.hasBtPermission()) false
            else {
                val d = b.adapter()?.bondedDevices?.firstOrNull { Bridge.key(it.address) == key }
                if (d == null) true
                else {
                    val started = d.javaClass.getMethod("removeBond").invoke(d) as? Boolean == true
                    // The pairing goes away a moment later; then the lists refresh.
                    if (started) repeat(20) { if (d.bondState == BluetoothDevice.BOND_BONDED) Thread.sleep(250) }
                    started
                }
            }
        } catch (e: Exception) {
            com.btbridge.app.core.Log.w("removeBond: $e")
            false
        }
        main.post {
            runCatching {
                b.store.version.update { it + 1 }
                if (ok) android.widget.Toast.makeText(app, "הצימוד בוטל", android.widget.Toast.LENGTH_SHORT).show()
                else {
                    android.widget.Toast.makeText(app, "לא הצלחתי לבטל את הצימוד. אפשר לבטל אותו כאן, בהגדרות הבלוטוס.", android.widget.Toast.LENGTH_LONG).show()
                    app.startActivity(Intent(Settings.ACTION_BLUETOOTH_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                }
            }
        }
    }
}

// ---------------------------------------------------------------------- search and pair

@OptIn(ExperimentalMaterial3Api::class)
@SuppressLint("MissingPermission")
@Composable
private fun ScanScreen(onBack: () -> Unit, onConnected: (Session) -> Unit) {
    val b = App.bridge
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val found = remember { mutableStateMapOf<String, BluetoothDevice>() }
    var scanning by remember { mutableStateOf(false) }
    var bonding by remember { mutableStateOf<String?>(null) }
    var message by remember { mutableStateOf<String?>(null) }
    BackHandler(onBack = onBack)

    fun start() {
        val a = b.adapter() ?: return
        if (!a.isEnabled) { message = "הבלוטוס כבוי"; return }
        if (Build.VERSION.SDK_INT < 31 && !locationOn(ctx)) {
            message = "באנדרואיד 11 חיפוש מכשירי בלוטוס עובד רק כשהמיקום דלוק."
            return
        }
        found.clear()
        scanning = a.startDiscovery()
        if (!scanning) message = "לא ניתן להתחיל חיפוש"
    }

    val perms = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        if (nearbyGranted(ctx)) start() else message = "בלי ההרשאה אי אפשר לחפש מכשירים חדשים"
    }

    DisposableEffect(Unit) {
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) {
                @Suppress("DEPRECATION")
                val d: BluetoothDevice? = i.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE)
                when (i.action) {
                    BluetoothDevice.ACTION_FOUND -> if (d != null && d.bondState != BluetoothDevice.BOND_BONDED) found[d.address] = d
                    BluetoothAdapter.ACTION_DISCOVERY_FINISHED -> scanning = false
                    BluetoothDevice.ACTION_BOND_STATE_CHANGED -> if (d != null && d.address == bonding) {
                        when (i.getIntExtra(BluetoothDevice.EXTRA_BOND_STATE, BluetoothDevice.BOND_NONE)) {
                            BluetoothDevice.BOND_BONDED -> {
                                bonding = null
                                scope.launch {
                                    try { onConnected(withContext(Dispatchers.IO) { b.connect(d) }) }
                                    catch (e: Exception) { message = "הצימוד הצליח, אבל החיבור לגשר בלוטוס נכשל: ${com.btbridge.app.core.Heb.of(e)}\nודא שהתוכנה פתוחה במחשב." }
                                }
                            }
                            BluetoothDevice.BOND_NONE -> { bonding = null; message = "הצימוד נכשל או בוטל" }
                        }
                    }
                }
            }
        }
        val filter = IntentFilter().apply {
            addAction(BluetoothDevice.ACTION_FOUND)
            addAction(BluetoothAdapter.ACTION_DISCOVERY_FINISHED)
            addAction(BluetoothDevice.ACTION_BOND_STATE_CHANGED)
        }
        if (Build.VERSION.SDK_INT >= 33) ctx.registerReceiver(receiver, filter, Context.RECEIVER_EXPORTED)
        else ctx.registerReceiver(receiver, filter)
        if (nearbyGranted(ctx)) start() else perms.launch(nearbyPermissions())
        onDispose {
            ctx.unregisterReceiver(receiver)
            runCatching { b.adapter()?.cancelDiscovery() }
        }
    }

    Scaffold(topBar = {
        TopAppBar(title = { Text("חיפוש וצימוד") },
            navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "חזור") } })
    }) { pad ->
        Column(Modifier.padding(pad)) {
            if (scanning) LinearProgressIndicator(Modifier.fillMaxWidth())
            Text("במחשב: פתח את הגדרות הבלוטוס של ווינדוס כדי שיהיה גלוי. הקש על המחשב כדי לצמד, ואשר את הקוד בשני המכשירים.",
                Modifier.padding(16.dp), style = MaterialTheme.typography.bodySmall)
            LazyColumn(Modifier.weight(1f)) {
                items(found.values.toList(), key = { it.address }) { d ->
                    ListItem(
                        headlineContent = { Text(d.name ?: d.address) },
                        supportingContent = { Text(if (bonding == d.address) "מצמד… אשר את הקוד בשני המכשירים" else d.address) },
                        leadingContent = { Icon(Icons.Filled.Bluetooth, null) },
                        modifier = Modifier.combinedClick(onClick = {
                            runCatching { b.adapter()?.cancelDiscovery() }
                            bonding = d.address
                            if (!d.createBond()) { bonding = null; message = "לא ניתן להתחיל צימוד" }
                        }, onLongClick = {}),
                    )
                    HorizontalDivider()
                }
            }
            Button(onClick = { if (nearbyGranted(ctx)) start() else perms.launch(nearbyPermissions()) }, Modifier.padding(16.dp), enabled = !scanning) {
                Text(if (scanning) "מחפש…" else "חפש שוב")
            }
        }
    }
    message?.let { MessageDialog(it) { message = null } }
}

// ---------------------------------------------------------------------- password

@Composable
fun PasswordPrompt() {
    val req by App.bridge.passwordRequest.collectAsState()
    val r = req ?: return
    var pw by remember(r) { mutableStateOf("") }
    var remember by remember(r) { mutableStateOf(true) }
    AlertDialog(
        onDismissRequest = { r.answer.complete(null) },
        title = { Text("הסיסמה של \"${r.peerName}\"") },
        text = {
            Column {
                Text("זו הסיסמה שנקבעה במכשיר השני, בהגדרות של גשר בלוטוס.")
                if (r.wrongBefore) Text("הסיסמה שגויה. נסה שוב.", color = MaterialTheme.colorScheme.error)
                OutlinedTextField(pw, { pw = it }, singleLine = true, visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password), modifier = Modifier.padding(top = 8.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(remember, { remember = it })
                    Text("אל תבקש שוב סיסמה ממכשיר זה")
                }
            }
        },
        confirmButton = {
            TextButton(enabled = pw.isNotEmpty(), onClick = { r.answer.complete(com.btbridge.app.core.PasswordAnswer(pw, remember)) }) { Text("התחבר") }
        },
        dismissButton = { TextButton(onClick = { r.answer.complete(null) }) { Text("ביטול") } },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SetPasswordScreen(firstRun: Boolean, onDone: () -> Unit) {
    var a by remember { mutableStateOf("") }
    var c by remember { mutableStateOf("") }
    var err by remember { mutableStateOf<String?>(null) }
    Scaffold(topBar = { TopAppBar(title = { Text(if (firstRun) "ברוך הבא לגשר בלוטוס" else "שינוי סיסמה") }) }) { pad ->
        Column(Modifier.padding(pad).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("קבע סיסמה לטלפון הזה", fontWeight = FontWeight.SemiBold)
            Text("מכשיר שמתחבר לטלפון בפעם הראשונה יצטרך להזין אותה. אם יסמן שם \"אל תבקש שוב\", לא יצטרך להזין אותה שוב.")
            OutlinedTextField(a, { a = it }, label = { Text("סיסמה (לפחות 4 תווים)") }, singleLine = true,
                visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth())
            OutlinedTextField(c, { c = it }, label = { Text("שוב, לאימות") }, singleLine = true,
                visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth())
            err?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            Button(onClick = {
                when {
                    a.length < 4 -> err = "הסיסמה קצרה מדי"
                    a != c -> err = "הסיסמאות לא זהות"
                    else -> { App.bridge.store.setPassword(a); onDone() }
                }
            }) { Text("שמור") }
        }
    }
}

// ---------------------------------------------------------------------- transfers

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TransfersScreen(onBack: () -> Unit) {
    val b = App.bridge
    val list by b.transfers.collectAsState()
    BackHandler(onBack = onBack)
    // Downloaded files that were deleted since leave the list: right when the app comes back, and every few seconds.
    val owner = LocalContext.current as? LifecycleOwner
    LaunchedEffect(owner) {
        owner?.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            while (isActive) {
                runCatching { withContext(Dispatchers.IO) { b.dropGoneDownloads() } }
                delay(3000)
            }
        }
    }
    Scaffold(topBar = {
        TopAppBar(title = { Text("העברות") },
            navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "חזור") } },
            actions = { TextButton(onClick = { b.clearFinished() }) { Text("נקה שהסתיימו") } })
    }) { pad ->
        if (list.isEmpty()) Text("אין העברות.", Modifier.padding(pad).padding(16.dp))
        LazyColumn(Modifier.padding(pad)) {
            items(list) { t -> TransferRow(t); HorizontalDivider() }
        }
    }
}

@Composable
fun TransferRow(t: Transfer) {
    val tick by App.bridge.tick.collectAsState()
    tick.let { }
    val ctx = LocalContext.current
    Column(Modifier.fillMaxWidth().padding(16.dp, 8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(if (t.upload) "⬆" else "⬇")
            Spacer(Modifier.width(8.dp))
            Text(t.name, Modifier.weight(1f), maxLines = 1, fontWeight = FontWeight.SemiBold)
            if (t.active) TextButton(onClick = { t.cancel.cancel() }) { Text("בטל") }
            else if (t.canResume) TextButton(onClick = { App.bridge.resume(t) }) { Text("המשך") }
            else if (!t.upload && t.resultPath != null && !t.status.startsWith("שגיאה") && t.status != "בוטל") {
                TextButton(onClick = { LocalFiles.open(ctx, java.io.File(t.resultPath!!)) }) { Text("פתח") }
            }
        }
        LinearProgressIndicator(progress = { t.percent }, modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp))
        Text(
            "${Transfer.formatSize(t.done.get())} / ${Transfer.formatSize(t.total)}" +
                (if (t.active && t.speed > 0) " · ${Transfer.formatSize(t.speed)}/ש'" else "") +
                (if (t.linkKind.isNotEmpty()) " · ${t.linkKind}" else "") + " · ${t.status}",
            style = MaterialTheme.typography.bodySmall,
        )
        // Where a downloaded file landed, so it can be found in the phone's file manager too.
        val saved = t.resultPath
        if (!t.upload && !t.active && saved != null && !saved.startsWith(ctx.cacheDir.path))
            Text("נשמר ב: ${friendlyPath(saved)}", style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2)
    }
}

/** "/storage/emulated/0/Download/גשר בלוטוס/a.jpg" → "זיכרון פנימי ← הורדות ← גשר בלוטוס ← a.jpg". */
fun friendlyPath(p: String): String {
    val root = Environment.getExternalStorageDirectory().path
    if (!p.startsWith(root)) return p.split('/').filter { it.isNotEmpty() }.joinToString(" ← ")
    val parts = p.removePrefix(root).split('/').filter { it.isNotEmpty() }.toMutableList()
    // The phone's standard folders, by the names the user knows.
    val known = mapOf("Download" to "הורדות", "DCIM" to "מצלמה", "Pictures" to "תמונות", "Documents" to "מסמכים",
        "Movies" to "סרטונים", "Music" to "מוזיקה")
    if (parts.isNotEmpty()) parts[0] = known[parts[0]] ?: parts[0]
    return (listOf("זיכרון פנימי") + parts).joinToString(" ← ")
}

// ---------------------------------------------------------------------- settings

@OptIn(ExperimentalMaterial3Api::class)
@SuppressLint("BatteryLife", "MissingPermission")
@Composable
private fun SettingsScreen(resumed: Int, onBack: () -> Unit) {
    val b = App.bridge
    val ctx = LocalContext.current
    val v by b.store.version.collectAsState()
    var name by remember { mutableStateOf(b.store.deviceName) }
    var changingPw by remember { mutableStateOf(false) }
    val perms = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { }
    // When Android no longer shows the question (refused before), the app's notification settings open instead.
    val askNotifications = rememberPermissionAsk(Manifest.permission.POST_NOTIFICATIONS) { c ->
        if (Build.VERSION.SDK_INT >= 26) c.startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, c.packageName))
    }
    val askStorage = rememberStorageAsk()
    var removing by remember { mutableStateOf<RemoveTarget?>(null) }
    BackHandler(onBack = onBack)
    if (changingPw) {
        BackHandler { changingPw = false }
        SetPasswordScreen(firstRun = false, onDone = { changingPw = false })
        return
    }

    @Composable
    fun Perm(title: String, detail: String, ok: Boolean, action: () -> Unit) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text((if (ok) "✓ " else "✗ ") + title, fontWeight = FontWeight.SemiBold,
                    color = if (ok) Color(0xFF16A34A) else MaterialTheme.colorScheme.error)
                Text(detail, style = MaterialTheme.typography.bodySmall)
            }
            if (!ok) TextButton(onClick = action) { Text("אשר") }
        }
    }

    /** Something this Android version doesn't have: shown greyed, with the reason. */
    @Composable
    fun NotHere(title: String, detail: String) {
        Column(Modifier.fillMaxWidth()) {
            Text("– $title", fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }

    @Composable
    fun Toggle(title: String, detail: String, on: Boolean, set: (Boolean) -> Unit) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(title)
                Text(detail, style = MaterialTheme.typography.bodySmall)
            }
            Switch(on, set)
        }
    }

    Scaffold(topBar = {
        TopAppBar(title = { Text("הגדרות") },
            navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "חזור") } })
    }) { pad ->
        Column(Modifier.padding(pad).padding(16.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            v.let { }
            resumed.let { }
            OutlinedTextField(name, { name = it }, label = { Text("שם הטלפון (כפי שיופיע במחשב)") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            Button(enabled = name.isNotBlank() && name != b.store.deviceName, onClick = { b.store.setDeviceName(name.trim()) }) { Text("שמור שם") }
            OutlinedButton(onClick = { changingPw = true }) { Text("שינוי הסיסמה של הטלפון…") }
            var cleanupMenu by remember { mutableStateOf(false) }
            val cleanupChoices = listOf(1 to "שעה", 6 to "6 שעות", 24 to "יום", 24 * 7 to "שבוע")
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("מחיקת קבצים שנפתחו רק לצפייה")
                    Text("קבצים שהורדת בכפתור \"הורד\" נשמרים בהורדות ולא נמחקים.", style = MaterialTheme.typography.bodySmall)
                }
                Box {
                    TextButton(onClick = { cleanupMenu = true }) {
                        Text("אחרי " + (cleanupChoices.firstOrNull { it.first == b.store.viewCleanupHours }?.second ?: "שעה"))
                    }
                    androidx.compose.material3.DropdownMenu(cleanupMenu, { cleanupMenu = false }) {
                        cleanupChoices.forEach { (h, label) ->
                            androidx.compose.material3.DropdownMenuItem(text = { Text(label) }, onClick = { cleanupMenu = false; b.store.viewCleanupHours = h })
                        }
                    }
                }
            }
            Text("תיקיית הורדות: " + if (allFilesGranted()) "הורדות ← גשר בלוטוס" else "תיקייה פנימית של האפליקציה (אשר \"גישה לכל הקבצים\" כדי לשמור בהורדות)", style = MaterialTheme.typography.bodySmall)

            HorizontalDivider()
            Toggle("לאפשר שליטה בטלפון",
                if (BridgeAccessibility.gesturesSupported) "מכשירים מוכרים יכולים לצפות במסך ולשלוט בו"
                else "מכשירים מוכרים יכולים לצפות במסך. שליטה מהמחשב דורשת אנדרואיד 7 ומעלה.",
                b.store.allowControl) { b.store.allowControl = it }
            if (DirectHotspot.supported) Toggle("רשת ישירה", "כשאין רשת משותפת, לפתוח נקודה חמה זמנית למחשב (להעברות גדולות ולשליטה). נסגרת לבד אחרי 90 שניות בלי שימוש.",
                b.store.directMode != "off") { b.store.directMode = if (it) "auto" else "off" }
            else NotHere("רשת ישירה", "דורשת אנדרואיד 8 ומעלה. בטלפון הזה החיבור המהיר עובד רק כשהטלפון והמחשב באותה רשת וויפי.")
            Toggle("לוח הדבקה משותף", "טקסט שמעתיקים במחשב אפשר להדביק בטלפון. מהטלפון למחשב: כשפותחים את האפליקציה, " +
                "או בכפתור \"שלח את הלוח למחשב\" בהתראה של גשר בלוטוס (אנדרואיד לא מרשה לקרוא את הלוח ברקע).",
                b.store.shareClipboard) { b.store.shareClipboard = it }
            Toggle("התראות הטלפון במחשב", "התראות שמופיעות בטלפון יופיעו גם במחשב, ליד השעון. צריך לאשר \"גישה להתראות\" למטה.",
                b.store.forwardNotifications) { b.store.forwardNotifications = it }

            HorizontalDivider()
            Text("הרשאות", fontWeight = FontWeight.SemiBold)
            Perm(if (Build.VERSION.SDK_INT >= 30) "גישה לכל הקבצים" else "גישה לקבצים", "כדי שהמחשב יראה את כל הקבצים", allFilesGranted()) {
                askAllFiles(ctx, askStorage)
            }
            if (BridgeAccessibility.gesturesSupported) Perm("שליטה מהמחשב (נגישות)", "הגדרות ← נגישות ← גשר בלוטוס ← הפעל. בלי זה המחשב רק רואה את המסך.", BridgeAccessibility.isEnabled(ctx)) {
                ctx.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
            }
            else NotHere("שליטה מהמחשב", "דורשת אנדרואיד 7 ומעלה. בטלפון הזה המחשב יכול רק לצפות במסך.")
            Perm(if (DirectHotspot.supported) "מיקום (לרשת ישירה ולחיפוש מכשירים)" else "מיקום (לחיפוש מכשירים)",
                if (Build.VERSION.SDK_INT < 31) "בחלון האישור בחר \"בזמן השימוש באפליקציה\" ולא \"רק הפעם\", אחרת תתבקש לאשר שוב בכל פעם. " +
                    "באנדרואיד 11 צריך גם שהמיקום יהיה דלוק בזמן השימוש." else "",
                nearbyGranted(ctx)) { perms.launch(nearbyPermissions()) }
            if (Build.VERSION.SDK_INT >= 33) Perm("התראות של גשר בלוטוס", "כדי לראות כשמגיע קובץ ומה מצב החיבור",
                granted(ctx, Manifest.permission.POST_NOTIFICATIONS)) { askNotifications() }
            Perm("גישה להתראות", "כדי שהתראות הטלפון יופיעו במחשב", BridgeNotifications.isEnabled(ctx)) {
                // Android 5.0 has no direct page for it: it sits under "security" there.
                runCatching { ctx.startActivity(Intent(if (Build.VERSION.SDK_INT >= 22) Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS else Settings.ACTION_SECURITY_SETTINGS)) }
            }
            // Both are always allowed before Android 6.0 / 8.0, so their buttons appear only on newer phones.
            Perm("פעולה ברקע בלי הגבלה", "כדי שהחיבור לא ינותק כשהמסך כבוי", batteryUnrestricted(ctx)) {
                if (Build.VERSION.SDK_INT >= 23) runCatching { ctx.startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:" + ctx.packageName))) }
            }
            Perm("התקנת אפליקציות", "כדי להתקין אפליקציות שנשלחו מהמחשב", LocalFiles.canInstallApps(ctx)) {
                if (Build.VERSION.SDK_INT >= 26) ctx.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + ctx.packageName)))
            }

            HorizontalDivider()
            Text("מכשירים מוכרים (מתחברים בלי סיסמה)", fontWeight = FontWeight.SemiBold)
            val trusted = b.store.trusted()
            if (trusted.isEmpty()) Text("אין עדיין.")
            trusted.forEach { t ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(b.store.nameFor(t.fp, t.name))
                        Text(com.btbridge.app.core.Crypto.fingerprintDigits(t.fp), style = MaterialTheme.typography.bodySmall)
                    }
                    TextButton(onClick = {
                        // Like "forget device" on the home screen: its open connection closes too.
                        b.sessionFor(t.fp)?.let { s -> b.runInBackground { runCatching { s.close() } } }
                        b.store.forget(t.fp)
                    }) { Text("הסר") }
                    TextButton(onClick = {
                        val paired = b.hasBtPermission() && runCatching {
                            b.adapter()?.bondedDevices?.any { Bridge.key(it.address) == t.address }
                        }.getOrNull() == true
                        removing = RemoveTarget(t.fp, t.address, b.store.nameFor(t.fp, t.name), paired)
                    }) { Text("הסר מהרשימה…") }
                }
            }
            HorizontalDivider()
            Text("טביעת האצבע של הטלפון: ${com.btbridge.app.core.Crypto.fingerprintDigits(b.me.fp)}", style = MaterialTheme.typography.bodySmall)
            Text("גרסה ${com.btbridge.app.core.AppInfo.version}", style = MaterialTheme.typography.bodySmall)
        }
    }
    removing?.let { RemoveDeviceDialogs(it) { removing = null } }
}

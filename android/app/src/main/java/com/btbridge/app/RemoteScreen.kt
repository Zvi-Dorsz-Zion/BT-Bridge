package com.btbridge.app

import android.app.Activity
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.VibrationEffect
import android.os.Vibrator
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateCentroid
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.ArrowForward
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.DesktopWindows
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.HighQuality
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.KeyboardCommandKey
import androidx.compose.material.icons.filled.Mouse
import androidx.compose.material.icons.filled.ZoomOutMap
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.btbridge.app.core.ScreenClient
import com.btbridge.app.core.Session
import com.btbridge.app.core.Transfer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withContext
import org.json.JSONArray
import kotlin.math.abs
import kotlin.math.roundToInt

private val keys = listOf(
    "יציאה (אסקייפ)" to ("esc" to emptyList()), "טאב" to ("tab" to emptyList()), "אנטר" to ("enter" to emptyList()),
    "מחיקה אחורה" to ("back" to emptyList()), "מחיקה" to ("del" to emptyList()), "מקש חלונות" to ("win" to emptyList()),
    "חץ שמאלה" to ("left" to emptyList()), "חץ ימינה" to ("right" to emptyList()), "חץ למעלה" to ("up" to emptyList()), "חץ למטה" to ("down" to emptyList()),
    "תחילת השורה" to ("home" to emptyList()), "סוף השורה" to ("end" to emptyList()),
    "העתק" to ("c" to listOf("ctrl")), "הדבק" to ("v" to listOf("ctrl")), "גזור" to ("x" to listOf("ctrl")),
    "בטל פעולה אחרונה" to ("z" to listOf("ctrl")), "בחר הכל" to ("a" to listOf("ctrl")), "שמור" to ("s" to listOf("ctrl")),
    "מעבר בין חלונות" to ("tab" to listOf("alt")), "סגור חלון" to ("f4" to listOf("alt")), "הצג את שולחן העבודה" to ("d" to listOf("win")),
    "רענון" to ("f5" to emptyList()), "צילום מסך" to ("prtsc" to emptyList()),
)

/** One program's window on the computer ([window]), or the separate browser ([browser]). */
data class RemoteTarget(val title: String, val window: Long? = null, val browser: Boolean = false)

/** A finger held this long without moving is a long press. */
private const val LONG_MS = 500L
/** A second tap starting this soon after the first one ended makes a double tap. */
private const val DOUBLE_MS = 350L
/** Moves, scrolls and touches go out at most this often. */
private const val EVERY_MS = 30L

/**
 * The computer's screen on the phone.
 * The computer's screen or one program: tap = click, double tap = double click, one finger drag = scroll,
 * hold then drag = mouse drag, hold = right click. "Mouse mode" (in the bar): drag = mouse drag, hold = right click.
 * Two fingers: pinch = zoom (around the fingers), move = move the zoomed picture (or scroll at normal size),
 * double tap with two fingers = normal size.
 * The separate browser, when the computer supports touches: taps and drags are real touches (a drag scrolls the page).
 * With a [target], only that program (or the separate browser) is shown and controlled, and the computer's
 * screen and mouse are left alone.
 */
@Composable
fun RemoteScreen(s: Session, target: RemoteTarget?, onClose: () -> Unit) {
    val b = App.bridge
    val ctx = LocalContext.current
    val haptic = LocalHapticFeedback.current
    val density = LocalDensity.current.density
    val focusManager = LocalFocusManager.current
    var reopen by remember { mutableIntStateOf(0) }
    var monitor by remember { mutableIntStateOf(-1) }
    var profile by remember { mutableStateOf("auto") }
    var restoreCursor by remember { mutableStateOf(false) }
    val client = remember(s, reopen) { ScreenClient(s) }
    var status by remember { mutableStateOf(if (target == null) "מתחבר למסך המחשב…" else "פותח את ${target.title}…") }
    /** The separate browser's page, and how taps reach the program ("uia" / "window"). */
    var pageUrl by remember { mutableStateOf("") }
    var address by remember { mutableStateOf("") }
    var addressFocused by remember { mutableStateOf(false) }
    var mode by remember { mutableStateOf("") }
    val config = androidx.compose.ui.platform.LocalConfiguration.current
    var bitmap by remember { mutableStateOf<Bitmap?>(null) }
    var version by remember { mutableIntStateOf(0) }
    var fps by remember { mutableIntStateOf(0) }
    var bar by remember { mutableStateOf(true) }
    var keyboard by remember { mutableStateOf(false) }
    val keysHolder = remember { RemoteKeysHolder() }
    var menu by remember { mutableStateOf("") }
    var scale by remember { mutableFloatStateOf(1f) }
    var pan by remember { mutableStateOf(Offset.Zero) }
    var viewSize by remember { mutableStateOf(IntSize.Zero) }
    var mouseMode by remember { mutableStateOf(b.store.remoteMouseMode) }
    val main = remember { Handler(Looper.getMainLooper()) }
    val isBrowser = target?.browser == true
    /** The computer turns our taps and drags into real touches on the page (PROTOCOL.md section 7, "touch"). */
    val browserTouch = isBrowser && s.has("touch")
    /** The computer understands the second half of a double click ("after"). */
    val dbl = s.has("dbl")

    BackHandler(onBack = onClose)

    // Full screen, screen stays on.
    DisposableEffect(Unit) {
        val win = (ctx as Activity).window
        val ctl = WindowCompat.getInsetsController(win, win.decorView)
        ctl.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        ctl.hide(WindowInsetsCompat.Type.systemBars())
        win.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        onDispose {
            ctl.show(WindowInsetsCompat.Type.systemBars())
            win.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    // Leaving (or reopening) always ends the stream, also while it is still opening.
    DisposableEffect(client) { onDispose { client.close() } }

    LaunchedEffect(client) {
        var frames = 0
        var lastSecond = System.currentTimeMillis()
        // A frame is acknowledged only once it is drawn, so frames don't pile up while drawing lags behind.
        client.manualAck = true
        client.onFrame = { seq, w, h, tiles ->
            // Decode on the link thread, draw on the main thread (in order; every frame, since tiles are changes).
            val decoded = tiles.mapNotNull { t -> BitmapFactory.decodeByteArray(t.jpeg, 0, t.jpeg.size)?.let { t to it } }
            main.post {
                try {
                    var bmp = bitmap
                    if (bmp == null || bmp.width != w || bmp.height != h) {
                        bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
                        bitmap = bmp
                    }
                    val c = android.graphics.Canvas(bmp!!)
                    for ((t, tb) in decoded) {
                        c.drawBitmap(tb, t.x.toFloat(), t.y.toFloat(), null)
                        tb.recycle()
                    }
                    version++
                    frames++
                    val now = System.currentTimeMillis()
                    if (now - lastSecond >= 1000) { fps = frames; frames = 0; lastSecond = now }
                    if (status.isNotEmpty()) status = ""
                } finally {
                    client.ack(seq)
                }
            }
        }
        client.onClosed = { why -> main.post { status = "השיתוף הסתיים: $why" } }
        client.onInfo = { ev ->
            main.post {
                // The address the user is typing is not replaced under their fingers.
                ev.optString("url").takeIf { it.isNotEmpty() }?.let { if (pageUrl != it) { pageUrl = it; if (!addressFocused) address = it } }
                ev.optString("mode").takeIf { it.isNotEmpty() }?.let { mode = it }
            }
        }
        try {
            // The separate browser's page is laid out for the space under its bars: wait until that space is known.
            val pageH = if (isBrowser) snapshotFlow { viewSize }.first { it.height > 0 }.height else 0
            withContext(Dispatchers.IO) {
                b.ensureFastLink(s)
                client.open(monitor, profile, restoreCursor) {
                    // Wider than twice this screen is never needed, even zoomed in.
                    if (profile == "auto") put("maxw", 2 * ctx.resources.displayMetrics.widthPixels)
                    when {
                        isBrowser -> {
                            // The page is laid out for this phone's screen, as a phone browser would.
                            // The computer lays out at least 320 wide: on a narrower phone the height grows by the same
                            // ratio, so the picture still fills the space under the bars.
                            val vw = config.screenWidthDp
                            val vh = (pageH / density) * (maxOf(vw, 320) / vw.toFloat())
                            put("browser", true).put("vw", vw).put("vh", vh.roundToInt())
                            if (browserTouch) put("touch", true)
                        }
                        target?.window != null -> put("window", target.window)
                    }
                }
            }
            mode = client.mode
            status = "ממתין לתמונה…"
        } catch (e: Exception) {
            status = "השיתוף לא נפתח: ${com.btbridge.app.core.Heb.of(e)}"
        }
    }

    /** Screen point → frame pixel. */
    fun toFrame(p: Offset): Offset? {
        val bmp = bitmap ?: return null
        if (viewSize.width == 0) return null
        val base = minOf(viewSize.width / bmp.width.toFloat(), viewSize.height / bmp.height.toFloat())
        val k = base * scale
        val left = (viewSize.width - bmp.width * k) / 2 + pan.x
        val top = (viewSize.height - bmp.height * k) / 2 + pan.y
        return Offset(((p.x - left) / k).coerceIn(0f, bmp.width - 1f), ((p.y - top) / k).coerceIn(0f, bmp.height - 1f))
    }

    fun send(type: String, p: Offset?, extra: (org.json.JSONObject.() -> Unit)? = null) {
        val f = p?.let { toFrame(it) } ?: Offset.Zero
        client.input(type, f.x.toDouble(), f.y.toDouble(), extra)
    }

    fun key(k: String, mods: List<String>) = client.input("key", extra = { put("k", k).put("mods", JSONArray(mods)) })

    /** The zoomed picture may move only as far as it still covers the screen (or stays centred where it is smaller). */
    fun clampPan(p: Offset, sc: Float): Offset {
        val bmp = bitmap ?: return Offset.Zero
        if (viewSize.width == 0) return Offset.Zero
        val base = minOf(viewSize.width / bmp.width.toFloat(), viewSize.height / bmp.height.toFloat())
        val mx = maxOf(0f, (bmp.width * base * sc - viewSize.width) / 2)
        val my = maxOf(0f, (bmp.height * base * sc - viewSize.height) / 2)
        return Offset(p.x.coerceIn(-mx, mx), p.y.coerceIn(-my, my))
    }

    /** Zooms by [z], keeping the point under [c] (the fingers) in place. */
    fun zoomBy(z: Float, c: Offset) {
        val ns = (scale * z).coerceIn(1f, 6f)
        if (ns < 1.02f) { scale = 1f; pan = Offset.Zero; return }
        val zr = ns / scale
        val center = Offset(viewSize.width / 2f, viewSize.height / 2f)
        pan = clampPan((c - center) * (1 - zr) + pan * zr, ns)
        scale = ns
    }

    fun resetZoom() { scale = 1f; pan = Offset.Zero }

    /** Finger travel (screen pixels) for one wheel notch: about 100 pixels of the computer, so the content follows the finger. */
    fun notchPx(): Float {
        val bmp = bitmap
        if (bmp == null || viewSize.width == 0) return 30 * density
        val k = minOf(viewSize.width / bmp.width.toFloat(), viewSize.height / bmp.height.toFloat()) * scale
        val ms = client.monitors
        val m = ms.firstOrNull { it.optInt("i") == monitor } ?: ms.firstOrNull { it.optBoolean("primary") } ?: ms.firstOrNull()
        val realW = m?.optInt("w")?.takeIf { it >= bmp.width } ?: bmp.width
        return (100f * bmp.width / realW * k).coerceIn(12 * density, 60 * density)
    }

    /** A short buzz: the long press turned into a mouse drag / right click. */
    fun buzz() {
        runCatching {
            val v = ctx.service<Vibrator>() ?: return@runCatching
            if (Build.VERSION.SDK_INT >= 26) v.vibrate(VibrationEffect.createOneShot(30, VibrationEffect.DEFAULT_AMPLITUDE))
            else @Suppress("DEPRECATION") v.vibrate(30)
        }
    }

    /** The bars: drawn over the picture, or (the separate browser) above it. Touches on them never reach the computer. */
    val bars: @Composable (Modifier) -> Unit = { m ->
        Column(m.fillMaxWidth().pointerInput(Unit) {
            // Only here so a touch on an empty part of a bar stops at the bar.
            awaitEachGesture { do { val e = awaitPointerEvent() } while (e.changes.any { it.pressed }) }
        }) {
            if (bar) Row(
                Modifier.fillMaxWidth().background(Color(0xCC111827)).padding(horizontal = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                IconButton(onClick = onClose) { Icon(Icons.Filled.Close, "סגור", tint = Color.White) }
                // On a narrow phone the buttons scroll sideways; close and hide always stay in view.
                Row(Modifier.weight(1f).horizontalScroll(rememberScrollState()), verticalAlignment = Alignment.CenterVertically) {
                    IconButton(onClick = {
                        // The phone keyboard was closed some other way: one tap opens it again.
                        val v = keysHolder.view
                        if (keyboard && v != null && !v.imeVisible()) v.show() else keyboard = !keyboard
                    }) { Icon(Icons.Filled.Keyboard, "מקלדת", tint = Color.White) }
                    if (!isBrowser) IconButton(onClick = {
                        mouseMode = !mouseMode
                        b.store.remoteMouseMode = mouseMode
                        Toast.makeText(ctx, if (mouseMode) "מצב עכבר: גרירה מזיזה את העכבר, החזקה היא קליק ימני"
                            else "גרירה גוללת. החזקה וגרירה מזיזות את העכבר", Toast.LENGTH_SHORT).show()
                    }) {
                        Icon(Icons.Filled.Mouse, if (mouseMode) "מצב עכבר פועל" else "מצב עכבר כבוי",
                            tint = if (mouseMode) Color(0xFF60A5FA) else Color(0xFF9CA3AF))
                    }
                    Box {
                        IconButton(onClick = { menu = "keys" }) { Icon(Icons.Filled.KeyboardCommandKey, "מקשים", tint = Color.White) }
                        DropdownMenu(menu == "keys", { menu = "" }) {
                            keys.forEach { (label, k) -> DropdownMenuItem(text = { Text(label) }, onClick = { menu = ""; key(k.first, k.second) }) }
                        }
                    }
                    Box {
                        IconButton(onClick = { menu = "quality" }) { Icon(Icons.Filled.HighQuality, "איכות", tint = Color.White) }
                        DropdownMenu(menu == "quality", { menu = "" }) {
                            listOf("auto" to "איכות אוטומטית (לפי החיבור)", "sharp" to "חד", "fast" to "מהיר").forEach { (p, label) ->
                                DropdownMenuItem(text = { Text((if (profile == p) "✓ " else "") + label) }, onClick = {
                                    menu = ""; profile = p; client.configure(profile = p, key = true)
                                })
                            }
                        }
                    }
                    if (target == null) Box {
                        IconButton(onClick = { menu = "monitor" }) { Icon(Icons.Filled.DesktopWindows, "מסך", tint = Color.White) }
                        DropdownMenu(menu == "monitor", { menu = "" }) {
                            client.monitors.forEach { m ->
                                val i = m.optInt("i")
                                DropdownMenuItem(text = { Text("${m.optString("name")}  ${m.optInt("w")}×${m.optInt("h")}") }, onClick = {
                                    menu = ""; monitor = i; client.configure(monitor = i, key = true)
                                })
                            }
                            HorizontalDivider()
                            DropdownMenuItem(
                                text = { Text((if (restoreCursor) "✓ " else "") + "אל תזיז לי את העכבר במחשב (למסך וירטואלי)") },
                                leadingIcon = { Icon(Icons.Filled.Mouse, null) },
                                onClick = { menu = ""; restoreCursor = !restoreCursor; bitmap = null; reopen++ },
                            )
                        }
                    }
                    if (scale > 1.01f) IconButton(onClick = { resetZoom() }) { Icon(Icons.Filled.ZoomOutMap, "גודל רגיל", tint = Color.White) }
                    // How taps reach the program: through accessibility, or as messages straight to its window.
                    val how = when (mode) {
                        "uia" -> " · דרך נגישות"
                        "window" -> " · ישירות לחלון"
                        "browser" -> " · דפדפן נפרד"
                        else -> ""
                    }
                    Text((target?.title?.let { "$it · " } ?: "") + "$fps תמונות/ש' · ${s.linkText}$how" +
                        if (scale > 1.01f) " · ×${"%.1f".format(scale)}" else "",
                        Modifier.padding(horizontal = 6.dp), color = Color(0xFFD1D5DB), fontSize = 12.sp, maxLines = 1)
                }
                IconButton(onClick = { bar = false }) { Icon(Icons.Filled.ExpandLess, "הסתר", tint = Color.White) }
            }
            // The separate browser: back, forward, reload and an address bar (an address, or words to search).
            if (bar && isBrowser) Row(
                Modifier.fillMaxWidth().background(Color(0xCC111827)).padding(horizontal = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                fun go() {
                    client.input("navigate", extra = { put("url", address) })
                    focusManager.clearFocus()
                }
                // The app is right to left, so these arrows already point the Hebrew way.
                IconButton(onClick = { client.input("back") }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "אחורה", tint = Color.White) }
                IconButton(onClick = { client.input("forward") }) { Icon(Icons.AutoMirrored.Filled.ArrowForward, "קדימה", tint = Color.White) }
                IconButton(onClick = { client.input("reload") }) { Icon(Icons.Filled.Refresh, "רענן", tint = Color.White) }
                BasicTextField(
                    address, { address = it },
                    Modifier.weight(1f).background(Color(0xFF374151)).padding(8.dp).onFocusChanged { f ->
                        // Left without going anywhere: show the page's address again.
                        if (addressFocused && !f.isFocused) address = pageUrl
                        addressFocused = f.isFocused
                    },
                    singleLine = true,
                    textStyle = TextStyle(color = Color.White, fontSize = 14.sp),
                    cursorBrush = SolidColor(Color.White),
                    keyboardOptions = KeyboardOptions(imeAction = androidx.compose.ui.text.input.ImeAction.Go, autoCorrectEnabled = false),
                    keyboardActions = androidx.compose.foundation.text.KeyboardActions(onGo = { go() }),
                )
                IconButton(onClick = { go() }) {
                    Icon(Icons.Filled.Search, "עבור", tint = Color.White)
                }
            }
        }
    }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        Column(Modifier.fillMaxSize()) {
            // The separate browser's bars take their own space, so they don't hide the top of the page.
            if (isBrowser) bars(Modifier)
            Box(Modifier.fillMaxWidth().weight(1f)) {
                CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) {
                    Canvas(
                        Modifier.fillMaxSize()
                            .onSizeChanged { viewSize = it; pan = clampPan(pan, scale) }
                            .pointerInput(client) {
                                val slop = viewConfiguration.touchSlop
                                // The last single tap, for double taps: when it ended, where it was on the phone and on the computer.
                                var tapUp = 0L
                                var tapAt = Offset.Zero
                                var tapFrame: Offset? = null
                                // The last two-finger tap.
                                var twoTapAt = 0L
                                awaitEachGesture {
                                    if (isBrowser && !browserTouch) {
                                        // The separate browser on a computer without touches: as before 0.5.
                                        val down = awaitFirstDown(requireUnconsumed = false)
                                        val start = down.position
                                        val t0 = System.currentTimeMillis()
                                        var lm = 0 // 0 = undecided, 1 = mouse drag, 2 = two fingers
                                        var held = false
                                        var lastMove = 0L
                                        var wheelAcc = 0f
                                        while (true) {
                                            val waitLong = lm == 0 && !held
                                            val ev = if (waitLong) withTimeoutOrNull(maxOf(1L, 550 - (System.currentTimeMillis() - t0))) { awaitPointerEvent() }
                                            else awaitPointerEvent()
                                            if (ev == null) {
                                                // Held without moving: right click.
                                                held = true
                                                haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                                send("click", start) { put("b", 1).put("n", 1) }
                                                continue
                                            }
                                            val pressed = ev.changes.filter { it.pressed }
                                            if (pressed.size >= 2 || lm == 2) {
                                                if (lm == 1) send("up", pressed.firstOrNull()?.position ?: start) { put("b", 0) }
                                                lm = 2
                                                if (pressed.isEmpty()) break
                                                val zoom = ev.calculateZoom()
                                                val move = ev.calculatePan()
                                                if (abs(zoom - 1f) > 0.01f || scale > 1.01f) {
                                                    scale = (scale * zoom).coerceIn(1f, 6f)
                                                    pan = if (scale <= 1.01f) Offset.Zero else pan + move
                                                } else {
                                                    // Two fingers at normal size: scroll.
                                                    wheelAcc += move.y
                                                    while (abs(wheelAcc) >= 60f) {
                                                        val dir = if (wheelAcc > 0) 1 else -1
                                                        send("wheel", pressed[0].position) { put("dy", dir) }
                                                        wheelAcc -= 60f * dir
                                                    }
                                                }
                                                ev.changes.forEach { it.consume() }
                                                continue
                                            }
                                            if (pressed.isEmpty()) {
                                                val up = ev.changes.firstOrNull()?.position ?: start
                                                when {
                                                    lm == 1 -> send("up", up) { put("b", 0) }
                                                    !held -> send("click", start) { put("b", 0).put("n", 1) }
                                                }
                                                break
                                            }
                                            val p = pressed[0].position
                                            if (lm == 0 && !held && (p - start).getDistance() > viewConfiguration.touchSlop) {
                                                lm = 1
                                                send("down", start) { put("b", 0) }
                                            }
                                            if (lm == 1 && System.currentTimeMillis() - lastMove > 30) {
                                                send("move", p)
                                                lastMove = System.currentTimeMillis()
                                            }
                                            pressed[0].consume()
                                        }
                                        return@awaitEachGesture
                                    }

                                    val down = awaitFirstDown(requireUnconsumed = false)
                                    val start = down.position
                                    val t0 = System.currentTimeMillis()
                                    val mouse = mouseMode && !isBrowser
                                    // 0 = undecided, 1 = mouse drag, 2 = two fingers, 3 = scroll, 4 = touch drag (browser)
                                    var g = 0
                                    var held = false
                                    var last = start
                                    var sentAt = start
                                    var lastSent = 0L
                                    var wheelAcc = 0f
                                    var lastWheel = 0L
                                    // Two fingers.
                                    var t2 = 0L
                                    var zoomAcc = 1f
                                    var zooming = false
                                    var noZoom = false
                                    var twoMoved = 0f
                                    var twoTap = true

                                    /** Sends what was scrolled so far: in parts of a notch, or whole notches for one program. */
                                    fun wheel(final: Boolean) {
                                        val v = if (target?.window != null) wheelAcc.toInt().toFloat() else wheelAcc
                                        if (abs(v) < (if (final) 0.01f else 0.02f)) return
                                        send("wheel", start) { put("dy", (v * 1000).roundToInt() / 1000.0) }
                                        wheelAcc -= v
                                        lastWheel = System.currentTimeMillis()
                                    }

                                    /** A short tap: a click, or the second half of a double click. */
                                    fun tap() {
                                        val now = System.currentTimeMillis()
                                        val first = tapFrame
                                        if (first != null && t0 - tapUp < DOUBLE_MS && now - tapUp < 500 && (start - tapAt).getDistance() < slop * 3) {
                                            // At the first tap's point, so a slightly moved finger still opens what the first tap hit.
                                            client.input("click", first.x.toDouble(), first.y.toDouble()) {
                                                put("b", 0)
                                                if (dbl) put("n", 2).put("after", true) else put("n", 1)
                                                if (browserTouch) put("touch", true)
                                            }
                                            tapFrame = null
                                            tapUp = 0L
                                        } else {
                                            send("click", start) { put("b", 0).put("n", 1); if (browserTouch) put("touch", true) }
                                            tapFrame = toFrame(start)
                                            tapUp = now
                                            tapAt = start
                                        }
                                    }

                                    while (true) {
                                        val waitLong = g == 0 && !held && !isBrowser
                                        val ev = if (waitLong) withTimeoutOrNull(maxOf(1L, LONG_MS - (System.currentTimeMillis() - t0))) { awaitPointerEvent() }
                                        else awaitPointerEvent()
                                        if (ev == null) {
                                            // Held without moving: right click (mouse mode: now; otherwise on release, or a drag if the finger moves).
                                            held = true
                                            buzz()
                                            if (mouse) send("click", start) { put("b", 1).put("n", 1) }
                                            continue
                                        }
                                        val pressed = ev.changes.filter { it.pressed }
                                        val now = System.currentTimeMillis()
                                        if (pressed.size >= 2 || g == 2) {
                                            if (g != 2) {
                                                // A second finger: end what the first one was doing.
                                                when (g) {
                                                    1 -> send("up", last) { put("b", 0) }
                                                    3 -> wheel(true)
                                                    4 -> send("up", last) { put("touch", true) }
                                                }
                                                g = 2
                                                t2 = now
                                                wheelAcc = 0f
                                            }
                                            if (pressed.isEmpty()) {
                                                wheel(true)
                                                // Two quick two-finger taps: back to normal size.
                                                if (twoTap && now - t2 < 300) {
                                                    if (now - twoTapAt < 500) { resetZoom(); twoTapAt = 0L } else twoTapAt = now
                                                }
                                                break
                                            }
                                            val z = ev.calculateZoom()
                                            val move = ev.calculatePan()
                                            val c = ev.calculateCentroid(useCurrent = true)
                                            twoMoved += move.getDistance()
                                            zoomAcc *= z
                                            if (twoMoved > slop || abs(zoomAcc - 1f) > 0.04f) twoTap = false
                                            if (zooming) zoomBy(z, c)
                                            else if (!noZoom && abs(zoomAcc - 1f) > 0.08f) { zooming = true; zoomBy(zoomAcc, c) }
                                            if (zooming || scale > 1.01f) {
                                                pan = clampPan(pan + move, scale)
                                            } else {
                                                // Two fingers at normal size: scroll, as with one. A clear scroll doesn't turn into a zoom.
                                                if (twoMoved > slop * 3) noZoom = true
                                                wheelAcc += move.y / notchPx()
                                                if (now - lastWheel >= EVERY_MS) wheel(false)
                                            }
                                            ev.changes.forEach { it.consume() }
                                            continue
                                        }
                                        if (pressed.isEmpty()) {
                                            val up = ev.changes.firstOrNull()?.position ?: last
                                            when (g) {
                                                1 -> { if (up != sentAt) send("move", up); send("up", up) { put("b", 0) } }
                                                3 -> wheel(true)
                                                4 -> {
                                                    if (up != sentAt) send("move", up) { put("touch", true) }
                                                    send("up", up) { put("touch", true) }
                                                }
                                                else -> when {
                                                    held && mouse -> {} // the right click already went out
                                                    held -> send("click", start) { put("b", 1).put("n", 1) }
                                                    else -> tap()
                                                }
                                            }
                                            break
                                        }
                                        val p = pressed[0].position
                                        if (g == 0 && !(held && mouse) && (p - start).getDistance() > slop) {
                                            g = when {
                                                browserTouch -> { send("down", start) { put("touch", true) }; 4 }
                                                held || mouse -> { send("down", start) { put("b", 0) }; 1 }
                                                else -> 3
                                            }
                                            last = p
                                        }
                                        when (g) {
                                            1, 4 -> if (now - lastSent >= EVERY_MS) {
                                                if (g == 4) send("move", p) { put("touch", true) } else send("move", p)
                                                sentAt = p
                                                lastSent = now
                                            }
                                            3 -> {
                                                // Finger down = the content moves down with it = wheel up (dy positive).
                                                wheelAcc += (p.y - last.y) / notchPx()
                                                if (now - lastWheel >= EVERY_MS) wheel(false)
                                            }
                                        }
                                        last = p
                                        pressed[0].consume()
                                    }
                                }
                            }
                    ) {
                        version.let { }
                        val bmp = bitmap ?: return@Canvas
                        val base = minOf(size.width / bmp.width, size.height / bmp.height)
                        val k = base * scale
                        val w = bmp.width * k
                        val h = bmp.height * k
                        drawImage(
                            bmp.asImageBitmap(),
                            dstOffset = IntOffset(((size.width - w) / 2 + pan.x).toInt(), ((size.height - h) / 2 + pan.y).toInt()),
                            dstSize = IntSize(w.toInt(), h.toInt()),
                        )
                    }
                }

                if (status.isNotEmpty()) Text(status, Modifier.align(Alignment.Center).padding(24.dp), color = Color.White)

                if (!isBrowser) bars(Modifier.align(Alignment.TopCenter))
                if (!bar) IconButton(onClick = { bar = true }, Modifier.align(Alignment.TopEnd).background(Color(0x88111827))) {
                    Icon(Icons.Filled.ExpandMore, "הצג", tint = Color.White)
                }
            }
        }

        if (keyboard) RemoteKeyboard(
            Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(1.dp), keysHolder,
            onText = { client.input("text", extra = { put("s", it) }) },
            onKey = { k, mods -> key(k, mods) },
            onHidden = { keyboard = false },
        )
    }
}

package com.btbridge.app

import android.app.Activity
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Handler
import android.os.Looper
import android.view.WindowManager
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.DesktopWindows
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.HighQuality
import androidx.compose.material.icons.filled.Keyboard
import androidx.compose.material.icons.filled.KeyboardCommandKey
import androidx.compose.material.icons.filled.Mouse
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.TextFieldValue
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
import kotlinx.coroutines.withContext
import org.json.JSONArray
import kotlin.math.abs

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

/**
 * The computer's screen on the phone. One finger: tap = click, hold = right click, drag = drag.
 * Two fingers: pinch = zoom, move = scroll (or pan when zoomed in).
 */
@Composable
fun RemoteScreen(s: Session, onClose: () -> Unit) {
    val b = App.bridge
    val ctx = LocalContext.current
    val haptic = LocalHapticFeedback.current
    var reopen by remember { mutableIntStateOf(0) }
    var monitor by remember { mutableIntStateOf(-1) }
    var profile by remember { mutableStateOf("auto") }
    var restoreCursor by remember { mutableStateOf(false) }
    val client = remember(s, reopen) { ScreenClient(s) }
    var status by remember { mutableStateOf("מתחבר למסך המחשב…") }
    var bitmap by remember { mutableStateOf<Bitmap?>(null) }
    var version by remember { mutableIntStateOf(0) }
    var fps by remember { mutableIntStateOf(0) }
    var bar by remember { mutableStateOf(true) }
    var keyboard by remember { mutableStateOf(false) }
    var menu by remember { mutableStateOf("") }
    var scale by remember { mutableFloatStateOf(1f) }
    var pan by remember { mutableStateOf(Offset.Zero) }
    var viewSize by remember { mutableStateOf(IntSize.Zero) }
    val main = remember { Handler(Looper.getMainLooper()) }

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

    DisposableEffect(client) { onDispose { client.close() } }

    LaunchedEffect(client) {
        var frames = 0
        var lastSecond = System.currentTimeMillis()
        client.onFrame = { _, w, h, tiles ->
            // Decode on the link thread, draw on the main thread.
            val decoded = tiles.mapNotNull { t -> BitmapFactory.decodeByteArray(t.jpeg, 0, t.jpeg.size)?.let { t to it } }
            main.post {
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
            }
        }
        client.onClosed = { why -> main.post { status = "השיתוף הסתיים: $why" } }
        try {
            withContext(Dispatchers.IO) {
                b.ensureFastLink(s)
                client.open(monitor, profile, restoreCursor)
            }
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

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) {
            Canvas(
                Modifier.fillMaxSize()
                    .pointerInput(client) {
                        awaitEachGesture {
                            val down = awaitFirstDown(requireUnconsumed = false)
                            val start = down.position
                            val t0 = System.currentTimeMillis()
                            var mode = 0 // 0 = undecided, 1 = mouse drag, 2 = two fingers
                            var held = false
                            var lastMove = 0L
                            var wheelAcc = 0f
                            while (true) {
                                val waitLong = mode == 0 && !held
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
                                if (pressed.size >= 2 || mode == 2) {
                                    if (mode == 1) send("up", pressed.firstOrNull()?.position ?: start) { put("b", 0) }
                                    mode = 2
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
                                        mode == 1 -> send("up", up) { put("b", 0) }
                                        !held -> send("click", start) { put("b", 0).put("n", 1) }
                                    }
                                    break
                                }
                                val p = pressed[0].position
                                if (mode == 0 && !held && (p - start).getDistance() > viewConfiguration.touchSlop) {
                                    mode = 1
                                    send("down", start) { put("b", 0) }
                                }
                                if (mode == 1 && System.currentTimeMillis() - lastMove > 30) {
                                    send("move", p)
                                    lastMove = System.currentTimeMillis()
                                }
                                pressed[0].consume()
                            }
                        }
                    }
            ) {
                viewSize = IntSize(size.width.toInt(), size.height.toInt())
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

        Column(Modifier.align(Alignment.TopCenter).fillMaxWidth()) {
            if (bar) Row(
                Modifier.fillMaxWidth().background(Color(0xCC111827)).padding(horizontal = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                IconButton(onClick = onClose) { Icon(Icons.Filled.Close, "סגור", tint = Color.White) }
                IconButton(onClick = { keyboard = !keyboard }) { Icon(Icons.Filled.Keyboard, "מקלדת", tint = Color.White) }
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
                Box {
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
                Text("$fps תמונות/ש' · ${s.linkText}" + if (scale > 1.01f) " · ×${"%.1f".format(scale)}" else "",
                    Modifier.weight(1f).padding(horizontal = 6.dp), color = Color(0xFFD1D5DB), fontSize = 12.sp, maxLines = 1)
                IconButton(onClick = { bar = false }) { Icon(Icons.Filled.ExpandLess, "הסתר", tint = Color.White) }
            } else Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.TopEnd) {
                IconButton(onClick = { bar = true }, Modifier.background(Color(0x88111827))) {
                    Icon(Icons.Filled.ExpandMore, "הצג", tint = Color.White)
                }
            }
        }

        if (keyboard) KeyboardBridge(Modifier.align(Alignment.BottomCenter), onText = { client.input("text", extra = { put("s", it) }) },
            onBackspace = { key("back", emptyList()) }, onEnter = { key("enter", emptyList()) })
    }
}

/**
 * A small text field that turns the phone keyboard into key presses on the computer:
 * typed characters go out as text, Backspace and Enter as keys.
 */
@Composable
private fun KeyboardBridge(modifier: Modifier, onText: (String) -> Unit, onBackspace: () -> Unit, onEnter: () -> Unit) {
    val sentinel = " "
    var value by remember { mutableStateOf(TextFieldValue(sentinel, TextRange(1))) }
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }
    BasicTextField(
        value = value,
        onValueChange = { v ->
            val t = v.text
            when {
                t.length < sentinel.length -> onBackspace()
                t.startsWith(sentinel) && t.length > sentinel.length -> {
                    val added = t.substring(sentinel.length)
                    val lines = added.split('\n')
                    lines.forEachIndexed { i, part ->
                        if (part.isNotEmpty()) onText(part)
                        if (i < lines.lastIndex) onEnter()
                    }
                }
            }
            value = TextFieldValue(sentinel, TextRange(1))
        },
        keyboardOptions = KeyboardOptions(autoCorrectEnabled = false),
        textStyle = TextStyle(color = Color.Transparent),
        cursorBrush = SolidColor(Color.Transparent),
        modifier = modifier.fillMaxWidth().height(1.dp).focusRequester(focus),
    )
}

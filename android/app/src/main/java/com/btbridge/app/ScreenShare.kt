package com.btbridge.app

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.app.Activity
import android.app.Notification
import android.app.PendingIntent
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Path
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.provider.Settings
import android.util.DisplayMetrics
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import com.btbridge.app.core.Frame
import com.btbridge.app.core.InputSink
import com.btbridge.app.core.Log
import com.btbridge.app.core.ScreenGrabber
import com.btbridge.app.core.ScreenProto
import com.btbridge.app.core.TileCodec
import com.btbridge.app.core.str
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

/**
 * MediaProjection for sharing the phone's screen. The user approves once; the approval is kept while the
 * background service lives, so later requests from the computer don't ask again (Android 11).
 */
object Projection {
    @Volatile var projection: MediaProjection? = null
        private set
    @Volatile private var pending: CompletableFuture<Boolean>? = null
    private const val NOTIF_ID = 7

    /** Blocking (worker thread). Asks the user when there is no approval yet; null when not approved in time. */
    fun obtain(ctx: Context): MediaProjection? {
        projection?.let { return it }
        val f = CompletableFuture<Boolean>()
        pending = f
        val open = Intent(ctx, ProjectionActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        // From the background Android shows this as a notification; when the app is open it starts right away.
        runCatching { ctx.startActivity(open) }
        val pi = PendingIntent.getActivity(ctx, 3, open, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val n = Notification.Builder(ctx, App.CHANNEL_EVENTS)
            .setSmallIcon(android.R.drawable.ic_menu_view)
            .setContentTitle("המחשב מבקש לראות את המסך")
            .setContentText("הקש כדי לאשר שיתוף מסך")
            .setContentIntent(pi)
            .setFullScreenIntent(pi, true)
            .setAutoCancel(true)
            .build()
        val nm = ctx.getSystemService(android.app.NotificationManager::class.java)
        runCatching { nm.notify(NOTIF_ID, n) }
        return try {
            if (f.get(90, TimeUnit.SECONDS)) projection else null
        } catch (e: Exception) {
            null
        } finally {
            pending = null
            nm.cancel(NOTIF_ID)
        }
    }

    fun onResult(ctx: Context, code: Int, data: Intent?) {
        if (code != Activity.RESULT_OK || data == null) {
            pending?.complete(false)
            return
        }
        try {
            // Android 10+ requires a running "media projection" foreground service before getMediaProjection.
            BridgeService.promoteForProjection(ctx)
            val mp = ctx.getSystemService(MediaProjectionManager::class.java).getMediaProjection(code, data)
                ?: error("המערכת לא אישרה את שיתוף המסך")
            mp.registerCallback(object : MediaProjection.Callback() {
                override fun onStop() {
                    projection = null
                    Log.w("projection stopped")
                }
            }, Handler(ctx.mainLooper))
            projection = mp
            pending?.complete(true)
        } catch (e: Exception) {
            Log.w("projection: ${e.message}")
            pending?.complete(false)
        }
    }

    fun stop() {
        projection?.stop()
        projection = null
    }
}

/** Shows the system "start recording or casting?" dialog and hands the result to [Projection]. */
class ProjectionActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val mpm = getSystemService(MediaProjectionManager::class.java)
        @Suppress("DEPRECATION")
        startActivityForResult(mpm.createScreenCaptureIntent(), 1)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION")
        super.onActivityResult(requestCode, resultCode, data)
        Projection.onResult(applicationContext, resultCode, data)
        finish()
    }
}

/** Reads the screen through a VirtualDisplay + ImageReader at the requested (scaled) size. */
class ProjectionGrabber(private val ctx: Context, private val mp: MediaProjection) : ScreenGrabber {
    private val thread = HandlerThread("capture").apply { start() }
    private val handler = Handler(thread.looper)
    private var vd: VirtualDisplay? = null
    private var reader: ImageReader? = null
    private var curW = 0
    private var curH = 0
    private var last: Frame? = null

    private fun metrics(): DisplayMetrics {
        val m = DisplayMetrics()
        @Suppress("DEPRECATION")
        ctx.getSystemService(WindowManager::class.java).defaultDisplay.getRealMetrics(m)
        return m
    }

    override val realW: Int get() = metrics().widthPixels
    override val realH: Int get() = metrics().heightPixels

    @Synchronized
    override fun grab(maxW: Int): Frame? {
        val m = metrics()
        val (w, h) = ScreenProto.fit(m.widthPixels, m.heightPixels, maxW)
        if (w != curW || h != curH) {
            val r = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 2)
            val v = vd
            if (v == null) {
                vd = mp.createVirtualDisplay("btbridge", w, h, m.densityDpi,
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, r.surface, null, handler)
            } else {
                v.resize(w, h, m.densityDpi)
                v.surface = r.surface
            }
            reader?.close()
            reader = r
            curW = w
            curH = h
            last = null
        }
        val img = reader?.acquireLatestImage() ?: return last
        try {
            val plane = img.planes[0]
            val buf = plane.buffer
            val rowStride = plane.rowStride
            val pixStride = plane.pixelStride
            val px = IntArray(w * h)
            for (y in 0 until h) {
                var at = y * rowStride
                val row = y * w
                for (x in 0 until w) {
                    val r = buf.get(at).toInt() and 0xff
                    val g = buf.get(at + 1).toInt() and 0xff
                    val b = buf.get(at + 2).toInt() and 0xff
                    px[row + x] = (0xff shl 24) or (r shl 16) or (g shl 8) or b
                    at += pixStride
                }
            }
            return Frame(w, h, px).also { last = it }
        } finally {
            img.close()
        }
    }

    @Synchronized
    override fun release() {
        runCatching { vd?.release() }
        runCatching { reader?.close() }
        vd = null
        reader = null
        curW = 0
        curH = 0
        thread.quitSafely()
    }
}

/** JPEG tiles through Bitmap.compress. */
object AndroidTileCodec : TileCodec {
    override fun encode(px: IntArray, stride: Int, x: Int, y: Int, w: Int, h: Int, quality: Int): ByteArray {
        val bmp = Bitmap.createBitmap(px, y * stride + x, stride, w, h, Bitmap.Config.ARGB_8888)
        val out = ByteArrayOutputStream()
        bmp.compress(Bitmap.CompressFormat.JPEG, quality, out)
        bmp.recycle()
        return out.toByteArray()
    }
}

/**
 * Taps, swipes, system buttons and typing on the phone, from the computer.
 * The user turns it on once in the accessibility settings.
 */
class BridgeAccessibility : AccessibilityService() {
    companion object {
        @Volatile var instance: BridgeAccessibility? = null
            private set

        fun isEnabled(ctx: Context): Boolean {
            if (instance != null) return true
            val list = Settings.Secure.getString(ctx.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return false
            // The system may store either "pkg/pkg.Class" or the short "pkg/.Class": compare the parsed names.
            val me = ComponentName(ctx, BridgeAccessibility::class.java)
            return list.split(':').any { ComponentName.unflattenFromString(it.trim()) == me }
        }
    }

    override fun onServiceConnected() { instance = this }
    override fun onDestroy() { instance = null; super.onDestroy() }
    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}
    override fun onInterrupt() {}

    private fun stroke(path: Path, ms: Long) {
        dispatchGesture(GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0, ms.coerceIn(1, 10_000)))
            .build(), null, null)
    }

    fun tap(x: Int, y: Int, long: Boolean) = stroke(Path().apply { moveTo(x.toFloat(), y.toFloat()) }, if (long) 700 else 50)

    fun swipe(x1: Int, y1: Int, x2: Int, y2: Int, ms: Long) =
        stroke(Path().apply { moveTo(x1.toFloat(), y1.toFloat()); lineTo(x2.toFloat(), y2.toFloat()) }, ms)

    fun global(a: String) {
        val action = when (a) {
            "back" -> GLOBAL_ACTION_BACK
            "home" -> GLOBAL_ACTION_HOME
            "recents" -> GLOBAL_ACTION_RECENTS
            "notifications" -> GLOBAL_ACTION_NOTIFICATIONS
            "quick" -> GLOBAL_ACTION_QUICK_SETTINGS
            "power" -> GLOBAL_ACTION_POWER_DIALOG
            "lock" -> if (Build.VERSION.SDK_INT >= 28) GLOBAL_ACTION_LOCK_SCREEN else return
            else -> return
        }
        performGlobalAction(action)
    }

    /** Types into the focused field: replaces the selection, or inserts at the cursor. */
    fun type(s: String) = edit { text, start, end -> Triple(text.replaceRange(start, end, s), start + s.length, true) }

    fun key(k: String) {
        when (k.lowercase()) {
            "back" -> edit { text, start, end ->
                when {
                    start != end -> Triple(text.removeRange(start, end), start, true)
                    start > 0 -> Triple(text.removeRange(start - 1, start), start - 1, true)
                    else -> Triple(text, start, false)
                }
            }
            "enter" -> {
                val node = rootInActiveWindow?.findFocus(AccessibilityNodeInfo.FOCUS_INPUT) ?: return
                if (Build.VERSION.SDK_INT >= 30) node.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
            }
        }
    }

    private fun edit(change: (String, Int, Int) -> Triple<String, Int, Boolean>) {
        val node = rootInActiveWindow?.findFocus(AccessibilityNodeInfo.FOCUS_INPUT) ?: return
        if (!node.isEditable) return
        val current = if (Build.VERSION.SDK_INT >= 26 && node.isShowingHintText) "" else node.text?.toString() ?: ""
        var start = node.textSelectionStart.takeIf { it in 0..current.length } ?: current.length
        var end = node.textSelectionEnd.takeIf { it in 0..current.length } ?: start
        if (start > end) start = end.also { end = start }
        val (text, caret, changed) = change(current, start, end)
        if (!changed) return
        node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, Bundle().apply {
            putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text)
        })
        node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, Bundle().apply {
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, caret)
            putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, caret)
        })
    }
}

/** Remote input for the phone screen (PROTOCOL.md section 7, Android types). */
object AccessibilityInput : InputSink {
    override fun input(ev: JSONObject, x: Int, y: Int, x2: Int, y2: Int) {
        val a = BridgeAccessibility.instance ?: return
        when (ev.str("t")) {
            "tap" -> a.tap(x, y, long = false)
            "long" -> a.tap(x, y, long = true)
            "swipe" -> a.swipe(x, y, x2, y2, ev.optLong("ms", 300))
            "global" -> a.global(ev.str("a") ?: "")
            "text" -> a.type(ev.str("s") ?: "")
            "key" -> a.key(ev.str("k") ?: "")
        }
    }
}

package com.btbridge.app

import android.app.Activity
import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.MediaPlayer
import android.media.RingtoneManager
import android.os.Handler
import android.os.Looper
import android.os.VibrationEffect
import android.os.Vibrator
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.widget.Toast
import androidx.core.app.NotificationManagerCompat
import com.btbridge.app.core.Log

/**
 * "Ring the phone" from the computer: the alarm sound at full alarm volume, with vibration, for 30 seconds
 * or until stopped from the notification or by opening the app.
 */
object Ring {
    private const val NOTIF_ID = 9
    private const val STOP = "com.btbridge.app.RING_STOP"
    private val main = Handler(Looper.getMainLooper())
    private var player: MediaPlayer? = null
    private var oldVolume = -1
    private var token = 0

    fun start(ctx: Context, from: String) {
        val app = ctx.applicationContext
        main.post { runCatching { startHere(app, from) }.onFailure { Log.w("ring: ${it.message}") } }
    }

    fun stop(ctx: Context) {
        val app = ctx.applicationContext
        main.post { stopHere(app) }
    }

    private fun startHere(ctx: Context, from: String) {
        stopHere(ctx)
        val am = ctx.getSystemService(AudioManager::class.java)
        runCatching {
            oldVolume = am.getStreamVolume(AudioManager.STREAM_ALARM)
            am.setStreamVolume(AudioManager.STREAM_ALARM, am.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0)
        }
        val uri = RingtoneManager.getActualDefaultRingtoneUri(ctx, RingtoneManager.TYPE_ALARM)
            ?: RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE)
        player = MediaPlayer().apply {
            setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ALARM)
                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build())
            setDataSource(ctx, uri)
            isLooping = true
            prepare()
            start()
        }
        @Suppress("DEPRECATION")
        runCatching {
            ctx.getSystemService(Vibrator::class.java)?.vibrate(VibrationEffect.createWaveform(longArrayOf(0, 600, 400), 0))
        }
        val stopIntent = PendingIntent.getBroadcast(ctx, 9, Intent(ctx, RingStopReceiver::class.java).setAction(STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val n = Notification.Builder(ctx, App.CHANNEL_EVENTS)
            .setSmallIcon(android.R.drawable.ic_lock_silent_mode_off)
            .setContentTitle("$from מצלצל לטלפון")
            .setContentText("הקש כדי להפסיק")
            .setContentIntent(stopIntent)
            .setDeleteIntent(stopIntent)
            .addAction(Notification.Action.Builder(null, "הפסק", stopIntent).build())
            .setAutoCancel(true)
            .build()
        runCatching { ctx.getSystemService(NotificationManager::class.java).notify(NOTIF_ID, n) }
        val mine = ++token
        main.postDelayed({ if (token == mine) stopHere(ctx) }, 30_000)
    }

    private fun stopHere(ctx: Context) {
        val p = player ?: return
        player = null
        runCatching { p.stop() }
        runCatching { p.release() }
        runCatching { ctx.getSystemService(Vibrator::class.java)?.cancel() }
        if (oldVolume >= 0) runCatching {
            ctx.getSystemService(AudioManager::class.java).setStreamVolume(AudioManager.STREAM_ALARM, oldVolume, 0)
        }
        oldVolume = -1
        runCatching { ctx.getSystemService(NotificationManager::class.java).cancel(NOTIF_ID) }
    }
}

class RingStopReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) = Ring.stop(context)
}

/**
 * Sends the phone's notifications to the computer (PROTOCOL.md section 5, "notif").
 * The user allows it once: Settings → Notification access → גשר בלוטוס.
 */
class BridgeNotifications : NotificationListenerService() {
    /** The last text sent per notification, so an update that changes nothing isn't sent again. */
    private val recent = object : LinkedHashMap<String, String>() {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, String>?) = size > 200
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        try {
            if (sbn.packageName == packageName || sbn.isOngoing) return
            val n = sbn.notification
            if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0) return
            val ex = n.extras
            val title = ex.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
            val text = (ex.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: ex.getCharSequence(Notification.EXTRA_TEXT))?.toString().orEmpty()
            if (title.isBlank() && text.isBlank()) return
            val sig = "$title\n$text"
            synchronized(recent) {
                if (recent[sbn.key] == sig) return
                recent[sbn.key] = sig
            }
            val app = runCatching {
                packageManager.getApplicationLabel(packageManager.getApplicationInfo(sbn.packageName, 0)).toString()
            }.getOrDefault(sbn.packageName)
            App.bridge.runInBackground { App.bridge.forwardNotification(app, title, text) }
        } catch (e: Exception) {
            Log.w("notification: ${e.message}")
        }
    }

    companion object {
        fun isEnabled(ctx: Context) = ctx.packageName in NotificationManagerCompat.getEnabledListenerPackages(ctx)
    }
}

/** Sends the phone's clipboard to the computer. Android lets only the app on screen read the clipboard. */
object ClipSend {
    /** Returns a message for the user. */
    fun send(text: String?): String {
        val b = App.bridge
        return when {
            text.isNullOrEmpty() -> "הלוח ריק"
            !b.store.shareClipboard -> "הלוח המשותף כבוי בהגדרות"
            b.sessions.value.none { it.has("clip") } -> "אין מחשב מחובר"
            b.shareClipboard(text, force = true) -> "הלוח נשלח למחשב"
            else -> "הלוח לא נשלח"
        }
    }

    fun current(ctx: Context): String? = runCatching {
        ctx.getSystemService(ClipboardManager::class.java)?.primaryClip?.takeIf { it.itemCount > 0 }
            ?.getItemAt(0)?.coerceToText(ctx)?.toString()
    }.getOrNull()
}

/**
 * "Send clipboard to computer" from the notification: an invisible screen that reads the clipboard
 * the moment it has focus (Android 10+ allows that only then), sends it, and closes.
 */
class ClipSendActivity : Activity() {
    private var done = false

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || done) return
        done = true
        Toast.makeText(this, ClipSend.send(ClipSend.current(this)), Toast.LENGTH_SHORT).show()
        finish()
    }
}

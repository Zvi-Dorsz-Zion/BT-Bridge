package com.btbridge.app

import android.app.Application
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.os.Build
import androidx.core.content.ContextCompat
import com.btbridge.app.core.Log
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class App : Application() {
    companion object {
        lateinit var bridge: Bridge
            private set
        const val CHANNEL_SERVICE = "service"
        const val CHANNEL_EVENTS = "events"
    }

    override fun onCreate() {
        super.onCreate()
        val logFile = File(filesDir, "log.txt")
        val fmt = SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.US)
        Log.sink = { msg ->
            android.util.Log.i("BtBridge", msg)
            synchronized(logFile) {
                if (logFile.length() > 1_000_000) logFile.renameTo(File(filesDir, "log.old.txt"))
                logFile.appendText("${fmt.format(Date())} $msg\n")
            }
        }
        Log.tail = { max ->
            synchronized(logFile) {
                runCatching {
                    java.io.RandomAccessFile(logFile, "r").use { f ->
                        val start = maxOf(0L, f.length() - max)
                        f.seek(start)
                        val b = ByteArray((f.length() - start).toInt())
                        f.readFully(b)
                        String(b, Charsets.UTF_8)
                    }
                }.getOrElse { "היומן לא נקרא: ${it.message}" }
            }
        }
        com.btbridge.app.core.AppInfo.version = runCatching { packageManager.getPackageInfo(packageName, 0).versionName }.getOrNull() ?: "0"
        // Notification channels exist from Android 8.0; before it the notifications simply have no channel.
        if (Build.VERSION.SDK_INT >= 26) {
            val nm = getSystemService(NotificationManager::class.java)
            nm.createNotificationChannel(NotificationChannel(CHANNEL_SERVICE, "פועל ברקע", NotificationManager.IMPORTANCE_LOW))
            nm.createNotificationChannel(NotificationChannel(CHANNEL_EVENTS, "חיבורים", NotificationManager.IMPORTANCE_DEFAULT))
        }
        bridge = Bridge(this)
    }
}

/** A system service; works on every Android version (getSystemService(Class) exists only from 6.0). */
inline fun <reified T : Any> Context.service(): T? = ContextCompat.getSystemService(this, T::class.java)

/** PendingIntent.FLAG_IMMUTABLE exists from Android 6.0; before it every PendingIntent is like that anyway. */
val PI_IMMUTABLE: Int = if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0

/** A notification on [channel]; Android 7.1 and older have no channels. */
fun notificationBuilder(ctx: Context, channel: String): Notification.Builder =
    if (Build.VERSION.SDK_INT >= 26) Notification.Builder(ctx, channel)
    else Notification.Builder(ctx)

/** A text-only button on a notification. */
fun notificationAction(title: String, intent: PendingIntent): Notification.Action =
    if (Build.VERSION.SDK_INT >= 23) Notification.Action.Builder(null as android.graphics.drawable.Icon?, title, intent).build()
    else Notification.Action.Builder(0, title, intent).build()

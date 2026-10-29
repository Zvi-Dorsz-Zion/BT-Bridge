package com.btbridge.app

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import androidx.core.content.FileProvider
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** Keeps Bluetooth listening and connections alive while the app is in the background. */
class BridgeService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var text = "מוכן לחיבורים"

    companion object {
        private const val ID = 1
        @Volatile private var instance: BridgeService? = null

        fun start(ctx: Context) = ctx.startForegroundService(Intent(ctx, BridgeService::class.java))

        /** Android 10+ needs a "media projection" foreground service before screen capture starts. */
        fun promoteForProjection(ctx: Context) {
            val s = instance ?: return
            s.startForeground(ID, s.build(s.text),
                ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE or ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        instance = this
        try {
            startForeground(ID, build(text), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        } catch (e: Exception) {
            // Android 14+ refuses this service type until the Bluetooth permission is granted.
            com.btbridge.app.core.Log.w("startForeground: ${e.message}")
        }

        val b = App.bridge
        b.start()
        val nm = getSystemService(NotificationManager::class.java)
        scope.launch {
            b.sessions.collect { list ->
                text = if (list.isEmpty()) "מוכן לחיבורים" else "מחובר ל: " + list.joinToString(", ") { b.displayName(it) }
                nm.notify(ID, build(text))
            }
        }
        scope.launch {
            var n = 100
            b.notices.collect { msg -> runCatching { nm.notify(n++, event(msg, openApp())) } }
        }
        scope.launch {
            var n = 1000
            b.received.collect { (file, from) ->
                val open = runCatching {
                    val uri = FileProvider.getUriForFile(this@BridgeService, "$packageName.files", file)
                    val mime = android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(file.extension.lowercase()) ?: "*/*"
                    PendingIntent.getActivity(this@BridgeService, n,
                        Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),
                        PendingIntent.FLAG_IMMUTABLE)
                }.getOrNull() ?: openApp()
                runCatching { nm.notify(n++, event("התקבל ${file.name} מ-$from", open)) }
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onDestroy() {
        instance = null
        scope.cancel()
        Projection.stop()
        super.onDestroy()
    }

    private fun openApp(): PendingIntent = PendingIntent.getActivity(
        this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    private fun event(msg: String, intent: PendingIntent): Notification =
        Notification.Builder(this, App.CHANNEL_EVENTS)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentTitle("גשר בלוטוס")
            .setContentText(msg)
            .setStyle(Notification.BigTextStyle().bigText(msg))
            .setContentIntent(intent)
            .setAutoCancel(true)
            .build()

    private fun build(text: String): Notification =
        Notification.Builder(this, App.CHANNEL_SERVICE)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentTitle("גשר בלוטוס")
            .setContentText(text)
            .setOngoing(true)
            .setContentIntent(openApp())
            .build()
}

/** Starts the background service after the phone boots, so the computer can connect without opening the app. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == Intent.ACTION_BOOT_COMPLETED || intent.action == Intent.ACTION_MY_PACKAGE_REPLACED)
            runCatching { BridgeService.start(context) }
    }
}

package com.btbridge.app

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
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
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL_SERVICE, "פועל ברקע", NotificationManager.IMPORTANCE_LOW))
        nm.createNotificationChannel(NotificationChannel(CHANNEL_EVENTS, "חיבורים", NotificationManager.IMPORTANCE_DEFAULT))
        bridge = Bridge(this)
    }
}

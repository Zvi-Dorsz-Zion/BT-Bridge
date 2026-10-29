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
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL_SERVICE, "פועל ברקע", NotificationManager.IMPORTANCE_LOW))
        nm.createNotificationChannel(NotificationChannel(CHANNEL_EVENTS, "חיבורים", NotificationManager.IMPORTANCE_DEFAULT))
        bridge = Bridge(this)
    }
}

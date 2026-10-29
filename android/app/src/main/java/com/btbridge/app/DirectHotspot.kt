package com.btbridge.app

import android.annotation.SuppressLint
import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import com.btbridge.app.core.Log
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * Direct network (PROTOCOL.md section 8): a temporary private hotspot (random name and password,
 * no internet sharing) that the computer joins when there is no shared network.
 * Android 11 needs the location permission and location turned on for this.
 */
class DirectHotspot(private val ctx: Context) {
    class Credentials(val ssid: String, val pass: String)

    private val main = Handler(Looper.getMainLooper())
    @Volatile private var reservation: WifiManager.LocalOnlyHotspotReservation? = null
    @Volatile private var creds: Credentials? = null

    val active: Boolean get() = reservation != null

    /** Opens the hotspot (or returns the open one). Blocks up to 20 seconds; throws with a Hebrew message. */
    @SuppressLint("MissingPermission")
    fun start(): Credentials {
        creds?.let { if (reservation != null) return it }
        val wifi = ctx.applicationContext.getSystemService(WifiManager::class.java) ?: error("אין וויפי בטלפון")
        val done = CountDownLatch(1)
        var error: String? = null
        val cb = object : WifiManager.LocalOnlyHotspotCallback() {
            override fun onStarted(r: WifiManager.LocalOnlyHotspotReservation) {
                reservation = r
                creds = read(r)
                done.countDown()
            }

            override fun onFailed(reason: Int) {
                error = when (reason) {
                    ERROR_INCOMPATIBLE_MODE -> "הטלפון לא יכול לפתוח נקודה חמה בזמן שהוא מחובר לוויפי אחר"
                    ERROR_TETHERING_DISALLOWED -> "פתיחת נקודה חמה חסומה בטלפון הזה"
                    else -> "פתיחת הנקודה החמה נכשלה (קוד $reason)"
                }
                done.countDown()
            }

            override fun onStopped() {
                reservation = null
                creds = null
            }
        }
        main.post {
            try {
                wifi.startLocalOnlyHotspot(cb, main)
            } catch (e: SecurityException) {
                error = "לרשת ישירה צריך הרשאת מיקום ומיקום דלוק בטלפון"
                done.countDown()
            } catch (e: IllegalStateException) {
                error = "נקודה חמה אחרת כבר פתוחה בטלפון"
                done.countDown()
            }
        }
        if (!done.await(20, TimeUnit.SECONDS)) error("הנקודה החמה לא נפתחה בזמן")
        error?.let { error(it) }
        val c = creds ?: error("לא התקבלו פרטי הרשת")
        Log.w("direct network up: ${c.ssid}")
        return c
    }

    fun stop() {
        val r = reservation ?: return
        reservation = null
        creds = null
        runCatching { r.close() }
        Log.w("direct network closed")
    }

    @Suppress("DEPRECATION")
    private fun read(r: WifiManager.LocalOnlyHotspotReservation): Credentials? =
        if (Build.VERSION.SDK_INT >= 30) {
            val c = r.softApConfiguration
            val ssid = c.ssid
            val pass = c.passphrase
            if (ssid != null && pass != null) Credentials(ssid.trim('"'), pass) else null
        } else {
            val c = r.wifiConfiguration
            if (c?.SSID != null && c.preSharedKey != null) Credentials(c.SSID.trim('"'), c.preSharedKey.trim('"')) else null
        }
}

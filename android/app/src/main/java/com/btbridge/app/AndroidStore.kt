package com.btbridge.app

import android.content.Context
import android.os.Build
import android.os.Environment
import com.btbridge.app.core.Crypto
import com.btbridge.app.core.Identity
import com.btbridge.app.core.Store
import com.btbridge.app.core.TrustedDevice
import kotlinx.coroutines.flow.MutableStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.KeyFactory
import java.security.KeyPair
import java.security.spec.PKCS8EncodedKeySpec
import java.security.spec.X509EncodedKeySpec

/** A folder or file on another device that the user saved for quick access. */
data class SavedPlace(val name: String, val path: String, val isDir: Boolean)

/** Settings, password key and trusted devices, in the app's private storage. */
class AndroidStore(val ctx: Context) : Store {
    private val prefs = ctx.getSharedPreferences("btbridge", Context.MODE_PRIVATE)

    /** Bumped on every change, so the UI can refresh. */
    val version = MutableStateFlow(0)

    override val deviceName: String
        get() = prefs.getString("name", null) ?: "הטלפון שלי"

    fun setDeviceName(v: String) { prefs.edit().putString("name", v).apply(); version.value++ }

    /** Download/גשר בלוטוס. Without "all files access" Android blocks it, so the app's own folder is used instead. */
    val downloadDir: File
        get() = if (Build.VERSION.SDK_INT < 30 || Environment.isExternalStorageManager())
            File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "גשר בלוטוס")
        else File(ctx.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), "גשר בלוטוס")

    /** Where "download" saves, in words for the user. */
    val downloadLabel: String
        get() = if (Build.VERSION.SDK_INT < 30 || Environment.isExternalStorageManager()) "הורדות ← גשר בלוטוס"
        else "התיקייה הפנימית של האפליקציה"

    /** Files opened only for viewing are deleted after this many hours. */
    var viewCleanupHours: Int
        get() = prefs.getInt("viewCleanupHours", 1)
        set(v) { prefs.edit().putInt("viewCleanupHours", v).apply(); version.value++ }

    /** A new temporary folder for a file opened only for viewing (deleted automatically later). */
    fun newViewDir(): File = File(File(ctx.cacheDir, "צפייה"), System.currentTimeMillis().toString()).apply { mkdirs() }

    /** Lets known devices view and control this phone's screen. */
    var allowControl: Boolean
        get() = prefs.getBoolean("allowControl", true)
        set(v) { prefs.edit().putBoolean("allowControl", v).apply(); version.value++ }

    /** Text copied here can be pasted on the computer, and the other way. */
    var shareClipboard: Boolean
        get() = prefs.getBoolean("shareClipboard", true)
        set(v) { prefs.edit().putBoolean("shareClipboard", v).apply(); version.value++ }

    /** Notifications that appear on the phone also show up on the computer (needs "notification access"). */
    var forwardNotifications: Boolean
        get() = prefs.getBoolean("forwardNotifications", true)
        set(v) { prefs.edit().putBoolean("forwardNotifications", v).apply(); version.value++ }

    /** Optional permissions the user chose not to be reminded about on the home screen. */
    var dismissedPermissions: Set<String>
        get() = prefs.getStringSet("dismissedPermissions", emptySet()) ?: emptySet()
        set(v) { prefs.edit().putStringSet("dismissedPermissions", v).apply(); version.value++ }

    /** The saved key could not be read and a new one was made: known devices will ask for the password again. */
    @Volatile var identityWasReset = false

    /** "auto": open a temporary hotspot for the computer when there is no shared network. "off": never. */
    var directMode: String
        get() = prefs.getString("directMode", "auto") ?: "auto"
        set(v) { prefs.edit().putString("directMode", v).apply(); version.value++ }

    /** Sort chosen for a folder of a device ("name|asc", "date|desc"...), or null for the usual order. */
    fun sortFor(fp: String, path: String): Pair<String, Boolean>? =
        prefs.getString("sort:$fp|$path", null)?.split('|')?.takeIf { it.size == 2 }?.let { it[0] to (it[1] == "desc") }

    fun setSort(fp: String, path: String, by: String, desc: Boolean) {
        prefs.edit().putString("sort:$fp|$path", "$by|${if (desc) "desc" else "asc"}").apply()
        version.value++
    }

    /** Saved places ("שמורים") on a device, by its fingerprint, in the order they were added. */
    @Synchronized
    fun savedFor(fp: String): List<SavedPlace> {
        val arr = JSONArray(prefs.getString("saved:$fp", "[]"))
        return (0 until arr.length()).map {
            val o = arr.getJSONObject(it)
            SavedPlace(o.getString("name"), o.getString("path"), o.optBoolean("dir", true))
        }
    }

    fun isSaved(fp: String, path: String) = savedFor(fp).any { it.path == path }

    @Synchronized
    fun addSaved(fp: String, p: SavedPlace) {
        val list = savedFor(fp)
        if (list.any { it.path == p.path }) return
        writeSaved(fp, list + p)
    }

    @Synchronized
    fun removeSaved(fp: String, path: String) = writeSaved(fp, savedFor(fp).filterNot { it.path == path })

    private fun writeSaved(fp: String, list: List<SavedPlace>) {
        val arr = JSONArray()
        list.forEach { arr.put(JSONObject().put("name", it.name).put("path", it.path).put("dir", it.isDir)) }
        prefs.edit().putString("saved:$fp", arr.toString()).apply()
        version.value++
    }

    /** Local nickname for a device, by fingerprint (or Bluetooth address for devices never connected). */
    fun nameFor(key: String, fallback: String): String = prefs.getString("nick:$key", null)?.takeIf { it.isNotBlank() } ?: fallback

    fun setNickname(key: String, name: String?) {
        if (name.isNullOrBlank()) prefs.edit().remove("nick:$key").apply()
        else prefs.edit().putString("nick:$key", name.trim()).apply()
        version.value++
    }

    override val pwSalt: String get() = prefs.getString("pwSalt", "") ?: ""
    override val pwIter: Int get() = prefs.getInt("pwIter", 50_000)
    override val hasPassword: Boolean get() = prefs.contains("pwKey")
    override fun passwordKey(): ByteArray = Crypto.unb64(prefs.getString("pwKey", "")!!)

    fun setPassword(password: String) {
        val salt = Crypto.random(16)
        val key = Crypto.pbkdf2(password, salt, 50_000)
        prefs.edit().putString("pwSalt", Crypto.b64(salt)).putInt("pwIter", 50_000).putString("pwKey", Crypto.b64(key)).apply()
        version.value++
    }

    // ------------------------------------------------------------------ trust

    @Synchronized
    fun trusted(): List<TrustedDevice> {
        val arr = JSONArray(prefs.getString("trusted", "[]"))
        return (0 until arr.length()).map {
            val o = arr.getJSONObject(it)
            TrustedDevice(o.getString("fp"), o.getString("pub"), o.getString("name"), o.optString("address"), o.optString("os"))
        }
    }

    @Synchronized
    private fun saveTrusted(list: List<TrustedDevice>) {
        val arr = JSONArray()
        list.forEach {
            arr.put(JSONObject().put("fp", it.fp).put("pub", it.pub).put("name", it.name).put("address", it.address).put("os", it.os))
        }
        prefs.edit().putString("trusted", arr.toString()).apply()
        version.value++
    }

    override fun findByFp(fp: String) = trusted().firstOrNull { it.fp == fp }
    override fun findByAddress(address: String) =
        if (address.isEmpty()) null else trusted().firstOrNull { it.address == address }

    @Synchronized
    override fun trust(fp: String, pub: ByteArray, name: String, address: String, os: String) {
        val list = trusted().filterNot { it.fp == fp || (address.isNotEmpty() && it.address == address) }
        saveTrusted(list + TrustedDevice(fp, Crypto.b64(pub), name, address, os))
    }

    @Synchronized
    override fun touch(fp: String, name: String, address: String) {
        val list = trusted()
        val t = list.firstOrNull { it.fp == fp } ?: return
        if (t.name == name && (address.isEmpty() || t.address == address)) return
        saveTrusted(list.map { if (it.fp == fp) it.copy(name = name, address = address.ifEmpty { it.address }) else it })
    }

    @Synchronized
    fun forget(fp: String) = saveTrusted(trusted().filterNot { it.fp == fp })

    // ------------------------------------------------------------------ identity

    fun identity(): Identity {
        val f = File(ctx.filesDir, "identity.key")
        if (f.exists()) {
            try {
                val o = JSONObject(f.readText())
                val kf = KeyFactory.getInstance("EC")
                val pub = kf.generatePublic(X509EncodedKeySpec(Crypto.unb64(o.getString("pub"))))
                val priv = kf.generatePrivate(PKCS8EncodedKeySpec(Crypto.unb64(o.getString("priv"))))
                return Identity(KeyPair(pub, priv))
            } catch (e: Exception) {
                // Keep the unreadable file and tell the user once.
                com.btbridge.app.core.Log.w("identity load failed, creating new: ${e.message}")
                runCatching { f.copyTo(File(ctx.filesDir, "identity.key.bad-${System.currentTimeMillis()}"), overwrite = true) }
                identityWasReset = true
            }
        }
        val kp = Crypto.newKeyPair()
        f.writeText(JSONObject().put("pub", Crypto.b64(kp.public.encoded)).put("priv", Crypto.b64(kp.private.encoded)).toString())
        return Identity(kp)
    }
}

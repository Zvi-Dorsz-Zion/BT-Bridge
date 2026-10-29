package com.btbridge.app.core

import com.btbridge.app.core.Crypto.b64
import com.btbridge.app.core.Crypto.concat
import com.btbridge.app.core.Crypto.expand
import com.btbridge.app.core.Crypto.hmac
import com.btbridge.app.core.Crypto.same
import com.btbridge.app.core.Crypto.sha256
import com.btbridge.app.core.Crypto.u32
import com.btbridge.app.core.Crypto.unb64
import com.btbridge.app.core.Crypto.utf8
import org.json.JSONArray
import org.json.JSONObject
import java.io.InputStream
import java.io.OutputStream
import java.security.KeyPair
import java.security.interfaces.ECPublicKey
import java.util.concurrent.CancellationException

data class TrustedDevice(val fp: String, val pub: String, val name: String, val address: String, val os: String)

/** Persistent settings and trust list. Android: SharedPreferences. Tests: in memory. */
interface Store {
    val deviceName: String
    val pwSalt: String
    val pwIter: Int
    val hasPassword: Boolean
    fun passwordKey(): ByteArray
    fun findByFp(fp: String): TrustedDevice?
    fun findByAddress(address: String): TrustedDevice?
    fun trust(fp: String, pub: ByteArray, name: String, address: String, os: String)
    fun touch(fp: String, name: String, address: String)
}

/** This device's long-term ECDSA P-256 key. */
class Identity(private val kp: KeyPair) {
    val public: ByteArray = Crypto.publicBytes(kp.public as ECPublicKey)
    val fp: String = Crypto.fingerprint(public)
    fun sign(data: ByteArray): ByteArray = Crypto.sign(kp.private, data)
}

class PasswordAnswer(val password: String, val remember: Boolean)

/** Asks the user for the other device's password. Returns null when cancelled. Called on a worker thread. */
fun interface AskPassword {
    fun ask(peerName: String, wrongBefore: Boolean): PasswordAnswer?
}

class HandshakeResult(
    val codec: RecordCodec, val linkKey: ByteArray,
    val peerName: String, val peerOs: String, val peerFp: String, val peerPub: ByteArray,
    val peerApp: String, val peerCaps: Set<String>,
)

class AuthException(val code: String, message: String) : ProtocolException(message)

/** This app's version, sent to the other side. The app sets it at start; tests leave the default. */
object AppInfo {
    @Volatile var version = "0"

    /** -1, 0 or 1, comparing "1.2.3" strings; anything unreadable counts as oldest. */
    fun compare(a: String, b: String): Int {
        fun parts(s: String) = s.split('.').map { it.toIntOrNull() ?: 0 }
        val x = parts(a)
        val y = parts(b)
        for (i in 0 until maxOf(x.size, y.size)) {
            val d = (x.getOrElse(i) { 0 }).compareTo(y.getOrElse(i) { 0 })
            if (d != 0) return d
        }
        return 0
    }
}

/** 5 wrong passwords in a row lock password attempts for 5 minutes. */
class Lockout {
    private var fails = 0
    private var until = 0L
    @get:Synchronized val locked: Boolean get() = System.currentTimeMillis() < until
    @Synchronized fun reset() { fails = 0 }
    @Synchronized fun fail() {
        if (++fails < 5) return
        fails = 0
        until = System.currentTimeMillis() + 5 * 60_000
    }
}

/** PROTOCOL.md section 2. */
object Handshake {
    const val VERSION = 2

    /** Optional features this side understands (PROTOCOL.md section 2, "caps"). */
    val CAPS = listOf("clip", "notif", "resume", "ring", "battery", "update", "log", "search")

    /** PBKDF2 rounds a peer may ask for: fewer would make a captured proof easy to crack, more would freeze us. */
    const val MIN_ITER = 10_000
    const val MAX_ITER = 5_000_000

    fun client(input: InputStream, out: OutputStream, me: Identity, st: Store, os: String,
               peerAddress: String, ask: AskPassword): HandshakeResult {
        val eph = Crypto.newKeyPair()
        val c = utf8(hello(me, st, eph, os).toString())
        Wire.write(out, c)

        val s = Wire.read(input)
        val sh = parseHello(s)
        val peerPub = unb64(sh.getString("id"))
        val fp = Crypto.fingerprint(peerPub)
        val peerName = sh.str("name") ?: "?"
        val peerOs = sh.str("os") ?: ""

        // Trust is by key. The Bluetooth address only catches a reinstalled device, to explain the password prompt.
        val known = st.findByFp(fp)
        val byAddress = st.findByAddress(peerAddress)
        if (known == null && byAddress != null)
            throw AuthException("keychanged",
                "המפתח של \"${byAddress.name}\" השתנה מאז הפעם הקודמת. אם התקנת אותו מחדש, בחר \"שכח מכשיר\" ונסה שוב.")

        val (codec, linkKey, th) = derive(eph, unb64(sh.getString("eph")), c, s, client = true)
        val pwInfo = sh.obj("pw") ?: throw ProtocolException("הודעת פתיחה לא תקינה")
        val trustsMe = sh.bool("trusted") == true
        val needPw = !(trustsMe && known != null)
        Log.w("handshake with $peerName ($peerAddress): they trust us=$trustsMe, we trust them=${known != null}")
        if (needPw && pwInfo.bool("set") == false)
            throw AuthException("nopassword", "ב-\"$peerName\" עוד לא נקבעה סיסמה. קבע סיסמה שם ונסה שוב.")
        val iter = pwInfo.optInt("iter")
        if (needPw && iter !in MIN_ITER..MAX_ITER) throw ProtocolException("הגדרות הסיסמה במכשיר השני לא תקינות")

        var wrong = false
        repeat(3) {
            var key: ByteArray? = null
            var remember = false
            if (needPw) {
                val a = ask.ask(peerName, wrong) ?: throw CancellationException("בוטל")
                key = Crypto.pbkdf2(a.password, unb64(pwInfo.getString("salt")), iter)
                remember = a.remember
            }
            val auth = JSONObject()
                .put("sig", b64(me.sign(concat(utf8("btb-c"), th))))
                .put("pw", if (key == null) JSONObject.NULL else b64(hmac(key, utf8("btb-pw-c"), th)))
                .put("remember", remember)
            Wire.write(out, codec.seal(Rec.json(Rec.AUTH, 0, auth)))

            val p = Rec.check(codec.open(Wire.read(input)))
            if (Rec.type(p) != Rec.AUTH_RESULT) throw ProtocolException("תשובה לא צפויה")
            val res = Rec.parseJson(p)
            if (res.bool("ok") == true) {
                if (!Crypto.verify(peerPub, concat(utf8("btb-s"), th), unb64(res.getString("sig"))))
                    throw AuthException("sig", "חתימת המכשיר השני לא תקינה")
                if (key != null) {
                    // The other side proves it knows its own password too, so we can trust it back.
                    val proof = res.str("pw")?.let { unb64(it) }
                    if (!same(proof, hmac(key, utf8("btb-pw-s"), th)))
                        throw AuthException("sig", "המכשיר השני לא הוכיח שהוא יודע את הסיסמה")
                    if (remember) st.trust(fp, peerPub, peerName, peerAddress, peerOs)
                } else st.touch(fp, peerName, peerAddress)
                return result(codec, linkKey, peerName, peerOs, fp, peerPub, sh)
            }
            val err = res.str("err") ?: ""
            if (err == "password" && needPw) { wrong = true; return@repeat }
            throw AuthException(err, when (err) {
                "password" -> "סיסמה שגויה"
                "locked" -> "יותר מדי ניסיונות שגויים. נסה שוב בעוד 5 דקות."
                "nopassword" -> "במכשיר השני עוד לא נקבעה סיסמה"
                else -> "האימות נכשל ($err)"
            })
        }
        throw AuthException("password", "סיסמה שגויה")
    }

    fun server(input: InputStream, out: OutputStream, me: Identity, st: Store, os: String,
               peerAddress: String, lockout: Lockout): HandshakeResult {
        val c = Wire.read(input)
        val ch = parseHello(c)
        val peerPub = unb64(ch.getString("id"))
        val fp = Crypto.fingerprint(peerPub)
        val peerName = ch.str("name") ?: "?"
        val peerOs = ch.str("os") ?: ""
        val trusted = st.findByFp(fp) != null

        val eph = Crypto.newKeyPair()
        // The password details always go out: the client may not trust us even when we trust it.
        val hello = hello(me, st, eph, os)
            .put("pw", JSONObject()
                .put("salt", if (st.hasPassword) st.pwSalt else b64(Crypto.random(16)))
                .put("iter", st.pwIter)
                .put("set", st.hasPassword))
            .put("trusted", trusted)
        val s = utf8(hello.toString())
        Wire.write(out, s)

        val (codec, linkKey, th) = derive(eph, unb64(ch.getString("eph")), c, s, client = false)
        repeat(3) {
            val p = Rec.check(codec.open(Wire.read(input)))
            if (Rec.type(p) != Rec.AUTH) throw ProtocolException("תשובה לא צפויה")
            val auth = Rec.parseJson(p)

            if (!Crypto.verify(peerPub, concat(utf8("btb-c"), th), unb64(auth.getString("sig"))))
                fail(out, codec, "sig")

            val proof = auth.str("pw")?.let { unb64(it) }
            if (proof == null) {
                if (!trusted) fail(out, codec, "password")
                st.touch(fp, peerName, peerAddress)
                ok(out, codec, me, th, null)
                Log.w("handshake with $peerName ($peerAddress): trusted both ways")
                return result(codec, linkKey, peerName, peerOs, fp, peerPub, ch)
            }
            if (!st.hasPassword) fail(out, codec, "nopassword")
            if (lockout.locked) fail(out, codec, "locked")

            val key = st.passwordKey()
            if (same(proof, hmac(key, utf8("btb-pw-c"), th))) {
                lockout.reset()
                val remember = auth.bool("remember") == true
                if (remember) st.trust(fp, peerPub, peerName, peerAddress, peerOs)
                ok(out, codec, me, th, hmac(key, utf8("btb-pw-s"), th))
                Log.w("handshake with $peerName ($peerAddress): password ok, remember=$remember, we trusted them before=$trusted")
                return result(codec, linkKey, peerName, peerOs, fp, peerPub, ch)
            }
            lockout.fail()
            Log.w("wrong password from $peerName ($peerAddress)")
            Wire.write(out, codec.seal(Rec.json(Rec.AUTH_RESULT, 0, JSONObject().put("ok", false).put("err", "password"))))
        }
        throw AuthException("password", "סיסמה שגויה")
    }

    // ------------------------------------------------------------------ helpers

    private fun hello(me: Identity, st: Store, eph: KeyPair, os: String) = JSONObject()
        .put("v", VERSION)
        .put("name", st.deviceName)
        .put("os", os)
        .put("app", AppInfo.version)
        .put("caps", JSONArray(CAPS))
        .put("id", b64(me.public))
        .put("eph", b64(Crypto.publicBytes(eph.public as ECPublicKey)))
        .put("nonce", b64(Crypto.random(32)))

    private fun result(codec: RecordCodec, linkKey: ByteArray, name: String, os: String, fp: String, pub: ByteArray, hello: JSONObject): HandshakeResult {
        val arr = hello.optJSONArray("caps")
        val caps = if (arr == null) emptySet() else (0 until arr.length()).map { arr.optString(it) }.toSet()
        return HandshakeResult(codec, linkKey, name, os, fp, pub, hello.str("app") ?: "", caps)
    }

    private fun parseHello(b: ByteArray): JSONObject {
        val o = try { JSONObject(String(b, Charsets.UTF_8)) } catch (e: Exception) { throw ProtocolException("הודעת פתיחה לא תקינה") }
        if (o.optInt("v") != VERSION)
            throw ProtocolException("גרסת הפרוטוקול במכשיר השני שונה. עדכן את שני הצדדים לאותה גרסה של גשר בלוטוס.")
        return o
    }

    private data class Keys(val codec: RecordCodec, val linkKey: ByteArray, val th: ByteArray)

    private fun derive(eph: KeyPair, peerEph: ByteArray, c: ByteArray, s: ByteArray, client: Boolean): Keys {
        val th = sha256(u32(c.size), c, u32(s.size), s)
        val prk = hmac(th, Crypto.ecdh(eph.private, peerEph))
        val c2s = expand(prk, "btb c2s")
        val s2c = expand(prk, "btb s2c")
        val codec = if (client) RecordCodec(c2s, s2c) else RecordCodec(s2c, c2s)
        return Keys(codec, expand(prk, "btb link"), th)
    }

    private fun ok(out: OutputStream, codec: RecordCodec, me: Identity, th: ByteArray, pwProof: ByteArray?) {
        val o = JSONObject()
            .put("ok", true)
            .put("sig", b64(me.sign(concat(utf8("btb-s"), th))))
            .put("pw", if (pwProof == null) JSONObject.NULL else b64(pwProof))
        Wire.write(out, codec.seal(Rec.json(Rec.AUTH_RESULT, 0, o)))
    }

    private fun fail(out: OutputStream, codec: RecordCodec, err: String): Nothing {
        try { Wire.write(out, codec.seal(Rec.json(Rec.AUTH_RESULT, 0, JSONObject().put("ok", false).put("err", err)))) }
        catch (_: Exception) { }
        throw AuthException(err, "האימות נכשל: $err")
    }
}

/** Logging that works both on Android and in JVM unit tests. */
object Log {
    @Volatile var sink: (String) -> Unit = { println(it) }
    fun w(msg: String) = try { sink(msg) } catch (_: Exception) { }

    /** The end of the log, for the other device's "show log" (Android sets it to read its log file). */
    @Volatile var tail: (Int) -> String = { "" }
}

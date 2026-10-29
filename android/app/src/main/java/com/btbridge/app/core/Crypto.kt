package com.btbridge.app.core

import org.json.JSONObject
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.math.BigInteger
import java.nio.ByteBuffer
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import java.text.Normalizer
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/** Crypto primitives shared with the Windows side. See PROTOCOL.md sections 2-3. */
object Crypto {
    private val rnd = SecureRandom()

    fun utf8(s: String): ByteArray = s.toByteArray(Charsets.UTF_8)

    fun concat(vararg parts: ByteArray): ByteArray {
        val o = ByteArray(parts.sumOf { it.size })
        var at = 0
        for (p in parts) { p.copyInto(o, at); at += p.size }
        return o
    }

    fun u32(v: Int): ByteArray = ByteBuffer.allocate(4).putInt(v).array()

    fun hmac(key: ByteArray, vararg parts: ByteArray): ByteArray {
        val m = Mac.getInstance("HmacSHA256")
        m.init(SecretKeySpec(key, "HmacSHA256"))
        for (p in parts) m.update(p)
        return m.doFinal()
    }

    fun sha256(vararg parts: ByteArray): ByteArray {
        val d = MessageDigest.getInstance("SHA-256")
        for (p in parts) d.update(p)
        return d.digest()
    }

    fun expand(prk: ByteArray, info: String): ByteArray = hmac(prk, utf8(info), byteArrayOf(1))

    fun random(n: Int): ByteArray = ByteArray(n).also { rnd.nextBytes(it) }

    /** PBKDF2-HMAC-SHA256, one 32-byte block. Written out so the password bytes are exactly UTF-8(NFC). */
    fun pbkdf2(password: String, salt: ByteArray, iter: Int): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(utf8(Normalizer.normalize(password, Normalizer.Form.NFC)), "HmacSHA256"))
        mac.update(salt)
        mac.update(byteArrayOf(0, 0, 0, 1))
        var u = mac.doFinal()
        val t = u.copyOf()
        for (i in 1 until iter) {
            u = mac.doFinal(u)
            for (j in t.indices) t[j] = (t[j].toInt() xor u[j].toInt()).toByte()
        }
        return t
    }

    fun same(a: ByteArray?, b: ByteArray?): Boolean =
        a != null && b != null && a.size == b.size && MessageDigest.isEqual(a, b)

    // ------------------------------------------------------------------ P-256 keys

    private val p256: ECParameterSpec by lazy { (newKeyPair().public as ECPublicKey).params }

    fun newKeyPair(): KeyPair {
        val g = KeyPairGenerator.getInstance("EC")
        g.initialize(ECGenParameterSpec("secp256r1"), rnd)
        return g.generateKeyPair()
    }

    private fun fixed32(b: BigInteger): ByteArray {
        val raw = b.toByteArray()
        return when {
            raw.size == 32 -> raw
            raw.size > 32 -> raw.copyOfRange(raw.size - 32, raw.size)
            else -> ByteArray(32 - raw.size) + raw
        }
    }

    fun publicBytes(k: ECPublicKey): ByteArray =
        concat(byteArrayOf(4), fixed32(k.w.affineX), fixed32(k.w.affineY))

    fun fromPublic(pub: ByteArray): ECPublicKey {
        if (pub.size != 65 || pub[0] != 4.toByte()) throw ProtocolException("מפתח ציבורי לא תקין")
        val x = BigInteger(1, pub.copyOfRange(1, 33))
        val y = BigInteger(1, pub.copyOfRange(33, 65))
        return KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(ECPoint(x, y), p256)) as ECPublicKey
    }

    fun fingerprint(pub: ByteArray): String =
        sha256(pub).copyOf(8).joinToString("") { "%02X".format(it.toInt() and 0xff) }

    /** The fingerprint for people: digits only ("1234-5678-9012"), the same on both sides. */
    fun fingerprintDigits(fp: String): String {
        val v = fp.toULongOrNull(16) ?: return fp
        val d = (v % 1_000_000_000_000UL).toString().padStart(12, '0')
        return "${d.substring(0, 4)}-${d.substring(4, 8)}-${d.substring(8)}"
    }

    fun ecdh(mine: PrivateKey, peerPub: ByteArray): ByteArray {
        val ka = KeyAgreement.getInstance("ECDH")
        ka.init(mine)
        ka.doPhase(fromPublic(peerPub), true)
        return ka.generateSecret()
    }

    fun sign(key: PrivateKey, data: ByteArray): ByteArray =
        Signature.getInstance("SHA256withECDSA").run { initSign(key); update(data); sign() }

    fun verify(pub: ByteArray, data: ByteArray, sig: ByteArray): Boolean = try {
        Signature.getInstance("SHA256withECDSA").run { initVerify(fromPublic(pub)); update(data); verify(sig) }
    } catch (e: Exception) { false }

    fun b64(b: ByteArray): String = Base64.getEncoder().encodeToString(b)
    fun unb64(s: String): ByteArray = Base64.getDecoder().decode(s)
}

open class ProtocolException(message: String) : IOException(message)

/** An error reported by the other side ({"err":code,"msg":text}). */
class RemoteException(val code: String, message: String) : IOException(message)

/** AES-256-GCM records with a per-direction counter nonce. */
class RecordCodec(sendKey: ByteArray, recvKey: ByteArray) {
    private val sendK = SecretKeySpec(sendKey, "AES")
    private val recvK = SecretKeySpec(recvKey, "AES")
    private val enc = Cipher.getInstance("AES/GCM/NoPadding")
    private val dec = Cipher.getInstance("AES/GCM/NoPadding")
    private var sendCtr = 0L
    private var recvCtr = 0L

    private fun nonce(c: Long) = ByteBuffer.allocate(12).putInt(0).putLong(c).array()

    fun seal(plain: ByteArray): ByteArray {
        enc.init(Cipher.ENCRYPT_MODE, sendK, GCMParameterSpec(128, nonce(sendCtr++)))
        return enc.doFinal(plain)
    }

    fun open(rec: ByteArray): ByteArray {
        if (rec.size < 16) throw ProtocolException("רשומה קצרה מדי")
        dec.init(Cipher.DECRYPT_MODE, recvK, GCMParameterSpec(128, nonce(recvCtr++)))
        return dec.doFinal(rec)
    }
}

/** Plaintext record layout: [u8 type][u32 id][body]. */
object Rec {
    const val REQ = 1; const val RES = 2; const val DATA = 3; const val END = 4; const val CANCEL = 5
    const val PING = 6; const val PONG = 7; const val EVT = 8; const val DONE = 9
    const val AUTH = 10; const val AUTH_RESULT = 11

    fun make(type: Int, id: Int, body: ByteArray = ByteArray(0), off: Int = 0, len: Int = body.size): ByteArray {
        val o = ByteArray(5 + len)
        o[0] = type.toByte()
        ByteBuffer.wrap(o, 1, 4).putInt(id)
        System.arraycopy(body, off, o, 5, len)
        return o
    }

    fun json(type: Int, id: Int, o: JSONObject) = make(type, id, Crypto.utf8(o.toString()))

    fun type(p: ByteArray): Int = p[0].toInt() and 0xff
    fun id(p: ByteArray): Int = ByteBuffer.wrap(p, 1, 4).int
    fun parseJson(p: ByteArray): JSONObject = JSONObject(String(p, 5, p.size - 5, Charsets.UTF_8))

    fun check(p: ByteArray): ByteArray {
        if (p.size < 5) throw ProtocolException("רשומה קצרה מדי")
        return p
    }
}

/** [u32 length][bytes] framing on a raw stream. */
object Wire {
    const val MAX_FRAME = 2 * 1024 * 1024

    fun write(out: OutputStream, data: ByteArray) {
        val o = ByteArray(4 + data.size)
        ByteBuffer.wrap(o).putInt(data.size)
        data.copyInto(o, 4)
        out.write(o)
        out.flush()
    }

    fun read(input: InputStream): ByteArray {
        val len = ByteArray(4)
        readExact(input, len)
        val n = ByteBuffer.wrap(len).int
        if (n < 0 || n > MAX_FRAME) throw ProtocolException("מסגרת גדולה מדי")
        val buf = ByteArray(n)
        readExact(input, buf)
        return buf
    }

    private fun readExact(input: InputStream, buf: ByteArray) {
        var at = 0
        while (at < buf.size) {
            val r = input.read(buf, at, buf.size - at)
            if (r <= 0) throw EOFException("החיבור נסגר")
            at += r
        }
    }
}

/** Helpers for org.json, where a JSON null and a missing key both mean "absent". */
fun JSONObject.str(key: String): String? = if (isNull(key)) null else optString(key)
fun JSONObject.obj(key: String): JSONObject? = if (isNull(key)) null else optJSONObject(key)
fun JSONObject.bool(key: String): Boolean? = if (isNull(key)) null else optBoolean(key)
fun JSONObject.long(key: String, def: Long = 0): Long = if (isNull(key)) def else optLong(key, def)

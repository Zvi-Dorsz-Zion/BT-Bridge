package com.btbridge.app.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test
import java.text.Normalizer
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec

/**
 * The core's own PBKDF2 (Crypto.pbkdf2, written over HmacSHA256 because SecretKeyFactory "PBKDF2WithHmacSHA256"
 * exists on Android only from 8.0) must give exactly what the JDK gives. Also B64 against java.util.Base64.
 */
class CryptoTest {
    private fun jdk(password: String, salt: ByteArray, iter: Int): ByteArray {
        val nfc = Normalizer.normalize(password, Normalizer.Form.NFC)
        val spec = PBEKeySpec(nfc.toCharArray(), salt, iter, 256)
        return SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded
    }

    @Test
    fun pbkdf2MatchesJdk() {
        val cases = listOf(
            Triple("password", "salt".toByteArray(), 1),
            Triple("password", "salt".toByteArray(), 4096),
            Triple("סיסמה-123", Crypto.random(16), 50_000),
            // The same Hebrew letter with a vowel point written two ways: both must give the NFC result.
            Triple("שָׁלוֹם", Crypto.random(16), 1000),
            Triple("שָׁלום", Crypto.random(16), 1000),
            Triple("a much longer password than the hash block of sixty-four bytes, so the key gets hashed first", Crypto.random(16), 100),
        )
        for ((pw, salt, iter) in cases) assertArrayEquals("\"$pw\" x$iter", jdk(pw, salt, iter), Crypto.pbkdf2(pw, salt, iter))
    }

    @Test
    fun pbkdf2KnownVector() {
        // RFC 7914 section 11 (PBKDF2-HMAC-SHA256, P="passwd", S="salt", c=1), first 32 bytes.
        val want = "55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dacbc"
        assertEquals(want, Crypto.pbkdf2("passwd", "salt".toByteArray(), 1).joinToString("") { "%02x".format(it) })
    }

    @Test
    fun b64MatchesJdk() {
        val rnd = java.util.Random(7)
        for (n in 0..70) {
            val b = ByteArray(n).also { rnd.nextBytes(it) }
            val e = java.util.Base64.getEncoder().encodeToString(b)
            assertEquals(e, B64.encode(b))
            assertArrayEquals(b, B64.decode(e))
            assertArrayEquals(b, B64.decode(e.trimEnd('=')))
        }
    }
}

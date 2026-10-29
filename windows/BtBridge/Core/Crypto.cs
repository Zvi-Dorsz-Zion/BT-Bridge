using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BtBridge.Core;

/// <summary>Crypto primitives shared with the Android side. See PROTOCOL.md section 2-3.</summary>
public static class Crypto
{
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    public static byte[] Concat(params byte[][] parts)
    {
        var o = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(o, at); at += p.Length; }
        return o;
    }

    public static byte[] U32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        return b;
    }

    public static byte[] Hmac(byte[] key, params byte[][] parts) => HMACSHA256.HashData(key, Concat(parts));
    public static byte[] Sha256(params byte[][] parts) => SHA256.HashData(Concat(parts));
    public static byte[] Expand(byte[] prk, string info) => Hmac(prk, Utf8(info), new byte[] { 1 });
    public static byte[] Random(int n) => RandomNumberGenerator.GetBytes(n);

    public static byte[] Pbkdf2(string password, byte[] salt, int iter) =>
        Rfc2898DeriveBytes.Pbkdf2(Utf8(password.Normalize(NormalizationForm.FormC)), salt, iter, HashAlgorithmName.SHA256, 32);

    public static bool Same(byte[]? a, byte[]? b) =>
        a != null && b != null && a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);

    // ---------------------------------------------------------------- P-256 keys

    public static byte[] PublicBytes(ECParameters p) => Concat(new byte[] { 4 }, p.Q.X!, p.Q.Y!);

    public static ECParameters FromPublic(byte[] pub)
    {
        if (pub.Length != 65 || pub[0] != 4) throw new ProtocolException("מפתח ציבורי לא תקין");
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
        };
    }

    public static string Fingerprint(byte[] pub) => Convert.ToHexString(SHA256.HashData(pub), 0, 8);

    /// <summary>The fingerprint for people: digits only ("1234-5678-9012"), the same on both sides.</summary>
    public static string FingerprintDigits(string fp)
    {
        if (!ulong.TryParse(fp, System.Globalization.NumberStyles.HexNumber, null, out var v)) return fp;
        var d = (v % 1_000_000_000_000UL).ToString("D12");
        return $"{d[..4]}-{d[4..8]}-{d[8..]}";
    }

    public static byte[] Ecdh(ECDiffieHellman mine, byte[] peerPub)
    {
        using var peer = ECDiffieHellman.Create(FromPublic(peerPub));
        return mine.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public static bool Verify(byte[] pub, byte[] data, byte[] sig)
    {
        try
        {
            using var k = ECDsa.Create(FromPublic(pub));
            return k.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch { return false; }
    }

    public static string B64(byte[] b) => Convert.ToBase64String(b);

    public static byte[] B64(JsonNode? n)
    {
        var s = n?.GetValue<string>() ?? throw new ProtocolException("שדה חסר");
        return Convert.FromBase64String(s);
    }
}

public class ProtocolException(string message) : Exception(message);

/// <summary>An error reported by the other side ({"err":code,"msg":text}).</summary>
public sealed class RemoteException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>AES-256-GCM records with a per-direction counter nonce.</summary>
public sealed class RecordCodec
{
    private readonly AesGcm _send, _recv;
    private ulong _sendCtr, _recvCtr;

    public RecordCodec(byte[] sendKey, byte[] recvKey)
    {
        _send = new AesGcm(sendKey, 16);
        _recv = new AesGcm(recvKey, 16);
    }

    private static byte[] Nonce(ulong counter)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), counter);
        return n;
    }

    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        var o = new byte[plain.Length + 16];
        _send.Encrypt(Nonce(_sendCtr++), plain, o.AsSpan(0, plain.Length), o.AsSpan(plain.Length));
        return o;
    }

    public byte[] Open(byte[] rec)
    {
        if (rec.Length < 16) throw new ProtocolException("רשומה קצרה מדי");
        var p = new byte[rec.Length - 16];
        _recv.Decrypt(Nonce(_recvCtr++), rec.AsSpan(0, p.Length), rec.AsSpan(p.Length), p);
        return p;
    }
}

/// <summary>Plaintext record layout: [u8 type][u32 id][body].</summary>
public static class Rec
{
    public const byte Req = 1, Res = 2, Data = 3, End = 4, Cancel = 5, Ping = 6, Pong = 7,
        Evt = 8, Done = 9, Auth = 10, AuthResult = 11;

    public static byte[] Make(byte type, uint id, ReadOnlySpan<byte> body)
    {
        var o = new byte[5 + body.Length];
        o[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(1), id);
        body.CopyTo(o.AsSpan(5));
        return o;
    }

    public static byte[] Json(byte type, uint id, JsonObject o) => Make(type, id, Crypto.Utf8(o.ToJsonString()));

    public static (byte Type, uint Id, ReadOnlyMemory<byte> Body) Parse(byte[] p)
    {
        if (p.Length < 5) throw new ProtocolException("רשומה קצרה מדי");
        return (p[0], BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(1)), p.AsMemory(5));
    }

    public static JsonObject ParseJson(ReadOnlyMemory<byte> body) =>
        JsonNode.Parse(body.Span) as JsonObject ?? throw new ProtocolException("נתונים לא תקינים");
}

/// <summary>[u32 length][bytes] framing on a raw stream.</summary>
public static class Wire
{
    public const int MaxFrame = 2 * 1024 * 1024;

    public static void Write(Stream s, byte[] data)
    {
        var o = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(o, data.Length);
        data.CopyTo(o, 4);
        s.Write(o, 0, o.Length);
        s.Flush();
    }

    public static byte[] Read(Stream s)
    {
        var len = new byte[4];
        ReadExact(s, len);
        int n = BinaryPrimitives.ReadInt32BigEndian(len);
        if (n < 0 || n > MaxFrame) throw new ProtocolException("מסגרת גדולה מדי");
        var buf = new byte[n];
        ReadExact(s, buf);
        return buf;
    }

    private static void ReadExact(Stream s, byte[] buf)
    {
        int at = 0;
        while (at < buf.Length)
        {
            int r = s.Read(buf, at, buf.Length - at);
            if (r <= 0) throw new EndOfStreamException("החיבור נסגר");
            at += r;
        }
    }
}

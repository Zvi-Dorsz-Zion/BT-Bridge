using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static BtBridge.Core.Crypto;

namespace BtBridge.Core;

public sealed record PasswordAnswer(string Password, bool Remember);

/// <summary>Asks the user for the other device's password. Returns null when cancelled.</summary>
public delegate PasswordAnswer? AskPassword(string peerName, bool wrongBefore);

public sealed record HandshakeResult(
    RecordCodec Codec, byte[] LinkKey, string PeerName, string PeerOs, string PeerFp, byte[] PeerPub);

public sealed class AuthException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Tracks wrong passwords: 5 in a row lock password attempts for 5 minutes.</summary>
public sealed class Lockout
{
    private int _fails;
    private DateTime _until;
    private readonly object _gate = new();

    public bool Locked { get { lock (_gate) return DateTime.UtcNow < _until; } }
    public void Reset() { lock (_gate) _fails = 0; }

    public void Fail()
    {
        lock (_gate)
        {
            if (++_fails < 5) return;
            _fails = 0;
            _until = DateTime.UtcNow.AddMinutes(5);
        }
    }
}

/// <summary>PROTOCOL.md section 2.</summary>
public static class Handshake
{
    public const int Version = 1;

    public static HandshakeResult Client(Stream s, Identity me, AppSettings st, string peerAddress, AskPassword ask)
    {
        var known = st.FindByAddress(peerAddress);
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = Hello(me, st, eph);
        hello["needPw"] = known == null;
        var c = Utf8(hello.ToJsonString());
        Wire.Write(s, c);

        var sBytes = Wire.Read(s);
        var sh = ParseHello(sBytes);
        var peerPub = B64(sh["id"]);
        var fp = Fingerprint(peerPub);
        var peerName = sh["name"]?.GetValue<string>() ?? "?";
        var peerOs = sh["os"]?.GetValue<string>() ?? "";
        if (known != null && known.Fp != fp)
            throw new AuthException("keychanged",
                $"המפתח של \"{known.Name}\" השתנה מאז הפעם הקודמת. אם התקנת אותו מחדש, לחץ \"שכח מכשיר\" ונסה שוב.");

        var (codec, linkKey, th) = Derive(eph, B64(sh["eph"]), c, sBytes, client: true);
        var pwInfo = sh["pw"] as JsonObject;
        if (pwInfo?["set"]?.GetValue<bool>() == false)
            throw new AuthException("nopassword", $"ב-\"{peerName}\" עוד לא נקבעה סיסמה. קבע סיסמה שם ונסה שוב.");
        bool wrong = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[]? key = null;
            bool remember = false;
            if (pwInfo != null)
            {
                var a = ask(peerName, wrong) ?? throw new OperationCanceledException("בוטל");
                key = Pbkdf2(a.Password, B64(pwInfo["salt"]), pwInfo["iter"]!.GetValue<int>());
                remember = a.Remember;
            }
            var auth = new JsonObject
            {
                ["sig"] = B64(me.Sign(Concat(Utf8("btb-c"), th))),
                ["pw"] = key == null ? null : B64(Hmac(key, Utf8("btb-pw-c"), th)),
                ["remember"] = remember,
            };
            Wire.Write(s, codec.Seal(Rec.Json(Rec.Auth, 0, auth)));

            var (type, _, body) = Rec.Parse(codec.Open(Wire.Read(s)));
            if (type != Rec.AuthResult) throw new ProtocolException("תשובה לא צפויה");
            var res = Rec.ParseJson(body);
            if (res["ok"]?.GetValue<bool>() == true)
            {
                if (!Verify(peerPub, Concat(Utf8("btb-s"), th), B64(res["sig"])))
                    throw new AuthException("sig", "חתימת המכשיר השני לא תקינה");
                if (key != null)
                {
                    // The other side proves it knows its own password too, so we can trust it back.
                    if (!Same(B64(res["pw"]), Hmac(key, Utf8("btb-pw-s"), th)))
                        throw new AuthException("sig", "המכשיר השני לא הוכיח שהוא יודע את הסיסמה");
                    if (remember) st.Trust(fp, peerPub, peerName, peerAddress, peerOs);
                }
                else if (known == null)
                {
                    throw new AuthException("sig", "המכשיר השני לא ביקש סיסמה, אבל הוא לא מוכר כאן");
                }
                else st.Touch(fp, peerName, peerAddress);
                return new HandshakeResult(codec, linkKey, peerName, peerOs, fp, peerPub);
            }
            var err = res["err"]?.GetValue<string>() ?? "";
            if (err == "password" && pwInfo != null) { wrong = true; continue; }
            throw new AuthException(err, err switch
            {
                "password" => "סיסמה שגויה",
                "locked" => "יותר מדי ניסיונות שגויים. נסה שוב בעוד 5 דקות.",
                "nopassword" => "במכשיר השני עוד לא נקבעה סיסמה",
                _ => "האימות נכשל (" + err + ")",
            });
        }
        throw new AuthException("password", "סיסמה שגויה");
    }

    public static HandshakeResult Server(Stream s, Identity me, AppSettings st, string peerAddress, Lockout lockout)
    {
        var c = Wire.Read(s);
        var ch = ParseHello(c);
        var peerPub = B64(ch["id"]);
        var fp = Fingerprint(peerPub);
        var peerName = ch["name"]?.GetValue<string>() ?? "?";
        var peerOs = ch["os"]?.GetValue<string>() ?? "";
        bool trusted = st.FindByFp(fp) != null && ch["needPw"]?.GetValue<bool>() != true;

        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = Hello(me, st, eph);
        hello["pw"] = trusted ? null : new JsonObject
        {
            ["salt"] = st.HasPassword ? st.PwSalt : B64(Random(16)),
            ["iter"] = st.PwIter,
            ["set"] = st.HasPassword,
        };
        var sBytes = Utf8(hello.ToJsonString());
        Wire.Write(s, sBytes);

        var (codec, linkKey, th) = Derive(eph, B64(ch["eph"]), c, sBytes, client: false);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var (type, _, body) = Rec.Parse(codec.Open(Wire.Read(s)));
            if (type != Rec.Auth) throw new ProtocolException("תשובה לא צפויה");
            var auth = Rec.ParseJson(body);

            if (!Verify(peerPub, Concat(Utf8("btb-c"), th), B64(auth["sig"])))
                Fail(s, codec, "sig");

            if (trusted)
            {
                st.Touch(fp, peerName, peerAddress);
                Ok(s, codec, me, th, null);
                return new HandshakeResult(codec, linkKey, peerName, peerOs, fp, peerPub);
            }
            if (!st.HasPassword) Fail(s, codec, "nopassword");
            if (lockout.Locked) Fail(s, codec, "locked");

            var key = st.PasswordKey();
            var proof = auth["pw"] is JsonNode n ? Convert.FromBase64String(n.GetValue<string>()) : null;
            if (Same(proof, Hmac(key, Utf8("btb-pw-c"), th)))
            {
                lockout.Reset();
                if (auth["remember"]?.GetValue<bool>() == true) st.Trust(fp, peerPub, peerName, peerAddress, peerOs);
                Ok(s, codec, me, th, Hmac(key, Utf8("btb-pw-s"), th));
                return new HandshakeResult(codec, linkKey, peerName, peerOs, fp, peerPub);
            }
            lockout.Fail();
            Log.Write($"wrong password from {peerName} ({peerAddress})");
            Wire.Write(s, codec.Seal(Rec.Json(Rec.AuthResult, 0, new JsonObject { ["ok"] = false, ["err"] = "password" })));
        }
        throw new AuthException("password", "סיסמה שגויה");
    }

    // ---------------------------------------------------------------- helpers

    private static JsonObject Hello(Identity me, AppSettings st, ECDiffieHellman eph) => new()
    {
        ["v"] = Version,
        ["name"] = st.DeviceName,
        ["os"] = "windows",
        ["id"] = B64(me.Public),
        ["eph"] = B64(PublicBytes(eph.ExportParameters(false))),
        ["nonce"] = B64(Random(32)),
    };

    private static JsonObject ParseHello(byte[] b)
    {
        var o = JsonNode.Parse(b) as JsonObject ?? throw new ProtocolException("הודעת פתיחה לא תקינה");
        if (o["v"]?.GetValue<int>() != Version)
            throw new ProtocolException("גרסת הפרוטוקול במכשיר השני שונה. עדכן את שני הצדדים.");
        return o;
    }

    private static (RecordCodec, byte[], byte[]) Derive(ECDiffieHellman eph, byte[] peerEph, byte[] c, byte[] s, bool client)
    {
        var th = Sha256(U32(c.Length), c, U32(s.Length), s);
        var prk = Hmac(th, Ecdh(eph, peerEph));
        var c2s = Expand(prk, "btb c2s");
        var s2c = Expand(prk, "btb s2c");
        var codec = client ? new RecordCodec(c2s, s2c) : new RecordCodec(s2c, c2s);
        return (codec, Expand(prk, "btb link"), th);
    }

    private static void Ok(Stream s, RecordCodec codec, Identity me, byte[] th, byte[]? pwProof)
    {
        var o = new JsonObject
        {
            ["ok"] = true,
            ["sig"] = B64(me.Sign(Concat(Utf8("btb-s"), th))),
            ["pw"] = pwProof == null ? null : B64(pwProof),
        };
        Wire.Write(s, codec.Seal(Rec.Json(Rec.AuthResult, 0, o)));
    }

    private static void Fail(Stream s, RecordCodec codec, string err)
    {
        try { Wire.Write(s, codec.Seal(Rec.Json(Rec.AuthResult, 0, new JsonObject { ["ok"] = false, ["err"] = err }))); }
        catch { }
        throw new AuthException(err, "האימות נכשל: " + err);
    }
}

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static BtBridge.Core.Crypto;

namespace BtBridge.Core;

public sealed record PasswordAnswer(string Password, bool Remember);

/// <summary>Asks the user for the other device's password. Returns null when cancelled.</summary>
public delegate PasswordAnswer? AskPassword(string peerName, bool wrongBefore);

public sealed record HandshakeResult(
    RecordCodec Codec, byte[] LinkKey, string PeerName, string PeerOs, string PeerFp, byte[] PeerPub,
    string PeerApp, IReadOnlySet<string> PeerCaps);

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
    public const int Version = 2;

    /// <summary>Optional features this side understands (PROTOCOL.md section 2, "caps").</summary>
    public static readonly string[] Caps = { "clip", "notif", "resume", "ring", "battery", "update", "log", "search" };

    /// <summary>PBKDF2 rounds a peer may ask for: fewer would make a captured proof easy to crack, more would freeze us.</summary>
    public const int MinIter = 10_000, MaxIter = 5_000_000;

    public static HandshakeResult Client(Stream s, Identity me, AppSettings st, string peerAddress, AskPassword ask)
    {
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var c = Utf8(Hello(me, st, eph).ToJsonString());
        Wire.Write(s, c);

        var sBytes = Wire.Read(s);
        var sh = ParseHello(sBytes);
        var peerPub = B64(sh["id"]);
        var fp = Fingerprint(peerPub);
        var peerName = sh["name"]?.GetValue<string>() ?? "?";
        var peerOs = sh["os"]?.GetValue<string>() ?? "";

        // Trust is by key. The Bluetooth address only catches a reinstalled device, to explain the password prompt.
        var known = st.FindByFp(fp);
        var byAddress = st.FindByAddress(peerAddress);
        if (known == null && byAddress != null)
            throw new AuthException("keychanged",
                $"המפתח של \"{byAddress.Name}\" השתנה מאז הפעם הקודמת. אם התקנת אותו מחדש, לחץ \"שכח מכשיר\" ונסה שוב.");

        var (codec, linkKey, th) = Derive(eph, B64(sh["eph"]), c, sBytes, client: true);
        var pwInfo = sh["pw"] as JsonObject ?? throw new ProtocolException("הודעת פתיחה לא תקינה");
        bool trustsMe = sh["trusted"]?.GetValue<bool>() == true;
        bool needPw = !(trustsMe && known != null);
        Log.Write($"handshake with {peerName} ({peerAddress}): they trust us={trustsMe}, we trust them={known != null}");
        if (needPw && pwInfo["set"]?.GetValue<bool>() == false)
            throw new AuthException("nopassword", $"ב-\"{peerName}\" עוד לא נקבעה סיסמה. קבע סיסמה שם ונסה שוב.");
        int iter = pwInfo["iter"]?.GetValue<int>() ?? 0;
        if (needPw && iter is < MinIter or > MaxIter)
            throw new ProtocolException("הגדרות הסיסמה במכשיר השני לא תקינות");

        bool wrong = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[]? key = null;
            bool remember = false;
            if (needPw)
            {
                var a = ask(peerName, wrong) ?? throw new OperationCanceledException("בוטל");
                key = Pbkdf2(a.Password, B64(pwInfo["salt"]), iter);
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
                else st.Touch(fp, peerName, peerAddress);
                return Result(codec, linkKey, peerName, peerOs, fp, peerPub, sh);
            }
            var err = res["err"]?.GetValue<string>() ?? "";
            if (err == "password" && needPw) { wrong = true; continue; }
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
        bool trusted = st.FindByFp(fp) != null;

        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = Hello(me, st, eph);
        // The password details always go out: the client may not trust us even when we trust it.
        hello["pw"] = new JsonObject
        {
            ["salt"] = st.HasPassword ? st.PwSalt : B64(Random(16)),
            ["iter"] = st.PwIter,
            ["set"] = st.HasPassword,
        };
        hello["trusted"] = trusted;
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

            var proof = auth["pw"] is JsonNode n ? Convert.FromBase64String(n.GetValue<string>()) : null;
            if (proof == null)
            {
                if (!trusted) Fail(s, codec, "password");
                st.Touch(fp, peerName, peerAddress);
                Ok(s, codec, me, th, null);
                Log.Write($"handshake with {peerName} ({peerAddress}): trusted both ways");
                return Result(codec, linkKey, peerName, peerOs, fp, peerPub, ch);
            }
            if (!st.HasPassword) Fail(s, codec, "nopassword");
            if (lockout.Locked) Fail(s, codec, "locked");

            var key = st.PasswordKey();
            if (Same(proof, Hmac(key, Utf8("btb-pw-c"), th)))
            {
                lockout.Reset();
                bool remember = auth["remember"]?.GetValue<bool>() == true;
                if (remember) st.Trust(fp, peerPub, peerName, peerAddress, peerOs);
                Ok(s, codec, me, th, Hmac(key, Utf8("btb-pw-s"), th));
                Log.Write($"handshake with {peerName} ({peerAddress}): password ok, remember={remember}, we trusted them before={trusted}");
                return Result(codec, linkKey, peerName, peerOs, fp, peerPub, ch);
            }
            lockout.Fail();
            Log.Write($"wrong password from {peerName} ({peerAddress})");
            Wire.Write(s, codec.Seal(Rec.Json(Rec.AuthResult, 0, new JsonObject { ["ok"] = false, ["err"] = "password" })));
        }
        throw new AuthException("password", "סיסמה שגויה");
    }

    // ---------------------------------------------------------------- helpers

    private static JsonObject Hello(Identity me, AppSettings st, ECDiffieHellman eph)
    {
        var caps = new JsonArray();
        foreach (var cap in Caps) caps.Add(cap);
        return new JsonObject
        {
            ["v"] = Version,
            ["name"] = st.DeviceName,
            ["os"] = "windows",
            ["app"] = AppInfo.Version,
            ["caps"] = caps,
            ["id"] = B64(me.Public),
            ["eph"] = B64(PublicBytes(eph.ExportParameters(false))),
            ["nonce"] = B64(Random(32)),
        };
    }

    private static HandshakeResult Result(RecordCodec codec, byte[] linkKey, string name, string os, string fp, byte[] pub, JsonObject hello)
    {
        var caps = new HashSet<string>();
        if (hello["caps"] is JsonArray arr)
            foreach (var n in arr) if (n?.GetValue<string>() is { } cap) caps.Add(cap);
        return new HandshakeResult(codec, linkKey, name, os, fp, pub, hello["app"]?.GetValue<string>() ?? "", caps);
    }

    private static JsonObject ParseHello(byte[] b)
    {
        var o = JsonNode.Parse(b) as JsonObject ?? throw new ProtocolException("הודעת פתיחה לא תקינה");
        if (o["v"]?.GetValue<int>() != Version)
            throw new ProtocolException("גרסת הפרוטוקול במכשיר השני שונה. עדכן את שני הצדדים לאותה גרסה של גשר בלוטוס.");
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

/// <summary>This program's version, sent to the other side (it offers updates and shows mismatches).</summary>
public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0";

    /// <summary>-1, 0 or 1, comparing "1.2.3" strings; anything unreadable counts as oldest.</summary>
    public static int Compare(string a, string b)
    {
        static System.Version P(string s) => System.Version.TryParse(s, out var v) ? v : new System.Version(0, 0);
        return P(a).CompareTo(P(b));
    }
}

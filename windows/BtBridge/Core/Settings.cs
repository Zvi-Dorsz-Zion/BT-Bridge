using System.Security.Cryptography;
using System.Text.Json;

namespace BtBridge.Core;

public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Settings, identity and log folder. Tests point it elsewhere.</summary>
    public static string Dir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BtBridge");

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var f = Path.Combine(Dir, "log.txt");
                if (File.Exists(f) && new FileInfo(f).Length > 2_000_000) File.Move(f, f + ".old", true);
                File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>The end of the log, for the other device's "show log" (PROTOCOL.md section 4, "log").</summary>
    public static string Tail(int maxBytes)
    {
        try
        {
            lock (Gate)
            {
                var f = Path.Combine(Dir, "log.txt");
                if (!File.Exists(f)) return "";
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(Math.Max(0, fs.Length - maxBytes), SeekOrigin.Begin);
                return new StreamReader(fs, System.Text.Encoding.UTF8).ReadToEnd();
            }
        }
        catch (Exception ex) { return "היומן לא נקרא: " + ex.Message; }
    }
}

/// <summary>Turns any error into a Hebrew message for the user; the original goes to the log.</summary>
public static class Heb
{
    public static bool IsHebrew(string? s) => s != null && s.Any(c => c >= '\u0590' && c <= '\u05FF');

    public static string Of(Exception ex)
    {
        if (ex is AggregateException { InnerException: { } inner }) ex = inner;
        if (IsHebrew(ex.Message)) return ex.Message;
        Log.Write("error (shown in Hebrew): " + ex);
        return ex switch
        {
            OperationCanceledException => "בוטל",
            TimeoutException => "אין תשובה מהמכשיר השני",
            System.Net.Sockets.SocketException => "החיבור נכשל. ודא שגשר בלוטוס פתוח במכשיר השני ושהבלוטוס דלוק.",
            UnauthorizedAccessException => "אין הרשאה לקובץ או לתיקייה הזו",
            FileNotFoundException or DirectoryNotFoundException => "הקובץ או התיקייה לא נמצאו",
            PathTooLongException => "הנתיב ארוך מדי",
            EndOfStreamException => "החיבור נסגר",
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "הקובץ פתוח בתוכנה אחרת",
            IOException io when (io.HResult & 0xFFFF) is 39 or 112 => "אין מספיק מקום בדיסק",
            IOException => "בעיה בחיבור או בקובץ",
            _ => "שגיאה לא צפויה (הפרטים נשמרו ביומן)",
        };
    }
}

public sealed class TrustedDevice
{
    public string Fp { get; set; } = "";
    public string Pub { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Os { get; set; } = "";
}

/// <summary>%APPDATA%\BtBridge\settings.json. Secrets (password key) are DPAPI-protected.</summary>
public sealed class AppSettings
{
    public string DeviceName { get; set; } = "המחשב שלי";
    public string DownloadDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "גשר בלוטוס");
    public bool CloseToTray { get; set; } = true;
    /// <summary>Files opened only for viewing (and clipboard copies) are deleted after this many hours.</summary>
    public int ViewCleanupHours { get; set; } = 1;
    /// <summary>Reconnects by itself to known devices that are in range.</summary>
    public bool AutoReconnect { get; set; } = true;
    /// <summary>Lets known devices view and control this computer's screen.</summary>
    public bool AllowControl { get; set; } = true;
    /// <summary>"auto": join the phone's temporary hotspot when there is no shared network. "off": never.</summary>
    public string DirectMode { get; set; } = "auto";
    /// <summary>Text copied on one device can be pasted on the other.</summary>
    public bool ShareClipboard { get; set; } = true;
    /// <summary>Notifications that appear on the phone show up next to the clock here too.</summary>
    public bool ShowPhoneNotifications { get; set; } = true;
    /// <summary>Local nicknames by fingerprint (or by Bluetooth address for devices never connected).</summary>
    public Dictionary<string, string> Nicknames { get; set; } = new();

    /// <summary>Sort chosen per folder ("name|asc", "date|desc", ...), keyed by device fingerprint + path.</summary>
    public Dictionary<string, string> Sorts { get; set; } = new();

    public string NameFor(string key, string fallback)
    {
        lock (Gate) return Nicknames.TryGetValue(key, out var n) && n.Length > 0 ? n : fallback;
    }

    public void SetNickname(string key, string? name)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(name)) Nicknames.Remove(key);
            else Nicknames[key] = name.Trim();
        }
        Save();
    }
    public string PwSalt { get; set; } = "";
    public int PwIter { get; set; } = 50_000;
    public string PwKeyProtected { get; set; } = "";
    public List<TrustedDevice> Trusted { get; set; } = new();

    private static string FilePath => Path.Combine(Log.Dir, "settings.json");
    private static readonly object Gate = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                // Older versions used the computer's technical (English) name.
                if (s.DeviceName == Environment.MachineName) s.DeviceName = "המחשב שלי";
                return s;
            }
        }
        catch (Exception ex) { Log.Write("settings load: " + ex.Message); }
        return new AppSettings();
    }

    public void Save()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Log.Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
    }

    // ---------------------------------------------------------------- password

    public bool HasPassword => PwKeyProtected.Length > 0;

    public void SetPassword(string password)
    {
        var salt = Crypto.Random(16);
        var key = Crypto.Pbkdf2(password, salt, PwIter);
        lock (Gate)
        {
            PwSalt = Crypto.B64(salt);
            PwKeyProtected = Crypto.B64(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
        }
        Save();
    }

    public byte[] PasswordKey() =>
        ProtectedData.Unprotect(Convert.FromBase64String(PwKeyProtected), null, DataProtectionScope.CurrentUser);

    // ---------------------------------------------------------------- trust

    public TrustedDevice? FindByFp(string fp)
    {
        lock (Gate) return Trusted.FirstOrDefault(t => t.Fp == fp);
    }

    public TrustedDevice? FindByAddress(string address)
    {
        if (address.Length == 0) return null;
        lock (Gate) return Trusted.FirstOrDefault(t => t.Address == address);
    }

    public void Trust(string fp, byte[] pub, string name, string address, string os)
    {
        lock (Gate)
        {
            Trusted.RemoveAll(t => t.Fp == fp || (address.Length > 0 && t.Address == address));
            Trusted.Add(new TrustedDevice { Fp = fp, Pub = Crypto.B64(pub), Name = name, Address = address, Os = os });
        }
        Save();
    }

    /// <summary>Keeps name/address of an already trusted device current.</summary>
    public void Touch(string fp, string name, string address)
    {
        bool changed = false;
        lock (Gate)
        {
            var t = Trusted.FirstOrDefault(x => x.Fp == fp);
            if (t == null) return;
            if (t.Name != name) { t.Name = name; changed = true; }
            if (address.Length > 0 && t.Address != address) { t.Address = address; changed = true; }
        }
        if (changed) Save();
    }

    public void Forget(string fp)
    {
        lock (Gate) Trusted.RemoveAll(t => t.Fp == fp);
        Save();
    }
}

/// <summary>This device's long-term ECDSA P-256 key (DPAPI-protected PKCS#8 on disk).</summary>
public sealed class Identity
{
    private readonly ECDsa _key;
    public byte[] Public { get; }
    public string Fp { get; }

    private Identity(ECDsa key)
    {
        _key = key;
        Public = Crypto.PublicBytes(key.ExportParameters(false));
        Fp = Crypto.Fingerprint(Public);
    }

    /// <summary>The saved key could not be read and a new one was made: known devices will ask for the password again.</summary>
    public static bool WasReset { get; private set; }

    public byte[] Sign(byte[] data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public static Identity LoadOrCreate()
    {
        var f = Path.Combine(Log.Dir, "identity.bin");
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(f))
        {
            try
            {
                var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(f), null, DataProtectionScope.CurrentUser);
                key.ImportPkcs8PrivateKey(pkcs8, out _);
                return new Identity(key);
            }
            catch (Exception ex)
            {
                // Keep the unreadable file (it may be readable again under the right Windows user) and tell the user once.
                Log.Write("identity load failed, creating new: " + ex.Message);
                try { File.Copy(f, f + $".bad-{DateTime.Now:yyyyMMdd-HHmmss}", true); } catch { }
                WasReset = true;
                key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            }
        }
        Directory.CreateDirectory(Log.Dir);
        File.WriteAllBytes(f, ProtectedData.Protect(key.ExportPkcs8PrivateKey(), null, DataProtectionScope.CurrentUser));
        return new Identity(key);
    }
}

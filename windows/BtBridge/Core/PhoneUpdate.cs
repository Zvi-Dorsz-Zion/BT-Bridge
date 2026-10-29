namespace BtBridge.Core;

/// <summary>
/// The phone app of the same version travels inside this program (built in by בנייה.cmd), so a phone with an older
/// version can be updated from here without internet.
/// </summary>
public static class PhoneUpdate
{
    private const string Resource = "phone.apk";

    public static bool Available => typeof(PhoneUpdate).Assembly.GetManifestResourceInfo(Resource) != null;

    /// <summary>Writes the app to a temporary file and returns its path.</summary>
    public static string Extract()
    {
        using var src = typeof(PhoneUpdate).Assembly.GetManifestResourceStream(Resource)
            ?? throw new IOException("האפליקציה לטלפון לא נמצאת בתוכנה הזו");
        var file = Path.Combine(TempFiles.NewDir("עדכון"), $"גשר בלוטוס {AppInfo.Version}.apk");
        using var dst = File.Create(file);
        src.CopyTo(dst);
        return file;
    }
}

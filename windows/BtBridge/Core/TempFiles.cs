namespace BtBridge.Core;

/// <summary>
/// Files brought over only to look at them (open), to put on the clipboard, or a pasted picture.
/// Each job gets its own folder under %TEMP%\BtBridge, and folders older than the chosen time are deleted.
/// Downloads the user asked for go to the downloads folder and are never touched here.
/// </summary>
public static class TempFiles
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "BtBridge");

    /// <summary>A new folder for one job ("צפייה", "העתקה", "הדבקה").</summary>
    public static string NewDir(string kind)
    {
        var dir = Path.Combine(Root, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Deletes job folders older than the given hours. A file still open in some program is skipped until next time.</summary>
    public static void Cleanup(int hours)
    {
        if (!Directory.Exists(Root)) return;
        var before = DateTime.Now.AddHours(-Math.Max(1, hours));
        foreach (var d in Directory.EnumerateDirectories(Root))
        {
            try
            {
                if (Directory.GetCreationTime(d) < before) Directory.Delete(d, recursive: true);
            }
            catch (Exception ex) { Log.Write($"temp cleanup {d}: {ex.Message}"); }
        }
        foreach (var f in Directory.EnumerateFiles(Root))
        {
            try { if (File.GetCreationTime(f) < before) File.Delete(f); } catch { }
        }
    }
}

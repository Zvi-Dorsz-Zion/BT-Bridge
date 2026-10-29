using System.Runtime.InteropServices;

namespace BtBridge.Core;

/// <summary>
/// File order like the file managers people know: folders first; names in "natural" order (2 before 10,
/// as in Windows Explorer); and folders that collect new files (Downloads, camera, screenshots) newest first.
/// </summary>
public static class FileOrder
{
    private static readonly string[] RecentFirst =
    {
        "downloads", "download", "הורדות", "dcim", "camera", "מצלמה", "screenshots", "צילומי מסך",
        "whatsapp images", "whatsapp video", "whatsapp documents", "telegram", "recordings", "bt bridge", "גשר בלוטוס",
    };

    /// <summary>The order a folder opens with when the user hasn't chosen one: ("name"|"date"|"size"|"type", descending).</summary>
    public static (string By, bool Desc) Default(string path)
    {
        var name = path.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault()?.ToLowerInvariant() ?? "";
        return RecentFirst.Contains(name) ? ("date", true) : ("name", false);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    public static readonly IComparer<string> Natural = Comparer<string>.Create((a, b) => StrCmpLogicalW(a, b));

    public static List<RemoteItem> Sort(IEnumerable<RemoteItem> items, string by, bool desc)
    {
        IEnumerable<RemoteItem> folders = items.Where(i => i.IsDir), files = items.Where(i => !i.IsDir);
        IOrderedEnumerable<RemoteItem> Apply(IEnumerable<RemoteItem> x) => by switch
        {
            "date" => desc ? x.OrderByDescending(i => i.Mtime) : x.OrderBy(i => i.Mtime),
            "size" => desc ? x.OrderByDescending(i => i.Size) : x.OrderBy(i => i.Size),
            "type" => desc ? x.OrderByDescending(i => Path.GetExtension(i.Name), StringComparer.OrdinalIgnoreCase)
                           : x.OrderBy(i => Path.GetExtension(i.Name), StringComparer.OrdinalIgnoreCase),
            _ => desc ? x.OrderByDescending(i => i.Name, Natural) : x.OrderBy(i => i.Name, Natural),
        };
        // Folders have no size: keep them by name when sorting by size.
        var f = by == "size" ? folders.OrderBy(i => i.Name, Natural) : Apply(folders);
        return f.Concat(Apply(files).ThenBy(i => i.Name, Natural)).ToList();
    }

    public static string Label(string by, bool desc) => by switch
    {
        "date" => desc ? "ממוין לפי תאריך, החדשים קודם" : "ממוין לפי תאריך, הישנים קודם",
        "size" => desc ? "ממוין לפי גודל, הגדולים קודם" : "ממוין לפי גודל, הקטנים קודם",
        "type" => "ממוין לפי סוג",
        _ => desc ? "ממוין לפי שם (ת-א)" : "ממוין לפי שם (א-ת)",
    };
}

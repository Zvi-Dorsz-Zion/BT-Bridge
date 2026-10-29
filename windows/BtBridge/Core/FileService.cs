using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace BtBridge.Core;

/// <summary>Writes an incoming DATA…END stream into a file.</summary>
public sealed class FileSink(FileStream fs, Action<int>? progress, Action<JsonObject> onEnd, Action<string> onFail) : IStreamSink
{
    public void OnData(ReadOnlySpan<byte> data)
    {
        fs.Write(data);
        progress?.Invoke(data.Length);
    }

    public void OnEnd(JsonObject end) => onEnd(end);
    public void OnFail(string why) => onFail(why);
}

/// <summary>Serves this computer's files to the other device (PROTOCOL.md section 4).</summary>
public sealed class FileService(Func<string> deviceName, Func<string> inboxDir, Action<Session, string>? onReceived = null)
{
    private const int Page = 1000;

    public async Task<JsonObject?> HandleAsync(Session s, uint id, JsonObject req)
    {
        var op = req["op"]?.GetValue<string>();
        switch (op)
        {
            case "info":
                return new JsonObject
                {
                    ["name"] = deviceName(),
                    ["os"] = "windows",
                    ["sep"] = "\\",
                    ["home"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                };
            case "list":
                return List(Str(req, "path"), req["offset"]?.GetValue<int>() ?? 0);
            case "mkdir":
            {
                var p = Str(req, "path");
                if (Exists(p)) throw new RemoteException("exists", "כבר קיים: " + p);
                Directory.CreateDirectory(p);
                return Ok();
            }
            case "rename":
            {
                var from = Str(req, "from");
                var to = Str(req, "to");
                if (Exists(to)) throw new RemoteException("exists", "כבר קיים: " + to);
                if (Directory.Exists(from)) Directory.Move(from, to);
                else File.Move(from, to);
                return Ok();
            }
            case "delete":
            {
                var paths = req["paths"]!.AsArray().Select(n => n!.GetValue<string>()).Where(Exists).ToList();
                if (paths.Count > 0) Recycle(paths);
                var left = paths.Where(Exists).ToList();
                if (left.Count > 0) throw new RemoteException("io", "לא נמחק: " + string.Join(", ", left.Select(Path.GetFileName)));
                return new JsonObject { ["ok"] = true, ["recycled"] = true };
            }
            case "read":
                return await ReadAsync(s, id, req);
            case "write":
                return Write(s, id, req);
            default:
                throw new RemoteException("unknown", "פעולה לא מוכרת: " + op);
        }
    }

    private static JsonObject Ok() => new() { ["ok"] = true };

    private static string Str(JsonObject o, string key) =>
        o[key]?.GetValue<string>() ?? throw new RemoteException("bad", "חסר שדה: " + key);

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private static long Ms(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // ---------------------------------------------------------------- list

    private static JsonObject List(string path, int offset)
    {
        var items = new JsonArray();
        if (path.Length == 0)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var (name, dir) in new[]
            {
                ("שולחן העבודה", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                ("מסמכים", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                ("הורדות", Path.Combine(home, "Downloads")),
                ("תמונות", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                ("סרטונים", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            })
            {
                if (Directory.Exists(dir)) items.Add(new JsonObject { ["n"] = name, ["p"] = dir, ["d"] = true, ["s"] = 0, ["m"] = 0 });
            }
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "כונן" : d.VolumeLabel;
                    items.Add(new JsonObject
                    {
                        ["n"] = $"{label} ({d.Name.TrimEnd('\\')})",
                        ["p"] = d.RootDirectory.FullName,
                        ["d"] = true,
                        ["s"] = d.TotalSize,
                        ["free"] = d.AvailableFreeSpace,
                        ["m"] = 0,
                    });
                }
                catch { }
            }
            return new JsonObject { ["items"] = items, ["parent"] = null, ["more"] = false };
        }

        var di = new DirectoryInfo(path);
        if (!di.Exists) throw new RemoteException("notfound", "התיקייה לא קיימת: " + path);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
        var all = di.EnumerateFileSystemInfos("*", opts)
            .OrderBy(f => f is FileInfo)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Skip(offset).Take(Page + 1).ToList();
        foreach (var f in all.Take(Page))
        {
            items.Add(new JsonObject
            {
                ["n"] = f.Name,
                ["p"] = f.FullName,
                ["d"] = f is DirectoryInfo,
                ["s"] = f is FileInfo fi ? fi.Length : 0,
                ["m"] = Ms(f.LastWriteTimeUtc),
            });
        }
        var parent = di.Parent?.FullName ?? "";
        return new JsonObject { ["items"] = items, ["parent"] = parent, ["more"] = all.Count > Page };
    }

    // ---------------------------------------------------------------- read / write

    private async Task<JsonObject?> ReadAsync(Session s, uint id, JsonObject req)
    {
        var path = Str(req, "path");
        if (Directory.Exists(path)) throw new RemoteException("isdir", "זו תיקייה: " + path);
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
        await using (fs)
        {
            var offset = req["offset"]?.GetValue<long>() ?? 0;
            if (offset > 0) fs.Seek(offset, SeekOrigin.Begin);
            s.Reply(id, new JsonObject { ["ok"] = true, ["size"] = fs.Length, ["m"] = Ms(File.GetLastWriteTimeUtc(path)) });
            await s.StreamOutAsync(id, fs, null, CancellationToken.None);
        }
        return null;
    }

    private JsonObject Write(Session s, uint id, JsonObject req)
    {
        var path = Str(req, "path");
        bool inbox = req["inbox"]?.GetValue<bool>() == true;
        if (inbox)
        {
            // "Send to device": a relative name inside this side's downloads folder, never outside it.
            var parts = path.Split('/', '\\').Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new RemoteException("bad", "שם קובץ לא תקין: " + path);
            path = Path.Combine(new[] { inboxDir() }.Concat(parts).ToArray());
        }
        bool overwrite = req["overwrite"]?.GetValue<bool>() == true;
        if (Exists(path) && !overwrite) path = UniquePath(path);
        if (Directory.Exists(path)) throw new RemoteException("exists", "קיימת תיקייה בשם הזה: " + path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".btpart";
        var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var mtime = req["mtime"]?.GetValue<long>() ?? 0;
        s.ExpectStream(id, new FileSink(fs, null,
            _ =>
            {
                try
                {
                    fs.Dispose();
                    File.Move(part, path, overwrite);
                    if (mtime > 0) File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(mtime).UtcDateTime);
                    s.SendDone(id, new JsonObject { ["ok"] = true, ["path"] = path });
                    if (inbox) onReceived?.Invoke(s, path);
                }
                catch (Exception ex)
                {
                    TryDelete(part);
                    try { s.SendDone(id, new JsonObject { ["err"] = "io", ["msg"] = Heb.Of(ex) }); } catch { }
                }
            },
            why =>
            {
                fs.Dispose();
                TryDelete(part);
                try { s.SendDone(id, new JsonObject { ["err"] = "cancelled", ["msg"] = why }); } catch { }
            }));
        return new JsonObject { ["ok"] = true, ["path"] = path };
    }

    public static string UniquePath(string path)
    {
        if (!Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!Exists(p)) return p;
        }
    }

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch { }
    }

    // ---------------------------------------------------------------- recycle bin

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private static void Recycle(IEnumerable<string> paths)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join("\0", paths) + "\0\0",
            fFlags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI,
        };
        SHFileOperation(ref op);
    }
}

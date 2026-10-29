using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace BtBridge.Core;

/// <summary>
/// Writes an incoming DATA…END stream into a file on its own thread, so a slow disk never holds up the link
/// (pings, requests and other streams keep flowing). Up to 64 chunks wait in memory; beyond that the link waits.
/// onEnd and onFail run on the writer thread after everything before them was written and the file was closed.
/// </summary>
public sealed class FileSink : IStreamSink
{
    private readonly FileStream _fs;
    private readonly Action<int>? _progress;
    private readonly Action<JsonObject> _onEnd;
    private readonly Action<string> _onFail;
    private readonly System.Collections.Concurrent.BlockingCollection<object> _q = new(64);
    private volatile Exception? _error;

    private sealed record EndMark(JsonObject End);
    private sealed record FailMark(string Why);

    public FileSink(FileStream fs, Action<int>? progress, Action<JsonObject> onEnd, Action<string> onFail)
    {
        _fs = fs;
        _progress = progress;
        _onEnd = onEnd;
        _onFail = onFail;
        new Thread(Run) { IsBackground = true, Name = "file-writer" }.Start();
    }

    public void OnData(ReadOnlySpan<byte> data)
    {
        if (_error is { } e) throw e;
        Put(data.ToArray());
    }

    public void OnEnd(JsonObject end) => Put(new EndMark(end));
    public void OnFail(string why) => Put(new FailMark(why));

    /// <summary>After the end or a failure nothing more is accepted (a late record is dropped instead of waiting forever).</summary>
    private void Put(object item)
    {
        try { _q.Add(item); } catch (InvalidOperationException) { }
    }

    private void Run()
    {
        foreach (var item in _q.GetConsumingEnumerable())
        {
            switch (item)
            {
                case byte[] b:
                    if (_error != null) continue;
                    try { _fs.Write(b); _progress?.Invoke(b.Length); }
                    catch (Exception ex) { _error = ex; }
                    break;
                case EndMark m:
                    _q.CompleteAdding();
                    Finish();
                    try
                    {
                        if (_error != null) _onFail(Heb.Of(_error));
                        else _onEnd(m.End);
                    }
                    catch (Exception ex) { Log.Write("file sink end: " + ex.Message); }
                    return;
                case FailMark f:
                    _q.CompleteAdding();
                    Finish();
                    try { _onFail(f.Why); } catch (Exception ex) { Log.Write("file sink fail: " + ex.Message); }
                    return;
            }
        }
    }

    private void Finish()
    {
        try { _fs.Dispose(); } catch (Exception ex) { _error ??= ex; }
    }

    /// <summary>A failure the user asked for (cancel). Anything else (a dropped connection) keeps the partial file for resuming.</summary>
    public static bool IsCancel(string why) => why.StartsWith("בוטל");
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
        // Resume (PROTOCOL.md section 4): the target name is chosen the same way again, so an interrupted
        // upload finds its own partial file and only the rest is sent.
        long have = 0;
        bool resume = req["resume"]?.GetValue<bool>() == true && File.Exists(part);
        var fs = new FileStream(part, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        if (resume) have = fs.Length;
        var mtime = req["mtime"]?.GetValue<long>() ?? 0;
        s.ExpectStream(id, new FileSink(fs, null,
            _ =>
            {
                try
                {
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
                if (FileSink.IsCancel(why)) TryDelete(part);
                try { s.SendDone(id, new JsonObject { ["err"] = "cancelled", ["msg"] = why }); } catch { }
            }));
        return new JsonObject { ["ok"] = true, ["path"] = path, ["have"] = have };
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

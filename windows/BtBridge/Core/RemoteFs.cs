using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace BtBridge.Core;

public sealed record RemoteItem(string Name, string Path, bool IsDir, long Size, long Mtime)
{
    public string Icon => IsDir ? "📁" : "📄";
    public string SizeText => IsDir ? "" : Transfer.FormatSize(Size);
    public string DateText => Mtime <= 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(Mtime).LocalDateTime.ToString("dd/MM/yyyy HH:mm");
}

public sealed record RemoteListing(List<RemoteItem> Items, string? Parent);

/// <summary>Client side of the file operations (PROTOCOL.md section 4).</summary>
public static class RemoteFs
{
    public static async Task<RemoteListing> ListAsync(Session s, string path, CancellationToken ct = default)
    {
        var items = new List<RemoteItem>();
        string? parent = null;
        while (true)
        {
            var res = await s.RequestAsync(new JsonObject { ["op"] = "list", ["path"] = path, ["offset"] = items.Count }, ct);
            parent = res["parent"]?.GetValue<string>();
            foreach (var n in res["items"]!.AsArray())
            {
                var o = n!.AsObject();
                items.Add(new RemoteItem(
                    o["n"]!.GetValue<string>(), o["p"]!.GetValue<string>(), o["d"]!.GetValue<bool>(),
                    o["s"]?.GetValue<long>() ?? 0, o["m"]?.GetValue<long>() ?? 0));
            }
            if (res["more"]?.GetValue<bool>() != true) break;
        }
        return new RemoteListing(items, parent);
    }

    public static Task MkdirAsync(Session s, string path) =>
        s.RequestAsync(new JsonObject { ["op"] = "mkdir", ["path"] = path });

    public static Task RenameAsync(Session s, string from, string to) =>
        s.RequestAsync(new JsonObject { ["op"] = "rename", ["from"] = from, ["to"] = to });

    public static Task DeleteAsync(Session s, IEnumerable<string> paths)
    {
        var arr = new JsonArray();
        foreach (var p in paths) arr.Add(p);
        return s.RequestAsync(new JsonObject { ["op"] = "delete", ["paths"] = arr }, timeoutSec: 300);
    }

    public static string Join(Session s, string dir, string name)
    {
        var sep = s.PeerOs == "windows" ? '\\' : '/';
        return dir.TrimEnd(sep) + sep + name;
    }

    /// <summary>
    /// Brings a remote file here. When <paramref name="t"/> was interrupted before (a dropped connection),
    /// the partial file is kept and only the rest is asked for (PROTOCOL.md section 4, "read" with "offset").
    /// </summary>
    public static async Task DownloadAsync(Session s, string remotePath, string localPath, Transfer t, CancellationToken ct,
        long expectSize = -1, long expectMtime = -1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var part = localPath + ".btpart";
        long offset = t.Attempts > 0 && expectSize > 0 && File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset >= expectSize) offset = 0;
        t.Attempts++;
        t.SetDone(offset);
        var fs = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = s.NewId();
        s.ExpectStream(id, new FileSink(fs, t.Add, _ => end.TrySetResult(), why => end.TrySetException(new IOException(why))));
        using var reg = ct.Register(() => s.Cancel(id));
        try
        {
            t.LinkKind = s.DataLink.Kind;
            var res = await s.RequestAsync(new JsonObject { ["op"] = "read", ["path"] = remotePath, ["offset"] = offset }, ct, id);
            t.Total = res["size"]?.GetValue<long>() ?? 0;
            var m = res["m"]?.GetValue<long>() ?? 0;
            if (offset > 0 && (t.Total != expectSize || (expectMtime > 0 && m != expectMtime)))
                throw new FileChangedException();
            await end.Task;
            File.Move(part, localPath, true);
            if (m > 0) File.SetLastWriteTimeUtc(localPath, DateTimeOffset.FromUnixTimeMilliseconds(m).UtcDateTime);
            return;
        }
        catch (Exception ex)
        {
            s.Cancel(id);
            try { await end.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            // Keep what arrived for resuming, unless the user cancelled, the file changed, or nothing came.
            if (ex is FileChangedException || ct.IsCancellationRequested || !File.Exists(part) || new FileInfo(part).Length == 0)
                TryDelete(part);
            if (ex is not FileChangedException) throw;
        }
        // The file changed since the first try: start over.
        t.Attempts = 0;
        await DownloadAsync(s, remotePath, localPath, t, ct);
    }

    private sealed class FileChangedException() : IOException("הקובץ השתנה");

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch { }
    }

    /// <summary>
    /// Returns the path the file got on the other side (renamed if the name was taken).
    /// A retry of an interrupted upload asks the other side to keep its partial file and sends only the rest.
    /// </summary>
    public static async Task<string> UploadAsync(Session s, string localPath, string remotePath, Transfer t, CancellationToken ct,
        bool inbox = false, bool update = false)
    {
        var fi = new FileInfo(localPath);
        t.Total = fi.Length;
        bool resume = t.Attempts > 0 && s.Has("resume");
        t.Attempts++;
        t.SetDone(0);
        var id = s.NewId();
        var done = s.ExpectDone(id);
        try
        {
            var res = await s.RequestAsync(new JsonObject
            {
                ["op"] = "write",
                ["path"] = remotePath,
                ["size"] = fi.Length,
                ["mtime"] = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                ["overwrite"] = false,
                ["inbox"] = inbox,
                ["update"] = update,
                ["resume"] = resume,
            }, ct, id);
            long have = res["have"]?.GetValue<long>() ?? 0;
            if (have < 0 || have > fi.Length) throw new IOException("החלק שכבר הגיע לא מתאים לקובץ. נסה שוב.");
            t.LinkKind = s.DataLink.Kind;
            await using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true))
            {
                if (have > 0) { fs.Seek(have, SeekOrigin.Begin); t.SetDone(have); }
                await s.StreamOutAsync(id, fs, t.Add, ct);
            }
            var r = await done.WaitAsync(TimeSpan.FromMinutes(5), ct);
            Session.ThrowIfError(r);
            return r["path"]?.GetValue<string>() ?? remotePath;
        }
        catch (IOException ex) when (ex.Message.StartsWith("החלק שכבר"))
        {
            // Start the next try from scratch.
            s.Cancel(id);
            t.Attempts = 0;
            throw;
        }
        finally { s.ForgetDone(id); }
    }
}

/// <summary>
/// Files already brought here for viewing. Copying or downloading the same file again (same size and date)
/// takes the local copy instead of sending it over the air a second time.
/// </summary>
public static class ViewCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Map = new();

    private static string Key(string fp, RemoteItem i) => $"{fp}|{i.Path}|{i.Size}|{i.Mtime}";

    public static void Add(string fp, RemoteItem i, string local) => Map[Key(fp, i)] = local;

    /// <summary>The local copy, or null when there is none (or it was cleaned up or changed).</summary>
    public static string? Find(string fp, RemoteItem i)
    {
        if (!Map.TryGetValue(Key(fp, i), out var p)) return null;
        try { if (File.Exists(p) && new FileInfo(p).Length == i.Size) return p; } catch { }
        Map.TryRemove(Key(fp, i), out _);
        return null;
    }
}

/// <summary>One file transfer, shown in the transfers list.</summary>
public sealed class Transfer : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public bool Upload { get; init; }
    public string Arrow => Upload ? "⬆" : "⬇";
    public CancellationTokenSource Cts { get; private set; } = new();

    /// <summary>How many times this transfer started. A retry (more than 0) resumes from the partial file.</summary>
    public int Attempts { get; set; }

    /// <summary>The device this transfer is with, and the work itself: a retry runs it again on that device's current connection.</summary>
    public string PeerFp { get; init; } = "";
    public Func<Session, CancellationToken, Task>? Work { get; init; }

    /// <summary>The connection dropped in the middle: it continues by itself when the device connects again.</summary>
    public bool Interrupted { get => _interrupted; set { _interrupted = value; OnChanged(); OnChanged(nameof(CanResume)); } }
    private bool _interrupted;
    public bool CanResume => !_active && Work != null && (_interrupted || _status.StartsWith("שגיאה"));

    /// <summary>Ready for another try: a fresh cancel token and the active state.</summary>
    public void Restart()
    {
        Cts = new CancellationTokenSource();
        Interrupted = false;
        Active = true;
        Status = "ממתין";
    }

    private long _done, _total, _lastDone, _lastTick = Environment.TickCount64;
    private string _status = "ממתין", _speed = "", _linkKind = "";
    private bool _active = true;

    public long Total { get => Interlocked.Read(ref _total); set => Interlocked.Exchange(ref _total, value); }
    public long Done => Interlocked.Read(ref _done);
    public void Add(int n) => Interlocked.Add(ref _done, n);
    /// <summary>Progress restarts here (0, or the part that arrived before an interruption).</summary>
    public void SetDone(long v) { Interlocked.Exchange(ref _done, v); _lastDone = v; }

    /// <summary>The local file (download target, upload source, or a received file).</summary>
    public string? LocalPath { get => _localPath; set { _localPath = value; OnChanged(); } }
    private string? _localPath;

    /// <summary>A finished item that didn't go through the queue (a file the other side sent).</summary>
    public static Transfer Finished(string name, string path, string status)
    {
        long size = 0;
        try { size = new FileInfo(path).Length; } catch { }
        var t = new Transfer { Name = name, _localPath = path, _status = status, _active = false };
        t._total = t._done = size;
        return t;
    }

    public double Percent => Total > 0 ? Math.Min(100, Done * 100.0 / Total) : 0;
    public string Status { get => _status; set { _status = value; OnChanged(); OnChanged(nameof(CanResume)); } }
    public string Speed => _speed;
    public string LinkKind { get => _linkKind; set { _linkKind = value; OnChanged(); } }
    public bool Active { get => _active; set { _active = value; OnChanged(); OnChanged(nameof(CanResume)); } }

    /// <summary>Called by the UI timer to refresh progress and speed.</summary>
    public void Tick()
    {
        if (!_active) return;
        var now = Environment.TickCount64;
        var dt = now - _lastTick;
        if (dt >= 1000)
        {
            var d = Done;
            _speed = FormatSize((long)((d - _lastDone) * 1000.0 / dt)) + "/ש'";
            _lastDone = d;
            _lastTick = now;
            OnChanged(nameof(Speed));
        }
        OnChanged(nameof(Percent));
        OnChanged(nameof(SizeText));
    }

    public string SizeText => $"{FormatSize(Done)} / {FormatSize(Total)}";

    public static string FormatSize(long b) => b switch
    {
        < 1024 => $"{b} בתים",
        < 1024 * 1024 => $"{b / 1024.0:0.#} ק״ב",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} מ״ב",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} ג״ב",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

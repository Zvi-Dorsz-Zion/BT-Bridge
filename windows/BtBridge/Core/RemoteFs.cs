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

    public static async Task DownloadAsync(Session s, string remotePath, string localPath, Transfer t, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var part = localPath + ".btpart";
        var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = s.NewId();
        s.ExpectStream(id, new FileSink(fs, t.Add, _ => end.TrySetResult(), why => end.TrySetException(new IOException(why))));
        using var reg = ct.Register(() => s.Cancel(id));
        try
        {
            t.LinkKind = s.DataLink.Kind;
            var res = await s.RequestAsync(new JsonObject { ["op"] = "read", ["path"] = remotePath, ["offset"] = 0 }, ct, id);
            t.Total = res["size"]?.GetValue<long>() ?? 0;
            await end.Task;
            fs.Dispose();
            File.Move(part, localPath, true);
            var m = res["m"]?.GetValue<long>() ?? 0;
            if (m > 0) File.SetLastWriteTimeUtc(localPath, DateTimeOffset.FromUnixTimeMilliseconds(m).UtcDateTime);
        }
        catch
        {
            s.Cancel(id);
            fs.Dispose();
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>Returns the path the file got on the other side (renamed if the name was taken).</summary>
    public static async Task<string> UploadAsync(Session s, string localPath, string remotePath, Transfer t, CancellationToken ct, bool inbox = false)
    {
        var fi = new FileInfo(localPath);
        t.Total = fi.Length;
        var id = s.NewId();
        var done = s.ExpectDone(id);
        try
        {
            await s.RequestAsync(new JsonObject
            {
                ["op"] = "write",
                ["path"] = remotePath,
                ["size"] = fi.Length,
                ["mtime"] = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                ["overwrite"] = false,
                ["inbox"] = inbox,
            }, ct, id);
            t.LinkKind = s.DataLink.Kind;
            await using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true))
                await s.StreamOutAsync(id, fs, t.Add, ct);
            var r = await done.WaitAsync(TimeSpan.FromMinutes(5), ct);
            Session.ThrowIfError(r);
            return r["path"]?.GetValue<string>() ?? remotePath;
        }
        finally { s.ForgetDone(id); }
    }
}

/// <summary>One file transfer, shown in the transfers list.</summary>
public sealed class Transfer : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public bool Upload { get; init; }
    public string Arrow => Upload ? "⬆" : "⬇";
    public CancellationTokenSource Cts { get; } = new();

    private long _done, _total, _lastDone, _lastTick = Environment.TickCount64;
    private string _status = "ממתין", _speed = "", _linkKind = "";
    private bool _active = true;

    public long Total { get => Interlocked.Read(ref _total); set => Interlocked.Exchange(ref _total, value); }
    public long Done => Interlocked.Read(ref _done);
    public void Add(int n) => Interlocked.Add(ref _done, n);

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
    public string Status { get => _status; set { _status = value; OnChanged(); } }
    public string Speed => _speed;
    public string LinkKind { get => _linkKind; set { _linkKind = value; OnChanged(); } }
    public bool Active { get => _active; set { _active = value; OnChanged(); } }

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

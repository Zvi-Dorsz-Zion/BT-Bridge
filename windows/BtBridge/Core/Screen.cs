using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace BtBridge.Core;

public sealed record MonitorInfo(int Index, string Name, int X, int Y, int W, int H, bool Primary);

/// <summary>Captures the screen. Windows: GDI. Tests: synthetic frames.</summary>
public interface IScreenGrabber
{
    IReadOnlyList<MonitorInfo> Monitors();
    /// <summary>The monitor scaled to w×h, 32-bit pixels, stride w. Null when capture is unavailable (e.g. the UAC screen).</summary>
    int[]? Grab(int monitor, int w, int h);
}

/// <summary>Performs remote input (PROTOCOL.md section 7). Coordinates are already in real screen pixels.</summary>
public interface IInputSink
{
    void Input(JsonObject ev, MonitorInfo mon, int x, int y, bool restoreCursor);
}

/// <summary>JPEG tiles. Windows: System.Drawing.</summary>
public interface ITileCodec
{
    byte[] Encode(int[] px, int stride, int x, int y, int w, int h, int quality);
    int[] Decode(byte[] jpeg, out int w, out int h);
}

public sealed record ScreenProfile(int MaxW, int Quality, int Fps)
{
    public static readonly ScreenProfile Sharp = new(1920, 70, 15);
    public static readonly ScreenProfile Fast = new(960, 35, 4);

    public static ScreenProfile For(string profile, bool wifi) => profile switch
    {
        "sharp" => Sharp,
        "fast" => Fast,
        _ => wifi ? Sharp : Fast,
    };
}

public sealed record ScreenTile(int X, int Y, int W, int H, byte[] Jpeg);

/// <summary>Frame message layout and change detection (PROTOCOL.md section 7).</summary>
public static class ScreenProto
{
    public const int Tile = 128;
    public const byte First = 1, Last = 2;

    public static byte[] Build(uint seq, int w, int h, IReadOnlyList<ScreenTile> tiles)
    {
        var o = new byte[11 + tiles.Sum(t => 12 + t.Jpeg.Length)];
        var s = o.AsSpan();
        s[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(s[1..], seq);
        BinaryPrimitives.WriteUInt16BigEndian(s[5..], (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(s[7..], (ushort)h);
        BinaryPrimitives.WriteUInt16BigEndian(s[9..], (ushort)tiles.Count);
        int at = 11;
        foreach (var t in tiles)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[at..], (ushort)t.X);
            BinaryPrimitives.WriteUInt16BigEndian(s[(at + 2)..], (ushort)t.Y);
            BinaryPrimitives.WriteUInt16BigEndian(s[(at + 4)..], (ushort)t.W);
            BinaryPrimitives.WriteUInt16BigEndian(s[(at + 6)..], (ushort)t.H);
            BinaryPrimitives.WriteInt32BigEndian(s[(at + 8)..], t.Jpeg.Length);
            t.Jpeg.CopyTo(o, at + 12);
            at += 12 + t.Jpeg.Length;
        }
        return o;
    }

    public static (uint Seq, int W, int H, List<ScreenTile> Tiles) Parse(byte[] m)
    {
        var s = m.AsSpan();
        if (m.Length < 11 || s[0] != 1) throw new ProtocolException("פריים לא תקין");
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(s[1..]);
        int w = BinaryPrimitives.ReadUInt16BigEndian(s[5..]);
        int h = BinaryPrimitives.ReadUInt16BigEndian(s[7..]);
        int n = BinaryPrimitives.ReadUInt16BigEndian(s[9..]);
        var tiles = new List<ScreenTile>(n);
        int at = 11;
        for (int i = 0; i < n; i++)
        {
            int x = BinaryPrimitives.ReadUInt16BigEndian(s[at..]);
            int y = BinaryPrimitives.ReadUInt16BigEndian(s[(at + 2)..]);
            int tw = BinaryPrimitives.ReadUInt16BigEndian(s[(at + 4)..]);
            int th = BinaryPrimitives.ReadUInt16BigEndian(s[(at + 6)..]);
            int len = BinaryPrimitives.ReadInt32BigEndian(s[(at + 8)..]);
            if (len < 0 || at + 12 + len > m.Length) throw new ProtocolException("פריים קטוע");
            tiles.Add(new ScreenTile(x, y, tw, th, m[(at + 12)..(at + 12 + len)]));
            at += 12 + len;
        }
        return (seq, w, h, tiles);
    }

    /// <summary>Rectangles that changed since prev (whole frame when prev is null). Neighbouring changed tiles in a row are merged.</summary>
    public static List<(int X, int Y, int W, int H)> Changed(int[] cur, int[]? prev, int w, int h)
    {
        var list = new List<(int, int, int, int)>();
        for (int ty = 0; ty < h; ty += Tile)
        {
            int th = Math.Min(Tile, h - ty);
            int runStart = -1;
            for (int tx = 0; ; tx += Tile)
            {
                bool inside = tx < w;
                bool changed = inside && (prev == null || TileDiffers(cur, prev, w, tx, ty, Math.Min(Tile, w - tx), th));
                if (runStart >= 0 && (!changed || tx - runStart >= 4 * Tile))
                {
                    list.Add((runStart, ty, Math.Min(tx, w) - runStart, th));
                    runStart = -1;
                }
                if (changed && runStart < 0) runStart = tx;
                if (!inside) break;
            }
        }
        return list;
    }

    private static bool TileDiffers(int[] a, int[] b, int stride, int x, int y, int w, int h)
    {
        for (int r = 0; r < h; r++)
        {
            int at = (y + r) * stride + x;
            if (!a.AsSpan(at, w).SequenceEqual(b.AsSpan(at, w))) return true;
        }
        return false;
    }

    public static (int W, int H) Fit(int w, int h, int maxW)
    {
        if (w <= maxW) return (w & ~1, h & ~1);
        double k = (double)maxW / w;
        return ((int)(w * k) & ~1, (int)(h * k) & ~1);
    }
}

/// <summary>The sharing side of one screen stream: captures, sends changed tiles, applies remote input.</summary>
public sealed class ScreenSource
{
    private static readonly ConcurrentDictionary<(Session, uint), ScreenSource> Active = new();

    public Session Session { get; }
    public uint Id { get; }
    private readonly IScreenGrabber _grab;
    private readonly IInputSink _input;
    private readonly ITileCodec _codec;
    private readonly CancellationTokenSource _cts = new();
    private volatile int _monitor;
    private volatile string _profile;
    private volatile bool _key = true;
    private readonly bool _restoreCursor;
    private long _acked;
    private readonly AutoResetEvent _ackSignal = new(false);

    public static event Action<ScreenSource, bool>? ActiveChanged;
    public static int Count => Active.Count;

    public ScreenSource(Session s, uint id, IScreenGrabber grab, IInputSink input, ITileCodec codec, JsonObject req)
    {
        Session = s;
        Id = id;
        _grab = grab;
        _input = input;
        _codec = codec;
        _monitor = req["monitor"]?.GetValue<int>() ?? -1;
        _profile = req["profile"]?.GetValue<string>() ?? "auto";
        _restoreCursor = req["restoreCursor"]?.GetValue<bool>() == true;
        var mons = grab.Monitors();
        if (_monitor < 0 || _monitor >= mons.Count) _monitor = Math.Max(0, mons.ToList().FindIndex(m => m.Primary));
    }

    public void Start()
    {
        Active[(Session, Id)] = this;
        Session.ExpectCancel(Id, Stop);
        Session.OnClosed += _ => Stop();
        ActiveChanged?.Invoke(this, true);
        new Thread(Loop) { IsBackground = true, Name = "screen-source" }.Start();
    }

    public void Stop()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _ackSignal.Set();
        Active.TryRemove((Session, Id), out _);
        ActiveChanged?.Invoke(this, false);
    }

    public static void StopAll(Session? s = null)
    {
        foreach (var src in Active.Values)
            if (s == null || src.Session == s) src.Stop();
    }

    /// <summary>Routes screen-ack / screen-cfg / input events. Returns false when the event is not for a screen stream.</summary>
    public static bool OnEvent(Session s, JsonObject ev)
    {
        var name = ev["ev"]?.GetValue<string>();
        if (name is not ("screen-ack" or "screen-cfg" or "input")) return false;
        var id = ev["id"]?.GetValue<uint>() ?? 0;
        if (!Active.TryGetValue((s, id), out var src)) return true;
        switch (name)
        {
            case "screen-ack":
                Interlocked.Exchange(ref src._acked, ev["seq"]?.GetValue<long>() ?? 0);
                src._ackSignal.Set();
                break;
            case "screen-cfg":
                if (ev["profile"] is JsonNode p) src._profile = p.GetValue<string>();
                if (ev["monitor"] is JsonNode m) src._monitor = Math.Clamp(m.GetValue<int>(), 0, src._grab.Monitors().Count - 1);
                src._key = true;
                break;
            case "input":
                src.ApplyInput(ev);
                break;
        }
        return true;
    }

    private void ApplyInput(JsonObject ev)
    {
        try
        {
            var mons = _grab.Monitors();
            var mon = mons[Math.Min(_monitor, mons.Count - 1)];
            int fw = Math.Max(1, ev["fw"]?.GetValue<int>() ?? mon.W);
            int fh = Math.Max(1, ev["fh"]?.GetValue<int>() ?? mon.H);
            int x = mon.X + (int)Math.Round((ev["x"]?.GetValue<double>() ?? 0) * mon.W / fw);
            int y = mon.Y + (int)Math.Round((ev["y"]?.GetValue<double>() ?? 0) * mon.H / fh);
            _input.Input(ev, mon, x, y, _restoreCursor);
        }
        catch (Exception ex) { Log.Write("input: " + ex.Message); }
    }

    private void Loop()
    {
        uint seq = 0;
        int[]? prev = null;
        int pw = 0, ph = 0;
        long lastSent = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                long start = Environment.TickCount64;
                var link = Session.DataLink;
                var prof = ScreenProfile.For(_profile, link.IsWifi);
                var mons = _grab.Monitors();
                var mon = mons[Math.Min(_monitor, mons.Count - 1)];
                var (w, h) = ScreenProto.Fit(mon.W, mon.H, prof.MaxW);
                var px = _grab.Grab(mon.Index, w, h);
                if (px == null) { Thread.Sleep(500); continue; }

                bool full = _key || prev == null || w != pw || h != ph;
                _key = false;
                var rects = ScreenProto.Changed(px, full ? null : prev, w, h);
                // An empty frame every few seconds keeps the stream (and its watchdog) alive.
                if (rects.Count > 0 || start - lastSent > 5000)
                {
                    var tiles = rects.Select(r => new ScreenTile(r.X, r.Y, r.W, r.H, _codec.Encode(px, w, r.X, r.Y, r.W, r.H, prof.Quality))).ToList();
                    SendFrame(link, ScreenProto.Build(++seq, w, h, tiles));
                    lastSent = start;
                    prev = px; pw = w; ph = h;
                    // At most 2 frames in flight.
                    while (!_cts.IsCancellationRequested && seq - Interlocked.Read(ref _acked) > 2)
                        if (!_ackSignal.WaitOne(3000)) break;
                }
                int wait = 1000 / prof.Fps - (int)(Environment.TickCount64 - start);
                if (wait > 0) _cts.Token.WaitHandle.WaitOne(wait);
            }
        }
        catch (Exception ex)
        {
            // Stopping (the viewer cancelled) also lands here; an uncaught exception would end the whole app.
            if (!_cts.IsCancellationRequested) Log.Write("screen source: " + ex.Message);
        }
        Stop();
        try { Session.DataLink.SendData(Rec.Json(Rec.End, Id, new JsonObject { ["ok"] = true }), CancellationToken.None); } catch { }
    }

    private void SendFrame(Link link, byte[] msg)
    {
        int chunk = link.ChunkSize - 1;
        for (int at = 0; at < msg.Length; at += chunk)
        {
            int n = Math.Min(chunk, msg.Length - at);
            var body = new byte[n + 1];
            body[0] = (byte)((at == 0 ? ScreenProto.First : 0) | (at + n >= msg.Length ? ScreenProto.Last : 0));
            Buffer.BlockCopy(msg, at, body, 1, n);
            link.SendData(Rec.Make(Rec.Data, Id, body), _cts.Token);
        }
    }
}

/// <summary>The viewing side of one screen stream.</summary>
public sealed class ScreenClient : IStreamSink
{
    private readonly Session _s;
    private readonly MemoryStream _buf = new();
    private bool _inFrame;
    public uint Id { get; private set; }
    public int FrameW { get; private set; }
    public int FrameH { get; private set; }
    public string Kind { get; private set; } = "";
    /// <summary>False when the phone can show its screen but can't take input (accessibility service off).</summary>
    public bool InputAvailable { get; private set; } = true;
    public IReadOnlyList<MonitorInfo> Monitors { get; private set; } = Array.Empty<MonitorInfo>();

    /// <summary>Called on the link's reader thread. Decode there; draw on the UI thread.</summary>
    public event Action<uint, int, int, List<ScreenTile>>? Frame;
    public event Action<string>? Closed;

    public ScreenClient(Session s) { _s = s; }

    public async Task OpenAsync(int monitor, string profile, bool restoreCursor, CancellationToken ct = default)
    {
        Id = _s.NewId();
        _s.ExpectStream(Id, this, sticky: false);
        try
        {
            var res = await _s.RequestAsync(new JsonObject
            {
                ["op"] = "screen-open",
                ["monitor"] = monitor,
                ["profile"] = profile,
                ["restoreCursor"] = restoreCursor,
            }, ct, Id, timeoutSec: 100);
            Kind = res["kind"]?.GetValue<string>() ?? "";
            InputAvailable = res["input"]?.GetValue<bool>() != false;
            Monitors = res["monitors"]?.AsArray().Select(n => new MonitorInfo(
                n!["i"]!.GetValue<int>(), n["name"]?.GetValue<string>() ?? "", 0, 0,
                n["w"]!.GetValue<int>(), n["h"]!.GetValue<int>(), n["primary"]?.GetValue<bool>() == true)).ToList()
                ?? new List<MonitorInfo>();
        }
        catch
        {
            _s.ForgetStream(Id);
            throw;
        }
    }

    public void Configure(string? profile = null, int? monitor = null, bool key = false)
    {
        var o = new JsonObject { ["ev"] = "screen-cfg", ["id"] = Id, ["key"] = key };
        if (profile != null) o["profile"] = profile;
        if (monitor != null) o["monitor"] = monitor;
        try { _s.SendEvent(o); } catch { }
    }

    /// <summary>Sends an input event. x/y are in frame pixels.</summary>
    public void Input(string type, double x = 0, double y = 0, Action<JsonObject>? extra = null)
    {
        var o = new JsonObject { ["ev"] = "input", ["id"] = Id, ["t"] = type, ["x"] = x, ["y"] = y, ["fw"] = FrameW, ["fh"] = FrameH };
        extra?.Invoke(o);
        try { _s.SendEvent(o); } catch { }
    }

    public void Close() => _s.Cancel(Id);

    public void OnData(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        byte flags = data[0];
        if ((flags & ScreenProto.First) != 0) { _buf.SetLength(0); _inFrame = true; }
        if (!_inFrame) return;
        _buf.Write(data[1..]);
        if ((flags & ScreenProto.Last) == 0) return;
        _inFrame = false;
        var (seq, w, h, tiles) = ScreenProto.Parse(_buf.ToArray());
        FrameW = w;
        FrameH = h;
        try { Frame?.Invoke(seq, w, h, tiles); }
        catch (Exception ex) { Log.Write("frame: " + ex.Message); }
        try { _s.SendEvent(new JsonObject { ["ev"] = "screen-ack", ["id"] = Id, ["seq"] = seq }); } catch { }
    }

    public void OnEnd(JsonObject end) => Closed?.Invoke("השיתוף הסתיים");
    public void OnFail(string why) => Closed?.Invoke(why);
}

using System.Collections.Concurrent;

namespace BtBridge.Core;

/// <summary>
/// One encrypted transport (Bluetooth or Wi-Fi). Control records always go before data records,
/// so a big transfer never blocks requests.
/// </summary>
public sealed class Link
{
    public bool IsWifi { get; }
    public string Kind => IsWifi ? "וויפי" : "בלוטוס";
    public int ChunkSize => IsWifi ? 256 * 1024 : 16 * 1024;
    public bool Alive => !_cts.IsCancellationRequested;

    public Action<Link, byte[]>? OnRecord;
    public Action<Link>? OnClosed;

    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly RecordCodec _codec;
    private readonly BlockingCollection<byte[]> _high = new();
    private readonly BlockingCollection<byte[]> _low = new(8);
    private readonly CancellationTokenSource _cts = new();
    private long _lastRx = Environment.TickCount64;
    private Timer? _pinger;
    private int _closed;

    public Link(bool wifi, Stream stream, IDisposable? owner, RecordCodec codec)
    {
        IsWifi = wifi;
        _stream = stream;
        _owner = owner;
        _codec = codec;
    }

    public void Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = Kind + "-read" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = Kind + "-write" }.Start();
        _pinger = new Timer(_ =>
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastRx) > 45_000) { Close("אין תגובה"); return; }
            try { Send(Rec.Make(Rec.Ping, 0, default)); } catch { }
        }, null, 15_000, 15_000);
    }

    /// <summary>Control record. Never blocks.</summary>
    public void Send(byte[] plain)
    {
        if (!Alive) throw new IOException("החיבור נסגר");
        try { _high.Add(plain); } catch (InvalidOperationException) { throw new IOException("החיבור נסגר"); }
    }

    /// <summary>Data record. Blocks while the queue is full (back-pressure).</summary>
    public void SendData(byte[] plain, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        try { _low.Add(plain, linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("החיבור נסגר"); }
    }

    private void WriteLoop()
    {
        var queues = new[] { _high, _low };
        try
        {
            while (true)
            {
                BlockingCollection<byte[]>.TakeFromAny(queues, out var item, _cts.Token);
                Wire.Write(_stream, _codec.Seal(item));
            }
        }
        catch (Exception ex) { Close(ex is OperationCanceledException ? null : ex.Message); }
    }

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                var plain = _codec.Open(Wire.Read(_stream));
                Interlocked.Exchange(ref _lastRx, Environment.TickCount64);
                switch (plain[0])
                {
                    case Rec.Ping: Send(Rec.Make(Rec.Pong, 0, default)); break;
                    case Rec.Pong: break;
                    default: OnRecord?.Invoke(this, plain); break;
                }
            }
        }
        catch (Exception ex) { Close(ex.Message); }
    }

    public void Close(string? why = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        if (why != null) Log.Write($"{Kind} link closed: {why}");
        _cts.Cancel();
        _pinger?.Dispose();
        try { _stream.Dispose(); } catch { }
        try { _owner?.Dispose(); } catch { }
        OnClosed?.Invoke(this);
    }
}

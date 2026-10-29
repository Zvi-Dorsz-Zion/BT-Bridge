using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace BtBridge.Core;

public interface IRequestHandler
{
    /// <summary>Returns the response, or null when the handler replied itself (streams).</summary>
    Task<JsonObject?> HandleAsync(Session s, uint id, JsonObject req);
    void OnEvent(Session s, JsonObject ev);
}

/// <summary>Consumer of an incoming DATA…END stream.</summary>
public interface IStreamSink
{
    void OnData(ReadOnlySpan<byte> data);
    void OnEnd(JsonObject end);
    void OnFail(string why);
}

/// <summary>One authenticated connection to another device, over one or two links.</summary>
public sealed class Session
{
    public string PeerName { get; }
    public string PeerOs { get; }
    public string PeerFp { get; }
    public string PeerAddress { get; }
    public bool Initiator { get; }
    public byte[] LinkKey { get; }
    public bool Closed { get; private set; }
    /// <summary>The other side's program version ("0.3.0").</summary>
    public string PeerApp { get; }
    public IReadOnlySet<string> PeerCaps { get; }
    public bool Has(string cap) => PeerCaps.Contains(cap);

    /// <summary>Wi-Fi addresses the other side announced (PROTOCOL.md section 5).</summary>
    public string[] PeerIps { get; set; } = Array.Empty<string>();
    public int PeerPort { get; set; }

    /// <summary>The phone's battery (PROTOCOL.md section 5, "battery"), or -1 before it reported.</summary>
    public int Battery { get; set; } = -1;
    public bool Charging { get; set; }

    /// <summary>Wi-Fi link ids already accepted on this session: a recorded hello can't be replayed (PROTOCOL.md section 6).</summary>
    public HashSet<string> UsedLinkIds { get; } = new();

    public event Action<Session>? Changed;
    public event Action<Session>? OnClosed;
    /// <summary>The Wi-Fi link dropped while Bluetooth is still up.</summary>
    public event Action<Session>? WifiLost;

    private readonly IRequestHandler _handler;
    private readonly object _gate = new();
    private Link? _bt, _wifi;
    private long _nextId;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<JsonObject>> _done = new();
    private readonly ConcurrentDictionary<uint, SinkEntry> _sinks = new();
    private readonly ConcurrentDictionary<uint, CancellationTokenSource> _outgoing = new();
    private readonly Timer _watchdog;

    private readonly ConcurrentDictionary<uint, Action> _cancelHandlers = new();

    /// <summary>Last time a file or screen stream moved data (for closing an idle direct network).</summary>
    public long LastDataActivity => Interlocked.Read(ref _lastData);
    private long _lastData = Environment.TickCount64;
    public bool HasActiveStreams => !_sinks.IsEmpty || !_outgoing.IsEmpty || !_cancelHandlers.IsEmpty;

    private sealed class SinkEntry(IStreamSink sink, bool sticky)
    {
        public IStreamSink Sink { get; } = sink;
        /// <summary>Sticky streams (files) fail when their link drops. Screen streams survive a link change.</summary>
        public bool Sticky { get; } = sticky;
        public Link? Link;
        public long LastActivity = Environment.TickCount64;
    }

    public Session(HandshakeResult hr, string peerAddress, bool initiator, IRequestHandler handler)
    {
        PeerName = hr.PeerName;
        PeerOs = hr.PeerOs;
        PeerFp = hr.PeerFp;
        PeerAddress = peerAddress;
        Initiator = initiator;
        LinkKey = hr.LinkKey;
        PeerApp = hr.PeerApp;
        PeerCaps = hr.PeerCaps;
        _handler = handler;
        _nextId = initiator ? -1 : 0;
        _watchdog = new Timer(_ => Watchdog(), null, 15_000, 15_000);
    }

    public bool HasWifi { get { lock (_gate) return _wifi is { Alive: true }; } }
    public string LinkText => HasWifi ? "וויפי + בלוטוס" : "בלוטוס";

    private Link Control
    {
        get { lock (_gate) return _bt ?? _wifi ?? throw new IOException("החיבור נסגר"); }
    }

    public Link DataLink
    {
        get { lock (_gate) return _wifi is { Alive: true } w ? w : _bt ?? throw new IOException("החיבור נסגר"); }
    }

    public void Attach(Link link)
    {
        Link? old;
        lock (_gate)
        {
            if (link.IsWifi) { old = _wifi; _wifi = link; }
            else { old = _bt; _bt = link; }
        }
        link.OnRecord = Dispatch;
        link.OnClosed = LinkClosed;
        link.Start();
        old?.Close("הוחלף");
        Log.Write($"session {PeerName}: {link.Kind} link up");
        Changed?.Invoke(this);
    }

    private void LinkClosed(Link link)
    {
        bool dead, wifiLost = false;
        lock (_gate)
        {
            if (_bt == link) _bt = null;
            if (_wifi == link) { _wifi = null; wifiLost = true; }
            dead = _bt == null && _wifi == null;
        }
        foreach (var (id, e) in _sinks)
            if (dead || (e.Sticky && e.Link == link))
                if (_sinks.TryRemove(id, out var gone)) gone.Sink.OnFail("החיבור נותק");
        if (dead) Close();
        else
        {
            Changed?.Invoke(this);
            if (wifiLost) WifiLost?.Invoke(this);
        }
    }

    public void Close()
    {
        Link? bt, wifi;
        lock (_gate)
        {
            if (Closed) return;
            Closed = true;
            bt = _bt; wifi = _wifi;
        }
        _watchdog.Dispose();
        bt?.Close();
        wifi?.Close();
        foreach (var t in _pending.Values) t.TrySetException(new IOException("החיבור נסגר"));
        foreach (var t in _done.Values) t.TrySetException(new IOException("החיבור נסגר"));
        foreach (var c in _outgoing.Values) c.Cancel();
        foreach (var e in _sinks.Values) e.Sink.OnFail("החיבור נסגר");
        _sinks.Clear();
        Log.Write($"session {PeerName} closed");
        OnClosed?.Invoke(this);
    }

    private void Watchdog()
    {
        try
        {
            var now = Environment.TickCount64;
            foreach (var (id, e) in _sinks)
                if (now - Interlocked.Read(ref e.LastActivity) > 90_000 && _sinks.TryRemove(id, out _))
                    e.Sink.OnFail("ההעברה נתקעה");
        }
        catch (Exception ex) { Log.Write("watchdog: " + ex.Message); }
    }

    // ---------------------------------------------------------------- outgoing

    public uint NewId() => (uint)Interlocked.Add(ref _nextId, 2);

    public async Task<JsonObject> RequestAsync(JsonObject req, CancellationToken ct = default, uint? id = null, int timeoutSec = 60)
    {
        var rid = id ?? NewId();
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[rid] = tcs;
        try
        {
            Control.Send(Rec.Json(Rec.Req, rid, req));
            var res = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSec), ct);
            ThrowIfError(res);
            return res;
        }
        catch (TimeoutException) { throw new IOException("אין תשובה מהמכשיר השני"); }
        finally { _pending.TryRemove(rid, out _); }
    }

    public static void ThrowIfError(JsonObject o)
    {
        if (o["err"] is JsonNode e)
            throw new RemoteException(e.GetValue<string>(), o["msg"]?.GetValue<string>() ?? e.GetValue<string>());
    }

    public void Reply(uint id, JsonObject o) => Control.Send(Rec.Json(Rec.Res, id, o));
    public void SendEvent(JsonObject o) => Control.Send(Rec.Json(Rec.Evt, 0, o));
    public void SendDone(uint id, JsonObject o) => Control.Send(Rec.Json(Rec.Done, id, o));

    public void Cancel(uint id)
    {
        if (_outgoing.TryGetValue(id, out var c)) c.Cancel();
        if (_sinks.TryRemove(id, out var e)) e.Sink.OnFail("בוטל");
        if (_cancelHandlers.TryRemove(id, out var h)) h();
        try { Control.Send(Rec.Make(Rec.Cancel, id, default)); } catch { }
    }

    public void ExpectStream(uint id, IStreamSink sink, bool sticky = true) => _sinks[id] = new SinkEntry(sink, sticky);
    public void ForgetStream(uint id) => _sinks.TryRemove(id, out _);

    /// <summary>Runs when the other side cancels stream id (used by long-running sources such as screen sharing).</summary>
    public void ExpectCancel(uint id, Action onCancel) => _cancelHandlers[id] = onCancel;
    public void ForgetCancel(uint id) => _cancelHandlers.TryRemove(id, out _);
    public void TouchData() => Interlocked.Exchange(ref _lastData, Environment.TickCount64);

    public Task<JsonObject> ExpectDone(uint id)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _done[id] = tcs;
        return tcs.Task;
    }

    public void ForgetDone(uint id) => _done.TryRemove(id, out _);

    /// <summary>Sends src as DATA records on the data link, then END on the same link.</summary>
    public async Task StreamOutAsync(uint id, Stream src, Action<int>? progress, CancellationToken ct)
    {
        var link = DataLink;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _outgoing[id] = cts;
        try
        {
            var buf = new byte[link.ChunkSize];
            int n;
            while ((n = await src.ReadAsync(buf, cts.Token)) > 0)
            {
                var rec = Rec.Make(Rec.Data, id, buf.AsSpan(0, n));
                await Task.Run(() => link.SendData(rec, cts.Token), CancellationToken.None);
                progress?.Invoke(n);
                TouchData();
            }
            link.SendData(Rec.Json(Rec.End, id, new JsonObject { ["ok"] = true }), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            try { link.SendData(Rec.Json(Rec.End, id, new JsonObject { ["err"] = "cancelled" }), CancellationToken.None); } catch { }
            throw;
        }
        catch (Exception ex) when (ex is not IOException || link.Alive)
        {
            // A local read error: tell the other side, so it doesn't wait for the rest.
            try { link.SendData(Rec.Json(Rec.End, id, new JsonObject { ["err"] = Heb.Of(ex) }), CancellationToken.None); } catch { }
            throw;
        }
        finally { _outgoing.TryRemove(id, out _); }
    }

    // ---------------------------------------------------------------- incoming

    private void Dispatch(Link link, byte[] plain)
    {
        var (type, id, body) = Rec.Parse(plain);
        switch (type)
        {
            case Rec.Req:
                var req = Rec.ParseJson(body);
                _ = Task.Run(() => HandleRequest(id, req));
                break;
            case Rec.Res:
                if (_pending.TryRemove(id, out var p)) p.TrySetResult(Rec.ParseJson(body));
                break;
            case Rec.Done:
                if (_done.TryRemove(id, out var d)) d.TrySetResult(Rec.ParseJson(body));
                break;
            case Rec.Data:
                Interlocked.Exchange(ref _lastData, Environment.TickCount64);
                if (_sinks.TryGetValue(id, out var e))
                {
                    e.Link ??= link;
                    Interlocked.Exchange(ref e.LastActivity, Environment.TickCount64);
                    try { e.Sink.OnData(body.Span); }
                    catch (Exception ex)
                    {
                        _sinks.TryRemove(id, out _);
                        e.Sink.OnFail(Heb.Of(ex));
                        try { Control.Send(Rec.Make(Rec.Cancel, id, default)); } catch { }
                    }
                }
                break;
            case Rec.End:
                if (_sinks.TryRemove(id, out var end))
                {
                    var o = Rec.ParseJson(body);
                    if (o["err"] != null) end.Sink.OnFail(o["err"]!.GetValue<string>() == "cancelled" ? "בוטל" : o["err"]!.GetValue<string>());
                    else end.Sink.OnEnd(o);
                }
                break;
            case Rec.Cancel:
                if (_outgoing.TryGetValue(id, out var c)) c.Cancel();
                if (_sinks.TryRemove(id, out var cs)) cs.Sink.OnFail("בוטל בצד השני");
                if (_cancelHandlers.TryRemove(id, out var ch)) ch();
                break;
            case Rec.Evt:
                try { _handler.OnEvent(this, Rec.ParseJson(body)); }
                catch (Exception ex) { Log.Write("event: " + ex.Message); }
                break;
        }
    }

    private async Task HandleRequest(uint id, JsonObject req)
    {
        JsonObject? res;
        try { res = await _handler.HandleAsync(this, id, req); }
        catch (RemoteException ex) { res = new JsonObject { ["err"] = ex.Code, ["msg"] = Heb.Of(ex) }; }
        catch (FileNotFoundException ex) { res = new JsonObject { ["err"] = "notfound", ["msg"] = Heb.Of(ex) }; }
        catch (DirectoryNotFoundException ex) { res = new JsonObject { ["err"] = "notfound", ["msg"] = Heb.Of(ex) }; }
        catch (UnauthorizedAccessException ex) { res = new JsonObject { ["err"] = "denied", ["msg"] = Heb.Of(ex) }; }
        catch (OperationCanceledException) { res = null; }
        catch (Exception ex) { res = new JsonObject { ["err"] = "io", ["msg"] = Heb.Of(ex) }; }
        if (res != null)
        {
            try { Reply(id, res); } catch { }
        }
    }
}

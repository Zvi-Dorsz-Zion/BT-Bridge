using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using InTheHand.Net;
using InTheHand.Net.Sockets;
using static BtBridge.Core.Crypto;

namespace BtBridge.Core;

/// <summary>The app's core: Bluetooth listener, outgoing connections, sessions.</summary>
public sealed class Bridge : IRequestHandler
{
    public static readonly Guid ServiceUuid = new("7f3c2a10-5b8e-4d21-9c47-2e6b1a9d0f53");

    public AppSettings Settings { get; }
    public Identity Me { get; }
    public FileService Files { get; }
    public WifiLinks Wifi { get; }
    public string ListenStatus { get; private set; } = "מתחיל…";

    /// <summary>Set by the UI. Called on a worker thread.</summary>
    public AskPassword? AskPassword { get; set; }

    public event Action? SessionsChanged;
    public event Action<string>? Notice;
    /// <summary>A file arrived in the downloads folder through "send to device".</summary>
    public event Action<Session, string>? Received;

    public DirectNetwork Direct { get; } = new();
    /// <summary>Screen capture and input for screen sharing. Tests replace them.</summary>
    public Func<IScreenGrabber> Grabber { get; set; } = () => new GdiGrabber();
    public IInputSink Input { get; set; } = new WinInput();
    public ITileCodec Codec { get; } = new GdiTileCodec();

    private readonly Lockout _lockout = new();
    private readonly List<Session> _sessions = new();
    private readonly HashSet<string> _connecting = new();

    public Bridge()
    {
        Settings = AppSettings.Load();
        Me = Identity.LoadOrCreate();
        Files = new FileService(() => Settings.DeviceName, () => Settings.DownloadDir, (s, p) => Received?.Invoke(s, p));
        Wifi = new WifiLinks(this);
    }

    public bool IsConnecting(string address)
    {
        lock (_connecting) return _connecting.Contains(address);
    }

    public string DisplayName(Session s) => Settings.NameFor(s.PeerFp, s.PeerName);

    public IReadOnlyList<Session> Sessions
    {
        get { lock (_sessions) return _sessions.Where(s => !s.Closed).ToList(); }
    }

    public Session? SessionFor(string address) =>
        Sessions.FirstOrDefault(s => s.PeerAddress == address);

    public void Start()
    {
        Wifi.Start();
        new Thread(ListenLoop) { IsBackground = true, Name = "bt-listen" }.Start();
        new Thread(ReconnectLoop) { IsBackground = true, Name = "bt-reconnect" }.Start();
        _cleanup = new Timer(_ => { try { TempFiles.Cleanup(Settings.ViewCleanupHours); } catch { } }, null, 10_000, 15 * 60_000);
    }

    private Timer? _cleanup;

    /// <summary>
    /// Keeps known devices connected: tries each one that is paired and not connected, backing off
    /// from 30 seconds to 5 minutes so an absent phone doesn't keep the Bluetooth radio busy.
    /// </summary>
    private void ReconnectLoop()
    {
        var next = new Dictionary<string, (DateTime At, int Delay)>();
        Thread.Sleep(5000);
        while (true)
        {
            try
            {
                if (Settings.AutoReconnect)
                {
                    List<InTheHand.Net.Sockets.BluetoothDeviceInfo> paired;
                    using (var bc = new BluetoothClient()) paired = bc.PairedDevices.ToList();
                    foreach (var t in Settings.Trusted.ToList())
                    {
                        if (t.Address.Length == 0 || SessionFor(t.Address) != null || IsConnecting(t.Address)) { next.Remove(t.Address); continue; }
                        var dev = paired.FirstOrDefault(d => KeyOf(d.DeviceAddress) == t.Address);
                        if (dev == null) continue;
                        if (next.TryGetValue(t.Address, out var n) && DateTime.UtcNow < n.At) continue;
                        try
                        {
                            ConnectAsync(dev.DeviceAddress, interactive: false).GetAwaiter().GetResult();
                            next.Remove(t.Address);
                        }
                        catch
                        {
                            int delay = Math.Min(300, next.TryGetValue(t.Address, out var p) ? p.Delay * 2 : 30);
                            next[t.Address] = (DateTime.UtcNow.AddSeconds(delay), delay);
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("reconnect: " + ex.Message); }
            Thread.Sleep(10_000);
        }
    }

    public static string KeyOf(BluetoothAddress a) =>
        new string(a.ToString().Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    // ---------------------------------------------------------------- incoming

    private void ListenLoop()
    {
        while (true)
        {
            BluetoothListener? listener = null;
            try
            {
                listener = new BluetoothListener(ServiceUuid) { ServiceName = "גשר בלוטוס" };
                listener.Start();
                SetStatus("מוכן לחיבורים בבלוטוס");
                while (true)
                {
                    var c = listener.AcceptBluetoothClient();
                    _ = Task.Run(() => Incoming(c));
                }
            }
            catch (Exception ex)
            {
                Log.Write("listen: " + ex.Message);
                SetStatus("הבלוטוס לא זמין. מנסה שוב…");
                try { listener?.Stop(); } catch { }
                Thread.Sleep(5000);
            }
        }
    }

    private void SetStatus(string s)
    {
        if (ListenStatus == s) return;
        ListenStatus = s;
        SessionsChanged?.Invoke();
    }

    private void Incoming(BluetoothClient c)
    {
        var addr = AddressOf(c);
        bool done = false;
        using var guard = new Timer(_ => { if (!done) try { c.Close(); } catch { } }, null, 180_000, Timeout.Infinite);
        try
        {
            var s = Serve(c.GetStream(), c, addr);
            done = true;
            Notice?.Invoke($"{s.PeerName} התחבר");
        }
        catch (Exception ex)
        {
            done = true;
            Log.Write($"incoming from {addr} failed: {ex.Message}");
            try { c.Close(); } catch { }
        }
    }

    private static string AddressOf(BluetoothClient client)
    {
        try
        {
            // 32feet exposes the underlying socket as "Client" on Windows.
            var socket = client.GetType().GetProperty("Client")?.GetValue(client) as Socket;
            if (socket?.RemoteEndPoint is BluetoothEndPoint ep) return KeyOf(ep.Address);
        }
        catch { }
        try
        {
            var name = client.RemoteMachineName;
            using var bc = new BluetoothClient();
            var d = bc.PairedDevices.FirstOrDefault(x => x.DeviceName == name);
            if (d != null) return KeyOf(d.DeviceAddress);
        }
        catch { }
        return "";
    }

    // ---------------------------------------------------------------- outgoing

    /// <summary>Connects over Bluetooth. Not interactive: never asks for a password (automatic reconnects).</summary>
    public Task<Session> ConnectAsync(BluetoothAddress address, bool interactive = true)
    {
        var addr = KeyOf(address);
        if (SessionFor(addr) is { } existing) return Task.FromResult(existing);
        lock (_connecting)
            if (!_connecting.Add(addr)) return Task.FromException<Session>(new InvalidOperationException("כבר מתחבר"));
        SessionsChanged?.Invoke();
        return Task.Run(() =>
        {
            var c = new BluetoothClient();
            try
            {
                c.Connect(address, ServiceUuid);
                var stream = c.GetStream();
                var hr = Handshake.Client(stream, Me, Settings, addr,
                    interactive ? AskPassword ?? throw new InvalidOperationException("no password UI") : (_, _) => null);
                return AddSession(hr, addr, true, new Link(false, stream, c, hr.Codec));
            }
            catch
            {
                try { c.Close(); } catch { }
                throw;
            }
            finally
            {
                lock (_connecting) _connecting.Remove(addr);
                SessionsChanged?.Invoke();
            }
        });
    }

    // ---------------------------------------------------------------- fast link

    /// <summary>
    /// Before a big transfer or screen sharing: when there is no Wi-Fi link to a phone, asks it to open
    /// a direct network (PROTOCOL.md section 8) and waits a little for the link. Never throws.
    /// </summary>
    public async Task EnsureFastLinkAsync(Session s)
    {
        if (s.HasWifi) return;
        // On a shared network the Wi-Fi link may just have dropped: try it again first, it is faster than a hotspot.
        Wifi.Refresh(s);
        for (int i = 0; i < 12 && !s.HasWifi && !s.Closed; i++) await Task.Delay(250);
        if (s.HasWifi || Settings.DirectMode == "off" || s.PeerOs != "android") return;
        if (!DirectNetwork.HasWifiAdapter()) return;
        try
        {
            await s.RequestAsync(new JsonObject { ["op"] = "direct" }, timeoutSec: 45);
            for (int i = 0; i < 50 && !s.HasWifi && !s.Closed; i++) await Task.Delay(500);
        }
        catch (Exception ex)
        {
            Log.Write("direct request: " + ex.Message);
            Notice?.Invoke("רשת ישירה לא נפתחה: " + Heb.Of(ex) + "\nממשיך בבלוטוס.");
        }
    }

    private async Task JoinDirect(Session s, string ssid, string pass)
    {
        if (Settings.DirectMode == "off") return;
        if (!await Direct.JoinAsync(ssid, pass)) return;
        s.OnClosed += _ => { if (!Sessions.Any(x => x.PeerOs == "android")) Direct.Leave(); };
        Wifi.Kick(s);
    }

    /// <summary>Runs the server handshake on a connected stream (Bluetooth, or TCP in tests).</summary>
    public Session Serve(Stream stream, IDisposable owner, string addr)
    {
        var hr = Handshake.Server(stream, Me, Settings, addr, _lockout);
        return AddSession(hr, addr, false, new Link(false, stream, owner, hr.Codec));
    }

    /// <summary>Runs the client handshake on a connected stream (Bluetooth, or TCP in tests).</summary>
    public Session Open(Stream stream, IDisposable owner, string addr)
    {
        var hr = Handshake.Client(stream, Me, Settings, addr,
            AskPassword ?? throw new InvalidOperationException("no password UI"));
        return AddSession(hr, addr, true, new Link(false, stream, owner, hr.Codec));
    }

    private Session AddSession(HandshakeResult hr, string addr, bool initiator, Link link)
    {
        var s = new Session(hr, addr, initiator, this);
        List<Session> older;
        lock (_sessions)
        {
            older = _sessions.Where(x => x.PeerFp == hr.PeerFp).ToList();
            _sessions.RemoveAll(x => x.PeerFp == hr.PeerFp);
            _sessions.Add(s);
        }
        foreach (var o in older) o.Close();
        s.OnClosed += _ =>
        {
            lock (_sessions) _sessions.Remove(s);
            SessionsChanged?.Invoke();
        };
        s.Changed += _ => SessionsChanged?.Invoke();
        // A dropped Wi-Fi link comes back by itself within seconds, not only on the 30-second announcement.
        s.WifiLost += _ => Task.Run(async () =>
        {
            foreach (var wait in new[] { 2000, 8000 })
            {
                await Task.Delay(wait);
                if (s.Closed || s.HasWifi) return;
                Wifi.Refresh(s);
            }
        });
        s.Attach(link);
        Wifi.Announce(s);
        SessionsChanged?.Invoke();
        return s;
    }

    // ---------------------------------------------------------------- IRequestHandler

    public Task<JsonObject?> HandleAsync(Session s, uint id, JsonObject req)
    {
        switch (req["op"]?.GetValue<string>())
        {
            case "screen-open":
            {
                if (!Settings.AllowControl)
                    throw new RemoteException("denied", "השליטה במחשב הזה כבויה בהגדרות של גשר בלוטוס במחשב");
                var grab = Grabber();
                var src = new ScreenSource(s, id, grab, Input, Codec, req);
                var mons = new JsonArray();
                foreach (var m in grab.Monitors())
                    mons.Add(new JsonObject { ["i"] = m.Index, ["name"] = m.Name, ["w"] = m.W, ["h"] = m.H, ["primary"] = m.Primary });
                s.Reply(id, new JsonObject { ["ok"] = true, ["kind"] = "windows", ["monitors"] = mons });
                src.Start();
                Notice?.Invoke($"{DisplayName(s)} צופה במסך ושולט בו");
                return Task.FromResult<JsonObject?>(null);
            }
            case "direct":
                throw new RemoteException("unsupported", "רשת ישירה נפתחת רק בטלפון");
            case "ring":
                throw new RemoteException("unsupported", "צלצול קיים רק בטלפון");
            case "log":
                return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true, ["text"] = Log.Tail(64 * 1024) });
            default:
                return Files.HandleAsync(s, id, req);
        }
    }

    /// <summary>Text the other device copied (PROTOCOL.md section 5, "clip"). Raised on a worker thread.</summary>
    public event Action<Session, string>? ClipboardReceived;
    /// <summary>A notification that appeared on the phone: app, title, text.</summary>
    public event Action<Session, string, string, string>? PhoneNotification;

    /// <summary>Sends copied text to every connected device that shares the clipboard.</summary>
    public void ShareClipboard(string text)
    {
        if (!Settings.ShareClipboard || text.Length == 0 || text.Length > MaxClip) return;
        foreach (var s in Sessions.Where(x => x.Has("clip")))
            try { s.SendEvent(new JsonObject { ["ev"] = "clip", ["text"] = text }); } catch { }
    }

    public const int MaxClip = 200_000;

    public void OnEvent(Session s, JsonObject ev)
    {
        if (ScreenSource.OnEvent(s, ev)) return;
        switch (ev["ev"]?.GetValue<string>())
        {
            case "direct":
                var ssid = ev["ssid"]?.GetValue<string>();
                var pass = ev["pass"]?.GetValue<string>();
                if (ssid != null && pass != null) _ = JoinDirect(s, ssid, pass);
                break;
            case "direct-end":
                Direct.Leave();
                break;
            case "clip":
                if (Settings.ShareClipboard && ev["text"]?.GetValue<string>() is { Length: > 0 and <= MaxClip } text)
                    ClipboardReceived?.Invoke(s, text);
                break;
            case "notif":
                if (Settings.ShowPhoneNotifications)
                    PhoneNotification?.Invoke(s, ev["app"]?.GetValue<string>() ?? "", ev["title"]?.GetValue<string>() ?? "",
                        ev["text"]?.GetValue<string>() ?? "");
                break;
            case "battery":
                s.Battery = ev["level"]?.GetValue<int>() ?? -1;
                s.Charging = ev["charging"]?.GetValue<bool>() == true;
                SessionsChanged?.Invoke();
                break;
            default:
                Wifi.OnEvent(s, ev);
                break;
        }
    }
}

/// <summary>Extra encrypted TCP links over a shared network (PROTOCOL.md section 6).</summary>
public sealed class WifiLinks(Bridge bridge)
{
    private TcpListener? _listener;
    private int _port;
    private Timer? _timer;
    private readonly HashSet<Session> _trying = new();

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoop(_listener);
        }
        catch (Exception ex) { Log.Write("tcp listen: " + ex.Message); }
        NetworkChange.NetworkAddressChanged += (_, _) => AnnounceAll();
        _timer = new Timer(_ => AnnounceAll(), null, 30_000, 30_000);
    }

    private void AnnounceAll()
    {
        foreach (var s in bridge.Sessions)
            if (!s.HasWifi) Announce(s);
    }

    public void Announce(Session s)
    {
        var ips = new JsonArray();
        foreach (var ip in LocalIps()) ips.Add(ip);
        try { s.SendEvent(new JsonObject { ["ev"] = "net", ["ips"] = ips, ["port"] = _port }); }
        catch { }
    }

    /// <summary>After joining a new network: announce our address and try the other side's right away.</summary>
    public void Kick(Session s)
    {
        Announce(s);
        if (!s.HasWifi) _ = TryConnect(s, notifyFail: true);
    }

    /// <summary>
    /// Brings a dropped Wi-Fi link back. Only the side that opened Bluetooth connects, so the two sides never race
    /// each other into two links; the other side just announces itself, which makes the opener try.
    /// </summary>
    public void Refresh(Session s)
    {
        Announce(s);
        if (s.Initiator && !s.HasWifi) _ = TryConnect(s, notifyFail: true);
    }

    public void OnEvent(Session s, JsonObject ev)
    {
        switch (ev["ev"]?.GetValue<string>())
        {
            case "net":
                s.PeerIps = ev["ips"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? Array.Empty<string>();
                s.PeerPort = ev["port"]?.GetValue<int>() ?? 0;
                if (s.Initiator && !s.HasWifi) _ = TryConnect(s, notifyFail: true);
                break;
            case "net-fail":
                if (!s.Initiator && !s.HasWifi) _ = TryConnect(s, notifyFail: false);
                break;
        }
    }

    public static List<string> LocalIps()
    {
        var list = new List<(int, string)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            int rank = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0
                : ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                  || ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    list.Add((rank, ua.Address.ToString()));
        }
        return list.OrderBy(x => x.Item1).Select(x => x.Item2).Distinct().ToList();
    }

    private async Task TryConnect(Session s, bool notifyFail)
    {
        if (s.PeerIps.Length == 0 || s.PeerPort == 0) return;
        lock (_trying) if (!_trying.Add(s)) return;
        try
        {
            using var cts = new CancellationTokenSource();
            var attempts = s.PeerIps.Select(ip => ConnectOne(ip, s.PeerPort, cts.Token)).ToList();
            TcpClient? won = null;
            while (attempts.Count > 0 && won == null)
            {
                var t = await Task.WhenAny(attempts);
                attempts.Remove(t);
                won = await t;
            }
            cts.Cancel();
            foreach (var t in attempts) _ = t.ContinueWith(x => x.Result?.Dispose(), TaskScheduler.Default);

            if (won != null && await LinkHandshake(s, won)) return;
            won?.Dispose();
            if (notifyFail) try { s.SendEvent(new JsonObject { ["ev"] = "net-fail" }); } catch { }
        }
        catch (Exception ex) { Log.Write("wifi connect: " + ex.Message); }
        finally { lock (_trying) _trying.Remove(s); }
    }

    private static async Task<TcpClient?> ConnectOne(string ip, int port, CancellationToken ct)
    {
        var c = new TcpClient();
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(2500);
            await c.ConnectAsync(IPAddress.Parse(ip), port, t.Token);
            return c;
        }
        catch
        {
            c.Dispose();
            return null;
        }
    }

    /// <summary>
    /// PROTOCOL.md section 6. Both sides add fresh randomness to the link keys, and the accepting side refuses a link id
    /// it has seen, so a recorded hello can neither take over the link nor make us reuse an AES-GCM nonce.
    /// </summary>
    public static (byte[] Up, byte[] Down) LinkKeys(byte[] linkKey, byte[] linkId, byte[] srvNonce)
    {
        var tag = Convert.ToHexString(linkId).ToLowerInvariant() + Convert.ToHexString(srvNonce).ToLowerInvariant();
        return (Expand(linkKey, "up " + tag), Expand(linkKey, "down " + tag));
    }

    private static async Task<bool> LinkHandshake(Session s, TcpClient c)
    {
        try
        {
            c.NoDelay = true;
            var st = c.GetStream();
            var linkId = Random(16);
            st.ReadTimeout = 5000;
            var srvNonce = await Task.Run(() =>
            {
                Wire.Write(st, Concat(linkId, Hmac(s.LinkKey, Utf8("btb-wifi"), linkId)));
                var r = Wire.Read(st);
                if (r.Length != 48) throw new ProtocolException("wifi ack");
                var nonce = r[..16];
                if (!Same(r[16..], Hmac(s.LinkKey, Utf8("btb-wifi-ok"), linkId, nonce)))
                    throw new ProtocolException("wifi ack");
                return nonce;
            });
            st.ReadTimeout = Timeout.Infinite;
            var (up, down) = LinkKeys(s.LinkKey, linkId, srvNonce);
            s.Attach(new Link(true, st, c, new RecordCodec(up, down)));
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("wifi handshake: " + ex.Message);
            return false;
        }
    }

    private async Task AcceptLoop(TcpListener l)
    {
        while (true)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(); }
            catch (Exception ex) { Log.Write("tcp accept: " + ex.Message); return; }
            _ = Task.Run(() => Accept(c));
        }
    }

    private void Accept(TcpClient c)
    {
        try
        {
            c.NoDelay = true;
            var st = c.GetStream();
            st.ReadTimeout = 5000;
            var f = Wire.Read(st);
            if (f.Length != 48) throw new ProtocolException("wifi hello");
            var linkId = f[..16];
            var mac = f[16..];
            var s = bridge.Sessions.FirstOrDefault(x => Same(mac, Hmac(x.LinkKey, Utf8("btb-wifi"), linkId)))
                ?? throw new ProtocolException("unknown wifi link");
            lock (s.UsedLinkIds)
                if (!s.UsedLinkIds.Add(Convert.ToHexString(linkId))) throw new ProtocolException("wifi link id replayed");
            var srvNonce = Random(16);
            Wire.Write(st, Concat(srvNonce, Hmac(s.LinkKey, Utf8("btb-wifi-ok"), linkId, srvNonce)));
            st.ReadTimeout = Timeout.Infinite;
            var (up, down) = LinkKeys(s.LinkKey, linkId, srvNonce);
            s.Attach(new Link(true, st, c, new RecordCodec(down, up)));
        }
        catch (Exception ex)
        {
            Log.Write("wifi accept: " + ex.Message);
            c.Dispose();
        }
    }
}

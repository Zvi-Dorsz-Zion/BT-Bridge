using System.Net;
using System.Net.Sockets;
using BtBridge.Core;

// Usage:
//   InteropTest server <stateDir>                 prints "PORT <n>", serves until stdin closes
//   InteropTest client <port> <stateDir> <remoteDir>   runs file operations against a Kotlin server
// The shared password contains Hebrew on purpose (UTF-8 + NFC on both sides).
const string Password = "סיסמה-123";

Console.OutputEncoding = System.Text.Encoding.UTF8;
var mode = args[0];
if (mode == "icon")
{
    // Build tooling: writes windows\BtBridge\app.ico from the same drawing the app uses.
    BtBridge.AppIcon.WriteIco(args[1]);
    return 0;
}
if (mode == "setpw")
{
    // Smoke test helper: give a settings folder a password so the app skips its first-run dialog.
    Log.Dir = args[1];
    var st = AppSettings.Load();
    st.SetPassword(args[2]);
    return 0;
}
Log.Dir = args[mode == "server" ? 1 : 2];
Directory.CreateDirectory(Log.Dir);
var bridge = new Bridge();
bridge.Settings.DeviceName = "מחשב-בדיקה";
bridge.Settings.DownloadDir = Path.Combine(Log.Dir, "inbox");
if (!bridge.Settings.HasPassword) bridge.Settings.SetPassword(Password);
// Never touch the real screen or mouse in tests.
bridge.Grabber = () => new FakeGrabber();
bridge.Input = new PrintInput();
bridge.Received += (_, p) => Console.WriteLine("RECEIVED " + p);
bridge.Wifi.Start();

if (mode == "server")
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    Console.WriteLine("PORT " + ((IPEndPoint)l.LocalEndpoint).Port);
    _ = Task.Run(async () =>
    {
        while (true)
        {
            var c = await l.AcceptTcpClientAsync();
            _ = Task.Run(() =>
            {
                try { var s = bridge.Serve(c.GetStream(), c, "AABBCCDDEE01"); Console.WriteLine("SESSION " + s.PeerName); }
                catch (Exception ex) { Console.WriteLine("SERVE-FAIL " + ex.Message); c.Dispose(); }
            });
        }
    });
    Console.In.ReadToEnd();
    return 0;
}

// ---------------------------------------------------------------- client
try
{
    int port = int.Parse(args[1]);
    var remoteDir = args[3];
    int asked = 0;
    bridge.AskPassword = (peer, wrong) =>
    {
        asked++;
        Console.WriteLine($"ASK peer={peer} wrong={wrong}");
        return new PasswordAnswer(asked == 1 ? "wrong-password" : Password, true);
    };

    var s = Connect();
    if (asked != 2) throw new Exception("expected a wrong password first, then the right one; asked=" + asked);
    Check(s.PeerName == "טלפון-בדיקה", "peer name " + s.PeerName);
    for (int i = 0; i < 50 && !s.HasWifi; i++) Thread.Sleep(100);
    Console.WriteLine("WIFI " + s.HasWifi);

    var listing = await RemoteFs.ListAsync(s, remoteDir);
    Check(listing.Items.Any(x => x.Name == "from-kotlin.bin" && x.Size == 3_000_000), "list");

    var local = Path.Combine(Log.Dir, "got.bin");
    var t = new Transfer { Name = "got.bin" };
    await RemoteFs.DownloadAsync(s, RemoteFs.Join(s, remoteDir, "from-kotlin.bin"), local, t, default);
    Check(Pattern.Same(File.ReadAllBytes(local), 3_000_000), "download content");

    var up = Path.Combine(Log.Dir, "up.bin");
    File.WriteAllBytes(up, Pattern.Make(2_500_000));
    var t2 = new Transfer { Name = "up.bin", Upload = true };
    var landed = await RemoteFs.UploadAsync(s, up, RemoteFs.Join(s, remoteDir, "from-windows.bin"), t2, default);
    Console.WriteLine("UPLOADED " + landed);

    await RemoteFs.MkdirAsync(s, RemoteFs.Join(s, remoteDir, "תיקייה"));
    await RemoteFs.RenameAsync(s, RemoteFs.Join(s, remoteDir, "תיקייה"), RemoteFs.Join(s, remoteDir, "שם-חדש"));
    try { await RemoteFs.MkdirAsync(s, RemoteFs.Join(s, remoteDir, "שם-חדש")); throw new Exception("mkdir twice should fail"); }
    catch (RemoteException ex) when (ex.Code == "exists") { }
    await RemoteFs.MkdirAsync(s, RemoteFs.Join(s, remoteDir, "למחיקה"));
    await RemoteFs.DeleteAsync(s, new[] { RemoteFs.Join(s, remoteDir, "למחיקה") });
    try { await RemoteFs.ListAsync(s, RemoteFs.Join(s, remoteDir, "לא-קיים")); throw new Exception("list missing should fail"); }
    catch (RemoteException ex) when (ex.Code == "notfound") { }

    // "Send to device": lands in the phone's downloads folder.
    var inboxSrc = Path.Combine(Log.Dir, "inbox-src.bin");
    File.WriteAllBytes(inboxSrc, Pattern.Make(300_000));
    var inboxPath = await RemoteFs.UploadAsync(s, inboxSrc, "תת/מחשב.bin", new Transfer { Name = "inbox", Upload = true }, default, inbox: true);
    Console.WriteLine("INBOX " + inboxPath);
    try { await RemoteFs.UploadAsync(s, inboxSrc, "../escape.bin", new Transfer(), default, inbox: true); throw new Exception("inbox escape allowed"); }
    catch (RemoteException ex) when (ex.Code == "bad") { }

    // Screen sharing from the (fake) phone screen: frames arrive, tiles decode, input reaches the phone.
    var sc = new ScreenClient(s);
    var codec = new GdiTileCodec();
    int frames = 0, tilesSeen = 0;
    var gotFrames = new TaskCompletionSource();
    sc.Frame += (seq, w, h, tiles) =>
    {
        foreach (var t in tiles)
        {
            codec.Decode(t.Jpeg, out var tw, out var th);
            Check(tw == t.W && th == t.H, $"tile size {tw}x{th} vs {t.W}x{t.H}");
            tilesSeen++;
        }
        if (++frames >= 4) gotFrames.TrySetResult();
    };
    await sc.OpenAsync(0, "sharp", false);
    Check(sc.Kind == "android", "screen kind " + sc.Kind);
    await gotFrames.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Console.WriteLine($"SCREEN frames={frames} tiles={tilesSeen} size={sc.FrameW}x{sc.FrameH}");
    sc.Input("tap", sc.FrameW / 2.0, sc.FrameH / 4.0);
    sc.Input("swipe", 10, 20, o => { o["x2"] = 30; o["y2"] = 40; o["ms"] = 300; });
    await Task.Delay(500);
    sc.Close();

    // Second connection: both sides remember each other, so no password this time.
    s.Close();
    Thread.Sleep(300);
    asked = 0;
    bridge.AskPassword = (_, _) => { asked++; return null; };
    var s2 = Connect();
    Check(asked == 0, "password asked again on a trusted connection");
    var again = await RemoteFs.ListAsync(s2, remoteDir);
    Check(again.Items.Any(x => x.Name == "from-windows.bin"), "list after reconnect");
    s2.Close();
    Console.WriteLine("OK");
    return 0;

    Session Connect()
    {
        var c = new TcpClient();
        c.Connect(IPAddress.Loopback, port);
        return bridge.Open(c.GetStream(), c, "AABBCCDDEE02");
    }
}
catch (Exception ex)
{
    Console.WriteLine("FAIL " + ex);
    return 1;
}

static void Check(bool ok, string what)
{
    if (!ok) throw new Exception("check failed: " + what);
}

/// <summary>A 1280×720 "screen" with a moving square, so only a few tiles change per frame.</summary>
sealed class FakeGrabber : IScreenGrabber
{
    private int _n;
    public IReadOnlyList<MonitorInfo> Monitors() => new[] { new MonitorInfo(0, "בדיקה", 0, 0, 1280, 720, true) };

    public int[]? Grab(int monitor, int w, int h)
    {
        var px = new int[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = 0x202840 + (i % w) * 0x10101 / Math.Max(1, w / 64);
        int sx = (_n++ * 40) % Math.Max(1, w - 60);
        for (int y = 100; y < Math.Min(h, 160); y++)
            for (int x = sx; x < sx + 60; x++) px[y * w + x] = 0xFF2020;
        return px;
    }
}

sealed class PrintInput : IInputSink
{
    public void Input(System.Text.Json.Nodes.JsonObject ev, MonitorInfo mon, int x, int y, bool restoreCursor) =>
        Console.WriteLine($"INPUT {ev["t"]} {x} {y} {ev["s"]} {ev["k"]}");
}

static class Pattern
{
    public static byte[] Make(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((i * 31 + 7) & 0xff);
        return b;
    }

    public static bool Same(byte[] b, int n) => b.Length == n && b.AsSpan().SequenceEqual(Make(n));
}

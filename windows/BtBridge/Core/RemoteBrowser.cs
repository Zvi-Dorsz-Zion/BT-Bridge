using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace BtBridge.Core;

/// <summary>
/// A browser on this computer that only the other device sees and controls (PROTOCOL.md section 7, "browser").
/// Edge (or Chrome) runs without a window, with its own profile, and is driven through its built-in remote control
/// interface: it sends the page as pictures and takes taps and typing directly. The user's screen, mouse and own
/// browser are not touched. Browsing uses this computer's internet connection.
/// </summary>
public sealed class RemoteBrowser : IScreenGrabber, IInputSink, IDisposable
{
    /// <summary>Settable for the tests, which must never run on the user's profile (the logins live there).</summary>
    public static string ProfileDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BtBridge", "דפדפן");

    private readonly Process _proc;
    private readonly CancellationTokenSource _cts = new();
    private Conn _browser = null!;
    private volatile Conn _active = null!;
    private readonly List<Conn> _tabs = new();
    private readonly HashSet<string> _attaching = new();
    private string _lastTabs = "";
    private readonly int _port;
    private readonly string _home;
    private readonly bool _withTabs;
    private string _userAgent = "";
    private readonly int _vw, _vh;
    private byte[]? _frame;
    private readonly object _frameGate = new();
    private readonly string _profile;
    private readonly bool _touch;

    /// <summary>
    /// The page is drawn at twice its size in page pixels (like a phone's sharp screen), and the picture is sent at
    /// that size: before, it was shrunk to the page size and the phone blew it up again, which made it blurry.
    /// </summary>
    private const int Scale = 2;

    /// <summary>How much the page is shrunk or enlarged in the current picture (1 on a page made for phones).</summary>
    private volatile float _pageScaleF = 1f;
    private double _pageScale { get => _pageScaleF; set => _pageScaleF = (float)value; }

    /// <summary>The page address and title changed (sent to the viewer, to show in its address bar).</summary>
    public event Action<string, string>? PageChanged;

    /// <summary>The list of tabs changed: [{id, title, url, active}] (sent to the viewer).</summary>
    public event Action<JsonArray>? TabsChanged;

    public string Url { get; private set; } = "";

    private RemoteBrowser(Process proc, int port, string home, bool withTabs, int vw, int vh, string profile, bool touch)
    {
        _proc = proc;
        _port = port;
        _home = home;
        _withTabs = withTabs;
        _vw = vw;
        _vh = vh;
        _profile = profile;
        _touch = touch;
    }

    /// <summary>Profiles in use now: a second browser at the same time (a second phone) gets its own.</summary>
    private static readonly HashSet<string> Busy = new(StringComparer.OrdinalIgnoreCase);

    private static string TakeProfile()
    {
        lock (Busy)
        {
            for (int i = 1; ; i++)
            {
                var dir = i == 1 ? ProfileDir : ProfileDir + " " + i;
                if (Busy.Add(dir)) return dir;
            }
        }
    }

    private static void FreeProfile(string dir)
    {
        lock (Busy) Busy.Remove(dir);
    }

    /// <summary>Finds Edge or Chrome. <paramref name="prefer"/> is "edge" or "chrome".</summary>
    public static string? FindBrowser(string prefer)
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var edge = new[] { Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"), Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe") };
        var chrome = new[] { Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"), Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe") };
        var order = prefer == "chrome" ? chrome.Concat(edge) : edge.Concat(chrome);
        return order.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Starts the browser with a page of <paramref name="vw"/>×<paramref name="vh"/> (in page pixels, like a phone).
    /// <paramref name="touch"/>: the viewer sends touches (a finger drag scrolls the page, as on a phone) instead of mouse events.
    /// <paramref name="withTabs"/>: the viewer can show several tabs; otherwise a link to a new tab opens in the same one.
    /// </summary>
    public static async Task<RemoteBrowser> StartAsync(string prefer, string downloadDir, string startUrl, int vw, int vh, bool touch = false, bool withTabs = false)
    {
        var exe = FindBrowser(prefer) ?? throw new RemoteException("notfound", "לא נמצא במחשב דפדפן אדג' או כרום");
        // The browser that was just closed takes a few seconds to let go of the usual profile (with the user's logins):
        // wait for it a little before taking a second profile.
        for (int i = 0; i < 60; i++)
        {
            lock (Busy) if (!Busy.Contains(ProfileDir)) break;
            await Task.Delay(200);
        }
        var profileDir = TakeProfile();
        try { return await StartIn(profileDir, exe, downloadDir, startUrl, vw, vh, touch, withTabs); }
        catch { FreeProfile(profileDir); throw; }
    }

    private static async Task<RemoteBrowser> StartIn(string profileDir, string exe, string downloadDir, string startUrl, int vw, int vh, bool touch, bool withTabs)
    {
        Directory.CreateDirectory(profileDir);
        var portFile = Path.Combine(profileDir, "DevToolsActivePort");
        try { File.Delete(portFile); } catch { }
        // Its own profile: the remote control interface doesn't open on the everyday profile, and this way
        // the user's own browser stays untouched. Logins made here are kept for next time.
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            ArgumentList =
            {
                "--headless=new", "--remote-debugging-port=0", "--remote-allow-origins=*", $"--user-data-dir={profileDir}",
                "--no-first-run", "--no-default-browser-check", $"--window-size={vw},{vh}",
                // Sites (Google search among them) block browsers that announce they are driven by a program.
                "--disable-blink-features=AutomationControlled",
                startUrl,
            },
        };
        var proc = Process.Start(psi) ?? throw new IOException("הדפדפן לא נפתח");
        // Tied to this program: if it ends (even by crashing), Windows closes the hidden browser too, so it can't
        // stay behind holding its profile (which kept the next start from opening).
        KillWithUs(proc);
        try
        {
            int port = 0;
            for (int i = 0; i < 100 && port == 0; i++)
            {
                await Task.Delay(100);
                try { if (File.Exists(portFile)) int.TryParse(File.ReadLines(portFile).FirstOrDefault(), out port); } catch { }
            }
            if (port == 0) throw new IOException("הדפדפן לא פתח את ממשק השליטה");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            JsonObject? page = null;
            for (int i = 0; i < 30 && page == null; i++)
            {
                string json;
                try { json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"); }
                catch (HttpRequestException) when (i < 29)
                {
                    // The browser restarted itself right after opening (it does after updating): it writes a new port.
                    await Task.Delay(300);
                    try { if (File.Exists(portFile) && int.TryParse(File.ReadLines(portFile).FirstOrDefault(), out var p2)) port = p2; } catch { }
                    continue;
                }
                var list = JsonNode.Parse(json) as JsonArray;
                page = list?.OfType<JsonObject>().FirstOrDefault(o => o["type"]?.GetValue<string>() == "page");
                if (page == null) await Task.Delay(200);
            }
            if (page == null) throw new IOException("לא נמצא דף בדפדפן");
            var b = new RemoteBrowser(proc, port, startUrl, withTabs, vw, vh, profileDir, touch);
            var ver = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version")) as JsonObject
                ?? throw new IOException("הדפדפן לא ענה");
            b._browser = new Conn("", b._cts.Token, b.OnBrowserEvent);
            await b._browser.Connect(ver["webSocketDebuggerUrl"]!.GetValue<string>());
            b._userAgent = ver["User-Agent"]?.GetValue<string>()?.Replace("HeadlessChrome", "Chrome") ?? "";
            await b._browser.Call("Target.setDiscoverTargets", new JsonObject { ["discover"] = true });
            Directory.CreateDirectory(downloadDir);
            await b._browser.Call("Browser.setDownloadBehavior", new JsonObject { ["behavior"] = "allow", ["downloadPath"] = downloadDir });
            b.Select(await b.AttachTab(page["id"]!.GetValue<string>()));
            return b;
        }
        catch
        {
            try { proc.Kill(true); } catch { }
            throw;
        }
    }

    // ---------------------------------------------------------------- the remote control interface

    /// <summary>One connection to the browser's control interface: to the browser itself, or to one tab.</summary>
    private sealed class Conn(string id, CancellationToken stop, Action<Conn, string, JsonObject> onEvent)
    {
        public string Id { get; } = id;
        public string Url = "", Title = "";
        private readonly ClientWebSocket _ws = new();
        private readonly SemaphoreSlim _gate = new(1);
        private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _calls = new();
        private int _next;

        public async Task Connect(string url)
        {
            await _ws.ConnectAsync(new Uri(url), CancellationToken.None).ConfigureAwait(false);
            _ = ReadLoop();
        }

        public async Task<JsonObject> Call(string method, JsonObject? args = null)
        {
            var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id;
            lock (_calls) { id = ++_next; _calls[id] = tcs; }
            await Send(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = args ?? new JsonObject() }).ConfigureAwait(false);
            var res = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (res["error"] is JsonObject err) throw new IOException("הדפדפן: " + err["message"]?.GetValue<string>());
            return res["result"] as JsonObject ?? new JsonObject();
        }

        public void Fire(string method, JsonObject? args = null) => _ = Send(new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _next), ["method"] = method, ["params"] = args ?? new JsonObject(),
        });

        public async Task Send(JsonObject msg)
        {
            var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
            await _gate.WaitAsync().ConfigureAwait(false);
            try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, stop).ConfigureAwait(false); }
            catch (Exception ex) { if (!stop.IsCancellationRequested) Log.Write("browser send: " + ex.Message); }
            finally { _gate.Release(); }
        }

        public int NextId() => Interlocked.Increment(ref _next);

        public async Task<string> Eval(string expr)
        {
            var r = await Call("Runtime.evaluate", new JsonObject { ["expression"] = expr, ["returnByValue"] = true }).ConfigureAwait(false);
            return r["result"]?["value"]?.ToString() ?? "";
        }

        private async Task ReadLoop()
        {
            var buf = new byte[1 << 20];
            var msg = new MemoryStream();
            try
            {
                while (_ws.State == WebSocketState.Open && !stop.IsCancellationRequested)
                {
                    var r = await _ws.ReceiveAsync(buf, stop).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    msg.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    var o = JsonNode.Parse(msg.GetBuffer().AsSpan(0, (int)msg.Length)) as JsonObject;
                    msg.SetLength(0);
                    if (o == null) continue;
                    if (o["id"] is JsonNode idNode)
                    {
                        TaskCompletionSource<JsonObject>? tcs;
                        lock (_calls) _calls.Remove(idNode.GetValue<int>(), out tcs);
                        tcs?.TrySetResult(o);
                    }
                    else if (o["method"]?.GetValue<string>() is { } method)
                    {
                        // One bad event must not end the connection.
                        try { onEvent(this, method, o["params"] as JsonObject ?? new JsonObject()); }
                        catch (Exception ex) { Log.Write("browser event: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { if (!stop.IsCancellationRequested) Log.Write("browser read: " + ex.Message); }
        }

        public void Close()
        {
            try { _ws.Dispose(); } catch { }
        }
    }

    private Task<JsonObject> Call(string method, JsonObject? args = null) => _active.Call(method, args);
    private void Fire(string method, JsonObject? args = null) => _active.Fire(method, args);

    // ---------------------------------------------------------------- tabs

    /// <summary>More than this only uses up the computer's memory: every open tab keeps running.</summary>
    private const int MaxTabs = 12;

    /// <summary>Connects to a tab and gives it the phone's size and identity.</summary>
    private async Task<Conn> AttachTab(string id)
    {
        var c = new Conn(id, _cts.Token, OnTabEvent);
        await c.Connect($"ws://127.0.0.1:{_port}/devtools/page/{id}").ConfigureAwait(false);
        await c.Call("Page.enable").ConfigureAwait(false);
        await c.Call("Emulation.setDeviceMetricsOverride", new JsonObject
        {
            ["width"] = _vw, ["height"] = _vh, ["deviceScaleFactor"] = Scale, ["mobile"] = true,
        }).ConfigureAwait(false);
        // Pages see a touch screen, as on a phone (some sites only answer touches).
        if (_touch) await c.Call("Emulation.setTouchEmulationEnabled", new JsonObject { ["enabled"] = true, ["maxTouchPoints"] = 1 }).ConfigureAwait(false);
        // Without the word "Headless" sites treat it as an ordinary browser.
        if (_userAgent.Length > 0) await c.Call("Emulation.setUserAgentOverride", new JsonObject { ["userAgent"] = _userAgent }).ConfigureAwait(false);
        lock (_tabs) _tabs.Add(c);
        return c;
    }

    /// <summary>Shows this tab. Only the shown tab sends pictures; the others keep running without using the link.</summary>
    private void Select(Conn c)
    {
        var old = _active;
        if (old != null && old != c) old.Fire("Page.stopScreencast");
        _active = c;
        c.Fire("Page.bringToFront");
        c.Fire("Page.startScreencast", new JsonObject
        {
            ["format"] = "jpeg", ["quality"] = 85, ["maxWidth"] = _vw * Scale, ["maxHeight"] = _vh * Scale,
        });
        Url = c.Url;
        PageChanged?.Invoke(c.Url, c.Title);
        PushTabs();
    }

    /// <summary>A new tab, shown at once, with an address or the browser's first page.</summary>
    private async Task NewTab(string? url)
    {
        lock (_tabs) if (_tabs.Count >= MaxTabs) return;
        var r = await _browser.Call("Target.createTarget", new JsonObject { ["url"] = "about:blank" }).ConfigureAwait(false);
        var id = r["targetId"]!.GetValue<string>();
        var c = await AttachTab(id).ConfigureAwait(false);
        Select(c);
        c.Fire("Page.navigate", new JsonObject { ["url"] = string.IsNullOrWhiteSpace(url) ? _home : ToAddress(url) });
    }

    private async Task CloseTab(string id)
    {
        bool last;
        lock (_tabs) last = _tabs.Count == 1 && _tabs[0].Id == id;
        // The browser would end with its last tab: a fresh one comes first.
        if (last) await NewTab(null).ConfigureAwait(false);
        _browser.Fire("Target.closeTarget", new JsonObject { ["targetId"] = id });
    }

    private void SelectTab(string id)
    {
        Conn? c;
        lock (_tabs) c = _tabs.FirstOrDefault(x => x.Id == id);
        if (c != null && c != _active) Select(c);
    }

    /// <summary>Tells the viewer the list of tabs (only when something in it changed).</summary>
    public void PushTabs(bool force = false)
    {
        var arr = new JsonArray();
        lock (_tabs)
            foreach (var c in _tabs)
                arr.Add(new JsonObject
                {
                    ["id"] = c.Id,
                    ["title"] = c.Title.Length > 60 ? c.Title[..60] : c.Title,
                    ["url"] = c.Url.Length > 120 ? c.Url[..120] : c.Url,
                    ["active"] = c == _active,
                });
        var text = arr.ToJsonString();
        lock (_tabs)
        {
            if (!force && text == _lastTabs) return;
            _lastTabs = text;
        }
        TabsChanged?.Invoke(arr);
    }

    private void OnBrowserEvent(Conn self, string method, JsonObject p)
    {
        switch (method)
        {
            case "Target.targetCreated" or "Target.targetInfoChanged":
            {
                if (p["targetInfo"] is not JsonObject info || info["type"]?.GetValue<string>() != "page") return;
                var id = info["targetId"]?.GetValue<string>() ?? "";
                var url = info["url"]?.GetValue<string>() ?? "";
                var title = info["title"]?.GetValue<string>() ?? "";
                Conn? known;
                lock (_tabs) known = _tabs.FirstOrDefault(x => x.Id == id);
                if (known != null)
                {
                    known.Url = url;
                    known.Title = title;
                    if (known == _active)
                    {
                        Url = url;
                        PageChanged?.Invoke(url, title);
                    }
                    PushTabs();
                    return;
                }
                // Opened by a page we control: a link to a new tab.
                var opener = info["openerId"]?.GetValue<string>();
                bool ours;
                lock (_tabs) ours = opener != null && _tabs.Any(x => x.Id == opener);
                if (!ours || info["subtype"] != null) return;
                if (!_withTabs)
                {
                    // The viewer shows one page only: it opens here instead.
                    if (url.Length == 0 || url == "about:blank") return;
                    _browser.Fire("Target.closeTarget", new JsonObject { ["targetId"] = id });
                    Fire("Page.navigate", new JsonObject { ["url"] = url });
                    return;
                }
                lock (_tabs)
                {
                    if (!_attaching.Add(id)) return;
                    if (_tabs.Count >= MaxTabs)
                    {
                        // Too many tabs: this link opens in the tab being shown.
                        if (url.Length == 0 || url == "about:blank") { _attaching.Remove(id); return; }
                        _browser.Fire("Target.closeTarget", new JsonObject { ["targetId"] = id });
                        Fire("Page.navigate", new JsonObject { ["url"] = url });
                        return;
                    }
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var c = await AttachTab(id).ConfigureAwait(false);
                        c.Url = url;
                        Select(c);
                        // It started loading before it got the phone's size and identity.
                        if (url.Length > 0 && url != "about:blank") c.Fire("Page.reload");
                    }
                    catch (Exception ex) { Log.Write("browser tab: " + ex.Message); }
                    finally { lock (_tabs) _attaching.Remove(id); }
                });
                break;
            }
            case "Target.targetDestroyed":
            {
                var id = p["targetId"]?.GetValue<string>() ?? "";
                Conn? gone, next = null;
                lock (_tabs)
                {
                    gone = _tabs.FirstOrDefault(x => x.Id == id);
                    if (gone == null) return;
                    int at = _tabs.IndexOf(gone);
                    _tabs.RemoveAt(at);
                    if (gone == _active && _tabs.Count > 0) next = _tabs[Math.Min(at, _tabs.Count - 1)];
                }
                gone.Close();
                if (next != null) Select(next);
                else PushTabs();
                break;
            }
        }
    }

    private void OnTabEvent(Conn c, string method, JsonObject p)
    {
        switch (method)
        {
            case "Page.screencastFrame":
                c.Fire("Page.screencastFrameAck", new JsonObject { ["sessionId"] = p["sessionId"]!.GetValue<int>() });
                if (c != _active) return;
                lock (_frameGate) _frame = Convert.FromBase64String(p["data"]!.GetValue<string>());
                // How much the page is shrunk in this picture (a site without a phone layout: for example 0.3).
                if (p["metadata"]?["pageScaleFactor"] is JsonNode psf && psf.GetValue<double>() is > 0 and var k) _pageScale = k;
                break;
            case "Page.javascriptDialogOpening":
                // A page message ("are you sure?"): accepted, so the page doesn't stay stuck where nobody can answer it.
                c.Fire("Page.handleJavaScriptDialog", new JsonObject { ["accept"] = true });
                break;
            case "Page.loadEventFired" or "Page.navigatedWithinDocument":
                // The address bar on the phone: the page's current address and title.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        c.Url = await c.Eval("location.href").ConfigureAwait(false);
                        c.Title = await c.Eval("document.title").ConfigureAwait(false);
                        if (c == _active)
                        {
                            Url = c.Url;
                            PageChanged?.Invoke(c.Url, c.Title);
                        }
                        PushTabs();
                    }
                    catch { }
                });
                break;
        }
    }

    /// <summary>Runs a short script in the page and returns its result as text.</summary>
    public async Task<string> Eval(string expr)
    {
        var r = await Call("Runtime.evaluate", new JsonObject { ["expression"] = expr, ["returnByValue"] = true });
        return r["result"]?["value"]?.ToString() ?? "";
    }

    // ---------------------------------------------------------------- picture (IScreenGrabber)

    // In picture pixels (twice the page size). Taps come back in the same pixels and are turned into page pixels in Input.
    public IReadOnlyList<MonitorInfo> Monitors() => new[] { new MonitorInfo(0, "דפדפן", 0, 0, _vw * Scale, _vh * Scale, true) };

    public int[]? Grab(int monitor, int w, int h)
    {
        byte[]? jpeg;
        lock (_frameGate) jpeg = _frame;
        if (jpeg == null) return null;
        using var ms = new MemoryStream(jpeg);
        using var src = new System.Drawing.Bitmap(ms);
        using var dst = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, w, h);
        }
        var data = dst.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var px = new int[w * h];
            for (int r = 0; r < h; r++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + r * data.Stride, px, r * w, w);
            return px;
        }
        finally { dst.UnlockBits(data); }
    }

    // ---------------------------------------------------------------- taps and typing (IInputSink)

    public void Input(JsonObject ev, MonitorInfo mon, int x, int y, bool restoreCursor)
    {
        // Tabs (PROTOCOL.md section 7). In the background: this runs on the link's reader, and these wait for the browser.
        if (ev["t"]?.GetValue<string>() is "tab-new" or "tab-select" or "tab-close")
        {
            var what = ev["t"]!.GetValue<string>();
            var tab = ev["tab"]?.GetValue<string>() ?? "";
            var url = ev["url"]?.GetValue<string>();
            _ = Task.Run(async () =>
            {
                try
                {
                    if (what == "tab-new") await NewTab(url);
                    else if (what == "tab-select") SelectTab(tab);
                    else await CloseTab(tab);
                }
                catch (Exception ex) { Log.Write("browser tab: " + ex.Message); }
            });
            return;
        }
        // Picture pixels → page pixels. The browser takes page pixels of the page's own layout, so on a page shown shrunk
        // (page scale below 1) a tap must be divided by that too, or it lands up and to the left of the finger.
        double k = _pageScale;
        x = (int)Math.Round(x / (Scale * k));
        y = (int)Math.Round(y / (Scale * k));
        string button = (ev["b"]?.GetValue<int>() ?? 0) == 1 ? "right" : "left";
        var t = ev["t"]?.GetValue<string>();
        // A viewer that sends touches: a tap is a touch, and a finger drag scrolls the page as on a phone
        // (as mouse events it selected text or dragged links instead).
        if (_touch && ev["touch"]?.GetValue<bool>() == true && t is "click" or "down" or "move" or "up")
        {
            // A double tap whose first tap was already sent: one more tap.
            Touch(t, x, y, ev["after"]?.GetValue<bool>() == true ? 1 : Math.Clamp(ev["n"]?.GetValue<int>() ?? 1, 1, 2));
            return;
        }
        switch (t)
        {
            case "click":
            {
                int n = Math.Clamp(ev["n"]?.GetValue<int>() ?? 1, 1, 3);
                Mouse("mouseMoved", x, y, "none", 0);
                // The first click of this double click was already sent: only the second one, counted as the second.
                int from = ev["after"]?.GetValue<bool>() == true ? n : 1;
                for (int i = from; i <= n; i++)
                {
                    Mouse("mousePressed", x, y, button, i);
                    Mouse("mouseReleased", x, y, button, i);
                }
                break;
            }
            case "down": Mouse("mousePressed", x, y, button, 1); break;
            case "move": Mouse("mouseMoved", x, y, button, 0); break;
            case "up": Mouse("mouseReleased", x, y, button, 1); break;
            case "wheel":
                Fire("Input.dispatchMouseEvent", new JsonObject
                {
                    ["type"] = "mouseWheel", ["x"] = x, ["y"] = y, ["deltaX"] = 0,
                    ["deltaY"] = -(ev["dy"]?.GetValue<double>() ?? 0) * 120,
                });
                break;
            case "text":
                Fire("Input.insertText", new JsonObject { ["text"] = ev["s"]?.GetValue<string>() ?? "" });
                break;
            case "key":
                Key(ev["k"]?.GetValue<string>() ?? "", ev["mods"]?.AsArray().Select(m => m!.GetValue<string>()).ToList() ?? new());
                break;
            case "navigate":
                Navigate(ev["url"]?.GetValue<string>() ?? "");
                break;
            case "back": Fire("Runtime.evaluate", new JsonObject { ["expression"] = "history.back()" }); break;
            case "forward": Fire("Runtime.evaluate", new JsonObject { ["expression"] = "history.forward()" }); break;
            case "reload": Fire("Page.reload"); break;
        }
    }

    /// <summary>An address, or words to search for.</summary>
    public void Navigate(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        Fire("Page.navigate", new JsonObject { ["url"] = ToAddress(text) });
    }

    private static string ToAddress(string text)
    {
        text = text.Trim();
        bool hasScheme = System.Text.RegularExpressions.Regex.IsMatch(text, @"^(https?|ftp|file|data|about|blob):", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return hasScheme ? text
            : !text.Contains(' ') && text.Contains('.') ? "https://" + text
            : "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
    }

    private void Touch(string t, int x, int y, int taps)
    {
        JsonArray Point() => new() { new JsonObject { ["x"] = x, ["y"] = y } };
        switch (t)
        {
            case "click":
                for (int i = 0; i < taps; i++)
                {
                    Fire("Input.dispatchTouchEvent", new JsonObject { ["type"] = "touchStart", ["touchPoints"] = Point() });
                    Fire("Input.dispatchTouchEvent", new JsonObject { ["type"] = "touchEnd", ["touchPoints"] = new JsonArray() });
                }
                break;
            case "down": Fire("Input.dispatchTouchEvent", new JsonObject { ["type"] = "touchStart", ["touchPoints"] = Point() }); break;
            case "move": Fire("Input.dispatchTouchEvent", new JsonObject { ["type"] = "touchMove", ["touchPoints"] = Point() }); break;
            case "up": Fire("Input.dispatchTouchEvent", new JsonObject { ["type"] = "touchEnd", ["touchPoints"] = new JsonArray() }); break;
        }
    }

    private void Mouse(string type, int x, int y, string button, int clicks) =>
        Fire("Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = button, ["clickCount"] = clicks,
        });

    private void Key(string k, List<string> mods)
    {
        // Ctrl shortcuts become the browser's own editing commands.
        if (mods.Contains("ctrl"))
        {
            var cmd = k.ToLowerInvariant() switch
            {
                "a" => "selectAll", "c" => "copy", "v" => "paste", "x" => "cut", "z" => "undo", _ => null,
            };
            if (cmd != null)
            {
                Fire("Input.dispatchKeyEvent", new JsonObject
                {
                    ["type"] = "keyDown", ["modifiers"] = 2, ["key"] = k, ["commands"] = new JsonArray(cmd),
                });
                Fire("Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyUp", ["modifiers"] = 2, ["key"] = k });
                return;
            }
        }
        (string key, int vk, string? text) = k.ToLowerInvariant() switch
        {
            "enter" => ("Enter", 13, "\r"),
            "back" => ("Backspace", 8, null),
            "tab" => ("Tab", 9, null),
            "esc" => ("Escape", 27, null),
            "del" => ("Delete", 46, null),
            "left" => ("ArrowLeft", 37, null),
            "right" => ("ArrowRight", 39, null),
            "up" => ("ArrowUp", 38, null),
            "down" => ("ArrowDown", 40, null),
            "home" => ("Home", 36, null),
            "end" => ("End", 35, null),
            "pgup" => ("PageUp", 33, null),
            "pgdn" => ("PageDown", 34, null),
            _ => ("", 0, null),
        };
        if (vk == 0) return;
        var down = new JsonObject { ["type"] = text != null ? "keyDown" : "rawKeyDown", ["key"] = key, ["code"] = key, ["windowsVirtualKeyCode"] = vk };
        if (text != null) down["text"] = text;
        Fire("Input.dispatchKeyEvent", down);
        Fire("Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyUp", ["key"] = key, ["code"] = key, ["windowsVirtualKeyCode"] = vk });
    }

    public void Dispose()
    {
        lock (_tabs)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // In the background: this is often called from the window's thread (the "stop" button), which must not
        // freeze for the seconds the browser takes to close.
        var closing = Task.Run(() =>
        {
            try { _browser.Send(new JsonObject { ["id"] = _browser.NextId(), ["method"] = "Browser.close" }).Wait(1000); } catch { }
            // Let it finish by itself: closing is when it writes its cookies, so forcing it can lose a login.
            try { if (!_proc.WaitForExit(10_000)) { Log.Write("browser: did not close in time, forcing"); _proc.Kill(true); } } catch { }
            _cts.Cancel();
            lock (_tabs) { foreach (var c in _tabs) c.Close(); }
            _browser.Close();
            FreeProfile(_profile);
        });
        lock (Closing) Closing.Add(closing);
        _ = closing.ContinueWith(t => { lock (Closing) Closing.Remove(t); }, TaskScheduler.Default);
    }

    private bool _disposed;
    private static readonly List<Task> Closing = new();

    /// <summary>
    /// Waits for browsers that are closing. Before this program ends: when it ends, Windows closes whatever is left of
    /// the browser at once, and what the browser hasn't written yet (a new login) would be lost.
    /// </summary>
    public static void WaitClosed(TimeSpan most)
    {
        Task[] all;
        lock (Closing) all = Closing.ToArray();
        try { if (all.Length > 0) Task.WaitAll(all, most); } catch (Exception ex) { Log.Write("browser close wait: " + ex.Message); }
    }

    // ---------------------------------------------------------------- closing together with this program

    private static IntPtr _job;

    private static void KillWithUs(Process p)
    {
        try
        {
            lock (Busy)
            {
                if (_job == IntPtr.Zero)
                {
                    var job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    int size = System.Runtime.InteropServices.Marshal.SizeOf(info);
                    if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref info, (uint)size))
                        throw new IOException("job info " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    _job = job;
                }
            }
            if (!AssignProcessToJobObject(_job, p.Handle)) Log.Write("browser job: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
        catch (Exception ex) { Log.Write("browser job: " + ex.Message); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}

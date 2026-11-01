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
    public static string ProfileDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BtBridge", "דפדפן");

    private readonly Process _proc;
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1);
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _calls = new();
    private int _nextCall;
    private readonly string _targetId;
    private readonly int _vw, _vh;
    private byte[]? _frame;
    private readonly object _frameGate = new();

    /// <summary>The page address and title changed (sent to the viewer, to show in its address bar).</summary>
    public event Action<string, string>? PageChanged;

    public string Url { get; private set; } = "";

    private RemoteBrowser(Process proc, string targetId, int vw, int vh)
    {
        _proc = proc;
        _targetId = targetId;
        _vw = vw;
        _vh = vh;
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

    /// <summary>Starts the browser with a page of <paramref name="vw"/>×<paramref name="vh"/> (in page pixels, like a phone).</summary>
    public static async Task<RemoteBrowser> StartAsync(string prefer, string downloadDir, string startUrl, int vw, int vh)
    {
        var exe = FindBrowser(prefer) ?? throw new RemoteException("notfound", "לא נמצא במחשב דפדפן אדג' או כרום");
        Directory.CreateDirectory(ProfileDir);
        var portFile = Path.Combine(ProfileDir, "DevToolsActivePort");
        try { File.Delete(portFile); } catch { }
        // Its own profile: the remote control interface doesn't open on the everyday profile, and this way
        // the user's own browser stays untouched. Logins made here are kept for next time.
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            ArgumentList =
            {
                "--headless=new", "--remote-debugging-port=0", "--remote-allow-origins=*", $"--user-data-dir={ProfileDir}",
                "--no-first-run", "--no-default-browser-check", $"--window-size={vw},{vh}",
                // Sites (Google search among them) block browsers that announce they are driven by a program.
                "--disable-blink-features=AutomationControlled",
                startUrl,
            },
        };
        var proc = Process.Start(psi) ?? throw new IOException("הדפדפן לא נפתח");
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
                var list = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list")) as JsonArray;
                page = list?.OfType<JsonObject>().FirstOrDefault(o => o["type"]?.GetValue<string>() == "page");
                if (page == null) await Task.Delay(200);
            }
            if (page == null) throw new IOException("לא נמצא דף בדפדפן");
            var b = new RemoteBrowser(proc, page["id"]!.GetValue<string>(), vw, vh);
            await b._ws.ConnectAsync(new Uri(page["webSocketDebuggerUrl"]!.GetValue<string>()), CancellationToken.None);
            _ = b.ReadLoop();
            await b.Call("Page.enable");
            await b.Call("Target.setDiscoverTargets", new JsonObject { ["discover"] = true });
            await b.Call("Emulation.setDeviceMetricsOverride", new JsonObject
            {
                ["width"] = vw, ["height"] = vh, ["deviceScaleFactor"] = 2, ["mobile"] = true,
            });
            // Without the word "Headless" sites treat it as an ordinary browser.
            var ver = await b.Call("Browser.getVersion");
            var ua = ver["userAgent"]?.GetValue<string>()?.Replace("HeadlessChrome", "Chrome") ?? "";
            if (ua.Length > 0) await b.Call("Emulation.setUserAgentOverride", new JsonObject { ["userAgent"] = ua });
            Directory.CreateDirectory(downloadDir);
            await b.Call("Browser.setDownloadBehavior", new JsonObject { ["behavior"] = "allow", ["downloadPath"] = downloadDir });
            await b.Call("Page.startScreencast", new JsonObject
            {
                ["format"] = "jpeg", ["quality"] = 75, ["maxWidth"] = vw * 2, ["maxHeight"] = vh * 2,
            });
            return b;
        }
        catch
        {
            try { proc.Kill(true); } catch { }
            throw;
        }
    }

    // ---------------------------------------------------------------- the remote control interface

    private async Task<JsonObject> Call(string method, JsonObject? args = null)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (_calls) { id = ++_nextCall; _calls[id] = tcs; }
        await Send(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = args ?? new JsonObject() });
        var res = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (res["error"] is JsonObject err) throw new IOException("הדפדפן: " + err["message"]?.GetValue<string>());
        return res["result"] as JsonObject ?? new JsonObject();
    }

    private void Fire(string method, JsonObject? args = null) => _ = Send(new JsonObject
    {
        ["id"] = Interlocked.Increment(ref _nextCall), ["method"] = method, ["params"] = args ?? new JsonObject(),
    });

    private async Task Send(JsonObject msg)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendGate.WaitAsync();
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); }
        catch (Exception ex) { Log.Write("browser send: " + ex.Message); }
        finally { _sendGate.Release(); }
    }

    private async Task ReadLoop()
    {
        var buf = new byte[1 << 20];
        var msg = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buf, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                msg.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var o = JsonNode.Parse(msg.GetBuffer().AsSpan(0, (int)msg.Length)) as JsonObject;
                msg.SetLength(0);
                if (o != null) Handle(o);
            }
        }
        catch (Exception ex) { if (!_cts.IsCancellationRequested) Log.Write("browser read: " + ex.Message); }
    }

    private void Handle(JsonObject o)
    {
        if (o["id"] is JsonNode idNode)
        {
            TaskCompletionSource<JsonObject>? tcs;
            lock (_calls) { if (_calls.Remove(idNode.GetValue<int>(), out tcs)) { } }
            tcs?.TrySetResult(o);
            return;
        }
        var p = o["params"] as JsonObject;
        switch (o["method"]?.GetValue<string>())
        {
            case "Page.screencastFrame":
                lock (_frameGate) _frame = Convert.FromBase64String(p!["data"]!.GetValue<string>());
                Fire("Page.screencastFrameAck", new JsonObject { ["sessionId"] = p["sessionId"]!.GetValue<int>() });
                break;
            case "Page.javascriptDialogOpening":
                // A page message ("are you sure?"): accepted, so the page doesn't stay stuck where nobody can answer it.
                Fire("Page.handleJavaScriptDialog", new JsonObject { ["accept"] = true });
                break;
            case "Target.targetCreated" or "Target.targetInfoChanged":
            {
                var info = p!["targetInfo"] as JsonObject;
                var id = info?["targetId"]?.GetValue<string>();
                var url = info?["url"]?.GetValue<string>() ?? "";
                if (id == _targetId)
                {
                    Url = url;
                    PageChanged?.Invoke(url, info!["title"]?.GetValue<string>() ?? "");
                }
                else if (info?["type"]?.GetValue<string>() == "page" && info["openerId"]?.GetValue<string>() == _targetId
                         && url.Length > 0 && url != "about:blank")
                {
                    // A link that opens a new tab: there is only one view, so it opens here instead.
                    Fire("Target.closeTarget", new JsonObject { ["targetId"] = id });
                    Fire("Page.navigate", new JsonObject { ["url"] = url });
                }
                break;
            }
            case "Page.loadEventFired" or "Page.navigatedWithinDocument":
                // The address bar on the phone: the page's current address and title.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var url = await Eval("location.href");
                        Url = url;
                        PageChanged?.Invoke(url, await Eval("document.title"));
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

    public IReadOnlyList<MonitorInfo> Monitors() => new[] { new MonitorInfo(0, "דפדפן", 0, 0, _vw, _vh, true) };

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
        string button = (ev["b"]?.GetValue<int>() ?? 0) == 1 ? "right" : "left";
        switch (ev["t"]?.GetValue<string>())
        {
            case "click":
            {
                int n = Math.Clamp(ev["n"]?.GetValue<int>() ?? 1, 1, 3);
                Mouse("mouseMoved", x, y, "none", 0);
                for (int i = 1; i <= n; i++)
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
        bool hasScheme = System.Text.RegularExpressions.Regex.IsMatch(text, @"^(https?|ftp|file|data|about|blob):", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string url = hasScheme ? text
            : !text.Contains(' ') && text.Contains('.') ? "https://" + text
            : "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
        Fire("Page.navigate", new JsonObject { ["url"] = url });
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
        if (_cts.IsCancellationRequested) return;
        try { Send(new JsonObject { ["id"] = Interlocked.Increment(ref _nextCall), ["method"] = "Browser.close" }).Wait(1000); } catch { }
        _cts.Cancel();
        try { if (!_proc.WaitForExit(3000)) _proc.Kill(true); } catch { }
        _ws.Dispose();
    }
}

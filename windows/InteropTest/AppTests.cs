using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BtBridge.Core;

/// <summary>
/// Real checks of controlling one program and the separate browser, on this computer, without a phone.
/// "apptest": a small window placed off the screen (so it never gets in the way), with a button, a text field
/// and an area that knows nothing about accessibility. "browsertest": the separate browser on a test page.
/// </summary>
static class AppTests
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public static int RunWindow()
    {
        // Like the app itself: real pixels on every screen, whatever the Windows scaling.
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        int clicks = 0, plainClicks = 0;
        Window? w = null;
        Button? btn = null;
        TextBox? tb = null;
        Border? plain = null;
        IntPtr hwnd = IntPtr.Zero;
        var ready = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            btn = new Button { Content = "לחץ", Width = 120, Height = 40, Margin = new Thickness(10) };
            btn.Click += (_, _) => clicks++;
            tb = new TextBox { Width = 220, Height = 30, Margin = new Thickness(10) };
            plain = new Border { Width = 160, Height = 60, Margin = new Thickness(10), Background = Brushes.LightBlue };
            plain.MouseLeftButtonDown += (_, _) => plainClicks++;
            var sp = new StackPanel();
            sp.Children.Add(btn);
            sp.Children.Add(tb);
            sp.Children.Add(plain);
            w = new Window
            {
                Title = "בדיקת גשר בלוטוס", Content = sp, Width = 420, Height = 320, Left = -6000, Top = 100,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false,
            };
            w.Show();
            hwnd = new WindowInteropHelper(w).Handle;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        ready.Wait();
        Thread.Sleep(800);
        int fails = 0;
        try
        {
            var listed = AppWindows.List().OfType<JsonObject>().Any(o => o["h"]!.GetValue<long>() == hwnd.ToInt64());
            Console.WriteLine($"LIST {(listed ? "has" : "lacks")} the test window (it is off the taskbar, so lacking is fine)");
            Console.WriteLine("GUESS " + AppWindows.GuessMode(hwnd));

            var g = new WindowGrabber(hwnd);
            var mon = g.Monitors()[0];
            var px = g.Grab(0, mon.W, mon.H);
            bool captured = px != null && px.Distinct().Take(10).Count() > 3;
            Console.WriteLine($"CAPTURE {(captured ? "ok" : "FAILED")} {mon.W}x{mon.H} (the window is off the screen)");
            if (!captured) fails++;

            string mode = "";
            var input = new WindowInput(g, m => mode = m);
            Point Center(FrameworkElement e) => w!.Dispatcher.Invoke(() => e.PointToScreen(new Point(e.ActualWidth / 2, e.ActualHeight / 2)));
            void Tap(Point p) => input.Input(new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1 }, mon, (int)p.X, (int)p.Y, false);
            bool Wait(Func<bool> f) { for (int i = 0; i < 40 && !f(); i++) Thread.Sleep(50); return f(); }

            Tap(Center(btn!));
            bool ok = Wait(() => clicks == 1);
            Console.WriteLine($"BUTTON {(ok ? "ok" : "FAILED")} by {mode}");
            if (!ok) fails++;

            Tap(Center(tb!));
            Thread.Sleep(200);
            input.Input(new JsonObject { ["t"] = "text", ["s"] = "שלום" }, mon, 0, 0, false);
            ok = Wait(() => w!.Dispatcher.Invoke(() => tb!.Text) == "שלום");
            Console.WriteLine($"TEXT {(ok ? "ok" : "FAILED: " + w!.Dispatcher.Invoke(() => tb!.Text))} by {mode}");
            if (!ok) fails++;

            Tap(Center(plain!));
            ok = Wait(() => plainClicks == 1);
            Console.WriteLine($"PLAIN-AREA {(ok ? "ok" : "not reached")} by {mode} (no accessibility there: messages to the window)");
            input.Close();
        }
        finally { w?.Dispatcher.Invoke(() => w.Close()); }
        Console.WriteLine(fails == 0 ? "OK" : "FAIL");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>A classic window (like most older programs): an area without accessibility, reached by messages, and typing.</summary>
    public static int RunClassic(bool rtl = false)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        string tag = rtl ? "RTL" : "CLASSIC";
        int clicks = 0;
        System.Windows.Forms.Form? f = null;
        System.Windows.Forms.Panel? panel = null;
        System.Windows.Forms.TextBox? box = null;
        var ready = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            f = new System.Windows.Forms.Form
            {
                Text = "בדיקת גשר בלוטוס", StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(-6000, 100), Size = new System.Drawing.Size(420, 300), ShowInTaskbar = false,
            };
            // Like File Explorer in Hebrew Windows: the window is laid out right to left (WS_EX_LAYOUTRTL).
            if (rtl) { f.RightToLeft = System.Windows.Forms.RightToLeft.Yes; f.RightToLeftLayout = true; }
            panel = new System.Windows.Forms.Panel { Location = new System.Drawing.Point(10, 10), Size = new System.Drawing.Size(160, 60), BackColor = System.Drawing.Color.LightBlue };
            panel.MouseClick += (_, _) => clicks++;
            box = new System.Windows.Forms.TextBox { Location = new System.Drawing.Point(10, 90), Width = 220 };
            f.Controls.Add(panel);
            f.Controls.Add(box);
            f.Shown += (_, _) => ready.Set();
            System.Windows.Forms.Application.Run(f);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        ready.Wait();
        Thread.Sleep(500);
        int fails = 0;
        try
        {
            var hwnd = (IntPtr)f!.Invoke(() => f.Handle);
            var g = new WindowGrabber(hwnd);
            var mon = g.Monitors()[0];
            string mode = "";
            var input = new WindowInput(g, m => mode = m);
            System.Drawing.Point Center(System.Windows.Forms.Control c) =>
                (System.Drawing.Point)f.Invoke(() => c.PointToScreen(new System.Drawing.Point(c.Width / 2, c.Height / 2)));
            bool Wait(Func<bool> cond) { for (int i = 0; i < 40 && !cond(); i++) Thread.Sleep(50); return cond(); }

            var p = Center(panel!);
            if (rtl) Console.WriteLine($"RTL-STYLE {(WindowGrabber.IsMirrored(hwnd) ? "ok" : "FAILED (window not mirrored)")}");
            // The picture: the light blue area must be where it really is on the screen (not mirrored, not shifted).
            var px = g.Grab(0, mon.W, mon.H);
            int at = (p.Y - mon.Y) * mon.W + (p.X - mon.X);
            var c = px == null || at < 0 || at >= px.Length ? -1 : px[at] & 0xFFFFFF;
            bool picOk = c == (System.Drawing.Color.LightBlue.ToArgb() & 0xFFFFFF);
            Console.WriteLine($"{tag}-PICTURE {(picOk ? "ok" : $"FAILED (color {c:X6} at the area's place)")}");
            if (!picOk) fails++;
            input.Input(new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1 }, mon, p.X, p.Y, false);
            bool ok = Wait(() => clicks == 1);
            Console.WriteLine($"{tag}-AREA {(ok ? "ok" : "FAILED")} by {mode}");
            if (!ok) fails++;

            p = Center(box!);
            input.Input(new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1 }, mon, p.X, p.Y, false);
            Thread.Sleep(200);
            input.Input(new JsonObject { ["t"] = "text", ["s"] = "abc" }, mon, 0, 0, false);
            ok = Wait(() => (string)f.Invoke(() => box!.Text) == "abc");
            Console.WriteLine($"{tag}-TEXT {(ok ? "ok" : "FAILED: " + f.Invoke(() => box!.Text))} by {mode}");
            if (!ok) fails++;
            input.Close();
        }
        finally { f?.Invoke(() => f.Close()); }
        Console.WriteLine(fails == 0 ? "OK" : "FAIL");
        return fails == 0 ? 0 : 1;
    }

    public static async Task<int> RunBrowser(string prefer)
    {
        // Never the real profile: the user's logins live there.
        RemoteBrowser.ProfileDir = Path.Combine(Path.GetTempPath(), "btb-test-profile");
        const string html = "<meta name=viewport content='width=device-width,initial-scale=1'><body style='margin:0'>"
            + "<button style='position:absolute;left:10px;top:10px;width:200px;height:60px' onclick=\"document.title='clicked'\">x</button>"
            + "<input style='position:absolute;left:10px;top:100px;width:200px;height:40px' oninput=\"document.title='typed:'+this.value\">"
            + "</body>";
        var dir = Path.Combine(Path.GetTempPath(), "btb-browser-test");
        RemoteBrowser b;
        try { b = await RemoteBrowser.StartAsync(prefer, dir, "data:text/html," + Uri.EscapeDataString(html), 400, 700); }
        catch (Exception ex) { Console.WriteLine("START FAILED " + ex.Message); return 1; }
        int fails = 0;
        try
        {
            // The page's own title, read straight from it.
            string title = "";
            bool Wait(Func<bool> f) { for (int i = 0; i < 100 && !f(); i++) { Thread.Sleep(100); try { title = b.Eval("document.title").Result; } catch { } } return f(); }
            string? bar = null;
            b.PageChanged += (u, _) => bar = u;
            var mon = b.Monitors()[0];

            bool ok = Wait(() => b.Grab(0, 200, 350) != null);
            Console.WriteLine($"FRAME {(ok ? "ok" : "FAILED")}");
            if (!ok) fails++;

            // The picture is twice the page size, so it is sharp on the phone; taps come in picture pixels.
            Console.WriteLine($"SHARP {(mon.W == 800 && mon.H == 1400 ? "ok" : $"FAILED ({mon.W}x{mon.H})")}");
            if (mon.W != 800) fails++;

            b.Input(new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1 }, mon, 200, 80, false);
            ok = Wait(() => title == "clicked");
            Console.WriteLine($"CLICK {(ok ? "ok" : "FAILED, title=" + title)}");
            if (!ok) fails++;

            b.Input(new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1 }, mon, 200, 240, false);
            Thread.Sleep(300);
            b.Input(new JsonObject { ["t"] = "text", ["s"] = "שלום" }, mon, 0, 0, false);
            ok = Wait(() => title == "typed:שלום");
            Console.WriteLine($"TYPE {(ok ? "ok" : "FAILED, title=" + title)}");
            if (!ok) fails++;

            b.Input(new JsonObject { ["t"] = "navigate", ["url"] = "data:text/html,<title>second</title>" }, mon, 0, 0, false);
            ok = Wait(() => title == "second");
            Console.WriteLine($"NAVIGATE {(ok ? "ok" : "FAILED, title=" + title)}");
            if (!ok) fails++;
            Console.WriteLine($"ADDRESS-BAR {(bar?.StartsWith("data:") == true ? "ok" : "not reported: " + bar)}");

            // Searching from the address bar (a real site): the result page must not be the "unusual traffic" block.
            b.Input(new JsonObject { ["t"] = "navigate", ["url"] = "גשר בלוטוס" }, mon, 0, 0, false);
            Wait(() => title.Contains("Google") || title.Contains("גשר"));
            var href = b.Eval("location.href").Result;
            Console.WriteLine($"SEARCH {(href.Contains("/sorry/") ? "BLOCKED by Google" : "ok")} title={title}");
        }
        finally { b.Dispose(); }
        RemoteBrowser.WaitClosed(TimeSpan.FromSeconds(15));
        fails += await RunBrowserKeepsLogin(prefer);
        fails += await RunBrowserTabs(prefer);
        fails += await RunBrowserTouch(prefer);
        fails += await RunBrowserShrunk(prefer, touch: true);
        fails += await RunBrowserShrunk(prefer, touch: false);
        Console.WriteLine(fails == 0 ? "OK" : "FAIL");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// A real File Explorer window (mirrored in Hebrew Windows): the picture must be the window itself, not the desktop
    /// or the taskbar painted over it. Prints what it found and saves a small picture to <paramref name="png"/>.
    /// </summary>
    public static int RunExplorer(string png)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var items = AppWindows.List().Select(n => n!.AsObject()).Where(o => o["app"]?.GetValue<string>() == "explorer").ToList();
        if (items.Count == 0)
        {
            System.Diagnostics.Process.Start("explorer.exe", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            for (int i = 0; i < 40 && items.Count == 0; i++)
            {
                Thread.Sleep(250);
                items = AppWindows.List().Select(n => n!.AsObject()).Where(o => o["app"]?.GetValue<string>() == "explorer").ToList();
            }
        }
        if (items.Count == 0) { Console.WriteLine("NO EXPLORER WINDOW"); return 1; }
        var hwnd = new IntPtr(items[0]["h"]!.GetValue<long>());
        AppWindows.Prepare(hwnd);
        Thread.Sleep(400);
        var g = new WindowGrabber(hwnd);
        var mon = g.Monitors()[0];
        Console.WriteLine($"WINDOW mirrored={WindowGrabber.IsMirrored(hwnd)} {mon.W}x{mon.H} at {mon.X},{mon.Y} min={items[0]["min"]}");
        var c = g.Client();
        foreach (var p in g.Popups(c)) Console.WriteLine("POPUP " + Native.ClassOf(p));
        var (w, h) = (mon.W / 2, mon.H / 2);
        var px = g.Grab(0, w, h);
        if (px == null) { Console.WriteLine("NO PICTURE"); return 1; }
        // Mostly one color and dark = what the phone showed.
        int dark = px.Count(v => ((v >> 16) & 255) + ((v >> 8) & 255) + (v & 255) < 30);
        double pct = dark * 100.0 / px.Length;
        Console.WriteLine($"DARK {pct:0.0}%  COLORS {px.Select(v => v & 0xF0F0F0).Distinct().Count()}");
        using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
        {
            var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            System.Runtime.InteropServices.Marshal.Copy(px, 0, d.Scan0, px.Length);
            bmp.UnlockBits(d);
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }
        return 0;
    }

    /// <summary>
    /// A wide page without a phone layout: the browser shows it shrunk (page scale below 1). A tap at the place where the
    /// button is seen in the picture must press it.
    /// </summary>
    private static async Task<int> RunBrowserShrunk(string prefer, bool touch)
    {
        const string html = "<html><body style='margin:0;width:1200px;height:1600px'>"
            + "<button id='b' style='position:absolute;left:600px;top:500px;width:300px;height:150px' onclick=\"document.title='hit'\">x</button>"
            + "</body></html>";
        var page = html;
        var dir = Path.Combine(Path.GetTempPath(), "btb-browser-test");
        RemoteBrowser b;
        try { b = await RemoteBrowser.StartAsync(prefer, dir, "data:text/html," + Uri.EscapeDataString(page), 360, 640, touch: touch); }
        catch (Exception ex) { Console.WriteLine("SHRUNK START FAILED " + ex.Message); return 1; }
        int fails = 0;
        try
        {
            string title = "";
            bool Wait(Func<bool> f) { for (int i = 0; i < 50 && !f(); i++) { Thread.Sleep(100); try { title = b.Eval("document.title").Result; } catch { } } return f(); }
            var mon = b.Monitors()[0];
            Wait(() => b.Grab(0, 200, 350) != null);
            Thread.Sleep(800);
            double sc = double.Parse(b.Eval("String(window.visualViewport.scale)").Result, System.Globalization.CultureInfo.InvariantCulture);
            // Where the middle of the button is seen in the picture (picture pixels = twice the screen's).
            int x = (int)Math.Round((600 + 150) * sc * 2), y = (int)Math.Round((500 + 75) * sc * 2);
            Console.WriteLine($"SHRUNK page scale {sc:0.###}, button seen at {x},{y} of {mon.W}x{mon.H}");
            var ev = new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1 };
            if (touch) ev["touch"] = true;
            b.Input(ev, mon, x, y, false);
            bool ok = Wait(() => title == "hit");
            Console.WriteLine($"SHRUNK-{(touch ? "TOUCH" : "MOUSE")} {(ok ? "ok" : "FAILED, title=" + title)}");
            if (!ok) fails++;
        }
        finally { b.Dispose(); }
        return fails;
    }

    /// <summary>Tabs (0.5.1): a new tab, a link that opens one, switching between them, and closing.</summary>
    private static async Task<int> RunBrowserTabs(string prefer)
    {
        // A small real site for the link (browsers refuse links that open "data:" pages in a new tab).
        var site = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        site.Start();
        int sitePort = ((System.Net.IPEndPoint)site.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var c = await site.AcceptTcpClientAsync();
                    var st = c.GetStream();
                    await st.ReadAsync(new byte[4096]);
                    var body = "<title>linked</title>";
                    await st.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body));
                }
            }
            catch { }
        });
        string first = $"<title>one</title><a id=a target=_blank style='position:absolute;left:0;top:0;width:300px;height:200px;display:block' href=\"http://127.0.0.1:{sitePort}/\">x</a>";
        const string meta = "<meta name=viewport content='width=device-width,initial-scale=1'>";
        var dir = Path.Combine(Path.GetTempPath(), "btb-browser-test");
        RemoteBrowser b;
        try { b = await RemoteBrowser.StartAsync(prefer, dir, "data:text/html," + Uri.EscapeDataString(meta + first), 400, 700, withTabs: true); }
        catch (Exception ex) { Console.WriteLine("TABS START FAILED " + ex.Message); return 1; }
        int fails = 0;
        void Check(string name, bool ok) { Console.WriteLine($"{name} {(ok ? "ok" : "FAILED")}"); if (!ok) fails++; }
        JsonArray tabs = new();
        b.TabsChanged += a => tabs = a;
        try
        {
            var mon = b.Monitors()[0];
            bool Wait(Func<bool> f) { for (int i = 0; i < 80 && !f(); i++) Thread.Sleep(100); return f(); }
            string Title() { try { return b.Eval("document.title").Result; } catch { return ""; } }
            string Active() => tabs.OfType<JsonNode>().Where(t => t["active"]!.GetValue<bool>()).Select(t => t["id"]!.GetValue<string>()).FirstOrDefault() ?? "";
            Wait(() => Title() == "one");
            b.PushTabs(force: true);
            Wait(() => tabs.Count == 1);
            var firstId = Active();
            Check("TABS-FIRST", tabs.Count == 1 && firstId.Length > 0);

            b.Input(new JsonObject { ["t"] = "tab-new", ["url"] = "data:text/html,<title>two</title>" }, mon, 0, 0, false);
            Check("TABS-NEW", Wait(() => tabs.Count == 2 && Active() != firstId && Title() == "two"));
            var second = Active();
            Check("TABS-PICTURE", Wait(() => b.Grab(0, 100, 175) != null));

            b.Input(new JsonObject { ["t"] = "tab-select", ["tab"] = firstId }, mon, 0, 0, false);
            Check("TABS-SWITCH", Wait(() => Active() == firstId && Title() == "one"));

            // A link with target=_blank opens a tab of its own, and it is shown.
            Thread.Sleep(500);
            b.Input(new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1 }, mon, 200, 200, false);
            Check("TABS-LINK", Wait(() => tabs.Count == 3 && Title() == "linked"));

            b.Input(new JsonObject { ["t"] = "tab-close", ["tab"] = second }, mon, 0, 0, false);
            Check("TABS-CLOSE", Wait(() => tabs.Count == 2 && tabs.OfType<JsonNode>().All(t => t["id"]!.GetValue<string>() != second)));

            // Closing the last one leaves a fresh tab, not a dead browser.
            foreach (var id in tabs.OfType<JsonNode>().Select(t => t["id"]!.GetValue<string>()).ToList())
            {
                b.Input(new JsonObject { ["t"] = "tab-close", ["tab"] = id }, mon, 0, 0, false);
                Thread.Sleep(1200);
            }
            Check("TABS-LAST", Wait(() => tabs.Count == 1 && Active().Length > 0));
        }
        finally { b.Dispose(); }
        RemoteBrowser.WaitClosed(TimeSpan.FromSeconds(15));
        return fails;
    }

    /// <summary>A login cookie that was just set must still be there after the browser closes and opens again.</summary>
    private static async Task<int> RunBrowserKeepsLogin(string prefer)
    {
        var dir = Path.Combine(Path.GetTempPath(), "btb-browser-test");
        var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        int port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;
        string lastCookie = "";
        var loop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var c = await server.AcceptTcpClientAsync();
                    var st = c.GetStream();
                    var buf = new byte[8192];
                    int n = await st.ReadAsync(buf);
                    var req = System.Text.Encoding.ASCII.GetString(buf, 0, n);
                    if (!req.StartsWith("GET / ")) { await st.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 404 x\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); continue; }
                    foreach (var line in req.Split("\r\n")) if (line.StartsWith("Cookie:")) lastCookie = line;
                    var body = "<title>ok</title>";
                    await st.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nSet-Cookie: stay=1; Max-Age=86400; Path=/\r\nContent-Type: text/html\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body));
                }
            }
            catch { }
        });
        int fails = 0;
        try
        {
            var url = $"http://127.0.0.1:{port}/";
            for (int round = 1; round <= 2; round++)
            {
                var b = await RemoteBrowser.StartAsync(prefer, dir, url, 400, 700);
                for (int i = 0; i < 50 && b.Eval("document.title").Result != "ok"; i++) Thread.Sleep(100);
                b.Dispose();
                RemoteBrowser.WaitClosed(TimeSpan.FromSeconds(15));
            }
            bool ok = lastCookie.Contains("stay=1");
            Console.WriteLine($"KEEPS-LOGIN {(ok ? "ok" : "FAILED, sent: " + lastCookie)}");
            if (!ok) fails++;
        }
        catch (Exception ex) { Console.WriteLine("KEEPS-LOGIN FAILED " + ex.Message); fails++; }
        finally { server.Stop(); }
        return fails;
    }

    /// <summary>A viewer that sends touches (0.5): a tap presses a button, a finger drag scrolls the page.</summary>
    private static async Task<int> RunBrowserTouch(string prefer)
    {
        const string html = "<meta name=viewport content='width=device-width,initial-scale=1'><body style='margin:0;height:5000px'>"
            + "<button style='position:absolute;left:10px;top:10px;width:200px;height:60px' onclick=\"document.title='tapped'\">x</button>"
            + "</body>";
        var dir = Path.Combine(Path.GetTempPath(), "btb-browser-test");
        RemoteBrowser b;
        try { b = await RemoteBrowser.StartAsync(prefer, dir, "data:text/html," + Uri.EscapeDataString(html), 400, 700, touch: true); }
        catch (Exception ex) { Console.WriteLine("TOUCH START FAILED " + ex.Message); return 1; }
        int fails = 0;
        try
        {
            string title = "";
            bool Wait(Func<bool> f) { for (int i = 0; i < 60 && !f(); i++) { Thread.Sleep(100); try { title = b.Eval("document.title").Result; } catch { } } return f(); }
            var mon = b.Monitors()[0];
            Wait(() => b.Grab(0, 200, 350) != null);
            b.Input(new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1, ["touch"] = true }, mon, 200, 80, false);
            bool ok = Wait(() => title == "tapped");
            Console.WriteLine($"TOUCH-TAP {(ok ? "ok" : "FAILED, title=" + title)}");
            if (!ok) fails++;

            // Finger from the bottom up: the page moves down (as on a phone).
            b.Input(new JsonObject { ["t"] = "down", ["touch"] = true }, mon, 400, 1200, false);
            for (int y = 1150; y >= 400; y -= 50)
            {
                Thread.Sleep(20);
                b.Input(new JsonObject { ["t"] = "move", ["touch"] = true }, mon, 400, y, false);
            }
            b.Input(new JsonObject { ["t"] = "up", ["touch"] = true }, mon, 400, 400, false);
            string sy = "0";
            for (int i = 0; i < 30 && sy == "0"; i++) { Thread.Sleep(100); sy = b.Eval("String(Math.round(window.scrollY))").Result; }
            ok = sy != "0";
            Console.WriteLine($"TOUCH-SCROLL {(ok ? "ok" : "FAILED")} scrollY={sy}");
            if (!ok) fails++;
        }
        finally { b.Dispose(); }
        return fails;
    }
}

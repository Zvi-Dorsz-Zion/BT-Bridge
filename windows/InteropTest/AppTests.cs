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
    public static int RunClassic()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
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
            input.Input(new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1 }, mon, p.X, p.Y, false);
            bool ok = Wait(() => clicks == 1);
            Console.WriteLine($"CLASSIC-AREA {(ok ? "ok" : "FAILED")} by {mode}");
            if (!ok) fails++;

            p = Center(box!);
            input.Input(new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1 }, mon, p.X, p.Y, false);
            Thread.Sleep(200);
            input.Input(new JsonObject { ["t"] = "text", ["s"] = "abc" }, mon, 0, 0, false);
            ok = Wait(() => (string)f.Invoke(() => box!.Text) == "abc");
            Console.WriteLine($"CLASSIC-TEXT {(ok ? "ok" : "FAILED: " + f.Invoke(() => box!.Text))} by {mode}");
            if (!ok) fails++;
            input.Close();
        }
        finally { f?.Invoke(() => f.Close()); }
        Console.WriteLine(fails == 0 ? "OK" : "FAIL");
        return fails == 0 ? 0 : 1;
    }

    public static async Task<int> RunBrowser(string prefer)
    {
        const string html = "<body style='margin:0'>"
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

            b.Input(new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1 }, mon, 100, 40, false);
            ok = Wait(() => title == "clicked");
            Console.WriteLine($"CLICK {(ok ? "ok" : "FAILED, title=" + title)}");
            if (!ok) fails++;

            b.Input(new JsonObject { ["t"] = "click", ["b"] = 0, ["n"] = 1 }, mon, 100, 120, false);
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
        Console.WriteLine(fails == 0 ? "OK" : "FAIL");
        return fails == 0 ? 0 : 1;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace BtBridge.Core;

/// <summary>
/// Controlling one program on this computer from the other device, without taking over the screen
/// (PROTOCOL.md section 7, "window"). The picture comes from the program's own window, even when other windows
/// cover it; taps go to the program itself, not to the real mouse.
/// </summary>
public static class AppWindows
{
    /// <summary>Visible top-level windows with a title, the way the taskbar shows them.</summary>
    public static JsonArray List()
    {
        var list = new JsonArray();
        var me = Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.IsCloaked(h)) return true;
            var title = Native.Title(h);
            if (title.Length == 0) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == me) return true;
            string app = "";
            try { app = Process.GetProcessById((int)pid).ProcessName; } catch { }
            if (app is "TextInputHost" or "ApplicationFrameHost" && title is "Microsoft Text Input Application") return true;
            list.Add(new JsonObject { ["h"] = h.ToInt64(), ["title"] = title, ["app"] = app, ["min"] = Native.IsIconic(h) });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Gets a window ready to be captured: a minimized window is restored without being activated and put behind
    /// all other windows, so it draws itself without coming in front of the user's work.
    /// </summary>
    public static void Prepare(IntPtr h)
    {
        if (!Native.IsWindow(h)) throw new RemoteException("notfound", "החלון כבר נסגר");
        if (Native.IsIconic(h))
        {
            Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
            Native.SetWindowPos(h, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>"uia" when the program shows its buttons and fields to the accessibility interface, otherwise "window".</summary>
    public static string GuessMode(IntPtr h)
    {
        try
        {
            var task = Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(h);
                var cond = new OrCondition(
                    new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true),
                    new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
                return root.FindFirst(TreeScope.Descendants, cond) != null;
            });
            return task.Wait(1500) && task.Result ? "uia" : "window";
        }
        catch { return "window"; }
    }
}

/// <summary>Pictures of one window and the windows it opened (menus, dialogs), drawn together, even when covered.</summary>
public sealed class WindowGrabber(IntPtr hwnd) : IScreenGrabber
{
    public IntPtr Hwnd { get; } = hwnd;

    /// <summary>The window's inside (without its frame), in screen pixels.</summary>
    public Native.RECT Client()
    {
        Native.GetClientRect(Hwnd, out var r);
        var p = new Native.POINT { X = 0, Y = 0 };
        Native.ClientToScreen(Hwnd, ref p);
        // A right-to-left window (File Explorer and others in Hebrew Windows) counts from its right edge:
        // its point (0,0) is the top RIGHT corner, so the left edge is one width further left.
        if (IsMirrored(Hwnd)) p.X -= r.Right;
        return new Native.RECT { Left = p.X, Top = p.Y, Right = p.X + r.Right, Bottom = p.Y + r.Bottom };
    }

    /// <summary>A window laid out right to left (WS_EX_LAYOUTRTL): its picture from PrintWindow comes out mirrored.</summary>
    public static bool IsMirrored(IntPtr h) => (Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_LAYOUTRTL) != 0;

    public IReadOnlyList<MonitorInfo> Monitors()
    {
        // In real screen pixels from both corners: for a program that isn't DPI-aware (at 125%/150%) GetClientRect gives
        // a smaller, "logical" size, and taps computed from it landed up and to the left. The picture itself (Grab) doesn't
        // depend on this: it is drawn at the program's own size and scaled to the frame.
        Native.GetClientRect(Hwnd, out var r);
        var a = new Native.POINT { X = 0, Y = 0 };
        var b = new Native.POINT { X = r.Right, Y = r.Bottom };
        Native.ClientToScreen(Hwnd, ref a);
        Native.ClientToScreen(Hwnd, ref b);
        int left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
        return new[] { new MonitorInfo(0, Native.Title(Hwnd), left, top, Math.Max(2, Math.Abs(b.X - a.X)), Math.Max(2, Math.Abs(b.Y - a.Y)), true) };
    }

    public int[]? Grab(int monitor, int w, int h)
    {
        if (!Native.IsWindow(Hwnd) || Native.IsIconic(Hwnd)) return null;
        var c = Client();
        if (c.Width < 2 || c.Height < 2) return null;
        using var full = new System.Drawing.Bitmap(c.Width, c.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(full))
        {
            g.Clear(System.Drawing.Color.White);
            DrawWindow(g, Hwnd, c, clientOnly: true);
            // Menus and dialogs of the same program are separate windows: draw the ones over this window too.
            foreach (var p in Popups(c)) DrawWindow(g, p, c, clientOnly: false);
        }
        using var small = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, 0, 0, w, h);
        }
        var data = small.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var px = new int[w * h];
            for (int r = 0; r < h; r++) Marshal.Copy(data.Scan0 + r * data.Stride, px, r * w, w);
            return px;
        }
        finally { small.UnlockBits(data); }
    }

    private static void DrawWindow(System.Drawing.Graphics g, IntPtr h, Native.RECT origin, bool clientOnly)
    {
        Native.RECT r;
        if (clientOnly) r = origin;
        else Native.GetWindowRect(h, out r);
        if (r.Width < 1 || r.Height < 1) return;
        using var bmp = new System.Drawing.Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var bg = System.Drawing.Graphics.FromImage(bmp))
        {
            var dc = bg.GetHdc();
            try { Native.PrintWindow(h, dc, (clientOnly ? Native.PW_CLIENTONLY : 0) | Native.PW_RENDERFULLCONTENT); }
            finally { bg.ReleaseHdc(dc); }
        }
        // Right-to-left windows come out mirrored (the text read backwards): turn the picture back.
        if (WindowGrabber.IsMirrored(h)) bmp.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipX);
        g.DrawImageUnscaled(bmp, r.Left - origin.Left, r.Top - origin.Top);
    }

    private static readonly HashSet<string> ShellWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "ApplicationFrameWindow",
    };

    /// <summary>
    /// A window of the same program that belongs to this one (its menu, dialog, pop-up). File Explorer's process also owns
    /// the desktop, the taskbar and its other folder windows; another main window of any program is a window of its own
    /// (it has its own line in the list of programs). Painting those over this window hid it (all black in File Explorer).
    /// </summary>
    private bool IsPartOfThisWindow(IntPtr h)
    {
        if (Native.IsCloaked(h) || ShellWindows.Contains(Native.ClassOf(h))) return false;
        for (var o = Native.GetWindow(h, Native.GW_OWNER); o != IntPtr.Zero; o = Native.GetWindow(o, Native.GW_OWNER))
            if (o == Hwnd) return true;
        // Not owned by us: a separate main window when it has a title bar and is not a tool window.
        int style = Native.GetWindowLong(h, Native.GWL_STYLE);
        int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        return (style & Native.WS_CAPTION) != Native.WS_CAPTION || (ex & Native.WS_EX_TOOLWINDOW) != 0;
    }

    /// <summary>Visible windows of the same program that lie over this one (menus, dialogs, pop-ups), back to front.</summary>
    public List<IntPtr> Popups(Native.RECT area)
    {
        Native.GetWindowThreadProcessId(Hwnd, out var pid);
        var found = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            // From the top down: everything after this window is behind it (the desktop among them).
            if (h == Hwnd) return false;
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            Native.GetWindowThreadProcessId(h, out var p);
            if (p != pid || !IsPartOfThisWindow(h)) return true;
            Native.GetWindowRect(h, out var r);
            if (r.Right > area.Left && r.Left < area.Right && r.Bottom > area.Top && r.Top < area.Bottom) found.Add(h);
            return true;
        }, IntPtr.Zero);
        found.Reverse(); // EnumWindows goes from the top down; draw from the bottom up.
        return found;
    }
}

/// <summary>
/// Input for one program, without the real mouse and keyboard. Each tap first tries the accessibility interface
/// (pressing the button or choosing the item under the finger); when that has nothing to offer, it sends the click
/// straight to the window as messages. <see cref="Mode"/> tells which way worked last.
/// </summary>
public sealed class WindowInput(WindowGrabber win, Action<string>? modeChanged = null) : IInputSink
{
    /// <summary>"uia" (accessibility) or "window" (messages to the window).</summary>
    public string Mode { get; private set; } = "";
    private AutomationElement? _textTarget;
    private IntPtr _lastChild;
    private IntPtr _dragTarget;

    private void SetMode(string m)
    {
        if (Mode == m) return;
        Mode = m;
        try { modeChanged?.Invoke(m); } catch { }
    }

    /// <summary>Taps run one after another on their own thread: asking a slow program must not hold up the link.</summary>
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = StartWorker();

    private static System.Collections.Concurrent.BlockingCollection<Action> StartWorker()
    {
        var q = new System.Collections.Concurrent.BlockingCollection<Action>(256);
        new Thread(() =>
        {
            foreach (var a in q.GetConsumingEnumerable())
                try { a(); } catch (Exception ex) { Log.Write("window input: " + ex.Message); }
        }) { IsBackground = true, Name = "window-input" }.Start();
        return q;
    }

    public void Input(JsonObject ev, MonitorInfo mon, int x, int y, bool restoreCursor) =>
        _work.TryAdd(() => Apply(ev, x, y));

    /// <summary>Ends the input thread (the stream is over).</summary>
    public void Close() => _work.CompleteAdding();

    private void Apply(JsonObject ev, int x, int y)
    {
        var t = ev["t"]?.GetValue<string>();
        int b = ev["b"]?.GetValue<int>() ?? 0;
        switch (t)
        {
            case "click":
            {
                int n = Math.Clamp(ev["n"]?.GetValue<int>() ?? 1, 1, 3);
                if (b == 0 && n == 1 && TryUia(x, y)) { SetMode("uia"); return; }
                // A double tap on a file or folder in a list (File Explorer): "open" it the accessible way.
                if (b == 0 && n == 2 && TryUiaOpen(x, y)) { SetMode("uia"); return; }
                bool after = ev["after"]?.GetValue<bool>() == true;
                if (after && n == 2)
                {
                    // The first click was already sent by itself: only the second half of a double click.
                    var target = ChildAt(x, y);
                    PostMouse(x, y, b == 1 ? Native.WM_RBUTTONDBLCLK : Native.WM_LBUTTONDBLCLK, b == 1 ? Native.MK_RBUTTON : Native.MK_LBUTTON, target);
                    PostMouse(x, y, b == 1 ? Native.WM_RBUTTONUP : Native.WM_LBUTTONUP, 0, target);
                    SetMode("window");
                    break;
                }
                PostClick(x, y, b, n);
                SetMode("window");
                break;
            }
            case "down":
                _dragTarget = PostMouse(x, y, b == 1 ? Native.WM_RBUTTONDOWN : Native.WM_LBUTTONDOWN, b == 1 ? Native.MK_RBUTTON : Native.MK_LBUTTON);
                SetMode("window");
                break;
            case "move":
                if (_dragTarget != IntPtr.Zero) PostMouse(x, y, Native.WM_MOUSEMOVE, Native.MK_LBUTTON, _dragTarget);
                break;
            case "up":
                PostMouse(x, y, b == 1 ? Native.WM_RBUTTONUP : Native.WM_LBUTTONUP, 0, _dragTarget);
                _dragTarget = IntPtr.Zero;
                break;
            case "wheel":
            {
                double dy = ev["dy"]?.GetValue<double>() ?? 0;
                if (TryUiaScroll(x, y, dy)) { SetMode("uia"); return; }
                var target = ChildAt(x, y);
                int delta = (int)Math.Round(dy * 120);
                Native.PostMessage(target, Native.WM_MOUSEWHEEL, (IntPtr)(delta << 16), Native.MakeLParam(x, y));
                SetMode("window");
                break;
            }
            case "text":
                TypeText(ev["s"]?.GetValue<string>() ?? "");
                break;
            case "key":
                Key(ev["k"]?.GetValue<string>() ?? "");
                break;
        }
    }

    // ---------------------------------------------------------------- accessibility

    /// <summary>The element under a screen point, found by walking down from the window (works even when the window is covered).</summary>
    private AutomationElement? ElementAt(int x, int y)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var cur = AutomationElement.FromHandle(PopupAt(x, y) ?? win.Hwnd);
            for (int depth = 0; depth < 40; depth++)
            {
                AutomationElement? best = null;
                double bestArea = double.MaxValue;
                for (var c = walker.GetFirstChild(cur); c != null; c = walker.GetNextSibling(c))
                {
                    var r = c.Current.BoundingRectangle;
                    if (r.IsEmpty || !r.Contains(x, y)) continue;
                    double area = r.Width * r.Height;
                    if (area < bestArea) { best = c; bestArea = area; }
                }
                if (best == null) break;
                cur = best;
            }
            return cur;
        }
        catch { return null; }
    }

    private bool TryUia(int x, int y)
    {
        var e = ElementAt(x, y);
        // The pattern may sit on a parent (a text inside a button, a cell inside a list item).
        for (int up = 0; e != null && up < 4; up++, e = TreeWalker.ControlViewWalker.GetParent(e))
        {
            try
            {
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && !((ValuePattern)vp).Current.IsReadOnly
                    && e.Current.ControlType == ControlType.Edit)
                {
                    // A text field: typing will go into it. Focusing it would bring the window forward, so it isn't focused.
                    _textTarget = e;
                    return true;
                }
                if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); return true; }
                if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) { ((TogglePattern)tp).Toggle(); return true; }
                if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) { ((SelectionItemPattern)sp).Select(); return true; }
                if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep))
                {
                    var ec = (ExpandCollapsePattern)ep;
                    if (ec.Current.ExpandCollapseState == ExpandCollapseState.Expanded) ec.Collapse(); else ec.Expand();
                    return true;
                }
            }
            catch { return false; }
        }
        return false;
    }

    private bool TryUiaOpen(int x, int y)
    {
        var e = ElementAt(x, y);
        for (int up = 0; e != null && up < 4; up++, e = TreeWalker.ControlViewWalker.GetParent(e))
        {
            try
            {
                var ct = e.Current.ControlType;
                if (ct != ControlType.ListItem && ct != ControlType.DataItem && ct != ControlType.TreeItem) continue;
                if (!e.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) return false;
                ((InvokePattern)ip).Invoke();
                return true;
            }
            catch { return false; }
        }
        return false;
    }

    private bool TryUiaScroll(int x, int y, double dy)
    {
        var e = ElementAt(x, y);
        for (int up = 0; e != null && up < 8; up++, e = TreeWalker.ControlViewWalker.GetParent(e))
        {
            try
            {
                if (!e.TryGetCurrentPattern(ScrollPattern.Pattern, out var p)) continue;
                var sp = (ScrollPattern)p;
                if (!sp.Current.VerticallyScrollable) continue;
                var amount = dy > 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement;
                for (int i = 0; i < Math.Max(1, (int)Math.Round(Math.Abs(dy))) * 3; i++) sp.ScrollVertical(amount);
                return true;
            }
            catch { return false; }
        }
        return false;
    }

    // ---------------------------------------------------------------- messages to the window

    /// <summary>The window (main, menu or dialog) and its innermost child under a screen point.</summary>
    private IntPtr ChildAt(int x, int y)
    {
        var top = PopupAt(x, y) ?? win.Hwnd;
        var cur = top;
        for (int depth = 0; depth < 20; depth++)
        {
            var p = new Native.POINT { X = x, Y = y };
            Native.ScreenToClient(cur, ref p);
            var c = Native.ChildWindowFromPointEx(cur, p, Native.CWP_SKIPINVISIBLE | Native.CWP_SKIPTRANSPARENT);
            if (c == IntPtr.Zero || c == cur) break;
            cur = c;
        }
        return cur;
    }

    private IntPtr? PopupAt(int x, int y)
    {
        var c = win.Client();
        var pops = win.Popups(c);
        for (int i = pops.Count - 1; i >= 0; i--)
        {
            Native.GetWindowRect(pops[i], out var r);
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return pops[i];
        }
        return null;
    }

    private IntPtr PostMouse(int x, int y, uint msg, int keys, IntPtr target = default)
    {
        if (target == IntPtr.Zero) target = ChildAt(x, y);
        var p = new Native.POINT { X = x, Y = y };
        Native.ScreenToClient(target, ref p);
        Native.PostMessage(target, msg, (IntPtr)keys, Native.MakeLParam(p.X, p.Y));
        return target;
    }

    private void PostClick(int x, int y, int b, int n)
    {
        var target = ChildAt(x, y);
        _lastChild = target;
        _textTarget = null;
        PostMouse(x, y, Native.WM_MOUSEMOVE, 0, target);
        uint down = b == 1 ? Native.WM_RBUTTONDOWN : Native.WM_LBUTTONDOWN, up = b == 1 ? Native.WM_RBUTTONUP : Native.WM_LBUTTONUP;
        int key = b == 1 ? Native.MK_RBUTTON : Native.MK_LBUTTON;
        PostMouse(x, y, down, key, target);
        PostMouse(x, y, up, 0, target);
        if (n >= 2)
        {
            PostMouse(x, y, b == 1 ? Native.WM_RBUTTONDBLCLK : Native.WM_LBUTTONDBLCLK, key, target);
            PostMouse(x, y, up, 0, target);
        }
    }

    /// <summary>Where typed text goes when it is sent as messages: the program's focused control, or what was clicked last.</summary>
    private IntPtr TextWindow()
    {
        uint tid = Native.GetWindowThreadProcessId(win.Hwnd, out _);
        var gti = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (Native.GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero) return gti.hwndFocus;
        return _lastChild != IntPtr.Zero ? _lastChild : win.Hwnd;
    }

    private void TypeText(string s)
    {
        if (s.Length == 0) return;
        if (_textTarget != null)
        {
            try
            {
                var vp = (ValuePattern)_textTarget.GetCurrentPattern(ValuePattern.Pattern);
                vp.SetValue(vp.Current.Value + s);
                SetMode("uia");
                return;
            }
            catch { _textTarget = null; }
        }
        var h = TextWindow();
        foreach (var ch in s)
        {
            if (ch == '\n') { PostKey(h, 0x0D, '\r'); continue; }
            Native.PostMessage(h, Native.WM_CHAR, (IntPtr)ch, (IntPtr)1);
        }
        SetMode("window");
    }

    private void Key(string k)
    {
        if (_textTarget != null && k.Equals("back", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var vp = (ValuePattern)_textTarget.GetCurrentPattern(ValuePattern.Pattern);
                var v = vp.Current.Value;
                if (v.Length > 0) vp.SetValue(v[..^1]);
                return;
            }
            catch { _textTarget = null; }
        }
        var h = _textTarget != null && _textTarget.Current.NativeWindowHandle != 0
            ? (IntPtr)_textTarget.Current.NativeWindowHandle : TextWindow();
        switch (k.ToLowerInvariant())
        {
            case "enter": PostKey(h, 0x0D, '\r'); break;
            case "back": PostKey(h, 0x08, '\b'); break;
            case "tab": PostKey(h, 0x09, '\t'); break;
            case "esc": PostKey(h, 0x1B, (char)27); break;
            case "del": PostKey(h, 0x2E, null); break;
            case "left": PostKey(h, 0x25, null); break;
            case "right": PostKey(h, 0x27, null); break;
            case "up": PostKey(h, 0x26, null); break;
            case "down": PostKey(h, 0x28, null); break;
            case "home": PostKey(h, 0x24, null); break;
            case "end": PostKey(h, 0x23, null); break;
            case "pgup": PostKey(h, 0x21, null); break;
            case "pgdn": PostKey(h, 0x22, null); break;
        }
    }

    private static void PostKey(IntPtr h, int vk, char? ch)
    {
        Native.PostMessage(h, Native.WM_KEYDOWN, (IntPtr)vk, (IntPtr)1);
        if (ch != null) Native.PostMessage(h, Native.WM_CHAR, (IntPtr)ch.Value, (IntPtr)1);
        Native.PostMessage(h, Native.WM_KEYUP, (IntPtr)vk, unchecked((IntPtr)(int)0xC0000001));
    }
}

/// <summary>Windows functions for window capture and window messages.</summary>
public static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    public static string ClassOf(IntPtr h)
    {
        var s = new System.Text.StringBuilder(128);
        GetClassName(h, s, s.Capacity);
        return s.ToString();
    }

    public const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;
    public const int GW_OWNER = 4, GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, SW_SHOWNOACTIVATE = 4, WS_EX_LAYOUTRTL = 0x400000;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public const uint PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2;
    public const uint CWP_SKIPINVISIBLE = 1, CWP_SKIPTRANSPARENT = 4;
    public const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_RBUTTONDBLCLK = 0x206, WM_MOUSEWHEEL = 0x20A,
        WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102;
    public const int MK_LBUTTON = 1, MK_RBUTTON = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    public delegate bool EnumProc(IntPtr h, IntPtr l);

    public static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    public static string Title(IntPtr h)
    {
        int n = GetWindowTextLength(h);
        if (n <= 0) return "";
        var sb = new StringBuilder(n + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Windows hidden by the system (other virtual desktops, suspended store apps) look visible but aren't.</summary>
    public static bool IsCloaked(IntPtr h) =>
        DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int c, sizeof(int)) == 0 && c != 0;

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint flags);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO gti);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
}

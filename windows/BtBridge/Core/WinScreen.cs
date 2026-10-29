using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Forms = System.Windows.Forms;

namespace BtBridge.Core;

/// <summary>Screen capture with GDI (StretchBlt straight into a scaled 32-bit DIB, cursor drawn in).</summary>
public sealed class GdiGrabber : IScreenGrabber
{
    private readonly object _gate = new();
    private IntPtr _memDc, _dib, _bits;
    private int _dw, _dh;

    public IReadOnlyList<MonitorInfo> Monitors()
    {
        var screens = Forms.Screen.AllScreens;
        var list = new List<MonitorInfo>();
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            list.Add(new MonitorInfo(i, $"מסך {i + 1}" + (screens[i].Primary ? " (ראשי)" : ""), b.X, b.Y, b.Width, b.Height, screens[i].Primary));
        }
        return list;
    }

    public int[]? Grab(int monitor, int w, int h)
    {
        var mons = Monitors();
        var m = mons[Math.Min(monitor, mons.Count - 1)];
        lock (_gate)
        {
            var screen = GetDC(IntPtr.Zero);
            try
            {
                Ensure(screen, w, h);
                var old = SelectObject(_memDc, _dib);
                SetStretchBltMode(_memDc, HALFTONE);
                SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
                bool ok = StretchBlt(_memDc, 0, 0, w, h, screen, m.X, m.Y, m.W, m.H, SRCCOPY);
                if (ok) DrawCursor(m, w, h);
                SelectObject(_memDc, old);
                if (!ok) return null;
                var px = new int[w * h];
                Marshal.Copy(_bits, px, 0, px.Length);
                return px;
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }
    }

    private void DrawCursor(MonitorInfo m, int w, int h)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0) return;
        if (ci.ptScreenPos.X < m.X || ci.ptScreenPos.Y < m.Y || ci.ptScreenPos.X >= m.X + m.W || ci.ptScreenPos.Y >= m.Y + m.H) return;
        if (!GetIconInfo(ci.hCursor, out var ii)) return;
        try
        {
            double k = (double)w / m.W;
            int size = Math.Max(12, (int)(32 * Math.Max(k, 0.6)));
            int x = (int)((ci.ptScreenPos.X - m.X - ii.xHotspot) * k);
            int y = (int)((ci.ptScreenPos.Y - m.Y - ii.yHotspot) * k);
            DrawIconEx(_memDc, x, y, ci.hCursor, size, size, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
        }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
    }

    private void Ensure(IntPtr screen, int w, int h)
    {
        if (_memDc != IntPtr.Zero && _dw == w && _dh == h) return;
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        if (_memDc == IntPtr.Zero) _memDc = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = CreateDIBSection(screen, ref bi, 0, out _bits, IntPtr.Zero, 0);
        _dw = w;
        _dh = h;
    }

    private const int SRCCOPY = 0x00CC0020, HALFTONE = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO ii);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
}

/// <summary>JPEG tiles with System.Drawing.</summary>
public sealed class GdiTileCodec : ITileCodec
{
    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public byte[] Encode(int[] px, int stride, int x, int y, int w, int h, int quality)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int r = 0; r < h; r++)
                Marshal.Copy(px, (y + r) * stride + x, data.Scan0 + r * data.Stride, w);
        }
        finally { bmp.UnlockBits(data); }
        using var ms = new MemoryStream();
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        bmp.Save(ms, Jpeg, ep);
        return ms.ToArray();
    }

    public int[] Decode(byte[] jpeg, out int w, out int h)
    {
        using var ms = new MemoryStream(jpeg);
        using var src = new Bitmap(ms);
        w = src.Width;
        h = src.Height;
        var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var px = new int[w * h];
            for (int r = 0; r < h; r++) Marshal.Copy(data.Scan0 + r * data.Stride, px, r * w, w);
            return px;
        }
        finally { src.UnlockBits(data); }
    }
}

/// <summary>Mouse and keyboard with SendInput. With restoreCursor the pointer jumps back after each action.</summary>
public sealed class WinInput : IInputSink
{
    private bool _dragging;
    private POINT _saved;

    public void Input(JsonObject ev, MonitorInfo mon, int x, int y, bool restoreCursor)
    {
        var t = ev["t"]?.GetValue<string>();
        int b = ev["b"]?.GetValue<int>() ?? 0;
        switch (t)
        {
            case "move":
                if (!restoreCursor || _dragging) Move(x, y);
                break;
            case "down":
                if (restoreCursor) GetCursorPos(out _saved);
                Move(x, y);
                Mouse(Button(b, down: true));
                _dragging = true;
                break;
            case "up":
                Move(x, y);
                Mouse(Button(b, down: false));
                _dragging = false;
                if (restoreCursor) SetCursorPos(_saved.X, _saved.Y);
                break;
            case "click":
            {
                if (restoreCursor) GetCursorPos(out _saved);
                Move(x, y);
                int n = Math.Clamp(ev["n"]?.GetValue<int>() ?? 1, 1, 3);
                for (int i = 0; i < n; i++)
                {
                    Mouse(Button(b, down: true));
                    Mouse(Button(b, down: false));
                }
                if (restoreCursor) { Thread.Sleep(30); SetCursorPos(_saved.X, _saved.Y); }
                break;
            }
            case "wheel":
            {
                if (restoreCursor) GetCursorPos(out _saved);
                Move(x, y);
                int dy = (int)Math.Round((ev["dy"]?.GetValue<double>() ?? 0) * 120);
                Send(new INPUT { type = 0, mi = new MOUSEINPUT { mouseData = dy, dwFlags = 0x0800 /* WHEEL */ } });
                if (restoreCursor) SetCursorPos(_saved.X, _saved.Y);
                break;
            }
            case "text":
                foreach (var ch in ev["s"]?.GetValue<string>() ?? "")
                {
                    if (ch == '\n') { Key(0x0D, true); Key(0x0D, false); continue; }
                    Send(new INPUT { type = 1, ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 /* UNICODE */ } },
                         new INPUT { type = 1, ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 | 0x0002 } });
                }
                break;
            case "key":
            {
                var vk = Vk(ev["k"]?.GetValue<string>() ?? "");
                var mods = ev["mods"]?.AsArray().Select(m => Vk(m!.GetValue<string>())).Where(v => v != 0).ToList() ?? new List<ushort>();
                foreach (var m in mods) Key(m, true);
                if (vk != 0) { Key(vk, true); Key(vk, false); }
                for (int i = mods.Count - 1; i >= 0; i--) Key(mods[i], false);
                break;
            }
        }
    }

    private static ushort Vk(string name) => name.ToLowerInvariant() switch
    {
        "enter" => 0x0D, "esc" => 0x1B, "tab" => 0x09, "back" => 0x08, "del" => 0x2E, "space" => 0x20,
        "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
        "home" => 0x24, "end" => 0x23, "pgup" => 0x21, "pgdn" => 0x22, "ins" => 0x2D,
        "win" => 0x5B, "ctrl" => 0x11, "alt" => 0x12, "shift" => 0x10, "menu" => 0x5D, "prtsc" => 0x2C,
        var f when f.Length >= 2 && f[0] == 'f' && int.TryParse(f[1..], out var n) && n is >= 1 and <= 12 => (ushort)(0x70 + n - 1),
        var c when c.Length == 1 && char.IsAsciiLetterOrDigit(c[0]) => char.ToUpperInvariant(c[0]),
        _ => 0,
    };

    private static uint Button(int b, bool down) => (b, down) switch
    {
        (1, true) => 0x0008, (1, false) => 0x0010,
        (2, true) => 0x0020, (2, false) => 0x0040,
        (_, true) => 0x0002, (_, false) => 0x0004,
    };

    private static void Move(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        int nx = (int)((x - vx) * 65535L / Math.Max(1, vw - 1));
        int ny = (int)((y - vy) * 65535L / Math.Max(1, vh - 1));
        Send(new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = 0x0001 | 0x8000 | 0x4000 /* MOVE|ABSOLUTE|VIRTUALDESK */ } });
    }

    private static void Mouse(uint flags) => Send(new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } });

    private static void Key(ushort vk, bool down)
    {
        uint flags = down ? 0u : 0x0002u;
        if (vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x24 or 0x23 or 0x21 or 0x22 or 0x5B) flags |= 0x0001; // extended
        Send(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } });
    }

    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace BtBridge;

/// <summary>Draws the app icon at runtime (two arrows on a blue tile).</summary>
public static class AppIcon
{
    /// <summary>Writes app.ico (PNG entries 16-256) for the .exe icon. Run once from the build tooling.</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        // 256 as PNG; the small sizes as classic 32-bit DIBs, which every Windows component reads.
        var pngs = sizes.Select(s =>
        {
            using var bmp = Draw(s);
            using var ms = new MemoryStream();
            if (s >= 256)
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            using var bw = new BinaryWriter(ms);
            bw.Write(40); bw.Write(s); bw.Write(s * 2); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = s - 1; y >= 0; y--)
                for (int x = 0; x < s; x++) bw.Write(bmp.GetPixel(x, y).ToArgb());
            int maskRow = ((s + 31) / 32) * 4;
            bw.Write(new byte[maskRow * s]);
            bw.Flush();
            return ms.ToArray();
        }).ToList();
        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float r = size * 0.22f;
        using var path = new GraphicsPath();
        path.AddArc(0, 0, r * 2, r * 2, 180, 90);
        path.AddArc(size - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
        path.AddArc(size - r * 2 - 1, size - r * 2 - 1, r * 2, r * 2, 0, 90);
        path.AddArc(0, size - r * 2 - 1, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, size, size), Color.FromArgb(37, 99, 235), Color.FromArgb(14, 165, 233), 45f))
            g.FillPath(bg, path);

        using var pen = new Pen(Color.White, size * 0.09f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float a = size * 0.22f, b = size * 0.78f, y1 = size * 0.38f, y2 = size * 0.62f, h = size * 0.12f;
        g.DrawLine(pen, a, y1, b, y1);
        g.DrawLine(pen, b - h, y1 - h, b, y1);
        g.DrawLine(pen, b - h, y1 + h, b, y1);
        g.DrawLine(pen, b, y2, a, y2);
        g.DrawLine(pen, a + h, y2 - h, a, y2);
        g.DrawLine(pen, a + h, y2 + h, a, y2);
        return bmp;
    }

    public static Icon Tray()
    {
        using var bmp = Draw(32);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static BitmapSource Window()
    {
        using var bmp = Draw(64);
        var h = bmp.GetHbitmap();
        try { return Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
        finally { DeleteObject(h); }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);
}

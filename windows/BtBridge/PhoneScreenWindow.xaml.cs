using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BtBridge.Core;

namespace BtBridge;

/// <summary>
/// Shows the phone's screen and controls it: click = tap, hold = long press, drag = swipe,
/// right click = back, wheel = scroll, typing goes to the focused field on the phone.
/// </summary>
public partial class PhoneScreenWindow : Window
{
    private readonly Session _s;
    private readonly ScreenClient _sc;
    private readonly GdiTileCodec _codec = new();
    private WriteableBitmap? _bmp;
    private Point _downAt;
    private long _downTime;
    private int _frames;
    private long _bytes;
    private bool _closed;

    public PhoneScreenWindow(Session s, string name)
    {
        InitializeComponent();
        Icon = AppIcon.Window();
        _s = s;
        _sc = new ScreenClient(s);
        Title = "מסך הטלפון · " + name;
        WaitText.Text = "מבקש מהטלפון לשתף את המסך…\nאם זו הפעם הראשונה מאז שהאפליקציה נפתחה, אשר בטלפון.";
        _sc.Frame += OnFrame;
        _sc.Closed += why => Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            WaitText.Text = "השיתוף הסתיים: " + why;
            WaitText.Visibility = Visibility.Visible;
        });
        Loaded += async (_, _) => await Open();
        Closed += (_, _) => { _closed = true; _sc.Close(); };

        var stats = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        stats.Tick += (_, _) =>
        {
            StatusText.Text = $"{_frames} תמונות/ש' · {Transfer.FormatSize(_bytes)}/ש' · {_s.LinkText}" +
                              "  ·  לחיצה=הקשה, החזקה=לחיצה ארוכה, גרירה=החלקה, ימני=חזור, גלגלת=גלילה, הקלדה עוברת לטלפון";
            _frames = 0;
            _bytes = 0;
        };
        stats.Start();
    }

    private async Task Open()
    {
        try
        {
            await App.Core.EnsureFastLinkAsync(_s);
            await _sc.OpenAsync(0, "auto", false);
            WaitText.Text = "מתחבר למסך…";
            if (!_sc.InputAvailable)
                MessageBox.Show(this, "אפשר לראות את מסך הטלפון, אבל לא ללחוץ עליו.\nכדי לשלוט, הפעל בטלפון: הגדרות ← נגישות ← גשר בלוטוס.",
                    "שליטה בטלפון", MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK,
                    MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            Surface.Focus();
        }
        catch (Exception ex)
        {
            WaitText.Text = "השיתוף לא נפתח:\n" + Heb.Of(ex);
        }
    }

    /// <summary>Runs on the link's reader thread: decode here, draw on the UI thread.</summary>
    private void OnFrame(uint seq, int w, int h, List<ScreenTile> tiles)
    {
        var decoded = tiles.Select(t => (t, px: _codec.Decode(t.Jpeg, out var tw, out var th), tw, th)).ToList();
        _bytes += tiles.Sum(t => t.Jpeg.Length);
        Dispatcher.Invoke(() =>
        {
            if (_bmp == null || _bmp.PixelWidth != w || _bmp.PixelHeight != h)
            {
                _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                Screen.Source = _bmp;
            }
            foreach (var (t, px, tw, th) in decoded)
            {
                int cw = Math.Min(tw, w - t.X), ch = Math.Min(th, h - t.Y);
                if (cw <= 0 || ch <= 0) continue;
                _bmp.WritePixels(new Int32Rect(0, 0, cw, ch), px, tw * 4, t.X, t.Y);
            }
            WaitText.Visibility = Visibility.Collapsed;
            _frames++;
        });
    }

    // ---------------------------------------------------------------- input

    private Point ToFrame(MouseEventArgs e)
    {
        var p = e.GetPosition(Screen);
        if (_bmp == null || Screen.ActualWidth <= 0) return p;
        return new Point(p.X * _bmp.PixelWidth / Screen.ActualWidth, p.Y * _bmp.PixelHeight / Screen.ActualHeight);
    }

    private void Screen_Down(object sender, MouseButtonEventArgs e)
    {
        _downAt = ToFrame(e);
        _downTime = Environment.TickCount64;
        Screen.CaptureMouse();
        Surface.Focus();
    }

    private void Screen_Up(object sender, MouseButtonEventArgs e)
    {
        Screen.ReleaseMouseCapture();
        var up = ToFrame(e);
        long ms = Environment.TickCount64 - _downTime;
        double dist = (up - _downAt).Length;
        double slop = (_bmp?.PixelWidth ?? 1000) / 60.0;
        if (dist < slop)
            _sc.Input(ms >= 550 ? "long" : "tap", _downAt.X, _downAt.Y);
        else
            _sc.Input("swipe", _downAt.X, _downAt.Y, o => { o["x2"] = up.X; o["y2"] = up.Y; o["ms"] = Math.Clamp(ms, 80, 2000); });
    }

    private void Screen_Right(object sender, MouseButtonEventArgs e) => Global("back");

    private void Screen_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_bmp == null) return;
        var p = ToFrame(e);
        double d = _bmp.PixelHeight * 0.25 * Math.Sign(e.Delta);
        // Wheel up = content up = finger moves down.
        _sc.Input("swipe", p.X, p.Y, o => { o["x2"] = p.X; o["y2"] = Math.Clamp(p.Y + d, 0, _bmp.PixelHeight - 1); o["ms"] = 250; });
    }

    private void Window_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text == "\r" || e.Text == "\b") return;
        _sc.Input("text", extra: o => o["s"] = e.Text);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: _sc.Input("key", extra: o => o["k"] = "Enter"); e.Handled = true; break;
            case Key.Back: _sc.Input("key", extra: o => o["k"] = "Back"); e.Handled = true; break;
            case Key.Escape: Global("back"); e.Handled = true; break;
        }
    }

    private void Global(string a) => _sc.Input("global", extra: o => o["a"] = a);
    private void Back_Click(object sender, RoutedEventArgs e) => Global("back");
    private void Home_Click(object sender, RoutedEventArgs e) => Global("home");
    private void Recents_Click(object sender, RoutedEventArgs e) => Global("recents");
    private void Notifications_Click(object sender, RoutedEventArgs e) => Global("notifications");
    private void Quick_Click(object sender, RoutedEventArgs e) => Global("quick");
    private void Lock_Click(object sender, RoutedEventArgs e) => Global("lock");

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Also fires while the window loads, before the stream exists.
        if (ProfileBox.SelectedItem is ComboBoxItem { Tag: string p } && _sc is { Id: not 0 }) _sc.Configure(profile: p, key: true);
    }
}

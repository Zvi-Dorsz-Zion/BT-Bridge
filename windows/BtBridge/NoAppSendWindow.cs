using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BtBridge.Core;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace BtBridge;

/// <summary>
/// Sending to a device that doesn't have this program: a link to open in its browser (Wi-Fi, no internet needed),
/// or the plain "send over Bluetooth" every phone understands. Separate from the connection to devices with the app.
/// </summary>
public sealed class NoAppSendWindow : Window
{
    private readonly List<WebShareItem> _files = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _web = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _bt = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };

    private readonly ComboBox _ips = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBox _url = new() { IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight };
    private readonly Image _qr = new() { Width = 220, Height = 220, Margin = new Thickness(0, 10, 0, 6), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly CheckBox _allowUp = new() { Content = "לאפשר גם לקבל קבצים מהמכשיר (נשמרים בתיקיית ההורדות)", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _webStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    private readonly ListBox _devices = new() { Height = 110, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Button _sendBt = new() { Content = "שלח", MinWidth = 90 };
    private readonly Button _cancelBt = new() { Content = "בטל", MinWidth = 90, IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ProgressBar _bar = new() { Height = 12, Margin = new Thickness(0, 8, 0, 0), Maximum = 100 };
    private readonly TextBlock _btStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    private WebShare? _share;
    private CancellationTokenSource? _btCancel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public NoAppSendWindow()
    {
        Title = "שליחה למכשיר בלי התוכנה";
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcon.Window();

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(_summary);
        var pick = new StackPanel { Orientation = Orientation.Horizontal };
        var pickFiles = new Button { Content = "בחר קבצים…" };
        pickFiles.Click += (_, _) => PickFiles();
        var pickFolder = new Button { Content = "בחר תיקייה…", Margin = new Thickness(8, 0, 0, 0) };
        pickFolder.Click += (_, _) => PickFolder();
        pick.Children.Add(pickFiles);
        pick.Children.Add(pickFolder);
        root.Children.Add(pick);

        var how = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var byWeb = new Button { Content = "דרך הוויפי (קישור לדפדפן)", Style = (Style)Application.Current.Resources["Primary"] };
        byWeb.Click += (_, _) => ShowWeb();
        var byBt = new Button { Content = "דרך הבלוטוס", Margin = new Thickness(8, 0, 0, 0) };
        byBt.Click += async (_, _) => await ShowBt();
        how.Children.Add(byWeb);
        how.Children.Add(byBt);
        root.Children.Add(how);

        BuildWeb();
        BuildBt();
        root.Children.Add(_web);
        root.Children.Add(_bt);
        Content = root;

        ShowSummary();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _btCancel?.Cancel();
            StopShare();
        };
    }

    private static TextBlock Note(string s) => new()
    {
        Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 4, 0, 0),
    };

    // ---------------------------------------------------------------- choosing files

    private void ShowSummary() =>
        _summary.Text = _files.Count == 0 ? "בחר מה לשלוח."
            : $"נבחרו {_files.Count} קבצים, {Transfer.FormatSize(_files.Sum(f => f.Size))}.";

    private void PickFiles()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "בחר קבצים לשליחה" };
        if (d.ShowDialog(this) != true) return;
        foreach (var f in d.FileNames) AddFile(f, Path.GetFileName(f));
        FilesChanged();
    }

    private async void PickFolder()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "בחר תיקייה לשליחה" };
        if (d.ShowDialog(this) != true) return;
        var root = d.FolderName;
        var parent = Path.GetDirectoryName(root.TrimEnd('\\')) ?? root;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = true };
        var all = await Task.Run(() => Directory.EnumerateFiles(root, "*", options).ToList());
        foreach (var f in all) AddFile(f, Path.GetRelativePath(parent, f).Replace('\\', '/'));
        FilesChanged();
    }

    private void AddFile(string path, string name)
    {
        if (_files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        try { _files.Add(new WebShareItem(path, name, new FileInfo(path).Length)); }
        catch (Exception ex) { Log.Write("no-app send: " + ex.Message); }
    }

    private void FilesChanged()
    {
        ShowSummary();
        // The link lists the chosen files when it opens: a new choice makes a new link.
        if (_web.Visibility == Visibility.Visible) StartShare();
    }

    // ---------------------------------------------------------------- Wi-Fi: a link for the browser

    private void BuildWeb()
    {
        _web.Children.Add(new TextBlock
        {
            Text = "פתח את הכתובת בדפדפן של המכשיר השני, או סרוק את הקוד. שני המכשירים צריכים להיות באותה רשת. אין צורך באינטרנט.",
            TextWrapping = TextWrapping.Wrap,
        });
        _ips.SelectionChanged += (_, _) => ShowUrl();
        _web.Children.Add(_ips);
        var row = new DockPanel();
        var copy = new Button { Content = "העתק", Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => { if (_url.Text.Length > 0) Clipboard.SetText(_url.Text); };
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(copy);
        row.Children.Add(_url);
        _web.Children.Add(row);
        RenderOptions.SetBitmapScalingMode(_qr, BitmapScalingMode.NearestNeighbor);
        _web.Children.Add(_qr);
        _allowUp.Checked += (_, _) => { if (_share != null) _share.AllowUpload = true; };
        _allowUp.Unchecked += (_, _) => { if (_share != null) _share.AllowUpload = false; };
        _web.Children.Add(_allowUp);
        _web.Children.Add(_webStatus);
        _web.Children.Add(Note("החיבור הזה לא מוצפן: השתמש בו רק ברשת שאתה סומך עליה. הקישור נסגר כשסוגרים את החלון, או אחרי 15 דקות בלי שימוש."));
    }

    private void ShowWeb()
    {
        if (_files.Count == 0) { _summary.Text = "קודם צריך לבחור קבצים."; return; }
        _bt.Visibility = Visibility.Collapsed;
        _web.Visibility = Visibility.Visible;
        StartShare();
    }

    private void StartShare()
    {
        StopShare();
        try
        {
            _share = new WebShare(_files, App.Core.Settings.DownloadDir) { AllowUpload = _allowUp.IsChecked == true };
            _share.Uploaded += path => Dispatcher.BeginInvoke(() => ((App)Application.Current).Balloon("התקבל קובץ: " + Path.GetFileName(path)));
            _share.IdleStopped += () => Dispatcher.BeginInvoke(() =>
            {
                StopShare();
                _url.Text = "";
                _qr.Source = null;
                _webStatus.Text = "הקישור נסגר אחרי 15 דקות בלי שימוש. כדי לפתוח אותו שוב, לחץ על \"דרך הוויפי\".";
            });
            _share.Start();
            _webStatus.Text = "";
            RefreshIps(force: true);
        }
        catch (Exception ex)
        {
            Log.Write("no-app send, server: " + ex);
            _webStatus.Text = "הקישור לא נפתח: " + Heb.Of(ex);
        }
    }

    private void StopShare()
    {
        _share?.Dispose();
        _share = null;
    }

    private void RefreshIps(bool force = false)
    {
        List<string> now;
        try { now = WifiLinks.LocalIps(); }
        catch (Exception ex) { Log.Write("no-app send, addresses: " + ex.Message); return; }
        var had = _ips.Items.Cast<string>().ToList();
        if (!force && had.SequenceEqual(now)) return;
        var keep = _ips.SelectedItem as string;
        _ips.Items.Clear();
        foreach (var ip in now) _ips.Items.Add(ip);
        _ips.SelectedItem = keep != null && now.Contains(keep) ? keep : now.FirstOrDefault();
        _ips.Visibility = now.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowUrl();
    }

    private void ShowUrl()
    {
        if (_share == null) return;
        if (_ips.SelectedItem is not string ip)
        {
            _url.Text = "";
            _qr.Source = null;
            _webStatus.Text = "המחשב לא מחובר עכשיו לשום רשת. התחבר לרשת, או שלח דרך הבלוטוס.";
            return;
        }
        var url = _share.UrlFor(ip);
        if (url == _url.Text) return;
        _url.Text = url;
        _qr.Source = MakeQr(url);
    }

    private static BitmapSource MakeQr(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var m = data.ModuleMatrix;
        int size = m.Count;
        var px = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = m[y][x] ? (byte)0 : (byte)255;
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, px, size);
        bmp.Freeze();
        return bmp;
    }

    private void Tick()
    {
        if (_web.Visibility != Visibility.Visible || _share == null) return;
        RefreshIps();
        if (_share.SentBytes > 0 && _ips.SelectedItem != null) _webStatus.Text = "נשלחו עד עכשיו: " + Transfer.FormatSize(_share.SentBytes);
    }

    // ---------------------------------------------------------------- Bluetooth

    private void BuildBt()
    {
        _bt.Children.Add(new TextBlock { Text = "בחר מכשיר מצומד:", TextWrapping = TextWrapping.Wrap });
        _bt.Children.Add(_devices);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _sendBt.Click += async (_, _) => await SendBluetooth();
        _cancelBt.Click += (_, _) => _btCancel?.Cancel();
        row.Children.Add(_sendBt);
        row.Children.Add(_cancelBt);
        _bt.Children.Add(row);
        _bt.Children.Add(_bar);
        _bt.Children.Add(_btStatus);
        _bt.Children.Add(Note("המכשיר השני מתבקש לאשר כל קובץ. השליחה איטית יותר מוויפי. מחשב ווינדוס צריך קודם לפתוח \"קבלת קובץ בבלוטוס\". אייפון לא יכול לקבל כך."));
    }

    private async Task ShowBt()
    {
        if (_files.Count == 0) { _summary.Text = "קודם צריך לבחור קבצים."; return; }
        _web.Visibility = Visibility.Collapsed;
        StopShare();
        _bt.Visibility = Visibility.Visible;
        _btStatus.Text = "";
        try
        {
            var paired = await Task.Run(() =>
            {
                using var c = new BluetoothClient();
                return c.PairedDevices.Select(d => (d.DeviceName, d.DeviceAddress)).ToList();
            });
            _devices.Items.Clear();
            foreach (var (name, addr) in paired) _devices.Items.Add(new ListBoxItem { Content = name, Tag = addr });
            if (paired.Count == 0) _btStatus.Text = "אין מכשירים מצומדים. צמד מכשיר בהגדרות הבלוטוס של ווינדוס.";
            else _devices.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            Log.Write("no-app send, paired: " + ex.Message);
            _btStatus.Text = "הבלוטוס לא זמין: " + Heb.Of(ex);
        }
    }

    private async Task SendBluetooth()
    {
        if ((_devices.SelectedItem as ListBoxItem)?.Tag is not BluetoothAddress address) { _btStatus.Text = "בחר מכשיר."; return; }
        if (_files.Count == 0) return;
        var cts = _btCancel = new CancellationTokenSource();
        var ct = cts.Token;
        var items = _files.ToList();
        long total = Math.Max(1, items.Sum(f => f.Size));
        long done = 0;
        _sendBt.IsEnabled = false;
        _cancelBt.IsEnabled = true;
        _bar.Value = 0;
        try
        {
            await Task.Run(() =>
            {
                foreach (var f in items)
                {
                    ct.ThrowIfCancellationRequested();
                    Dispatcher.BeginInvoke(() => _btStatus.Text = $"שולח את {Path.GetFileName(f.Name)}. ייתכן שצריך לאשר במכשיר השני.");
                    using var c = new BluetoothClient();
                    // Closing is the only way to stop a device that is waiting for someone to approve.
                    using var stop = ct.Register(() => { try { c.Close(); } catch { } });
                    c.Connect(address, BluetoothService.ObexObjectPush);
                    using var fs = File.OpenRead(f.Path);
                    ObexSender.Send(c.GetStream(), Path.GetFileName(f.Name), f.Size, fs, n =>
                    {
                        long now = Interlocked.Add(ref done, n);
                        Dispatcher.BeginInvoke(() => _bar.Value = now * 100.0 / total);
                    }, ct);
                }
            }, ct);
            _bar.Value = 100;
            _btStatus.Text = items.Count == 1 ? "הקובץ נשלח." : "כל הקבצים נשלחו.";
        }
        catch (Exception) when (ct.IsCancellationRequested) { _btStatus.Text = "השליחה בוטלה."; }
        catch (SocketException) { _btStatus.Text = "המכשיר לא קיבל את החיבור. ודא שהוא דולק ובטווח. אם זה מחשב ווינדוס, פתח בו קודם \"קבלת קובץ בבלוטוס\"."; }
        catch (Exception ex)
        {
            Log.Write("no-app send, bluetooth: " + ex.Message);
            _btStatus.Text = "השליחה נכשלה: " + Heb.Of(ex);
        }
        finally
        {
            _sendBt.IsEnabled = true;
            _cancelBt.IsEnabled = false;
            _btCancel = null;
            cts.Dispose();
        }
    }
}

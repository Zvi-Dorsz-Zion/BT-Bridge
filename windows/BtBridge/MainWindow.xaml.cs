using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BtBridge.Core;
using InTheHand.Net;
using InTheHand.Net.Sockets;

namespace BtBridge;

public sealed class DeviceHeader
{
    public string Title { get; init; } = "";
    public string Action { get; init; } = "";
    public Visibility ActionVisibility => Action.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsHeader => true;
}

public sealed class DeviceItem
{
    public string Key { get; init; } = "";
    public string Fp { get; init; } = "";
    public string Name { get; init; } = "";
    public BluetoothAddress? Address { get; init; }
    public Session? Session { get; init; }
    public bool Trusted { get; init; }
    public bool Connecting { get; init; }
    public bool IsHeader => false;

    public string Sub =>
        Session != null ? "מחובר · " + Session.LinkText
        : Connecting ? "מתחבר…"
        : Trusted ? "מוכר · לא מחובר"
        : "מזווג · לחיצה כפולה מתחברת";

    public Brush SubBrush => Session != null ? Brushes.SeaGreen : (Brush)Application.Current.Resources["Muted"];
}

public partial class MainWindow : Window
{
    private readonly Bridge _b = App.Core;
    private readonly ObservableCollection<RemoteItem> _items = new();
    private readonly ObservableCollection<Transfer> _transfers = new();
    private readonly SemaphoreSlim _queue = new(1);
    private readonly Stack<string> _history = new();
    private List<(string Key, string Name, BluetoothAddress Address)> _paired = new();
    private Session? _current;
    private string _path = "";
    private string? _parent;
    private bool _trayHintShown, _showOthers;
    private ControlBanner? _banner;

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Window();
        FileList.ItemsSource = _items;
        TransferList.ItemsSource = _transfers;

        _b.AskPassword = (peer, wrong) => Dispatcher.Invoke(() =>
        {
            var d = new PasswordWindow(peer, wrong) { Owner = IsVisible ? this : null };
            return d.ShowDialog() == true ? d.Answer : null;
        });
        _b.SessionsChanged += () => Dispatcher.BeginInvoke(RenderDevices);
        _b.Received += (s, path) => Dispatcher.BeginInvoke(() =>
        {
            _transfers.Insert(0, Transfer.Finished(Path.GetFileName(path), path, "התקבל מ-" + _b.DisplayName(s)));
            ((App)Application.Current).Balloon($"התקבל {Path.GetFileName(path)} מ-{_b.DisplayName(s)}");
        });
        ScreenSource.ActiveChanged += (src, on) => Dispatcher.BeginInvoke(() => UpdateBanner(src));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => { foreach (var t in _transfers) t.Tick(); };
        timer.Start();

        Loaded += async (_, _) => await LoadPaired();
        Closing += OnClosing;
        Drop += (_, e) => FileList_Drop(this, e);
        RenderDevices();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (app.Exiting) return;
        e.Cancel = true;
        if (_b.Settings.CloseToTray)
        {
            Hide();
            if (!_trayHintShown) app.Balloon("גשר בלוטוס ממשיך לפעול ליד השעון ומוכן לחיבורים.");
            _trayHintShown = true;
        }
        else app.ExitApp();
    }

    public static void Err(Exception ex, string? prefix = null)
    {
        var msg = ex switch
        {
            System.Net.Sockets.SocketException => "לא הצלחתי להתחבר. ודא שגשר בלוטוס פתוח במכשיר השני ושהבלוטוס דלוק.",
            _ => Heb.Of(ex),
        };
        MessageBox.Show((prefix != null ? prefix + "\n\n" : "") + msg, "גשר בלוטוס", MessageBoxButton.OK, MessageBoxImage.Warning,
            MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
    }

    private void UpdateBanner(ScreenSource src)
    {
        if (ScreenSource.Count > 0)
        {
            _banner ??= new ControlBanner(() => ScreenSource.StopAll());
            _banner.SetText($"📱 {_b.DisplayName(src.Session)} צופה במסך ושולט בו");
            _banner.Show();
        }
        else
        {
            _banner?.Close();
            _banner = null;
        }
    }

    // ---------------------------------------------------------------- devices

    private async Task LoadPaired()
    {
        try
        {
            _paired = await Task.Run(() =>
            {
                using var c = new BluetoothClient();
                return c.PairedDevices.Select(d => (Bridge.KeyOf(d.DeviceAddress), d.DeviceName, d.DeviceAddress)).ToList();
            });
        }
        catch (Exception ex) { Log.Write("paired: " + ex.Message); }
        RenderDevices();
    }

    private void RenderDevices()
    {
        ListenText.Text = _b.ListenStatus;
        var st = _b.Settings;
        var mine = new List<DeviceItem>();
        var seen = new HashSet<string>();

        foreach (var s in _b.Sessions)
        {
            var addr = _paired.FirstOrDefault(p => p.Key == s.PeerAddress).Address;
            mine.Add(new DeviceItem { Key = s.PeerAddress.Length > 0 ? s.PeerAddress : s.PeerFp, Fp = s.PeerFp, Name = _b.DisplayName(s), Address = addr, Session = s, Trusted = true });
            seen.Add(s.PeerAddress);
        }
        foreach (var t in st.Trusted.Where(t => !seen.Contains(t.Address)))
        {
            var addr = _paired.FirstOrDefault(p => p.Key == t.Address).Address;
            if (addr == null && t.Address.Length == 12 && BluetoothAddress.TryParse(t.Address, out var parsed)) addr = parsed;
            mine.Add(new DeviceItem { Key = t.Address, Fp = t.Fp, Name = st.NameFor(t.Fp, t.Name), Address = addr, Trusted = true, Connecting = _b.IsConnecting(t.Address) });
            seen.Add(t.Address);
        }
        var others = _paired.Where(p => !seen.Contains(p.Key))
            .Select(p => new DeviceItem { Key = p.Key, Name = st.NameFor(p.Key, p.Name), Address = p.Address, Connecting = _b.IsConnecting(p.Key) })
            .ToList();

        var list = new List<object> { new DeviceHeader { Title = mine.Count > 0 ? "המכשירים שלי" : "עוד לא התחברת לאף מכשיר" } };
        list.AddRange(mine);
        if (others.Count > 0)
        {
            bool show = _showOthers || mine.Count == 0;
            list.Add(new DeviceHeader { Title = $"מזווגים נוספים ({others.Count})", Action = mine.Count == 0 ? "" : show ? "הסתר" : "הצג" });
            if (show) list.AddRange(others);
        }

        var selectedKey = Selected?.Key;
        DeviceList.ItemsSource = list;
        DeviceList.SelectedItem = list.OfType<DeviceItem>().FirstOrDefault(d => d.Key == selectedKey)
            ?? (selectedKey == null ? mine.FirstOrDefault(d => d.Session != null) : null);
        ShowSelected();
    }

    private void HeaderAction_Click(object sender, RoutedEventArgs e)
    {
        _showOthers = !_showOthers;
        RenderDevices();
    }

    private DeviceItem? Selected => DeviceList.SelectedItem as DeviceItem;

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceHeader)
        {
            DeviceList.SelectedItem = e.RemovedItems.OfType<DeviceItem>().FirstOrDefault();
            return;
        }
        ShowSelected();
    }

    private void DeviceList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var d = Selected;
        if (d == null) { e.Handled = true; return; }
        MenuConnect.IsEnabled = d is { Session: null, Address: not null, Connecting: false };
        MenuDisconnect.IsEnabled = d.Session != null;
        MenuForget.IsEnabled = d.Trusted;
    }

    private void ShowSelected()
    {
        var d = Selected;
        ConnectBtn.IsEnabled = d is { Session: null, Connecting: false, Address: not null };
        DisconnectBtn.IsEnabled = d?.Session != null;
        Toolbar.IsEnabled = PathBox.IsEnabled = SessionActions.IsEnabled = d?.Session != null;
        SessionActions.Visibility = d?.Session != null ? Visibility.Visible : Visibility.Collapsed;

        if (d?.Session == null)
        {
            _current = null;
            _items.Clear();
            PathBox.Text = "";
            HeaderText.Text = d?.Name ?? "";
            HeaderSub.Text = "";
            Placeholder.Text = d == null
                ? "בחר מכשיר מהרשימה.\nאם הטלפון לא מופיע, לחץ \"חפש וצמד מכשיר חדש\"."
                : d.Connecting ? "מתחבר…" : "לא מחובר.\nלחץ \"התחבר\", או לחיצה כפולה על המכשיר.";
            Placeholder.Visibility = Visibility.Visible;
            return;
        }

        HeaderText.Text = d.Name;
        HeaderSub.Text = d.Session.LinkText + (_b.Direct.Joined ? " · רשת ישירה" : "");
        SessionActions.Children[0].Visibility = d.Session.PeerOs == "android" ? Visibility.Visible : Visibility.Collapsed;
        if (_current != d.Session)
        {
            _current = d.Session;
            _history.Clear();
            _ = Navigate("", remember: false);
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectSelected();

    private async void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is { Session: null }) await ConnectSelected();
    }

    private async Task ConnectSelected()
    {
        var d = Selected;
        if (d?.Address == null || d.Session != null || _b.IsConnecting(d.Key)) return;
        try { await _b.ConnectAsync(d.Address); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Err(ex, $"החיבור ל-\"{d.Name}\" נכשל."); }
        RenderDevices();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => Selected?.Session?.Close();

    private void Rename_Device_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } d) return;
        var key = d.Fp.Length > 0 ? d.Fp : d.Key;
        var w = new InputWindow("כינוי למכשיר", "שם שיוצג אצלך (ריק = השם המקורי):", d.Name, allowEmpty: true) { Owner = this };
        if (w.ShowDialog() != true) return;
        _b.Settings.SetNickname(key, w.Value);
        RenderDevices();
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Trusted: true } d) return;
        if (MessageBox.Show($"לשכוח את \"{d.Name}\"?\nבחיבור הבא תידרש שוב סיסמה. הצימוד בבלוטוס לא משתנה.", "שכח מכשיר",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
                MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != MessageBoxResult.Yes) return;
        d.Session?.Close();
        if (d.Fp.Length > 0) _b.Settings.Forget(d.Fp);
        RenderDevices();
    }

    private async void PairNew_Click(object sender, RoutedEventArgs e)
    {
        var w = new PairWindow { Owner = this };
        w.ShowDialog();
        await LoadPaired();
        if (w.Paired is { } addr)
        {
            DeviceList.SelectedItem = (DeviceList.ItemsSource as List<object>)?.OfType<DeviceItem>().FirstOrDefault(x => x.Key == Bridge.KeyOf(addr));
            await ConnectSelected();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
        RenderDevices();
    }

    private void PhoneScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s) return;
        new PhoneScreenWindow(s, _b.DisplayName(s)).Show();
    }

    private async void SendTo_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s) return;
        var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "בחר קבצים לשליחה" };
        if (d.ShowDialog(this) == true) await SendToInbox(s, d.FileNames);
    }

    /// <summary>"Send to device": files land in the other side's downloads folder.</summary>
    private async Task SendToInbox(Session s, IEnumerable<string> paths)
    {
        var files = new List<(string Local, string Rel)>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) files.Add((p, Path.GetFileName(p)));
            else if (Directory.Exists(p))
            {
                var root = Path.GetDirectoryName(p.TrimEnd('\\')) ?? p;
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    files.Add((f, Path.GetRelativePath(root, f).Replace('\\', '/')));
            }
        }
        await MaybeFast(s, files.Sum(f => new FileInfo(f.Local).Length));
        await Task.WhenAll(files.Select(f =>
        {
            var t = new Transfer { Name = f.Rel, Upload = true, LocalPath = f.Local };
            return Queue(t, ct => RemoteFs.UploadAsync(s, f.Local, f.Rel, t, ct, inbox: true));
        }));
    }

    private Task MaybeFast(Session s, long bytes) =>
        bytes > 5 * 1024 * 1024 ? _b.EnsureFastLinkAsync(s) : Task.CompletedTask;

    // ---------------------------------------------------------------- browsing

    private async Task Navigate(string path, bool remember = true)
    {
        var s = _current;
        if (s == null) return;
        Placeholder.Text = "טוען…";
        Placeholder.Visibility = Visibility.Visible;
        try
        {
            var listing = await RemoteFs.ListAsync(s, path);
            if (s != _current) return;
            if (remember && path != _path) _history.Push(_path);
            _path = path;
            _parent = listing.Parent;
            PathBox.Text = path;
            ShowItems(listing.Items);
            Placeholder.Text = listing.Items.Count == 0 ? "התיקייה ריקה. אפשר לגרור לכאן קבצים." : "";
            Placeholder.Visibility = listing.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Placeholder.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Placeholder.Text = "";
            PathBox.Text = _path;
            Err(ex);
        }
    }

    private string SortKey => (_current?.PeerFp ?? "") + "|" + _path;

    /// <summary>The order the user chose for this folder, or the usual one (Downloads newest first, others by name).</summary>
    private (string By, bool Desc) SortFor()
    {
        if (_b.Settings.Sorts.TryGetValue(SortKey, out var v) && v.Split('|') is [var by, var dir]) return (by, dir == "desc");
        return FileOrder.Default(_path);
    }

    private void ShowItems(IEnumerable<RemoteItem> items)
    {
        var (by, desc) = SortFor();
        // Virtual root keeps the order the other side gave (quick folders, then drives).
        var list = _path.Length == 0 ? items.ToList() : FileOrder.Sort(items, by, desc);
        SortText.Text = _path.Length == 0 ? "" : FileOrder.Label(by, desc);
        _items.Clear();
        foreach (var i in list) _items.Add(i);
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader h || h.Column == null || _path.Length == 0) return;
        var by = h.Column.Header as string switch { "גודל" => "size", "תאריך שינוי" => "date", _ => "name" };
        var (curBy, curDesc) = SortFor();
        // Same column flips the direction; a new column starts the natural way (dates and sizes biggest first).
        bool desc = by == curBy ? !curDesc : by is "date" or "size";
        _b.Settings.Sorts[SortKey] = $"{by}|{(desc ? "desc" : "asc")}";
        _b.Settings.Save();
        ShowItems(_items.ToList());
    }

    /// <summary>Ctrl+C: brings the selected files to this computer and puts them on the clipboard, to paste in File Explorer.</summary>
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s) return;
        var sel = SelectedItems();
        if (sel.Count == 0) return;
        var dir = TempFiles.NewDir("העתקה");
        var jobs = new List<Task>();
        var top = new System.Collections.Specialized.StringCollection();
        try
        {
            await MaybeFast(s, sel.Any(i => i.IsDir) ? long.MaxValue : sel.Sum(i => i.Size));
            foreach (var i in sel)
            {
                if (i.IsDir) await CollectFolderDownload(s, i, dir, jobs);
                else jobs.Add(QueueDownload(s, i.Path, Path.Combine(dir, i.Name), i.Name));
                top.Add(Path.Combine(dir, i.Name));
            }
        }
        catch (Exception ex) { Err(ex); return; }
        await Task.WhenAll(jobs);
        if (jobs.OfType<Task<bool>>().Any(t => !t.Result))
        {
            MessageBox.Show("חלק מהקבצים לא הועתקו. ראה ברשימת ההעברות.", "גשר בלוטוס");
            return;
        }
        Clipboard.SetFileDropList(top);
        ((App)Application.Current).Balloon(sel.Count == 1
            ? $"\"{sel[0].Name}\" הועתק. אפשר להדביק אותו בסייר הקבצים או על שולחן העבודה."
            : $"{sel.Count} פריטים הועתקו. אפשר להדביק אותם בסייר הקבצים או על שולחן העבודה.");
    }

    private List<RemoteItem> SelectedItems() => FileList.SelectedItems.Cast<RemoteItem>().ToList();

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count > 0) await Navigate(_history.Pop(), remember: false);
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_parent != null) await Navigate(_parent);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Navigate(_path, remember: false);

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await Navigate(PathBox.Text.Trim());
    }

    private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is RemoteItem i) await OpenItem(i);
    }

    private async void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when FileList.SelectedItem is RemoteItem i: await OpenItem(i); break;
            case Key.Back: Up_Click(sender, e); break;
            case Key.F2: Rename_Click(sender, e); break;
            case Key.Delete: Delete_Click(sender, e); break;
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { e.Handled = true; await Navigate(_path, remember: false); }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !PathBox.IsKeyboardFocusWithin) { e.Handled = true; Paste_Click(sender, e); }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && FileList.IsKeyboardFocusWithin) { e.Handled = true; Copy_Click(sender, e); }
        else if (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; Back_Click(sender, e); }
    }

    private async Task OpenItem(RemoteItem i)
    {
        if (i.IsDir) { await Navigate(i.Path); return; }
        if (_current is not { } s) return;
        var dir = TempFiles.NewDir("צפייה");
        var local = Path.Combine(dir, i.Name);
        await MaybeFast(s, i.Size);
        if (await QueueDownload(s, i.Path, local, i.Name)) OpenLocal(local);
    }

    private static void OpenLocal(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Err(ex); }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is RemoteItem i) await OpenItem(i);
    }

    private void CopyRemotePath_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedItems();
        if (sel.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(i => i.Path)));
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s || !CheckInFolder()) return;
        var d = new InputWindow("תיקייה חדשה", "שם התיקייה:", "תיקייה חדשה") { Owner = this };
        if (d.ShowDialog() != true) return;
        try
        {
            await RemoteFs.MkdirAsync(s, RemoteFs.Join(s, _path, d.Value));
            await Navigate(_path, remember: false);
        }
        catch (Exception ex) { Err(ex); }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s || !CheckInFolder() || FileList.SelectedItem is not RemoteItem i) return;
        var d = new InputWindow("שינוי שם", "שם חדש:", i.Name) { Owner = this };
        if (d.ShowDialog() != true || d.Value == i.Name) return;
        try
        {
            await RemoteFs.RenameAsync(s, i.Path, RemoteFs.Join(s, _path, d.Value));
            await Navigate(_path, remember: false);
        }
        catch (Exception ex) { Err(ex); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s || !CheckInFolder()) return;
        var sel = SelectedItems();
        if (sel.Count == 0) return;
        var what = sel.Count == 1 ? $"\"{sel[0].Name}\"" : $"{sel.Count} פריטים";
        var how = s.PeerOs == "windows" ? "הם יעברו לסל המיחזור במחשב השני." : "המחיקה בטלפון היא לצמיתות, בלי סל מיחזור.";
        if (MessageBox.Show($"למחוק את {what}?\n{how}", "מחיקה", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != MessageBoxResult.Yes) return;
        try { await RemoteFs.DeleteAsync(s, sel.Select(x => x.Path)); }
        catch (Exception ex) { Err(ex); }
        await Navigate(_path, remember: false);
    }

    private bool CheckInFolder()
    {
        if (_path.Length > 0) return true;
        MessageBox.Show("היכנס קודם לתיקייה.", "גשר בלוטוס");
        return false;
    }

    // ---------------------------------------------------------------- transfers

    /// <summary>Runs transfers one at a time. Returns true when this one finished successfully.</summary>
    private async Task<bool> Queue(Transfer t, Func<CancellationToken, Task> work)
    {
        _transfers.Insert(0, t);
        try { await _queue.WaitAsync(t.Cts.Token); }
        catch (OperationCanceledException)
        {
            t.Status = "בוטל";
            t.Active = false;
            return false;
        }
        try
        {
            t.Status = "מעביר…";
            await work(t.Cts.Token);
            t.Tick();
            t.Status = "הושלם ✓";
            return true;
        }
        catch (OperationCanceledException) { t.Status = "בוטל"; }
        catch (Exception ex) { t.Status = "שגיאה: " + Heb.Of(ex); Log.Write($"transfer {t.Name}: {ex}"); }
        finally
        {
            t.Active = false;
            _queue.Release();
        }
        return false;
    }

    private Task<bool> QueueDownload(Session s, string remote, string local, string name)
    {
        var t = new Transfer { Name = name, LocalPath = local };
        return Queue(t, ct => RemoteFs.DownloadAsync(s, remote, local, t, ct));
    }

    private Task<bool> QueueUpload(Session s, string local, string remote)
    {
        var t = new Transfer { Name = Path.GetFileName(local), Upload = true, LocalPath = local };
        return Queue(t, ct => RemoteFs.UploadAsync(s, local, remote, t, ct));
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s) return;
        var sel = SelectedItems();
        if (sel.Count == 0) { MessageBox.Show("בחר קבצים או תיקיות להורדה.", "גשר בלוטוס"); return; }
        var root = _b.Settings.DownloadDir;
        var jobs = new List<Task>();
        try
        {
            await MaybeFast(s, sel.Any(i => i.IsDir) ? long.MaxValue : sel.Sum(i => i.Size));
            foreach (var i in sel)
            {
                if (i.IsDir) await CollectFolderDownload(s, i, root, jobs);
                else jobs.Add(QueueDownload(s, i.Path, FileService.UniquePath(Path.Combine(root, i.Name)), i.Name));
            }
        }
        catch (Exception ex) { Err(ex); }
        await Task.WhenAll(jobs);
    }

    private async Task CollectFolderDownload(Session s, RemoteItem dir, string localParent, List<Task> jobs)
    {
        var local = FileService.UniquePath(Path.Combine(localParent, dir.Name));
        Directory.CreateDirectory(local);
        var listing = await RemoteFs.ListAsync(s, dir.Path);
        foreach (var i in listing.Items)
        {
            if (i.IsDir) await CollectFolderDownload(s, i, local, jobs);
            else jobs.Add(QueueDownload(s, i.Path, Path.Combine(local, i.Name), i.Name));
        }
    }

    private async void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || !CheckInFolder()) return;
        var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "בחר קבצים להעלאה" };
        if (d.ShowDialog(this) == true) await UploadPaths(d.FileNames);
    }

    private async void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || !CheckInFolder()) return;
        var d = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "בחר תיקייה להעלאה" };
        if (d.ShowDialog(this) == true) await UploadPaths(d.FolderNames);
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (Clipboard.ContainsFileDropList())
        {
            if (!CheckInFolder()) return;
            await UploadPaths(Clipboard.GetFileDropList().Cast<string>().ToList());
        }
        else if (Clipboard.ContainsImage() && CheckInFolder())
        {
            // A copied picture (e.g. a screenshot): save it as PNG and upload it.
            var img = Clipboard.GetImage();
            var file = Path.Combine(TempFiles.NewDir("הדבקה"), $"תמונה {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using (var fs = File.Create(file))
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                enc.Save(fs);
            }
            await UploadPaths(new[] { file });
        }
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _current != null && _path.Length > 0
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && _current != null && CheckInFolder())
            await UploadPaths(paths);
    }

    private async Task UploadPaths(IEnumerable<string> paths)
    {
        if (_current is not { } s) return;
        var target = _path;
        var list = paths.ToList();
        var jobs = new List<Task<bool>>();
        try
        {
            long total = list.Sum(p => File.Exists(p) ? new FileInfo(p).Length
                : Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0);
            await MaybeFast(s, total);
            foreach (var p in list)
            {
                if (Directory.Exists(p)) await CollectFolderUpload(s, p, target, jobs);
                else if (File.Exists(p)) jobs.Add(QueueUpload(s, p, RemoteFs.Join(s, target, Path.GetFileName(p))));
            }
        }
        catch (Exception ex) { Err(ex); }
        await Task.WhenAll(jobs);
        if (_current == s && _path == target) await Navigate(target, remember: false);
    }

    private async Task CollectFolderUpload(Session s, string local, string remoteParent, List<Task<bool>> jobs)
    {
        var remote = RemoteFs.Join(s, remoteParent, Path.GetFileName(local.TrimEnd('\\')));
        try { await RemoteFs.MkdirAsync(s, remote); }
        catch (RemoteException ex) when (ex.Code == "exists") { }
        foreach (var f in Directory.EnumerateFiles(local))
            jobs.Add(QueueUpload(s, f, RemoteFs.Join(s, remote, Path.GetFileName(f))));
        foreach (var d in Directory.EnumerateDirectories(local))
            await CollectFolderUpload(s, d, remote, jobs);
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Transfer t) t.Cts.Cancel();
    }

    private void ClearTransfers_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _transfers.Where(t => !t.Active).ToList()) _transfers.Remove(t);
    }

    private void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_b.Settings.DownloadDir);
        Process.Start(new ProcessStartInfo(_b.Settings.DownloadDir) { UseShellExecute = true });
    }

    private Transfer? SelectedTransfer => TransferList.SelectedItem as Transfer;

    private void TransferList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => TransferOpen_Click(sender, e);

    private void TransferOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTransfer is { Active: false, LocalPath: { } p } && File.Exists(p)) OpenLocal(p);
    }

    private void TransferShow_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTransfer?.LocalPath is { } p && File.Exists(p))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
    }

    private void TransferCopy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTransfer?.LocalPath is { } p) Clipboard.SetText(p);
    }
}

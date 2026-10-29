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
        Session != null ? "מחובר · " + Session.LinkText + MainWindow.BatteryText(Session)
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
        _b.SessionsChanged += () => Dispatcher.BeginInvoke(() =>
        {
            RenderDevices();
            ResumeInterrupted();
        });
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
        MenuLog.IsEnabled = d.Session?.Has("log") == true;
    }

    private void ShowSelected()
    {
        var d = Selected;
        ConnectBtn.IsEnabled = d is { Session: null, Connecting: false, Address: not null };
        DisconnectBtn.IsEnabled = d?.Session != null;
        Toolbar.IsEnabled = PathBox.IsEnabled = SessionActions.IsEnabled = d?.Session != null;
        SessionActions.Visibility = d?.Session != null ? Visibility.Visible : Visibility.Collapsed;
        if (d?.Session == null) VersionNotice.Visibility = Visibility.Collapsed;

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
        HeaderSub.Text = d.Session.LinkText + (_b.Direct.Joined ? " · רשת ישירה" : "") + BatteryText(d.Session);
        bool phone = d.Session.PeerOs == "android";
        PhoneScreenBtn.Visibility = phone ? Visibility.Visible : Visibility.Collapsed;
        RingBtn.Visibility = phone && d.Session.Has("ring") ? Visibility.Visible : Visibility.Collapsed;
        ShowVersionNotice(d.Session);
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

    public static string BatteryText(Session s) =>
        s.Battery < 0 ? "" : $" · סוללה {s.Battery}%" + (s.Charging ? " (בטעינה)" : "");

    /// <summary>"Ring the phone": it plays its alarm sound for 30 seconds, even when it is on silent.</summary>
    private async void Ring_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s) return;
        try
        {
            await s.RequestAsync(new System.Text.Json.Nodes.JsonObject { ["op"] = "ring" });
            ((App)Application.Current).Balloon($"{_b.DisplayName(s)} מצלצל עכשיו. אפשר להפסיק מההתראה בטלפון.");
        }
        catch (Exception ex) { Err(ex, "הצלצול לא התחיל."); }
    }

    /// <summary>
    /// When the phone has an older version than this program, offers to send it the matching app
    /// (it travels inside this program). When the phone is newer, suggests updating the computer.
    /// </summary>
    private void ShowVersionNotice(Session s)
    {
        int cmp = AppInfo.Compare(s.PeerApp, AppInfo.Version);
        if (s.PeerOs == "android" && cmp < 0 && s.Has("update") && PhoneUpdate.Available)
        {
            VersionText.Text = $"בטלפון מותקנת גרסה {s.PeerApp}, ובמחשב גרסה {AppInfo.Version}. אפשר לשלוח לטלפון את הגרסה החדשה.";
            SendUpdateBtn.Visibility = Visibility.Visible;
            VersionNotice.Visibility = Visibility.Visible;
        }
        else if (cmp > 0)
        {
            VersionText.Text = $"ב\"{_b.DisplayName(s)}\" מותקנת גרסה חדשה יותר ({s.PeerApp}). כדאי לעדכן גם את התוכנה במחשב (כרגע {AppInfo.Version}).";
            SendUpdateBtn.Visibility = Visibility.Collapsed;
            VersionNotice.Visibility = Visibility.Visible;
        }
        else VersionNotice.Visibility = Visibility.Collapsed;
    }

    private async void SendUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s) return;
        string file;
        try { file = PhoneUpdate.Extract(); }
        catch (Exception ex) { Err(ex, "לא הצלחתי להכין את קובץ העדכון."); return; }
        await MaybeFast(s, new FileInfo(file).Length);
        var name = Path.GetFileName(file);
        Transfer t = null!;
        t = new Transfer
        {
            Name = name, Upload = true, LocalPath = file, PeerFp = s.PeerFp,
            Work = (ss, ct) => RemoteFs.UploadAsync(ss, file, name, t, ct, inbox: true, update: true),
        };
        if (await Queue(t))
            ((App)Application.Current).Balloon("העדכון נשלח. בטלפון: הקש על ההתראה \"התקבל עדכון\" כדי להתקין.");
    }

    private async void DeviceLog_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Session is not { } s) return;
        try
        {
            var res = await s.RequestAsync(new System.Text.Json.Nodes.JsonObject { ["op"] = "log" });
            var f = Path.Combine(TempFiles.NewDir("יומן"), $"יומן {_b.DisplayName(s)}.txt");
            await File.WriteAllTextAsync(f, res["text"]?.GetValue<string>() ?? "");
            OpenLocal(f);
        }
        catch (Exception ex) { Err(ex, "היומן לא התקבל."); }
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
            Transfer t = null!;
            t = new Transfer
            {
                Name = f.Rel, Upload = true, LocalPath = f.Local, PeerFp = s.PeerFp,
                Work = (ss, ct) => RemoteFs.UploadAsync(ss, f.Local, f.Rel, t, ct, inbox: true),
            };
            return Queue(t);
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
            EndSearchMode();
            var items = listing.Items;
            // The list of places starts with the user's saved places on this device.
            if (path.Length == 0)
                items = _b.Settings.SavedFor(s.PeerFp)
                    .Select(p => new RemoteItem(p.Name, p.Path, p.IsDir, 0, 0) { Saved = true })
                    .Concat(items).ToList();
            ShowItems(items);
            Placeholder.Text = items.Count == 0 ? "התיקייה ריקה. אפשר לגרור לכאן קבצים." : "";
            Placeholder.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        if (e.OriginalSource is not GridViewColumnHeader h || h.Column?.Header is not string header || _path.Length == 0 || _search != null) return;
        var by = header switch { "גודל" => "size", "תאריך שינוי" => "date", _ => "name" };
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
                else jobs.Add(QueueDownload(s, i, Path.Combine(dir, i.Name)));
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
        // From search results, "back" returns to the folder that was searched.
        if (_search != null) { await Navigate(_path, remember: false); return; }
        if (_history.Count > 0) await Navigate(_history.Pop(), remember: false);
    }

    /// <summary>After a change: the search results again when searching, otherwise the folder.</summary>
    private Task RefreshView() => _search is { } q ? RunSearch(q) : Navigate(_path, remember: false);

    // ---------------------------------------------------------------- search

    /// <summary>The text searched for while search results are shown; null while a folder is shown.</summary>
    private string? _search;

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearch(SearchBox.Text.Trim());

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await RunSearch(SearchBox.Text.Trim()); }
        else if (e.Key == Key.Escape && _search != null) { e.Handled = true; SearchBox.Text = ""; await Navigate(_path, remember: false); }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Searches the current folder and all its subfolders on the other device (PROTOCOL.md section 4, "search").</summary>
    private async Task RunSearch(string q)
    {
        if (_current is not { } s) return;
        if (q.Length == 0) { SearchBox.Focus(); return; }
        var where = _path.Length == 0 ? "התיקייה הראשית של המשתמש" : _path;
        Placeholder.Text = $"מחפש \"{q}\" ב-{where}…";
        Placeholder.Visibility = Visibility.Visible;
        _items.Clear();
        try
        {
            var (items, more) = await RemoteFs.SearchAsync(s, _path, q);
            if (s != _current) return;
            _search = q;
            FolderColumn.Width = 280;
            foreach (var i in items) _items.Add(i);
            SortText.Text = $"תוצאות החיפוש של \"{q}\" ב-{where}: {items.Count}" +
                            (more ? " (מוצגות רק הראשונות. כדאי לחפש בתיקייה מצומצמת יותר)" : "") + ". \"חזור\" חוזר לתיקייה.";
            Placeholder.Text = items.Count == 0 ? $"לא נמצא שום דבר עם \"{q}\" בשם." : "";
            Placeholder.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Placeholder.Text = "";
            Err(ex, "החיפוש נכשל.");
            await Navigate(_path, remember: false);
        }
    }

    private void EndSearchMode()
    {
        _search = null;
        FolderColumn.Width = 0;
    }

    private async void OpenContainingFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is RemoteItem i && i.Folder.Length > 0) await Navigate(i.Folder);
    }

    // ---------------------------------------------------------------- marking several

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        FileList.SelectAll();
        FileList.Focus();
    }

    // Dragging with the mouse draws a rectangle and selects the rows it covers, as in File Explorer.
    // Ctrl keeps what was already selected. Near the top or bottom edge the list scrolls by itself.
    private Point? _marqueeFrom;
    private bool _marqueeOn;
    private int _marqueeAnchor = -1;
    private List<RemoteItem> _marqueeBase = new();
    private DispatcherTimer? _marqueeTimer;

    private void FileList_MarqueeDown(object sender, MouseButtonEventArgs e)
    {
        _marqueeFrom = null;
        if (e.ClickCount > 1 || e.OriginalSource is not DependencyObject src) return;
        if (Ancestor<System.Windows.Controls.Primitives.ScrollBar>(src) != null || Ancestor<GridViewColumnHeader>(src) != null
            || Ancestor<CheckBox>(src) != null) return;
        _marqueeFrom = e.GetPosition(FileList);
        _marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? SelectedItems() : new();
    }

    private void FileList_MarqueeMove(object sender, MouseEventArgs e)
    {
        if (_marqueeFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(FileList);
        if (!_marqueeOn)
        {
            if (Math.Abs(p.X - from.X) < 8 && Math.Abs(p.Y - from.Y) < 8) return;
            _marqueeAnchor = IndexAt(from);
            if (_marqueeAnchor < 0) return;
            _marqueeOn = true;
            FileList.CaptureMouse();
            Marquee.Visibility = Visibility.Visible;
            _marqueeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) => MarqueeTick(), Dispatcher);
            _marqueeTimer.Start();
        }
        e.Handled = true;
        MarqueeUpdate(p);
    }

    private void FileList_MarqueeUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeOn) e.Handled = true;
        EndMarquee();
    }

    private void FileList_LostCapture(object sender, MouseEventArgs e) => EndMarquee();

    private void EndMarquee()
    {
        _marqueeFrom = null;
        if (!_marqueeOn) return;
        _marqueeOn = false;
        _marqueeTimer?.Stop();
        Marquee.Visibility = Visibility.Collapsed;
        if (FileList.IsMouseCaptured) FileList.ReleaseMouseCapture();
    }

    /// <summary>While the mouse is held near the top or bottom edge, the list keeps scrolling and the selection follows.</summary>
    private void MarqueeTick()
    {
        if (!_marqueeOn) return;
        var p = Mouse.GetPosition(FileList);
        var sv = Descendant<ScrollViewer>(FileList);
        if (sv == null) return;
        if (p.Y < 32) sv.LineUp();
        else if (p.Y > FileList.ActualHeight - 12) sv.LineDown();
        else return;
        FileList.UpdateLayout();
        MarqueeUpdate(p);
    }

    private void MarqueeUpdate(Point p)
    {
        if (_marqueeFrom is not { } from) return;
        var r = new Rect(from, p);
        Canvas.SetLeft(Marquee, r.X);
        Canvas.SetTop(Marquee, r.Y);
        Marquee.Width = r.Width;
        Marquee.Height = r.Height;
        // In a list of rows the rectangle covers a range of rows: from the row where the drag began to the row under the mouse.
        int cur = IndexAt(p);
        if (cur < 0 || _marqueeAnchor < 0) return;
        int lo = Math.Min(cur, _marqueeAnchor), hi = Math.Max(cur, _marqueeAnchor);
        var want = new HashSet<RemoteItem>(_marqueeBase);
        for (int i = lo; i <= hi && i < _items.Count; i++) want.Add(_items[i]);
        foreach (var x in FileList.SelectedItems.Cast<RemoteItem>().Where(x => !want.Contains(x)).ToList()) FileList.SelectedItems.Remove(x);
        foreach (var x in want) if (!FileList.SelectedItems.Contains(x)) FileList.SelectedItems.Add(x);
    }

    /// <summary>The row under a point of the list (the nearest one when the point is between, above or below rows), or -1.</summary>
    private int IndexAt(Point p)
    {
        if (_items.Count == 0) return -1;
        if (FileList.InputHitTest(p) is DependencyObject hit && Ancestor<ListViewItem>(hit) is { } row)
            return FileList.ItemContainerGenerator.IndexFromContainer(row);
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < _items.Count; i++)
        {
            if (FileList.ItemContainerGenerator.ContainerFromIndex(i) is not ListViewItem c || !c.IsVisible) continue;
            var b = c.TransformToAncestor(FileList).TransformBounds(new Rect(c.RenderSize));
            double d = p.Y < b.Top ? b.Top - p.Y : p.Y > b.Bottom ? p.Y - b.Bottom : 0;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private static T? Descendant<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (Descendant<T>(c) is { } found) return found;
        }
        return null;
    }

    private void AllBox_Click(object sender, RoutedEventArgs e)
    {
        if (AllBox.IsChecked == true) FileList.SelectAll();
        else FileList.UnselectAll();
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int n = FileList.SelectedItems.Count;
        SelectionText.Text = n > 1 ? $"נבחרו {n} פריטים  ·  " : "";
        AllBox.IsChecked = _items.Count > 0 && n == _items.Count;
    }

    // ---------------------------------------------------------------- saved places

    /// <summary>The "★ שמורים" button: the saved places on this device, and adding or removing the current folder.</summary>
    private void Saved_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s) return;
        var menu = new ContextMenu { FlowDirection = FlowDirection.RightToLeft, PlacementTarget = SavedBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var saved = _b.Settings.SavedFor(s.PeerFp);
        if (saved.Count == 0)
            menu.Items.Add(new MenuItem { Header = "עוד אין שמורים. אפשר לשמור תיקייה או קובץ בקליק ימני.", IsEnabled = false });
        foreach (var p in saved)
        {
            var item = new MenuItem { Header = (p.IsDir ? "📁 " : "📄 ") + p.Name, ToolTip = p.Path };
            item.Click += async (_, _) => await OpenItem(new RemoteItem(p.Name, p.Path, p.IsDir, 0, 0));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        bool here = _path.Length > 0 && _search == null;
        bool isSaved = here && _b.Settings.IsSaved(s.PeerFp, _path);
        var add = new MenuItem { Header = "★ שמור את התיקייה הנוכחית", IsEnabled = here && !isSaved };
        add.Click += (_, _) => _b.Settings.AddSaved(s.PeerFp, new SavedPlace { Name = FolderName(_path), Path = _path, IsDir = true });
        menu.Items.Add(add);
        var remove = new MenuItem { Header = "הסר את התיקייה הנוכחית מהשמורים", IsEnabled = isSaved };
        remove.Click += (_, _) => _b.Settings.RemoveSaved(s.PeerFp, _path);
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private static string FolderName(string path)
    {
        var t = path.TrimEnd('\\', '/');
        var cut = t.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 && cut < t.Length - 1 ? t[(cut + 1)..] : t;
    }

    private void SaveItems_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s) return;
        foreach (var i in SelectedItems().Where(i => !i.Saved))
            _b.Settings.AddSaved(s.PeerFp, new SavedPlace { Name = i.Name, Path = i.Path, IsDir = i.IsDir });
        ((App)Application.Current).Balloon("נשמר. אפשר לחזור אליו מהכפתור \"★ שמורים\" או מרשימת המקומות.");
    }

    private async void UnsaveItems_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s) return;
        foreach (var i in SelectedItems()) _b.Settings.RemoveSaved(s.PeerFp, i.Path);
        if (_path.Length == 0 && _search == null) await Navigate("", remember: false);
    }

    private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var sel = SelectedItems();
        var fp = _current?.PeerFp ?? "";
        MenuOpenFolder.Visibility = _search != null || (_path.Length == 0 && sel.Any(i => i.Saved)) ? Visibility.Visible : Visibility.Collapsed;
        MenuSave.IsEnabled = sel.Any(i => !_b.Settings.IsSaved(fp, i.Path));
        MenuUnsave.IsEnabled = sel.Any(i => _b.Settings.IsSaved(fp, i.Path));
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
        // Opened before and still here: no need to bring it again.
        if (ViewCache.Find(s.PeerFp, i) is { } cached) { OpenLocal(cached); return; }
        var dir = TempFiles.NewDir("צפייה");
        var local = Path.Combine(dir, i.Name);
        await MaybeFast(s, i.Size);
        if (await QueueDownload(s, i, local))
        {
            ViewCache.Add(s.PeerFp, i, local);
            OpenLocal(local);
        }
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
        if (_current is not { } s || !CheckInFolder(inResults: true) || FileList.SelectedItem is not RemoteItem i) return;
        var d = new InputWindow("שינוי שם", "שם חדש:", i.Name) { Owner = this };
        if (d.ShowDialog() != true || d.Value == i.Name) return;
        try
        {
            // In search results the item may sit in a subfolder: rename it where it is.
            await RemoteFs.RenameAsync(s, i.Path, RemoteFs.Join(s, i.Folder.Length > 0 ? i.Folder : _path, d.Value));
            await RefreshView();
        }
        catch (Exception ex) { Err(ex); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } s || !CheckInFolder(inResults: true)) return;
        var sel = SelectedItems();
        if (sel.Count == 0) return;
        var what = sel.Count == 1 ? $"\"{sel[0].Name}\"" : $"{sel.Count} פריטים";
        var how = s.PeerOs == "windows" ? "הם יעברו לסל המיחזור במחשב השני." : "המחיקה בטלפון היא לצמיתות, בלי סל מיחזור.";
        if (MessageBox.Show($"למחוק את {what}?\n{how}", "מחיקה", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != MessageBoxResult.Yes) return;
        try { await RemoteFs.DeleteAsync(s, sel.Select(x => x.Path)); }
        catch (Exception ex) { Err(ex); }
        await RefreshView();
    }

    /// <summary>Most actions need a real folder. Renaming and deleting also work on search results.</summary>
    private bool CheckInFolder(bool inResults = false)
    {
        if (_path.Length > 0 || (inResults && _search != null)) return true;
        MessageBox.Show("היכנס קודם לתיקייה.", "גשר בלוטוס");
        return false;
    }

    // ---------------------------------------------------------------- transfers

    /// <summary>Errors that mean "the connection went away", not "this file can't be transferred".</summary>
    private static readonly string[] LinkErrors = { "החיבור נותק", "החיבור נסגר", "ההעברה נתקעה", "אין תשובה מהמכשיר השני" };

    /// <summary>Adds a transfer to the list and runs it. Returns true when it finished successfully.</summary>
    private Task<bool> Queue(Transfer t)
    {
        _transfers.Insert(0, t);
        return Run(t);
    }

    /// <summary>
    /// Runs transfers one at a time, each on its device's current connection. When the connection drops in the middle,
    /// the transfer waits and continues from where it stopped once the device is back (or right away, when only
    /// the Wi-Fi link dropped and Bluetooth is still up).
    /// </summary>
    private async Task<bool> Run(Transfer t)
    {
        while (true)
        {
            try { await _queue.WaitAsync(t.Cts.Token); }
            catch (OperationCanceledException)
            {
                t.Status = "בוטל";
                t.Active = false;
                return false;
            }
            bool again = false;
            Session? s = null;
            try
            {
                s = _b.Sessions.FirstOrDefault(x => x.PeerFp == t.PeerFp);
                if (s == null)
                {
                    t.Interrupted = true;
                    t.Status = "ממתין לחיבור. ימשיך כשהמכשיר יתחבר שוב.";
                    return false;
                }
                t.Status = "מעביר…";
                await t.Work!(s, t.Cts.Token);
                t.Tick();
                t.Status = "הושלם ✓";
                return true;
            }
            catch (Exception) when (t.Cts.IsCancellationRequested) { t.Status = "בוטל"; }
            catch (Exception ex) when (s?.Closed == true)
            {
                t.Interrupted = true;
                t.Status = "נקטע. ימשיך כשהמכשיר יתחבר שוב.";
                Log.Write($"transfer {t.Name} interrupted: {ex.Message}");
            }
            catch (Exception ex) when (t.Attempts < 5 && (ex is OperationCanceledException || LinkErrors.Any(Heb.Of(ex).Contains)))
            {
                // Only the Wi-Fi link dropped; Bluetooth is still up. Go on right away from where it stopped.
                again = true;
                t.Status = "החיבור התחלף. ממשיך…";
                Log.Write($"transfer {t.Name} link changed: {ex.Message}");
            }
            catch (Exception ex) { t.Status = "שגיאה: " + Heb.Of(ex); Log.Write($"transfer {t.Name}: {ex}"); }
            finally
            {
                if (!again) t.Active = false;
                _queue.Release();
            }
            if (!again) return false;
        }
    }

    /// <summary>Interrupted transfers of devices that are connected again continue by themselves.</summary>
    private void ResumeInterrupted()
    {
        foreach (var t in _transfers.Where(t => t.Interrupted && !t.Active).Reverse().ToList())
        {
            if (!_b.Sessions.Any(x => x.PeerFp == t.PeerFp)) continue;
            t.Restart();
            _ = Run(t);
        }
    }

    /// <summary>
    /// Brings one remote file to <paramref name="local"/>. When it was already opened for viewing (same size and date),
    /// the local copy is used instead of the network.
    /// </summary>
    private Task<bool> QueueDownload(Session s, RemoteItem i, string local)
    {
        Transfer t = null!;
        t = new Transfer
        {
            Name = i.Name, LocalPath = local, PeerFp = s.PeerFp,
            Work = async (ss, ct) =>
            {
                if (ViewCache.Find(ss.PeerFp, i) is { } cached && !string.Equals(cached, local, StringComparison.OrdinalIgnoreCase))
                {
                    t.LinkKind = "מהעותק שכבר נפתח";
                    t.Total = i.Size;
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    await Task.Run(() => File.Copy(cached, local, true), ct);
                    t.SetDone(i.Size);
                    return;
                }
                await RemoteFs.DownloadAsync(ss, i.Path, local, t, ct, i.Size, i.Mtime);
            },
        };
        return Queue(t);
    }

    private Task<bool> QueueUpload(Session s, string local, string remote)
    {
        Transfer t = null!;
        t = new Transfer
        {
            Name = Path.GetFileName(local), Upload = true, LocalPath = local, PeerFp = s.PeerFp,
            Work = (ss, ct) => RemoteFs.UploadAsync(ss, local, remote, t, ct),
        };
        return Queue(t);
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
                else jobs.Add(QueueDownload(s, i, FileService.UniquePath(Path.Combine(root, i.Name))));
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
            else jobs.Add(QueueDownload(s, i, Path.Combine(local, i.Name)));
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

    /// <summary>"Continue": an interrupted or failed transfer tries again from where it stopped.</summary>
    private void TransferResume_Click(object sender, RoutedEventArgs e)
    {
        var t = (sender as FrameworkElement)?.DataContext as Transfer ?? SelectedTransfer;
        if (t is not { CanResume: true }) return;
        t.Restart();
        _ = Run(t);
    }

    private void TransferList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        MenuResume.IsEnabled = SelectedTransfer?.CanResume == true;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BtBridge.Core;

namespace BtBridge;

/// <summary>Small dialogs built in code: a common frame with a body and OK/Cancel.</summary>
public class DialogBase : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(20, 16, 20, 8) };
    protected readonly TextBlock Error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    protected readonly Button OkButton;

    protected DialogBase(string title, string okText = "אישור")
    {
        Title = title;
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcon.Window();
        MinWidth = 380;
        MaxWidth = 520;

        OkButton = new Button { Content = okText, IsDefault = true, Style = (Style)Application.Current.Resources["Primary"], MinWidth = 90 };
        OkButton.Click += (_, _) => { if (Validate()) DialogResult = true; };
        var cancel = new Button { Content = "ביטול", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 4, 16, 14) };
        buttons.Children.Add(OkButton);
        buttons.Children.Add(cancel);

        var root = new StackPanel();
        root.Children.Add(Body);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => FocusFirst();
    }

    protected virtual bool Validate() => true;
    protected virtual void FocusFirst() { }

    protected void ShowError(string msg)
    {
        Error.Text = msg;
        Error.Visibility = Visibility.Visible;
    }

    protected static TextBlock Text(string s, bool bold = false, double top = 0) => new()
    {
        Text = s,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Margin = new Thickness(0, top, 0, 4),
    };
}

/// <summary>Asks for the other device's password (first connection).</summary>
public sealed class PasswordWindow : DialogBase
{
    private readonly PasswordBox _pw = new();
    private readonly CheckBox _remember = new() { Content = "אל תבקש שוב סיסמה ממכשיר זה", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };

    public PasswordAnswer? Answer { get; private set; }

    public PasswordWindow(string peerName, bool wrongBefore) : base("סיסמה", "התחבר")
    {
        Body.Children.Add(Text($"הזן את הסיסמה של \"{peerName}\"", bold: true));
        Body.Children.Add(Text("זו הסיסמה שנקבעה במכשיר השני, בהגדרות של גשר בלוטוס."));
        Body.Children.Add(_pw);
        Body.Children.Add(_remember);
        Body.Children.Add(Error);
        if (wrongBefore) ShowError("הסיסמה שגויה. נסה שוב.");
    }

    protected override void FocusFirst() => _pw.Focus();

    protected override bool Validate()
    {
        if (_pw.Password.Length == 0) { ShowError("הזן סיסמה"); return false; }
        Answer = new PasswordAnswer(_pw.Password, _remember.IsChecked == true);
        return true;
    }
}

/// <summary>Sets this computer's password (first run, or from settings).</summary>
public sealed class SetPasswordWindow : DialogBase
{
    private readonly PasswordBox _a = new(), _b = new();

    public SetPasswordWindow(bool firstRun) : base(firstRun ? "ברוך הבא לגשר בלוטוס" : "שינוי סיסמה", "שמור")
    {
        if (firstRun)
        {
            Body.Children.Add(Text("קבע סיסמה למחשב הזה", bold: true));
            Body.Children.Add(Text("מכשיר שמתחבר למחשב בפעם הראשונה יצטרך להזין אותה. אם תסמן שם \"אל תבקש שוב\", הוא לא יצטרך להזין אותה שוב."));
        }
        Body.Children.Add(Text("סיסמה (לפחות 4 תווים)", top: 6));
        Body.Children.Add(_a);
        Body.Children.Add(Text("שוב, לאימות", top: 6));
        Body.Children.Add(_b);
        Body.Children.Add(Error);
    }

    protected override void FocusFirst() => _a.Focus();

    protected override bool Validate()
    {
        if (_a.Password.Length < 4) { ShowError("הסיסמה קצרה מדי"); return false; }
        if (_a.Password != _b.Password) { ShowError("הסיסמאות לא זהות"); return false; }
        App.Core.Settings.SetPassword(_a.Password);
        return true;
    }
}

/// <summary>One line of text (new folder, rename).</summary>
public sealed class InputWindow : DialogBase
{
    private readonly TextBox _box = new();
    private readonly bool _allowEmpty;
    public string Value => _box.Text.Trim();

    public InputWindow(string title, string prompt, string initial = "", bool allowEmpty = false) : base(title)
    {
        _allowEmpty = allowEmpty;
        Body.Children.Add(Text(prompt));
        _box.Text = initial;
        Body.Children.Add(_box);
        Body.Children.Add(Error);
    }

    protected override void FocusFirst()
    {
        _box.Focus();
        var dot = _box.Text.LastIndexOf('.');
        _box.Select(0, dot > 0 ? dot : _box.Text.Length);
    }

    protected override bool Validate()
    {
        if (Value.Length == 0) { if (_allowEmpty) return true; ShowError("הזן שם"); return false; }
        if (Value.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0) { ShowError("השם מכיל תו אסור"); return false; }
        return true;
    }
}

public sealed class SettingsWindow : DialogBase
{
    private readonly TextBox _name = new();
    private readonly TextBox _dir = new() { FlowDirection = FlowDirection.LeftToRight };
    private readonly ComboBox _cleanup = new() { MinWidth = 120, Margin = new Thickness(6, 0, 0, 0) };
    private static readonly (int Hours, string Label)[] CleanupChoices =
        { (1, "שעה"), (6, "6 שעות"), (24, "יום"), (24 * 7, "שבוע") };
    private readonly CheckBox _tray = new() { Content = "כשסוגרים את החלון, להמשיך לפעול ליד השעון", Margin = new Thickness(0, 10, 0, 0) };
    private readonly CheckBox _startup = new() { Content = "להפעיל יחד עם המחשב (מוסתר ליד השעון)", Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _reconnect = new() { Content = "להתחבר לבד למכשירים מוכרים כשהם בטווח", Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _control = new() { Content = "לאפשר למכשירים מוכרים לצפות במסך המחשב ולשלוט בו", Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _direct = new()
    {
        Content = "רשת ישירה: כשאין רשת משותפת, להתחבר לנקודה חמה זמנית של הטלפון (להעברות גדולות ולשליטה)",
        Margin = new Thickness(0, 6, 0, 0),
    };
    private readonly ListBox _trusted = new() { Height = 120, DisplayMemberPath = "Label" };

    private sealed record TrustedRow(string Fp, string Label);

    public SettingsWindow() : base("הגדרות", "שמור")
    {
        var st = App.Core.Settings;
        Body.Children.Add(Text("שם המחשב (כפי שיופיע בטלפון)"));
        _name.Text = st.DeviceName;
        Body.Children.Add(_name);

        Body.Children.Add(Text("תיקיית הורדות", top: 10));
        var dirRow = new DockPanel();
        var browse = new Button { Content = "בחר…" };
        browse.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(_dir.Text) ? _dir.Text : "" };
            if (d.ShowDialog(this) == true) _dir.Text = d.FolderName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        dirRow.Children.Add(browse);
        _dir.Text = st.DownloadDir;
        dirRow.Children.Add(_dir);
        Body.Children.Add(dirRow);

        var cleanupRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        cleanupRow.Children.Add(new TextBlock { Text = "קבצים שנפתחו רק לצפייה או הועתקו ללוח יימחקו אחרי", VerticalAlignment = VerticalAlignment.Center });
        foreach (var c in CleanupChoices) _cleanup.Items.Add(c.Label);
        _cleanup.SelectedIndex = Math.Max(0, Array.FindIndex(CleanupChoices, c => c.Hours == st.ViewCleanupHours));
        cleanupRow.Children.Add(_cleanup);
        Body.Children.Add(cleanupRow);
        Body.Children.Add(new TextBlock
        {
            Text = "קבצים שהורדת בכפתור \"הורד\" נשמרים בתיקיית ההורדות ולא נמחקים.",
            FontSize = 12, Foreground = (Brush)Application.Current.Resources["Muted"], TextWrapping = TextWrapping.Wrap,
        });

        _tray.IsChecked = st.CloseToTray;
        Body.Children.Add(_tray);
        _startup.IsChecked = Startup.Enabled;
        Body.Children.Add(_startup);
        _reconnect.IsChecked = st.AutoReconnect;
        Body.Children.Add(_reconnect);
        _control.IsChecked = st.AllowControl;
        Body.Children.Add(_control);
        _direct.IsChecked = st.DirectMode != "off";
        ((CheckBox)_direct).Content = new TextBlock { Text = (string)_direct.Content, TextWrapping = TextWrapping.Wrap };
        Body.Children.Add(_direct);
        Body.Children.Add(new TextBlock
        {
            Text = "מצב \"בלי להפריע\" (שלב 3): כדי שהטלפון יעבוד על מסך משלו, הוסף למחשב מסך וירטואלי " +
                   "(יש מנהלי התקן חינמיים למסך וירטואלי), ובחר אותו בטלפון במסך השליטה. " +
                   "שם אפשר גם לסמן \"אל תזיז לי את העכבר\".",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["Muted"],
            Margin = new Thickness(0, 6, 0, 0),
        });

        var pw = new Button { Content = "שינוי הסיסמה של המחשב…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        pw.Click += (_, _) => new SetPasswordWindow(false) { Owner = this }.ShowDialog();
        Body.Children.Add(pw);

        Body.Children.Add(Text("מכשירים מוכרים (מתחברים בלי סיסמה)", bold: true, top: 14));
        Body.Children.Add(_trusted);
        var forget = new Button { Content = "הסר מכשיר מוכר", HorizontalAlignment = HorizontalAlignment.Left };
        forget.Click += (_, _) =>
        {
            if (_trusted.SelectedItem is TrustedRow r) { st.Forget(r.Fp); LoadTrusted(); }
        };
        Body.Children.Add(forget);
        Body.Children.Add(Text($"טביעת האצבע של המחשב הזה: {Crypto.FingerprintDigits(App.Core.Me.Fp)}", top: 10));
        Body.Children.Add(Error);
        LoadTrusted();
    }

    private void LoadTrusted() =>
        _trusted.ItemsSource = App.Core.Settings.Trusted
            .Select(t => new TrustedRow(t.Fp, $"{App.Core.Settings.NameFor(t.Fp, t.Name)}  ·  {Crypto.FingerprintDigits(t.Fp)}")).ToList();

    protected override bool Validate()
    {
        if (_name.Text.Trim().Length == 0) { ShowError("הזן שם למחשב"); return false; }
        var st = App.Core.Settings;
        st.DeviceName = _name.Text.Trim();
        st.DownloadDir = _dir.Text.Trim();
        st.CloseToTray = _tray.IsChecked == true;
        st.ViewCleanupHours = CleanupChoices[Math.Max(0, _cleanup.SelectedIndex)].Hours;
        st.AutoReconnect = _reconnect.IsChecked == true;
        st.AllowControl = _control.IsChecked == true;
        st.DirectMode = _direct.IsChecked == true ? "auto" : "off";
        st.Save();
        if (!st.AllowControl) ScreenSource.StopAll();
        try { Startup.Enabled = _startup.IsChecked == true; }
        catch (Exception ex) { ShowError("לא הצלחתי לשנות את ההפעלה עם המחשב: " + Heb.Of(ex)); return false; }
        return true;
    }
}

/// <summary>"Start with Windows": a value under HKCU\...\Run (this user only).</summary>
public static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "גשר בלוטוס";
    /// <summary>The name version 0.2 used.</summary>
    private const string OldName = "BT Bridge";

    public static bool Enabled
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string || k?.GetValue(OldName) is string;
        }
        set
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
            if (k.GetValue(OldName) != null) k.DeleteValue(OldName);
            if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
            else if (k.GetValue(Name) != null) k.DeleteValue(Name);
        }
    }
}

/// <summary>Searches for nearby Bluetooth devices and pairs with the chosen one.</summary>
public sealed class PairWindow : DialogBase
{
    private readonly ListBox _list = new() { Height = 220, DisplayMemberPath = "Label" };
    private readonly TextBlock _status = Text("");
    private bool _busy;

    public InTheHand.Net.BluetoothAddress? Paired { get; private set; }

    private sealed record Found(string Label, InTheHand.Net.BluetoothAddress Address);

    public PairWindow() : base("חיפוש וצימוד מכשיר", "צמד")
    {
        Body.Children.Add(Text("ודא שהבלוטוס בטלפון דלוק ושהטלפון גלוי (מסך הגדרות הבלוטוס פתוח בטלפון).", top: 0));
        Body.Children.Add(_status);
        Body.Children.Add(_list);
        var again = new Button { Content = "חפש שוב", HorizontalAlignment = HorizontalAlignment.Left };
        again.Click += async (_, _) => await Search();
        Body.Children.Add(again);
        Body.Children.Add(Error);
        _list.MouseDoubleClick += (_, _) => { if (Validate()) { } };
        Loaded += async (_, _) => await Search();
    }

    private async Task Search()
    {
        if (_busy) return;
        _busy = true;
        _status.Text = "מחפש מכשירים בסביבה… (כ-10 שניות)";
        _list.ItemsSource = null;
        try
        {
            var found = await Task.Run(() =>
            {
                using var c = new InTheHand.Net.Sockets.BluetoothClient();
                return c.DiscoverDevices().Where(d => !d.Authenticated)
                    .Select(d => new Found($"{(string.IsNullOrWhiteSpace(d.DeviceName) ? "מכשיר ללא שם" : d.DeviceName)}  ·  {d.DeviceAddress}", d.DeviceAddress))
                    .ToList();
            });
            _list.ItemsSource = found;
            _status.Text = found.Count == 0 ? "לא נמצאו מכשירים חדשים. אפשר לחפש שוב." : "בחר מכשיר ולחץ \"צמד\":";
        }
        catch (Exception ex) { _status.Text = "החיפוש נכשל: " + Heb.Of(ex); }
        _busy = false;
    }

    protected override bool Validate()
    {
        if (_list.SelectedItem is not Found f) { ShowError("בחר מכשיר מהרשימה"); return false; }
        if (_busy) return false;
        _busy = true;
        _status.Text = $"מצמד… אשר את הקוד בשני המכשירים.";
        _ = Task.Run(() => InTheHand.Net.Bluetooth.BluetoothSecurity.PairRequest(f.Address, null)).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            _busy = false;
            if (t.IsCompletedSuccessfully && t.Result)
            {
                Paired = f.Address;
                DialogResult = true;
            }
            else
            {
                _status.Text = "";
                ShowError("הצימוד נכשל או בוטל.");
            }
        }));
        return false;
    }
}

/// <summary>A small always-on-top strip while a phone sees and controls this computer.</summary>
public sealed class ControlBanner : Window
{
    private readonly TextBlock _text = new() { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };

    public ControlBanner(Action stop)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
        var stopBtn = new Button { Content = "עצור", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6) };
        stopBtn.Click += (_, _) => stop();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_text);
        row.Children.Add(stopBtn);
        Content = row;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) =>
        {
            Left = (SystemParameters.WorkArea.Width - ActualWidth) / 2;
            Top = 0;
        };
    }

    public void SetText(string s) => _text.Text = s;
}

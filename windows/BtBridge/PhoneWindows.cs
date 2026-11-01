using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BtBridge.Core;

namespace BtBridge;

/// <summary>
/// The phone's notifications on the computer: answer one (in messaging apps), open it on the phone, or close it there
/// (PROTOCOL.md section 4, "notif-reply", "notif-open", "notif-dismiss").
/// </summary>
public sealed class NotesWindow : Window
{
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button _reply = new() { Content = "השב…", Style = (Style)Application.Current.Resources["Primary"] };
    private readonly Button _open = new() { Content = "פתח בטלפון" };
    private readonly Button _dismiss = new() { Content = "סגור בטלפון" };
    private readonly TextBlock _empty = new()
    {
        Text = "עוד לא הגיעו התראות מהטלפון.\nכדי שיגיעו, אשר בטלפון \"גישה להתראות\" לגשר בלוטוס.",
        TextAlignment = TextAlignment.Center, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20),
    };

    public NotesWindow()
    {
        Title = "התראות מהטלפון";
        Width = 460;
        Height = 560;
        FlowDirection = FlowDirection.RightToLeft;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        Icon = AppIcon.Window();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _list.ItemTemplate = Template();
        _list.SelectionChanged += (_, _) => Enable();
        _list.MouseDoubleClick += (_, _) => Act("notif-open");
        _reply.Click += (_, _) => Reply();
        _open.Click += (_, _) => Act("notif-open");
        _dismiss.Click += (_, _) => Act("notif-dismiss");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
        buttons.Children.Add(_reply);
        buttons.Children.Add(_open);
        buttons.Children.Add(_dismiss);
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var grid = new Grid();
        grid.Children.Add(_list);
        grid.Children.Add(_empty);
        root.Children.Add(grid);
        Content = root;
        Refresh();
    }

    private static DataTemplate Template()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(2, 6, 2, 6));
        var head = new FrameworkElementFactory(typeof(TextBlock));
        head.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PhoneNote.Head)));
        head.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PhoneNote.Text)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.MaxHeightProperty, 60.0);
        var when = new FrameworkElementFactory(typeof(TextBlock));
        when.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PhoneNote.When)));
        when.SetValue(TextBlock.FontSizeProperty, 11.0);
        when.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
        panel.AppendChild(head);
        panel.AppendChild(text);
        panel.AppendChild(when);
        return new DataTemplate { VisualTree = panel };
    }

    public void Refresh()
    {
        var sel = _list.SelectedItem as PhoneNote;
        List<PhoneNote> notes;
        lock (App.Core.Notes) notes = App.Core.Notes.ToList();
        _list.ItemsSource = notes;
        _list.SelectedItem = notes.FirstOrDefault(n => n.Key == sel?.Key);
        _empty.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Enable();
    }

    private void Enable()
    {
        var n = _list.SelectedItem as PhoneNote;
        bool live = n is { Key.Length: > 0 } && !n.S.Closed && n.S.Has("notif-act");
        _reply.IsEnabled = live && n!.CanReply;
        _open.IsEnabled = _dismiss.IsEnabled = live;
    }

    private async void Reply()
    {
        if (_list.SelectedItem is not PhoneNote n) return;
        var w = new InputWindow("תשובה", $"תשובה ל-{n.Head}:", allowEmpty: false) { Owner = this };
        if (w.ShowDialog() != true) return;
        try
        {
            await n.S.RequestAsync(new JsonObject { ["op"] = "notif-reply", ["key"] = n.Key, ["text"] = w.Value });
            ((App)Application.Current).Balloon("התשובה נשלחה מהטלפון.");
        }
        catch (Exception ex) { MainWindow.Err(ex, "התשובה לא נשלחה."); }
    }

    private async void Act(string op)
    {
        if (_list.SelectedItem is not PhoneNote n || n.Key.Length == 0) return;
        try
        {
            await n.S.RequestAsync(new JsonObject { ["op"] = op, ["key"] = n.Key });
            if (op == "notif-dismiss")
            {
                lock (App.Core.Notes) App.Core.Notes.Remove(n);
                Refresh();
            }
        }
        catch (Exception ex) { MainWindow.Err(ex); }
    }
}

/// <summary>The phone's apps: picking one opens it on the phone (PROTOCOL.md section 4, "apps" and "launch").</summary>
public sealed class PhoneAppsWindow : DialogBase
{
    private readonly TextBox _filter = new();
    private readonly ListBox _list = new() { Height = 360, DisplayMemberPath = "Label" };
    private List<(string Label, string Pkg)> _all = new();
    private readonly Session _s;

    private sealed record Row(string Label, string Pkg);

    public PhoneAppsWindow(Session s) : base("פתיחת אפליקציה בטלפון", "פתח")
    {
        _s = s;
        Body.Children.Add(Text("חיפוש:"));
        Body.Children.Add(_filter);
        Body.Children.Add(_list);
        Body.Children.Add(Error);
        _filter.TextChanged += (_, _) => Show();
        _list.MouseDoubleClick += (_, _) => { if (Validate()) DialogResult = true; };
        Loaded += async (_, _) =>
        {
            try
            {
                var res = await s.RequestAsync(new JsonObject { ["op"] = "apps" });
                _all = res["items"]!.AsArray().Select(n => (n!["label"]!.GetValue<string>(), n["pkg"]!.GetValue<string>())).ToList();
                Show();
            }
            catch (Exception ex) { ShowError(Heb.Of(ex)); }
        };
    }

    private void Show()
    {
        var f = _filter.Text.Trim();
        _list.ItemsSource = _all.Where(a => f.Length == 0 || a.Label.Contains(f, StringComparison.CurrentCultureIgnoreCase))
            .Select(a => new Row(a.Label, a.Pkg)).ToList();
    }

    protected override void FocusFirst() => _filter.Focus();

    protected override bool Validate()
    {
        if (_list.SelectedItem is not Row r) { ShowError("בחר אפליקציה"); return false; }
        _ = Launch(r);
        return true;
    }

    private async Task Launch(Row r)
    {
        try { await _s.RequestAsync(new JsonObject { ["op"] = "launch", ["pkg"] = r.Pkg }); }
        catch (Exception ex) { MainWindow.Err(ex, $"\"{r.Label}\" לא נפתחה."); }
    }
}

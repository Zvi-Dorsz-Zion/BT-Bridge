using System.Windows;
using BtBridge.Core;
using Forms = System.Windows.Forms;

namespace BtBridge;

public partial class App : Application
{
    private const string InstanceName = "BtBridge-SingleInstance-7f3c2a10";
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _main;
    private ClipboardWatcher? _clipboard;

    public static Bridge Core { get; private set; } = null!;
    public bool Exiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            // Already running: ask the other instance to show its window.
            try { EventWaitHandle.OpenExisting(InstanceName + "-show").Set(); } catch { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-show");
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                try { Dispatcher.Invoke(ShowMain); } catch (Exception ex) { Log.Write("show: " + ex.Message); }
        }) { IsBackground = true }.Start();

        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("CRASH: " + ex.ExceptionObject);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("UI error: " + ex.Exception);
            MessageBox.Show(Heb.Of(ex.Exception), "גשר בלוטוס", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        Core = new Bridge();
        if (!Core.Settings.HasPassword)
        {
            var dlg = new SetPasswordWindow(firstRun: true);
            if (dlg.ShowDialog() != true) { Shutdown(); return; }
        }
        if (Identity.WasReset)
            MessageBox.Show("המפתח של המחשב לא נטען, ולכן נוצר מפתח חדש.\nהמכשירים המוכרים יבקשו את הסיסמה פעם אחת.",
                "גשר בלוטוס", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK,
                MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        Core.Start();
        try { _clipboard = new ClipboardWatcher(Core); }
        catch (Exception ex) { Log.Write("clipboard watcher: " + ex.Message); }

        _tray = new Forms.NotifyIcon
        {
            Icon = AppIcon.Tray(),
            Text = "גשר בלוטוס",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip { RightToLeft = Forms.RightToLeft.Yes },
        };
        _tray.ContextMenuStrip.Items.Add("פתח", null, (_, _) => ShowMain());
        _tray.ContextMenuStrip.Items.Add("יציאה", null, (_, _) => ExitApp());
        _tray.DoubleClick += (_, _) => ShowMain();
        Core.Notice += msg => Dispatcher.BeginInvoke(() => Balloon(msg));
        // The phone's notifications, next to the clock.
        Core.PhoneNotification += n => Dispatcher.BeginInvoke(() =>
        {
            Balloon(string.IsNullOrWhiteSpace(n.Text) ? n.Title : (string.IsNullOrWhiteSpace(n.Title) ? n.Text : n.Title + "\n" + n.Text),
                $"{Core.DisplayName(n.S)} · {n.App}");
            _noteBalloon = true;
            _notes?.Refresh();
        });
        // A click on a phone notification opens the list, to answer or open it; any other message opens the main window.
        _tray.BalloonTipClicked += (_, _) => { if (_noteBalloon) ShowNotes(); else ShowMain(); };

        // Started with Windows: stay next to the clock.
        if (e.Args.Contains("--tray")) _main = new MainWindow();
        else ShowMain();
    }

    private bool _noteBalloon;
    private NotesWindow? _notes;

    /// <summary>The phone's notifications, with "answer", "open on the phone" and "close".</summary>
    public void ShowNotes()
    {
        if (_notes == null)
        {
            _notes = new NotesWindow();
            _notes.Closed += (_, _) => _notes = null;
        }
        _notes.Show();
        _notes.Activate();
    }

    public void Balloon(string msg, string title = "גשר בלוטוס")
    {
        _noteBalloon = false;
        if (_tray == null || msg.Length == 0) return;
        _tray.BalloonTipTitle = title.Length > 60 ? title[..60] : title;
        _tray.BalloonTipText = msg.Length > 250 ? msg[..250] + "…" : msg;
        _tray.ShowBalloonTip(3000);
    }

    public void ShowMain()
    {
        _main ??= new MainWindow();
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ExitApp()
    {
        Exiting = true;
        if (_tray != null) _tray.Visible = false;
        _tray?.Dispose();
        foreach (var s in Core.Sessions) s.Close();
        Shutdown();
    }
}

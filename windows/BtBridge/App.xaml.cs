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
        Core.Start();

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
        _tray.BalloonTipClicked += (_, _) => ShowMain();

        // Started with Windows: stay next to the clock.
        if (e.Args.Contains("--tray")) _main = new MainWindow();
        else ShowMain();
    }

    public void Balloon(string msg)
    {
        if (_tray == null) return;
        _tray.BalloonTipTitle = "גשר בלוטוס";
        _tray.BalloonTipText = msg;
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

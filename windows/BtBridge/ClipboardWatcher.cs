using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BtBridge.Core;

namespace BtBridge;

/// <summary>
/// Shared clipboard (PROTOCOL.md section 5, "clip"): text copied here goes to the connected devices,
/// and text copied there lands on this computer's clipboard.
/// </summary>
public sealed class ClipboardWatcher
{
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly Bridge _b;
    private readonly HwndSource _src;
    /// <summary>The last text that came from another device, so it isn't sent straight back.</summary>
    private string? _fromPeer;

    public ClipboardWatcher(Bridge b)
    {
        _b = b;
        // A hidden message-only window: it gets clipboard notices even while the main window is closed to the tray.
        _src = new HwndSource(new HwndSourceParameters("btbridge-clipboard") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        _src.AddHook(Hook);
        AddClipboardFormatListener(_src.Handle);
        b.ClipboardReceived += (_, text) => Application.Current.Dispatcher.BeginInvoke(() => Put(text));
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) OnChanged();
        return IntPtr.Zero;
    }

    private void OnChanged()
    {
        if (!_b.Settings.ShareClipboard) return;
        string? text = Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        if (string.IsNullOrEmpty(text) || text == _fromPeer) return;
        _fromPeer = null;
        Task.Run(() => _b.ShareClipboard(text));
    }

    private void Put(string text)
    {
        _fromPeer = text;
        Retry<object?>(() => { Clipboard.SetText(text); return null; });
    }

    /// <summary>Another program may hold the clipboard for a moment.</summary>
    private static T? Retry<T>(Func<T> f)
    {
        for (int i = 0; i < 5; i++)
        {
            try { return f(); }
            catch (COMException) { Thread.Sleep(50); }
            catch (Exception ex) { Log.Write("clipboard: " + ex.Message); return default; }
        }
        return default;
    }
}

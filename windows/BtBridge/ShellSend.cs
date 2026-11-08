using System.IO.Pipes;
using System.Text;
using BtBridge.Core;

namespace BtBridge;

/// <summary>
/// "Send to → גשר בלוטוס" in File Explorer's right-click menu: a shortcut in the user's Send To folder runs this program
/// with "--send" and the chosen files. When the program is already running, the files are handed to it over a local
/// pipe (only this Windows user can open it) and the new copy ends at once.
/// </summary>
public static class ShellSend
{
    public const string Arg = "--send";
    // The same in every copy of the program (string.GetHashCode differs between runs), and per Windows user.
    private static readonly string PipeName = "BtBridge-send-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16];

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "גשר בלוטוס.lnk");

    /// <summary>The files after "--send", or null when the program was not started that way.</summary>
    public static string[]? FilesIn(string[] args)
    {
        int i = Array.IndexOf(args, Arg);
        return i < 0 ? null : args.Skip(i + 1).Where(a => a.Length > 0).ToArray();
    }

    /// <summary>Hands the files to the copy that is already running. False when it could not be reached.</summary>
    public static bool TryHandOver(string[] files)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", files));
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("send-to hand over: " + ex.Message);
            return false;
        }
    }

    /// <summary>Listens for files from new copies started by "Send to". <paramref name="onFiles"/> runs on a background thread.</summary>
    public static void Listen(Action<string[]> onFiles)
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();
                    using var ms = new MemoryStream();
                    pipe.CopyTo(ms);
                    var files = Encoding.UTF8.GetString(ms.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (files.Length > 0) onFiles(files);
                }
                catch (Exception ex)
                {
                    Log.Write("send-to listen: " + ex.Message);
                    Thread.Sleep(1000);
                }
            }
        }) { IsBackground = true, Name = "send-to" }.Start();
    }

    /// <summary>Adds or removes the "Send to" entry. Kept pointing at this program's current place on every start.</summary>
    public static void Sync(bool on)
    {
        try
        {
            if (!on)
            {
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                return;
            }
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic lnk = shell.CreateShortcut(ShortcutPath);
                lnk.TargetPath = exe;
                lnk.Arguments = Arg;
                lnk.IconLocation = exe + ",0";
                lnk.Description = "שולח את הקבצים לטלפון דרך גשר בלוטוס";
                lnk.WorkingDirectory = Path.GetDirectoryName(exe);
                lnk.Save();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(lnk);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception ex) { Log.Write("send-to shortcut: " + ex.Message); }
    }
}

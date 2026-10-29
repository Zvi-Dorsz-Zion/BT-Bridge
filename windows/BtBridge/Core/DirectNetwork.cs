using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;

namespace BtBridge.Core;

/// <summary>
/// Direct network (PROTOCOL.md section 8): joins the phone's temporary hotspot with a temporary profile
/// through the native Wi-Fi API (wlanapi.dll, no admin rights), and later goes back to the previous network.
/// </summary>
public sealed class DirectNetwork
{
    private readonly object _gate = new();
    private string? _joined;
    private string? _previous;
    private Guid _iface;

    public bool Joined { get { lock (_gate) return _joined != null; } }

    public static bool HasWifiAdapter()
    {
        try
        {
            using var w = new Wlan();
            return w.Interface() != null;
        }
        catch { return false; }
    }

    /// <summary>Connects and waits (up to 30 seconds) for an address on the hotspot.</summary>
    public Task<bool> JoinAsync(string ssid, string pass) => Task.Run(async () =>
    {
        try
        {
            using var w = new Wlan();
            var iface = w.Interface() ?? throw new InvalidOperationException("אין מתאם וויפי");
            var current = w.CurrentProfile(iface);
            lock (_gate)
            {
                if (_joined == null && current != ssid) _previous = current;
                _iface = iface;
            }
            if (current != ssid || !HasAddress(iface))
            {
                Log.Write($"direct: joining {ssid} (was on {current ?? "-"})");
                w.SetProfile(iface, Profile(ssid, pass));
                w.Connect(iface, ssid);
            }
            for (int i = 0; i < 60; i++)
            {
                if (w.CurrentProfile(iface) == ssid && HasAddress(iface))
                {
                    lock (_gate) _joined = ssid;
                    Log.Write("direct: joined");
                    return true;
                }
                await Task.Delay(500);
            }
            Log.Write("direct: join timed out");
            lock (_gate) _joined = ssid;
            Leave();
            return false;
        }
        catch (Exception ex)
        {
            Log.Write("direct: join failed: " + ex.Message);
            return false;
        }
    });

    /// <summary>Deletes the temporary profile and reconnects to the network the computer was on before.</summary>
    public void Leave()
    {
        string? ssid, previous;
        Guid iface;
        lock (_gate)
        {
            ssid = _joined;
            previous = _previous;
            iface = _iface;
            _joined = null;
            _previous = null;
        }
        if (ssid == null) return;
        try
        {
            using var w = new Wlan();
            try { w.DeleteProfile(iface, ssid); } catch { }
            if (!string.IsNullOrEmpty(previous) && previous != ssid) w.Connect(iface, previous);
            Log.Write($"direct: left {ssid}, back to {previous ?? "-"}");
        }
        catch (Exception ex) { Log.Write("direct: leave failed: " + ex.Message); }
    }

    private static bool HasAddress(Guid iface)
    {
        var id = iface.ToString("B");
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
        return nic is { OperationalStatus: OperationalStatus.Up } &&
               nic.GetIPProperties().UnicastAddresses.Any(a =>
                   a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."));
    }

    private static string Profile(string ssid, string pass)
    {
        var name = SecurityElement.Escape(ssid);
        var key = SecurityElement.Escape(pass);
        return $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{name}</name>
              <SSIDConfig><SSID><name>{name}</name></SSID></SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>manual</connectionMode>
              <MSM><security>
                <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
                <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{key}</keyMaterial></sharedKey>
              </security></MSM>
            </WLANProfile>
            """;
    }

    /// <summary>Minimal wlanapi.dll wrapper.</summary>
    private sealed class Wlan : IDisposable
    {
        private readonly IntPtr _h;

        public Wlan()
        {
            int err = WlanOpenHandle(2, IntPtr.Zero, out _, out _h);
            if (err != 0) throw new InvalidOperationException("WlanOpenHandle " + err);
        }

        public void Dispose() => WlanCloseHandle(_h, IntPtr.Zero);

        /// <summary>The Wi-Fi adapter (the connected one when there are several).</summary>
        public Guid? Interface()
        {
            if (WlanEnumInterfaces(_h, IntPtr.Zero, out var list) != 0) return null;
            try
            {
                int n = Marshal.ReadInt32(list);
                Guid? first = null;
                for (int i = 0; i < n; i++)
                {
                    var item = list + 8 + i * 532; // WLAN_INTERFACE_INFO: GUID + WCHAR[256] + state
                    var g = Marshal.PtrToStructure<Guid>(item);
                    first ??= g;
                    if (Marshal.ReadInt32(item, 528) == 1) return g; // connected
                }
                return first;
            }
            finally { WlanFreeMemory(list); }
        }

        public string? CurrentProfile(Guid iface)
        {
            // wlan_intf_opcode_current_connection = 7; the profile name sits at offset 8 of WLAN_CONNECTION_ATTRIBUTES
            if (WlanQueryInterface(_h, ref iface, 7, IntPtr.Zero, out _, out var data, out _) != 0) return null;
            try { return Marshal.PtrToStringUni(data + 8); }
            finally { WlanFreeMemory(data); }
        }

        public void SetProfile(Guid iface, string xml)
        {
            int err = WlanSetProfile(_h, ref iface, 0, xml, null, true, IntPtr.Zero, out var reason);
            if (err != 0) err = WlanSetProfile(_h, ref iface, 2 /* per user */, xml, null, true, IntPtr.Zero, out reason);
            if (err != 0) throw new InvalidOperationException($"WlanSetProfile {err} ({reason})");
        }

        public void Connect(Guid iface, string profile)
        {
            var p = new CONNECTION_PARAMETERS { Mode = 0, Profile = profile, BssType = 1 };
            int err = WlanConnect(_h, ref iface, ref p, IntPtr.Zero);
            if (err != 0) throw new InvalidOperationException("WlanConnect " + err);
        }

        public void DeleteProfile(Guid iface, string profile) => WlanDeleteProfile(_h, ref iface, profile, IntPtr.Zero);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CONNECTION_PARAMETERS
        {
            public int Mode;
            [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
            public IntPtr Ssid, BssidList;
            public int BssType;
            public uint Flags;
        }

        [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint ver, IntPtr r, out uint negotiated, out IntPtr h);
        [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr h, IntPtr r);
        [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr h, IntPtr r, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);
        [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr h, ref Guid iface, int op, IntPtr r, out int size, out IntPtr data, out int type);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern int WlanSetProfile(IntPtr h, ref Guid iface, uint flags, string xml, string? sec, bool overwrite, IntPtr r, out uint reason);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern int WlanDeleteProfile(IntPtr h, ref Guid iface, string name, IntPtr r);
        [DllImport("wlanapi.dll")] private static extern int WlanConnect(IntPtr h, ref Guid iface, ref CONNECTION_PARAMETERS p, IntPtr r);
    }
}

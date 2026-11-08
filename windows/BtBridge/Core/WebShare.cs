using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BtBridge.Core;

/// <summary>A file offered through <see cref="WebShare"/>: its local path and the name the other device sees.</summary>
public sealed record WebShareItem(string Path, string Name, long Size);

/// <summary>
/// Sends files to a device that doesn't have this program: a tiny web server on the local network, reached with a
/// long secret address (no internet involved). Only the chosen files are served; nothing else on this computer is reachable.
/// Separate from the encrypted link between the program and the app.
/// </summary>
public sealed class WebShare : IDisposable
{
    private const int MaxConnections = 16;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);

    private readonly List<WebShareItem> _items;
    private readonly string? _uploadDir;
    private readonly TcpListener _listener = new(IPAddress.Any, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections);
    private long _lastActivity = Environment.TickCount64;
    private long _sent;
    private int _stopped;

    public string Token { get; } = NewToken();
    public int Port { get; private set; }
    public string PathPrefix => "/" + Token + "/";

    /// <summary>Whether the other device may send files back (only when an upload folder was given).</summary>
    public volatile bool AllowUpload;

    /// <summary>Bytes sent to devices so far.</summary>
    public long SentBytes => Interlocked.Read(ref _sent);

    /// <summary>A file arrived (runs on a background thread).</summary>
    public event Action<string>? Uploaded;

    /// <summary>The server closed by itself after a long time with no use (runs on a background thread).</summary>
    public event Action? IdleStopped;

    public WebShare(IEnumerable<WebShareItem> items, string? uploadDir)
    {
        _items = items.ToList();
        _uploadDir = uploadDir;
    }

    public string UrlFor(string ip) => $"http://{ip}:{Port}{PathPrefix}";

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(AcceptLoop) { IsBackground = true, Name = "web-share" }.Start();
        new Thread(IdleLoop) { IsBackground = true, Name = "web-share-idle" }.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    private void IdleLoop()
    {
        try
        {
            while (!_cts.Token.WaitHandle.WaitOne(15_000))
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastActivity) < IdleLimit.TotalMilliseconds) continue;
                Dispose();
                try { IdleStopped?.Invoke(); } catch (Exception ex) { Log.Write("web share idle: " + ex.Message); }
                return;
            }
        }
        catch (Exception ex) { Log.Write("web share idle: " + ex.Message); }
    }

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested) Log.Write("web share accept: " + ex.Message);
                return;
            }
            new Thread(() => Serve(client)) { IsBackground = true, Name = "web-share-client" }.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var net = client.GetStream();
                if (!_slots.Wait(0))
                {
                    Write(net, 503, "Service Unavailable", "text/plain; charset=utf-8", "עסוק");
                    return;
                }
                try
                {
                    client.ReceiveTimeout = 15_000;
                    client.SendTimeout = 60_000;
                    if (ReadHead(net) is not { } req) return;
                    client.ReceiveTimeout = 60_000;
                    Touch();
                    Route(net, req);
                }
                finally { _slots.Release(); }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { /* the other side left */ }
            catch (Exception ex) { Log.Write("web share: " + ex.Message); }
        }
    }

    // ---------------------------------------------------------------- HTTP

    private sealed record Request(string Method, string Path, string Query, Dictionary<string, string> Headers, byte[] Extra);

    private static Request? ReadHead(Stream s)
    {
        var buf = new byte[16 * 1024];
        int have = 0;
        while (have < buf.Length)
        {
            int n = s.Read(buf, have, buf.Length - have);
            if (n <= 0) return null;
            have += n;
            int end = IndexOfBlankLine(buf, have);
            if (end < 0) continue;
            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 3) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
            {
                int c = l.IndexOf(':');
                if (c > 0) headers[l[..c].Trim()] = l[(c + 1)..].Trim();
            }
            var target = first[1];
            int q = target.IndexOf('?');
            var extra = buf.AsSpan(end + 4, have - end - 4).ToArray();
            return new Request(first[0], q < 0 ? target : target[..q], q < 0 ? "" : target[(q + 1)..], headers, extra);
        }
        return null;
    }

    private static int IndexOfBlankLine(byte[] b, int n)
    {
        for (int i = 0; i + 3 < n; i++)
            if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
        return -1;
    }

    private void Route(Stream net, Request r)
    {
        if (!r.Path.StartsWith(PathPrefix, StringComparison.Ordinal))
        {
            // Not the secret address: nothing to learn from the answer, and no quick guessing.
            Thread.Sleep(200);
            Write(net, 404, "Not Found", "text/plain; charset=utf-8", "לא נמצא");
            return;
        }
        var rest = r.Path[PathPrefix.Length..];
        bool reading = r.Method is "GET" or "HEAD";
        if (rest.Length == 0 && reading)
        {
            Write(net, 200, "OK", "text/html; charset=utf-8", Page(), r.Method == "HEAD");
            return;
        }
        var m = Regex.Match(rest, @"^f/(\d+)(/|$)");
        if (m.Success && reading)
        {
            if (!int.TryParse(m.Groups[1].Value, out var i) || i < 0 || i >= _items.Count)
            {
                Write(net, 404, "Not Found", "text/plain; charset=utf-8", "לא נמצא");
                return;
            }
            SendFile(net, r, _items[i]);
            return;
        }
        if (rest == "upload" && r.Method == "POST")
        {
            Upload(net, r);
            return;
        }
        Write(net, 404, "Not Found", "text/plain; charset=utf-8", "לא נמצא");
    }

    private static string StatusHead(int code, string reason, string type, long? length, string? extra = null)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {code} {reason}\r\n");
        sb.Append($"Content-Type: {type}\r\n");
        if (length != null) sb.Append($"Content-Length: {length}\r\n");
        sb.Append("Connection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n");
        if (extra != null) sb.Append(extra);
        sb.Append("\r\n");
        return sb.ToString();
    }

    private static void Write(Stream net, int code, string reason, string type, string body, bool headOnly = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(StatusHead(code, reason, type, bytes.Length));
        net.Write(head);
        if (!headOnly) net.Write(bytes);
        net.Flush();
    }

    private void SendFile(Stream net, Request r, WebShareItem item)
    {
        FileStream fs;
        try { fs = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }
        catch (Exception ex)
        {
            Log.Write("web share open: " + ex.Message);
            Write(net, 404, "Not Found", "text/plain; charset=utf-8", "הקובץ לא נמצא");
            return;
        }
        using (fs)
        {
            long size = fs.Length, from = 0, to = size - 1;
            bool partial = false;
            if (r.Headers.TryGetValue("Range", out var range))
            {
                var m = Regex.Match(range, @"^bytes=(\d+)-(\d*)$");
                if (m.Success)
                {
                    from = long.Parse(m.Groups[1].Value);
                    if (m.Groups[2].Length > 0) to = Math.Min(to, long.Parse(m.Groups[2].Value));
                    if (from >= size || from > to)
                    {
                        var h416 = StatusHead(416, "Range Not Satisfiable", "text/plain; charset=utf-8", 0, $"Content-Range: bytes */{size}\r\n");
                        net.Write(Encoding.ASCII.GetBytes(h416));
                        return;
                    }
                    partial = true;
                }
            }
            long count = size == 0 ? 0 : to - from + 1;
            var extra = $"Accept-Ranges: bytes\r\nContent-Disposition: attachment; filename*=UTF-8''{Uri.EscapeDataString(Path.GetFileName(item.Name))}\r\n";
            if (partial) extra += $"Content-Range: bytes {from}-{to}/{size}\r\n";
            net.Write(Encoding.ASCII.GetBytes(StatusHead(partial ? 206 : 200, partial ? "Partial Content" : "OK", "application/octet-stream", count, extra)));
            if (r.Method == "HEAD") return;
            fs.Seek(from, SeekOrigin.Begin);
            var buf = new byte[1 << 16];
            long left = count;
            while (left > 0 && !_cts.IsCancellationRequested)
            {
                int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0) break;
                net.Write(buf, 0, n);
                left -= n;
                Interlocked.Add(ref _sent, n);
                Touch();
            }
            net.Flush();
        }
    }

    /// <summary>The last part of a name the other device sent, without anything that could lead outside the folder. Null when nothing is left.</summary>
    public static string? SafeName(string raw)
    {
        var name = raw.Replace('\\', '/').Split('/').Last();
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(c < 32 || "<>:\"|?*".IndexOf(c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length == 0 || s == "." || s == "..") return null;
        // Names Windows keeps for itself (con, nul, com1...).
        if (Regex.IsMatch(Path.GetFileNameWithoutExtension(s), @"^(con|prn|aux|nul|com\d|lpt\d)$", RegexOptions.IgnoreCase)) s = "_" + s;
        return s.Length > 200 ? s[^200..] : s;
    }

    private void Upload(Stream net, Request r)
    {
        const string json = "application/json; charset=utf-8";
        if (_uploadDir == null || !AllowUpload) { Write(net, 403, "Forbidden", json, "{\"ok\":false}"); return; }
        if (!r.Headers.TryGetValue("Content-Length", out var lenText) || !long.TryParse(lenText, out var length) || length < 0)
        {
            Write(net, 411, "Length Required", json, "{\"ok\":false}");
            return;
        }
        string? raw = null;
        foreach (var part in r.Query.Split('&'))
            if (part.StartsWith("name=", StringComparison.Ordinal)) raw = Uri.UnescapeDataString(part[5..].Replace('+', ' '));
        if (raw == null || SafeName(raw) is not { } name) { Write(net, 400, "Bad Request", json, "{\"ok\":false}"); return; }

        Directory.CreateDirectory(_uploadDir);
        try
        {
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_uploadDir))!).AvailableFreeSpace < length)
            {
                Write(net, 507, "Insufficient Storage", json, "{\"ok\":false}");
                return;
            }
        }
        catch (Exception ex) { Log.Write("web share free space: " + ex.Message); }

        var target = FileService.UniquePath(Path.Combine(_uploadDir, name));
        var part2 = target + ".btpart";
        long got = 0;
        try
        {
            using (var fs = new FileStream(part2, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                int first = (int)Math.Min(r.Extra.Length, length);
                fs.Write(r.Extra, 0, first);
                got = first;
                var buf = new byte[1 << 16];
                while (got < length && !_cts.IsCancellationRequested)
                {
                    int n = net.Read(buf, 0, (int)Math.Min(buf.Length, length - got));
                    if (n <= 0) break;
                    fs.Write(buf, 0, n);
                    got += n;
                    Touch();
                }
            }
            if (got != length) throw new IOException("נקטע");
            target = FileService.UniquePath(target);
            File.Move(part2, target);
        }
        catch
        {
            try { File.Delete(part2); } catch { }
            throw;
        }
        Write(net, 200, "OK", json, "{\"ok\":true}");
        try { Uploaded?.Invoke(target); } catch (Exception ex) { Log.Write("web share uploaded: " + ex.Message); }
    }

    // ---------------------------------------------------------------- the page

    private string Page()
    {
        var rows = new StringBuilder();
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            rows.Append("<li><span class=n>").Append(WebUtility.HtmlEncode(it.Name)).Append("</span><span class=s>")
                .Append(WebUtility.HtmlEncode(Transfer.FormatSize(it.Size))).Append("</span><a class=b href=\"f/").Append(i).Append('/')
                .Append(Uri.EscapeDataString(it.Name)).Append("\" download>הורד</a></li>");
        }
        var upload = _uploadDir != null && AllowUpload ? UploadBlock : "";
        return PageTop + rows + "</ul>" + upload + PageEnd;
    }

    private const string PageTop = """
<!doctype html><html dir="rtl" lang="he"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>גשר בלוטוס</title><style>
body{font-family:system-ui,Arial,sans-serif;margin:0;background:#f3f4f6;color:#111827}
main{max-width:640px;margin:0 auto;padding:16px}
h1{font-size:20px;margin:8px 0 14px}
ul{list-style:none;padding:0;margin:0}
li{display:flex;align-items:center;gap:10px;background:#fff;border-radius:8px;padding:10px 12px;margin-bottom:8px}
.n{flex:1;word-break:break-word}.s{color:#6b7280;font-size:13px;white-space:nowrap}
.b{background:#2563eb;color:#fff;text-decoration:none;padding:8px 14px;border-radius:6px}
.up{background:#fff;border-radius:8px;padding:12px;margin-top:16px}
.note{color:#6b7280;font-size:13px;margin-top:16px}
</style></head><body><main><h1>קבצים מגשר בלוטוס</h1><ul>
""";

    private const string UploadBlock = """
<div class=up><b>שליחת קבצים למחשב</b><br><br><input id=f type=file multiple><div id=m></div></div>
<script>
const f=document.getElementById('f'),m=document.getElementById('m');
function one(file){return new Promise((ok,no)=>{const x=new XMLHttpRequest();
x.open('POST','upload?name='+encodeURIComponent(file.name));
x.upload.onprogress=e=>{if(e.lengthComputable)m.textContent='שולח את '+file.name+': '+Math.round(e.loaded*100/e.total)+'%'};
x.onload=()=>x.status==200?ok():no();x.onerror=no;x.send(file)})}
f.onchange=async()=>{try{for(const x of f.files)await one(x);m.textContent='הקבצים נשלחו.'}catch(e){m.textContent='השליחה נכשלה.'}f.value=''};
</script>
""";

    private const string PageEnd = """
<p class=note>החיבור הזה לא מוצפן. השתמשו בו רק ברשת שאתם סומכים עליה.</p></main></body></html>
""";

    private static string NewToken()
    {
        const string abc = "abcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(32);
        for (int i = 0; i < 32; i++) sb.Append(abc[RandomNumberGenerator.GetInt32(abc.Length)]);
        return sb.ToString();
    }
}

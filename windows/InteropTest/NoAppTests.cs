using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using BtBridge.Core;

/// <summary>Sending to devices without the program: the web link and the Bluetooth (OBEX) sender.</summary>
public static class NoAppTests
{
    public static async Task<int> RunWeb()
    {
        int fails = 0;
        void Check(string name, bool ok, string more = "") { Console.WriteLine($"{name} {(ok ? "ok" : "FAILED " + more)}"); if (!ok) fails++; }
        var dir = Path.Combine(Path.GetTempPath(), "btb-web-" + Guid.NewGuid().ToString("N"));
        var inbox = Path.Combine(dir, "inbox");
        Directory.CreateDirectory(dir);
        var name = "קובץ בדיקה.bin";
        var path = Path.Combine(dir, name);
        var content = new byte[300_000];
        new Random(5).NextBytes(content);
        File.WriteAllBytes(path, content);
        using var share = new WebShare(new[] { new WebShareItem(path, name, content.Length) }, inbox);
        share.Start();
        var baseUrl = share.UrlFor("127.0.0.1");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };

        var page = await http.GetStringAsync(baseUrl);
        Check("WEB-PAGE", page.Contains(name) && page.Contains("dir=\"rtl\""));
        Check("WEB-NO-UPLOAD-FORM", !page.Contains("type=file"));

        var fileUrl = baseUrl + "f/0/" + Uri.EscapeDataString(name);
        var got = await http.GetByteArrayAsync(fileUrl);
        Check("WEB-DOWNLOAD", got.SequenceEqual(content), $"{got.Length}");

        var req = new HttpRequestMessage(HttpMethod.Get, fileUrl);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1999);
        var res = await http.SendAsync(req);
        var part = await res.Content.ReadAsByteArrayAsync();
        Check("WEB-RANGE", res.StatusCode == HttpStatusCode.PartialContent && part.SequenceEqual(content.Skip(1000).Take(1000)), $"{res.StatusCode} {part.Length}");

        var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, fileUrl));
        Check("WEB-HEAD", head.IsSuccessStatusCode && head.Content.Headers.ContentLength == content.Length);
        var cd = head.Content.Headers.ContentDisposition;
        Check("WEB-NAME", cd?.FileNameStar == name, cd?.ToString() ?? "");

        var wrong = await http.GetAsync($"http://127.0.0.1:{share.Port}/wrongtoken/");
        Check("WEB-WRONG-TOKEN", wrong.StatusCode == HttpStatusCode.NotFound);
        var outside = await http.GetAsync(baseUrl + "f/7/x");
        Check("WEB-NO-SUCH-FILE", outside.StatusCode == HttpStatusCode.NotFound);

        async Task<HttpResponseMessage> Up(string n, byte[] body) =>
            await http.PostAsync(baseUrl + "upload?name=" + Uri.EscapeDataString(n), new ByteArrayContent(body));

        var body = Encoding.UTF8.GetBytes("תוכן שהועלה");
        Check("WEB-UPLOAD-OFF", (await Up("a.txt", body)).StatusCode == HttpStatusCode.Forbidden);
        share.AllowUpload = true;
        Check("WEB-UPLOAD-FORM", (await http.GetStringAsync(baseUrl)).Contains("type=file"));
        var r1 = await Up("העלאה.txt", body);
        var saved = Path.Combine(inbox, "העלאה.txt");
        Check("WEB-UPLOAD", r1.IsSuccessStatusCode && File.Exists(saved) && File.ReadAllBytes(saved).SequenceEqual(body), r1.StatusCode.ToString());
        await Up("העלאה.txt", body);
        Check("WEB-UPLOAD-DUP", File.Exists(Path.Combine(inbox, "העלאה (1).txt")));
        await Up("../../evil.txt", body);
        Check("WEB-UPLOAD-SAFE", File.Exists(Path.Combine(inbox, "evil.txt")) && !File.Exists(Path.Combine(dir, "evil.txt")) && !File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "evil.txt")));
        await Up(@"..\..\evil2.txt", body);
        Check("WEB-UPLOAD-SAFE2", File.Exists(Path.Combine(inbox, "evil2.txt")));
        var bad = await Up("..", body);
        Check("WEB-UPLOAD-BADNAME", bad.StatusCode == HttpStatusCode.BadRequest, bad.StatusCode.ToString());
        Check("WEB-NO-LEFTOVERS", !Directory.EnumerateFiles(inbox).Any(f => f.EndsWith(".btpart")));

        share.Dispose();
        bool closed = false;
        try { await http.GetStringAsync(baseUrl); } catch (HttpRequestException) { closed = true; }
        Check("WEB-CLOSED", closed);
        try { Directory.Delete(dir, true); } catch { }
        Console.WriteLine(fails == 0 ? "OK" : "FAIL");
        return fails == 0 ? 0 : 1;
    }

    public static async Task<int> RunObex()
    {
        int fails = 0;
        void Check(string name, bool ok, string more = "") { Console.WriteLine($"{name} {(ok ? "ok" : "FAILED " + more)}"); if (!ok) fails++; }
        foreach (var (label, size, maxPacket) in new[] { ("EMPTY", 0, 4096), ("ONE", 1, 4096), ("BIG", 500_000, 4096), ("SMALL-PACKETS", 20_000, 255) })
        {
            var data = new byte[size];
            new Random(size).NextBytes(data);
            var name = "תמונה " + label + ".jpg";
            var (ok, gotName, gotLen, gotBody) = await Exchange(name, data, maxPacket, refuse: false);
            Check("OBEX-" + label, ok && gotName == name && gotLen == size && gotBody.SequenceEqual(data), $"{ok} {gotName} {gotLen} {gotBody.Length}");
        }
        // A packet size that makes the data end exactly on a packet: one more, empty, final packet.
        // 3 (packet head) + 7 (name "a") + 5 (length) + 3 (data head).
        var exact = new byte[4096 - 18];
        var (ok2, _, _, body2) = await Exchange("a", exact, 4096, refuse: false);
        Check("OBEX-EXACT", ok2 && body2.Length == exact.Length, $"{body2.Length}");
        var (refused, _, _, _) = await Exchange("x.bin", new byte[100], 4096, refuse: true);
        Check("OBEX-REFUSED", !refused);
        Console.WriteLine(fails == 0 ? "OK" : "FAIL");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>A small OBEX receiver on a local socket, as a phone would be.</summary>
    private static async Task<(bool Ok, string Name, long Length, byte[] Body)> Exchange(string name, byte[] data, int maxPacket, bool refuse)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        string gotName = "";
        long gotLen = -1;
        var body = new MemoryStream();
        var server = Task.Run(() =>
        {
            using var c = l.AcceptTcpClient();
            var s = c.GetStream();
            while (true)
            {
                var head = new byte[3];
                if (!ReadAll(s, head)) return;
                int len = (head[1] << 8) | head[2];
                var rest = new byte[len - 3];
                if (!ReadAll(s, rest)) return;
                switch (head[0])
                {
                    case 0x80:
                        s.Write(new byte[] { 0xA0, 0, 7, 0x10, 0, (byte)(maxPacket >> 8), (byte)maxPacket });
                        break;
                    case 0x81:
                        s.Write(new byte[] { 0xA0, 0, 3 });
                        return;
                    case 0x02 or 0x82:
                    {
                        for (int i = 0; i < rest.Length;)
                        {
                            byte h = rest[i];
                            if ((h & 0xC0) == 0xC0)
                            {
                                gotLen = (uint)((rest[i + 1] << 24) | (rest[i + 2] << 16) | (rest[i + 3] << 8) | rest[i + 4]);
                                i += 5;
                                continue;
                            }
                            int hl = (rest[i + 1] << 8) | rest[i + 2];
                            if (h == 0x01) gotName = Encoding.BigEndianUnicode.GetString(rest, i + 3, hl - 3).TrimEnd('\0');
                            else if (h is 0x48 or 0x49) body.Write(rest, i + 3, hl - 3);
                            i += hl;
                        }
                        if (refuse) { s.Write(new byte[] { 0xC3, 0, 3 }); return; }
                        s.Write(new byte[] { head[0] == 0x82 ? (byte)0xA0 : (byte)0x90, 0, 3 });
                        break;
                    }
                }
            }
        });
        bool ok = true;
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Port);
            try { ObexSender.Send(client.GetStream(), name, data.Length, new MemoryStream(data), null, CancellationToken.None); }
            catch (IOException) { ok = false; }
        }
        try { await server.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        l.Stop();
        return (ok, gotName, gotLen, body.ToArray());
    }

    private static bool ReadAll(Stream s, byte[] b)
    {
        int have = 0;
        while (have < b.Length)
        {
            int n = s.Read(b, have, b.Length - have);
            if (n <= 0) return false;
            have += n;
        }
        return true;
    }
}

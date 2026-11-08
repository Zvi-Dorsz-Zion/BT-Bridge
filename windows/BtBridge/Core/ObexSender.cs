using System.Buffers.Binary;
using System.Text;

namespace BtBridge.Core;

/// <summary>
/// Sends one file with OBEX Object Push, the plain "send over Bluetooth" every Android phone and Windows computer
/// understands, so the other device needs nothing installed. Works over any stream (RFCOMM, or memory in the tests).
/// </summary>
public static class ObexSender
{
    private const byte OpConnect = 0x80, OpDisconnect = 0x81, OpPut = 0x02, OpPutFinal = 0x82;
    private const byte HName = 0x01, HLength = 0xC3, HBody = 0x48, HEndBody = 0x49;
    private const byte RespContinue = 0x90, RespOk = 0xA0;

    public static void Send(Stream s, string name, long size, Stream data, Action<int>? progress, CancellationToken ct)
    {
        // CONNECT: OBEX version 1.0, no flags, the largest packet we take.
        var connect = new byte[] { OpConnect, 0, 7, 0x10, 0, 0xFF, 0xFE };
        s.Write(connect);
        s.Flush();
        var (code, body) = ReadResponse(s);
        if (code != RespOk || body.Length < 4) throw new IOException(Refused(code));
        int max = Math.Clamp((int)BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2)), 255, 0xFFFE);

        // A device with very small packets: the name must leave room for the rest (the extension is kept).
        int longest = Math.Max(8, (max - 32) / 2 - 1);
        if (name.Length > longest) name = name[^longest..];
        var nameBytes = Encoding.BigEndianUnicode.GetBytes(name + "\0");
        var head = new MemoryStream();
        head.WriteByte(HName);
        WriteU16(head, nameBytes.Length + 3);
        head.Write(nameBytes);
        // Files of 4 GB and more can't state their size; it is optional.
        if (size is >= 0 and <= uint.MaxValue)
        {
            head.WriteByte(HLength);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)size);
            head.Write(len);
        }

        var chunk = new byte[max];
        bool first = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var extra = first ? head.ToArray() : Array.Empty<byte>();
            int room = max - 3 - extra.Length - 3;
            int n = ReadFull(data, chunk, room);
            bool last = n < room;
            var packet = new MemoryStream(n + extra.Length + 6);
            packet.WriteByte(last ? OpPutFinal : OpPut);
            WriteU16(packet, 3 + extra.Length + 3 + n);
            packet.Write(extra);
            packet.WriteByte(last ? HEndBody : HBody);
            WriteU16(packet, n + 3);
            packet.Write(chunk, 0, n);
            s.Write(packet.GetBuffer(), 0, (int)packet.Length);
            s.Flush();
            first = false;
            (code, _) = ReadResponse(s);
            if (code != (last ? RespOk : RespContinue)) throw new IOException(Refused(code));
            progress?.Invoke(n);
            if (last) break;
        }

        try
        {
            s.Write(new byte[] { OpDisconnect, 0, 3 });
            s.Flush();
            ReadResponse(s);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* the file already arrived */ }
    }

    private static string Refused(byte code) => code switch
    {
        0xC3 or 0xC1 => "המכשיר השני סירב לקבל את הקובץ.",
        0xCD or 0xE0 => "אין מקום במכשיר השני.",
        _ => "המכשיר השני לא קיבל את הקובץ.",
    };

    private static void WriteU16(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static int ReadFull(Stream s, byte[] buf, int count)
    {
        int have = 0;
        while (have < count)
        {
            int n = s.Read(buf, have, count - have);
            if (n <= 0) break;
            have += n;
        }
        return have;
    }

    /// <summary>A response: its code (without the "final" meaning changing it) and what follows the 3-byte head.</summary>
    private static (byte Code, byte[] Body) ReadResponse(Stream s)
    {
        var head = new byte[3];
        if (ReadFull(s, head, 3) != 3) throw new IOException("החיבור נסגר");
        int len = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1));
        if (len < 3) throw new IOException("תשובה לא תקינה מהמכשיר");
        var body = new byte[len - 3];
        if (ReadFull(s, body, body.Length) != body.Length) throw new IOException("החיבור נסגר");
        return (head[0], body);
    }
}

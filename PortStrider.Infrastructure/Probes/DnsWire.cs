using System.Net;
using System.Text;

namespace PortStrider.Infrastructure.Probes;

/// <summary>Minimal DNS wire-format helpers for timing a single A query against a specific server.</summary>
internal static class DnsWire
{
    public static byte[] BuildAQuery(ushort id, string name)
    {
        var labels = name.Trim().TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var buffer = new List<byte>(12 + name.Length + 6)
        {
            (byte)(id >> 8), (byte)id,
            0x01, 0x00, // RD
            0x00, 0x01, // QDCOUNT
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buffer.Add((byte)Math.Min(bytes.Length, 63));
            buffer.AddRange(bytes.Take(63));
        }

        buffer.Add(0);
        buffer.AddRange([0x00, 0x01, 0x00, 0x01]); // QTYPE A, QCLASS IN
        return buffer.ToArray();
    }

    public static bool TryParseResponse(byte[] data, ushort expectedId, out int rcode, out string? firstAnswer)
    {
        rcode = -1;
        firstAnswer = null;
        if (data.Length < 12) return false;
        var id = (ushort)((data[0] << 8) | data[1]);
        if (id != expectedId) return false;
        if ((data[2] & 0x80) == 0) return false; // not a response
        rcode = data[3] & 0x0F;
        var qd = (data[4] << 8) | data[5];
        var an = (data[6] << 8) | data[7];

        var offset = 12;
        for (var i = 0; i < qd; i++)
        {
            if (!SkipName(data, ref offset)) return true;
            offset += 4;
        }

        for (var i = 0; i < an && offset < data.Length; i++)
        {
            if (!SkipName(data, ref offset)) return true;
            if (offset + 10 > data.Length) return true;
            var type = (data[offset] << 8) | data[offset + 1];
            var rdlen = (data[offset + 8] << 8) | data[offset + 9];
            offset += 10;
            if (offset + rdlen > data.Length) return true;
            if (type == 1 && rdlen == 4)
            {
                firstAnswer = new IPAddress(data.AsSpan(offset, 4)).ToString();
                return true;
            }
            if (type == 28 && rdlen == 16)
            {
                firstAnswer ??= new IPAddress(data.AsSpan(offset, 16)).ToString();
            }

            offset += rdlen;
        }

        return true;
    }

    public static string RcodeName(int rcode) => rcode switch
    {
        0 => "NOERROR",
        1 => "FORMERR",
        2 => "SERVFAIL",
        3 => "NXDOMAIN",
        4 => "NOTIMP",
        5 => "REFUSED",
        _ => $"RCODE {rcode}"
    };

    private static bool SkipName(byte[] data, ref int offset)
    {
        while (offset < data.Length)
        {
            var len = data[offset];
            if (len == 0)
            {
                offset++;
                return true;
            }

            if ((len & 0xC0) == 0xC0)
            {
                offset += 2;
                return true;
            }

            offset += len + 1;
        }

        return false;
    }
}

using System.Net;
using System.Text;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;

namespace PortStrider.Infrastructure.Sniffing;

internal static class EdpParser
{
    private static readonly byte[] ExtremeOui = [0x00, 0xE0, 0x2B];

    public static bool IsEdpFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length < 17) return false;
        var ethertype = (data[12] << 8) | data[13];
        var offset = 14;
        if (ethertype == 0x8100 && data.Length >= 18)
        {
            ethertype = (data[16] << 8) | data[17];
            offset = 18;
        }

        if (ethertype != 0x88B6 || offset + 3 > data.Length) return false;
        return data[offset] == ExtremeOui[0] && data[offset + 1] == ExtremeOui[1] && data[offset + 2] == ExtremeOui[2];
    }

    public static void Parse(ReadOnlySpan<byte> data, SwitchInfoBuilder b)
    {
        b.Protocol = DiscoveryProtocol.Edp;
        var offset = 14;
        var ethertype = (data[12] << 8) | data[13];
        if (ethertype == 0x8100 && data.Length >= 18)
            offset = 18;

        if (offset + 8 > data.Length) return;
        offset += 8; // OUI + header

        while (offset + 4 <= data.Length)
        {
            var type = (data[offset] << 8) | data[offset + 1];
            var length = (data[offset + 2] << 8) | data[offset + 3];
            if (length < 4 || offset + length > data.Length) break;
            var value = data.Slice(offset + 4, length - 4);
            offset += length;

            switch (type)
            {
                case 0x0001:
                    b.SwitchName = Ascii(value);
                    break;
                case 0x0002:
                    b.PortId = Ascii(value);
                    break;
                case 0x0003:
                    b.Model = Ascii(value);
                    break;
                case 0x0004 when value.Length >= 4:
                    b.ManagementIp = new IPAddress(value[..4].ToArray()).ToString();
                    break;
                case 0x0005 when value.Length >= 2:
                    b.VlanId = ((value[0] << 8) | value[1]).ToString();
                    break;
            }
        }
    }

    private static string Ascii(ReadOnlySpan<byte> value) =>
        Encoding.UTF8.GetString(value).Trim('\0', ' ', '\r', '\n');
}

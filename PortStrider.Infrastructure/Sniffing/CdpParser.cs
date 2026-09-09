using System.Net;
using System.Text;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;

namespace PortStrider.Infrastructure.Sniffing;

internal static class CdpParser
{
    public static void Parse(ReadOnlySpan<byte> frame, SwitchInfoBuilder b)
    {
        b.Protocol = DiscoveryProtocol.Cdp;
        var offset = FindTlvStart(frame);
        if (offset < 0) return;

        while (offset + 4 <= frame.Length)
        {
            var type = (ushort)((frame[offset] << 8) | frame[offset + 1]);
            var length = (ushort)((frame[offset + 2] << 8) | frame[offset + 3]);
            if (length < 4) break;
            if (offset + length > frame.Length) break;
            var value = frame.Slice(offset + 4, length - 4);
            offset += length;

            switch (type)
            {
                case 0x0001:
                    b.SwitchName = Ascii(value);
                    break;
                case 0x0002:
                    var ip = DecodeAddresses(value);
                    if (ip is not null) b.ManagementIp = ip;
                    break;
                case 0x0003:
                    b.PortId = Ascii(value);
                    break;
                case 0x0004:
                    b.Capabilities = DecodeCaps(value);
                    break;
                case 0x0005:
                    b.SoftwareVersion = Ascii(value).Replace("\r", " ").Replace("\n", " ");
                    break;
                case 0x0006:
                    b.Model = Ascii(value);
                    break;
                case 0x000A when value.Length >= 2:
                    b.NativeVlan = ((value[0] << 8) | value[1]).ToString();
                    if (b.VlanId is "—" or "") b.VlanId = b.NativeVlan;
                    break;
                case 0x000B when value.Length >= 1:
                    b.Duplex = value[0] == 1 ? "Full" : "Half";
                    break;
                case 0x000E when value.Length >= 3:
                    // VoIP VLAN Reply: data(1) + VLAN(2)
                    var voice = (value[1] << 8) | value[2];
                    if (voice > 0) b.VoiceVlan = voice.ToString();
                    b.AddDetail($"Voice VLAN {voice}");
                    break;
                case 0x000F when value.Length >= 3:
                    // VoIP VLAN Query (sent by phones) — note it, do not treat as switch data.
                    b.AddDetail($"Voice VLAN query {(value[1] << 8) | value[2]}");
                    break;
                case 0x0010 when value.Length >= 2:
                    b.Poe = $"CDP {((value[0] << 8) | value[1])} mW advertised";
                    break;
            }
        }
    }

    private static int FindTlvStart(ReadOnlySpan<byte> frame)
    {
        // LLC SNAP (AA AA 03) + Cisco OUI + CDP PID 0x2000, then 4-byte CDP header.
        for (var i = 0; i <= frame.Length - 12; i++)
        {
            if (frame[i] == 0xAA && frame[i + 1] == 0xAA && frame[i + 2] == 0x03
                && frame[i + 6] == 0x20 && frame[i + 7] == 0x00)
            {
                return i + 12;
            }
        }

        return frame.Length > 8 ? 8 : -1;
    }

    private static string? DecodeAddresses(ReadOnlySpan<byte> value)
    {
        if (value.Length < 8) return null;
        var count = (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];
        var offset = 4;
        string? first = null;
        for (var i = 0; i < count && offset + 5 <= value.Length; i++)
        {
            var protoType = value[offset];
            var protoLen = value[offset + 1];
            offset += 2;
            if (offset + protoLen + 2 > value.Length) break;
            var proto = value.Slice(offset, protoLen);
            offset += protoLen;
            var addrLen = (value[offset] << 8) | value[offset + 1];
            offset += 2;
            if (offset + addrLen > value.Length) break;
            var addr = value.Slice(offset, addrLen);
            offset += addrLen;

            var isIp = (protoType == 1 && protoLen == 1 && proto[0] == 0xCC) || protoLen == 0;
            if (isIp && addrLen == 4)
            {
                var ip = new IPAddress(addr.ToArray()).ToString();
                first ??= ip;
            }
        }

        return first;
    }

    private static string DecodeCaps(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4) return "—";
        var caps = (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];
        var names = new List<string>();
        if ((caps & 0x01) != 0) names.Add("Router");
        if ((caps & 0x02) != 0) names.Add("Trans Bridge");
        if ((caps & 0x04) != 0) names.Add("Source Route");
        if ((caps & 0x08) != 0) names.Add("Switch");
        if ((caps & 0x10) != 0) names.Add("Host");
        if ((caps & 0x20) != 0) names.Add("IGMP");
        if ((caps & 0x40) != 0) names.Add("Repeater");
        if ((caps & 0x80) != 0) names.Add("Phone");
        return names.Count == 0 ? "—" : string.Join(", ", names);
    }

    private static string Ascii(ReadOnlySpan<byte> value) =>
        Encoding.UTF8.GetString(value).Trim('\0', ' ', '\r', '\n');
}

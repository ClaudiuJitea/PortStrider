using System.Net;
using System.Text;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;

namespace PortStrider.Infrastructure.Sniffing;

internal static class LldpParser
{
    public static void Parse(ReadOnlySpan<byte> data, SwitchInfoBuilder b)
    {
        b.Protocol = DiscoveryProtocol.Lldp;
        var offset = 0;
        while (offset + 2 <= data.Length)
        {
            var header = (ushort)((data[offset] << 8) | data[offset + 1]);
            var type = header >> 9;
            var length = header & 0x01FF;
            offset += 2;
            if (type == 0) break;
            if (offset + length > data.Length) break;
            var value = data.Slice(offset, length);
            offset += length;

            switch (type)
            {
                case 1:
                    b.ChassisId = DecodeId(value, "chassis");
                    break;
                case 2:
                    b.PortId = DecodeId(value, "port");
                    break;
                case 4:
                    b.PortDescription = Ascii(value);
                    break;
                case 5:
                    b.SwitchName = Ascii(value);
                    break;
                case 6:
                    b.Model = FirstLine(Ascii(value));
                    b.SoftwareVersion = Ascii(value);
                    break;
                case 7:
                    b.Capabilities = DecodeCapabilities(value);
                    break;
                case 8:
                    var mgmt = DecodeManagementAddress(value);
                    if (mgmt is not null) b.ManagementIp = mgmt;
                    break;
                case 127:
                    ParseOrgSpecific(value, b);
                    break;
            }
        }
    }

    private static void ParseOrgSpecific(ReadOnlySpan<byte> value, SwitchInfoBuilder b)
    {
        if (value.Length < 4) return;
        var oui = (value[0] << 16) | (value[1] << 8) | value[2];
        var subtype = value[3];
        var rest = value[4..];

        if (oui == 0x0080C2) // IEEE 802.1
        {
            switch (subtype)
            {
                case 1 when rest.Length >= 2:
                    b.VlanId = ((rest[0] << 8) | rest[1]).ToString();
                    break;
                case 3 when rest.Length >= 3:
                    b.AddDetail($"VLAN {((rest[0] << 8) | rest[1])}: {Ascii(rest[2..])}");
                    break;
            }
        }
        else if (oui == 0x00120F) // IEEE 802.3
        {
            switch (subtype)
            {
                case 1 when rest.Length >= 5:
                    b.MauType = MauName((rest[3] << 8) | rest[4]);
                    var an = rest[0];
                    b.AddDetail($"Auto-neg {((an & 0x01) != 0 ? "supported" : "no")} · {((an & 0x02) != 0 ? "enabled" : "disabled")}");
                    break;
                case 2:
                    b.Poe = DecodePower(rest);
                    break;
                case 4 when rest.Length >= 2:
                    b.AddDetail($"Max frame {(rest[0] << 8) | rest[1]} bytes");
                    break;
            }
        }
        else if (oui == 0x0012BB) // LLDP-MED
        {
            if (subtype == 2 && rest.Length >= 4)
            {
                // ANSI/TIA-1057 Network Policy: app type(8) | U(1) T(1) X(1) VLAN ID(12) L2 priority(3) DSCP(6)
                // The 24 policy bits straddle bytes: rest[1] = U T X + VLAN[11:7], rest[2] = VLAN[6:0] + PRI[2],
                // rest[3] = PRI[1:0] + DSCP[5:0].
                var appType = rest[0];
                var unknown = (rest[1] & 0x80) != 0;
                var tagged = (rest[1] & 0x40) != 0;
                var vlan = ((rest[1] & 0x1F) << 7) | (rest[2] >> 1);
                var priority = ((rest[2] & 0x01) << 2) | (rest[3] >> 6);
                var dscp = rest[3] & 0x3F;
                var appName = MedApplication(appType);
                if (unknown)
                {
                    b.AddDetail($"MED {appName}: policy unknown");
                }
                else
                {
                    if (appType is 1 or 2 && vlan > 0) b.VoiceVlan = vlan.ToString();
                    b.AddDetail($"MED {appName}: VLAN {vlan}{(tagged ? " tagged" : " untagged")} · L2 pri {priority} · DSCP {dscp}");
                }
            }
            else if (subtype == 3 && rest.Length >= 3)
            {
                var tenths = (rest[1] << 8) | rest[2];
                b.Poe = $"LLDP-MED {tenths / 10.0:0.0} W";
            }
        }
    }

    private static string DecodePower(ReadOnlySpan<byte> rest)
    {
        if (rest.Length < 3) return "PoE TLV present";
        var support = rest[0];
        var pse = (support & 0x01) == 0;
        var on = (support & 0x04) != 0;
        var cls = rest.Length > 2 ? rest[2] : 0;
        var bits = new List<string>
        {
            pse ? "PSE" : "PD",
            on ? "power on" : "power off",
            $"class {cls}"
        };
        if (rest.Length >= 8)
        {
            var allocated = ((rest[6] << 8) | rest[7]) / 10.0;
            bits.Add($"{allocated:0.0} W allocated");
        }

        return string.Join(" · ", bits);
    }

    private static string DecodeId(ReadOnlySpan<byte> value, string kind)
    {
        if (value.Length == 0) return "—";
        var subtype = value[0];
        var rest = value[1..];
        return subtype switch
        {
            3 or 4 when rest.Length == 6 => FormatMac(rest),
            5 when rest.Length >= 5 && rest[0] == 1 => new IPAddress(rest[1..5].ToArray()).ToString(),
            _ => Ascii(rest).Length > 0 ? Ascii(rest) : $"{kind}-id subtype {subtype}"
        };
    }

    private static string? DecodeManagementAddress(ReadOnlySpan<byte> value)
    {
        if (value.Length < 2) return null;
        var addrLen = value[0];
        if (addrLen < 2 || 1 + addrLen > value.Length) return null;
        var subtype = value[1];
        var addr = value.Slice(2, addrLen - 1);
        try
        {
            return subtype switch
            {
                1 when addr.Length >= 4 => new IPAddress(addr[..4].ToArray()).ToString(),
                2 when addr.Length >= 16 => new IPAddress(addr[..16].ToArray()).ToString(),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string DecodeCapabilities(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4) return "—";
        var enabled = (value[2] << 8) | value[3];
        var names = new List<string>();
        if ((enabled & 0x02) != 0) names.Add("Repeater");
        if ((enabled & 0x04) != 0) names.Add("Bridge");
        if ((enabled & 0x08) != 0) names.Add("WLAN AP");
        if ((enabled & 0x10) != 0) names.Add("Router");
        if ((enabled & 0x20) != 0) names.Add("Phone");
        if ((enabled & 0x40) != 0) names.Add("DOCSIS");
        if ((enabled & 0x80) != 0) names.Add("Station");
        return names.Count == 0 ? "—" : string.Join(", ", names);
    }

    private static string MedApplication(int appType) => appType switch
    {
        1 => "Voice",
        2 => "Voice Signaling",
        3 => "Guest Voice",
        4 => "Guest Voice Signaling",
        5 => "Softphone Voice",
        6 => "Video Conferencing",
        7 => "Streaming Video",
        8 => "Video Signaling",
        _ => $"app {appType}"
    };

    private static string MauName(int type) => type switch
    {
        16 => "100BASE-TX FD",
        30 => "1000BASE-T FD",
        75 => "2.5GBASE-T",
        76 => "5GBASE-T",
        105 => "10GBASE-T",
        _ => $"MAU {type}"
    };

    private static string Ascii(ReadOnlySpan<byte> value)
    {
        var text = Encoding.UTF8.GetString(value).Trim('\0', ' ', '\r', '\n');
        return string.IsNullOrWhiteSpace(text) ? "" : text;
    }

    private static string FirstLine(string text)
    {
        var idx = text.IndexOfAny(['\n', '\r']);
        return idx < 0 ? text : text[..idx].Trim();
    }

    private static string FormatMac(ReadOnlySpan<byte> bytes) =>
        string.Join(":", bytes.ToArray().Select(b => b.ToString("X2")));
}

using System.Globalization;
using System.Text.RegularExpressions;
using PortStrider.Core.Models;

namespace PortStrider.Infrastructure.Wifi;

internal static class IwScanParser
{
    public static IReadOnlyList<WifiNetwork> Parse(string output)
    {
        var networks = new List<WifiNetwork>();
        foreach (var block in Regex.Split(output, @"(?m)^BSS ").Skip(1))
        {
            var bssid = Regex.Match(block, @"\A([0-9a-fA-F]{2}(?::[0-9a-fA-F]{2}){5})(?:\s|\()");
            var freq = Regex.Match(block, @"(?m)^\s*freq:\s*(\d+)");
            var signal = Regex.Match(block, @"(?m)^\s*signal:\s*(-?\d+(?:\.\d+)?)\s*dBm");
            if (!bssid.Success || !int.TryParse(freq.Groups[1].Value, out var frequency)
                || !double.TryParse(signal.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbm)
                || dbm is < -127 or > 0 || WifiChannels.Channel(frequency) == 0) continue;

            var ssid = Regex.Match(block, @"(?m)^\s*SSID: ?([^\r\n]*)").Groups[1].Value;
            var rsn = Regex.IsMatch(block, @"(?m)^\s*RSN:");
            var wpa = Regex.IsMatch(block, @"(?m)^\s*WPA:");
            var sae = block.Contains("SAE", StringComparison.Ordinal);
            var security = sae ? (block.Contains("PSK", StringComparison.Ordinal) ? "WPA2 / WPA3" : "WPA3")
                : block.Contains("OWE", StringComparison.Ordinal) ? "Enhanced Open"
                : rsn ? "WPA2" : wpa ? "WPA" : block.Contains("Privacy", StringComparison.Ordinal) ? "Protected (legacy)" : "Open";
            int? width = null;
            int? center = null;
            var secondary = Regex.Match(block, @"secondary channel offset:\s*(above|below|no secondary)");
            if (secondary.Success)
            {
                width = secondary.Groups[1].Value == "no secondary" ? 20 : 40;
                center = frequency + (secondary.Groups[1].Value == "above" ? 10 : secondary.Groups[1].Value == "below" ? -10 : 0);
            }

            // VHT operation describes the operating width, unlike VHT capabilities (maximum supported width).
            var vht = Regex.Match(block, @"VHT operation:([\s\S]*?)(?=\n\t[^\t *]|\z)");
            var widthCode = Regex.Match(vht.Value, @"channel width:\s*(\d+)");
            var segment1 = Regex.Match(vht.Value, @"center freq segment 1:\s*(\d+)");
            var segment2 = Regex.Match(vht.Value, @"center freq segment 2:\s*(\d+)");
            if (widthCode.Success && int.TryParse(segment1.Groups[1].Value, out var seg1) && seg1 > 0
                && WifiChannels.Band(frequency) == "5 GHz")
            {
                int.TryParse(segment2.Groups[1].Value, out var seg2);
                if (widthCode.Groups[1].Value == "1" && seg2 == 0) { width = 80; center = 5000 + seg1 * 5; }
                else if (widthCode.Groups[1].Value == "1" && Math.Abs(seg1 - seg2) == 8) { width = 160; center = 5000 + seg2 * 5; }
                else if (widthCode.Groups[1].Value == "2") { width = 160; center = 5000 + seg1 * 5; }
                else if (widthCode.Groups[1].Value is "1" or "3") { width = null; center = null; } // non-contiguous 80+80
            }
            networks.Add(new WifiNetwork(ssid, bssid.Groups[1].Value.ToUpperInvariant(), frequency, dbm, security, width, center));
        }
        return networks.GroupBy(n => n.Key).Select(g => g.OrderByDescending(n => n.SignalDbm).First())
            .OrderByDescending(n => n.SignalDbm).ToArray();
    }
}

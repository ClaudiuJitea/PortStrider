using System.Text.RegularExpressions;

namespace PortStrider.Infrastructure.Adapters;

/// <summary>Parses the output of <c>ethtool &lt;iface&gt;</c> (link settings) into structured values.</summary>
internal sealed partial class EthtoolSettings
{
    public IReadOnlyList<string> SupportedModes { get; init; } = [];
    public IReadOnlyList<string> AdvertisedModes { get; init; } = [];
    public IReadOnlyList<string> PartnerModes { get; init; } = [];
    public long SpeedMbps { get; init; }
    public string Duplex { get; init; } = "";
    public string AutoNegotiation { get; init; } = "";
    public string PartnerAutoNegotiation { get; init; } = "";
    public string MdiX { get; init; } = "";
    public string Port { get; init; } = "";
    public bool? LinkDetected { get; init; }

    public long AdvertisedMaxMbps => MaxSpeed(AdvertisedModes);
    public long PartnerMaxMbps => MaxSpeed(PartnerModes);
    public long SupportedMaxMbps => MaxSpeed(SupportedModes);
    public string AdvertisedDuplex => DuplexSummary(AdvertisedModes);
    public string PartnerDuplex => DuplexSummary(PartnerModes);

    /// <summary>
    /// A downshift is when the link runs slower than the highest speed both ends advertise.
    /// Mirrors the LinkRunner G2 yellow "Link" indication.
    /// </summary>
    public bool IsDownshift
    {
        get
        {
            if (SpeedMbps <= 0) return false;
            var local = AdvertisedMaxMbps;
            var partner = PartnerMaxMbps;
            if (local <= 0 && partner <= 0) return false;
            var expected = local > 0 && partner > 0 ? Math.Min(local, partner) : Math.Max(local, partner);
            return SpeedMbps < expected;
        }
    }

    public static EthtoolSettings Parse(string output)
    {
        var supported = new List<string>();
        var advertised = new List<string>();
        var partner = new List<string>();
        long speed = 0;
        string duplex = "", autoneg = "", partnerAutoneg = "", mdix = "", port = "";
        bool? link = null;

        List<string>? current = null;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var colon = trimmed.IndexOf(':');
            // Continuation lines of a mode list have no "key:" prefix (mode tokens like 1000baseT/Full contain no colon).
            if (colon < 0 || trimmed.StartsWith("Settings for", StringComparison.Ordinal))
            {
                if (current is not null && colon < 0) current.AddRange(Tokens(trimmed));
                continue;
            }

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();
            current = null;

            switch (key)
            {
                case "Supported link modes":
                    current = supported;
                    supported.AddRange(Tokens(value));
                    break;
                case "Advertised link modes":
                    current = advertised;
                    advertised.AddRange(Tokens(value));
                    break;
                case "Link partner advertised link modes":
                    current = partner;
                    partner.AddRange(Tokens(value));
                    break;
                case "Speed":
                    speed = ParseSpeed(value);
                    break;
                case "Duplex":
                    duplex = value;
                    break;
                case "Auto-negotiation":
                    autoneg = value;
                    break;
                case "Link partner advertised auto-negotiation":
                    partnerAutoneg = value;
                    break;
                case "MDI-X":
                    mdix = value;
                    break;
                case "Port":
                    port = value;
                    break;
                case "Link detected":
                    link = value.StartsWith("yes", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        return new EthtoolSettings
        {
            SupportedModes = supported,
            AdvertisedModes = advertised,
            PartnerModes = partner,
            SpeedMbps = speed,
            Duplex = duplex,
            AutoNegotiation = autoneg,
            PartnerAutoNegotiation = partnerAutoneg,
            MdiX = mdix,
            Port = port,
            LinkDetected = link
        };
    }

    public static string FormatMbps(long mbps) => mbps switch
    {
        <= 0 => "—",
        >= 1000 when mbps % 1000 == 0 => $"{mbps / 1000} Gbps",
        >= 1000 => $"{mbps / 1000d:0.#} Gbps",
        _ => $"{mbps} Mbps"
    };

    private static IEnumerable<string> Tokens(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => ModeRegex().IsMatch(t));

    private static long MaxSpeed(IEnumerable<string> modes)
    {
        long max = 0;
        foreach (var mode in modes)
        {
            var m = ModeRegex().Match(mode);
            if (m.Success && long.TryParse(m.Groups[1].Value, out var mbps)) max = Math.Max(max, mbps);
        }

        return max;
    }

    private static string DuplexSummary(IReadOnlyList<string> modes)
    {
        if (modes.Count == 0) return "—";
        var full = modes.Any(m => m.EndsWith("/Full", StringComparison.OrdinalIgnoreCase));
        var half = modes.Any(m => m.EndsWith("/Half", StringComparison.OrdinalIgnoreCase));
        return (full, half) switch
        {
            (true, true) => "Full/Half",
            (true, false) => "Full",
            (false, true) => "Half",
            _ => "—"
        };
    }

    private static long ParseSpeed(string value)
    {
        // "1000Mb/s", "Unknown!", "10000Mb/s"
        var m = SpeedRegex().Match(value);
        return m.Success && long.TryParse(m.Groups[1].Value, out var mbps) ? mbps : 0;
    }

    [GeneratedRegex(@"^(\d+)base\w+/(Full|Half)$", RegexOptions.IgnoreCase)]
    private static partial Regex ModeRegex();

    [GeneratedRegex(@"^(\d+)\s*Mb/s", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedRegex();
}

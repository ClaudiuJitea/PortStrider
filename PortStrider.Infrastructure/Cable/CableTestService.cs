using System.Text.RegularExpressions;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Cable;

public sealed class CableTestService : ICableTestService
{
    public FeatureCapability WiremapCapability() => new()
    {
        Id = "wiremap",
        Name = "Wiremap / remote IDs",
        Level = CapabilityLevel.Unavailable,
        Summary = "Requires dedicated cable-test hardware and remote identifiers.",
        Prerequisite = "Use an external wire mapper accessory."
    };

    public FeatureCapability ToneCapability() => new()
    {
        Id = "tone",
        Name = "Tone generation",
        Level = CapabilityLevel.Unavailable,
        Summary = "Requires dedicated tone hardware.",
        Prerequisite = "Use an external tone probe."
    };

    public async Task<CableTestResult> RunAsync(string adapterName, CableUnit unit, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        if (!HostOs.IsLinux)
        {
            return new CableTestResult
            {
                Supported = false,
                Summary = "TDR cable test needs a PHY that exposes ethtool on Linux. Windows NDIS does not provide pair-level TDR."
            };
        }

        if (!await ProcessUtil.CommandExistsAsync("ethtool", cancellationToken))
        {
            return new CableTestResult
            {
                Supported = false,
                Summary = "ethtool is not installed."
            };
        }

        progress.Report("Querying link partner…");
        var settings = await ProcessUtil.RunAsync("ethtool", [adapterName], cancellationToken);
        var settingsText = settings?.StandardOutput ?? "";

        progress.Report("Starting TDR / cable test (may take ~10s)…");
        var tdr = await ProcessUtil.RunAsync("ethtool", ["--cable-test", adapterName], cancellationToken)
                  ?? await ProcessUtil.RunAsync("ethtool", ["-t", adapterName, "online"], cancellationToken);

        var raw = (tdr?.StandardOutput ?? "") + "\n" + (tdr?.StandardError ?? "") + "\n" + settingsText;
        var pairs = ParsePairs(raw, unit);

        if (pairs.Count == 0 && tdr?.ExitCode != 0)
        {
            return new CableTestResult
            {
                Supported = false,
                Summary = "This NIC/driver does not expose cable test (common on USB and many consumer adapters).",
                RawOutput = raw.Trim()
            };
        }

        var bad = pairs.Where(p => !p.Status.Contains("OK", StringComparison.OrdinalIgnoreCase)
                                   && !p.Status.Contains("normal", StringComparison.OrdinalIgnoreCase)).ToArray();
        return new CableTestResult
        {
            Supported = true,
            Summary = bad.Length == 0
                ? "Pairs look healthy (or driver reported completion)."
                : $"{bad.Length} pair(s) reported a fault",
            Pairs = pairs,
            RawOutput = raw.Trim()
        };
    }

    private static List<CablePairResult> ParsePairs(string text, CableUnit unit)
    {
        var list = new List<CablePairResult>();
        var rx = new Regex(@"Pair\s+([A-D0-9]+)\s*[:\-]\s*([^\n]+)", RegexOptions.IgnoreCase);
        foreach (Match m in rx.Matches(text))
        {
            var status = m.Groups[2].Value.Trim();
            string? distance = null;
            var dist = Regex.Match(status, @"(\d+(?:\.\d+)?)\s*(m|ft|meter|feet)", RegexOptions.IgnoreCase);
            if (dist.Success)
            {
                if (double.TryParse(dist.Groups[1].Value, out var value))
                {
                    var isFeet = dist.Groups[2].Value.StartsWith("f", StringComparison.OrdinalIgnoreCase);
                    if (unit == CableUnit.Feet && !isFeet) value *= 3.28084;
                    if (unit == CableUnit.Meters && isFeet) value /= 3.28084;
                    distance = unit == CableUnit.Feet ? $"{value:0.0} ft" : $"{value:0.0} m";
                }
            }

            list.Add(new CablePairResult { Pair = "Pair " + m.Groups[1].Value, Status = status, Distance = distance });
        }

        return list;
    }
}

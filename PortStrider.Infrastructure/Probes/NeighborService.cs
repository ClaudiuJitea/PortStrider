using System.Net;
using System.Text.RegularExpressions;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Probes;

public sealed class NeighborService : INeighborService
{
    public async Task<IReadOnlyList<NeighborEntry>> ListAsync(string? adapterName, CancellationToken cancellationToken = default)
    {
        if (HostOs.IsLinux)
        {
            var ip = await ProcessUtil.RunAsync("ip", ["neigh"], cancellationToken);
            if (ip is not null && ip.ExitCode == 0)
                return ParseIpNeigh(ip.StandardOutput, adapterName);
        }

        if (HostOs.IsWindows)
        {
            var arp = await ProcessUtil.RunAsync("arp", ["-a"], cancellationToken);
            if (arp is not null) return ParseWindowsArp(arp.StandardOutput, adapterName);
        }

        return ParseProcNetArp(adapterName);
    }

    private static IReadOnlyList<NeighborEntry> ParseIpNeigh(string text, string? iface)
    {
        var list = new List<NeighborEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            var address = parts[0];
            string? dev = null;
            string? mac = null;
            var state = parts[^1];
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "dev") dev = parts[i + 1];
                if (parts[i] == "lladdr") mac = parts[i + 1];
            }

            if (iface is not null && dev is not null && !dev.Equals(iface, StringComparison.OrdinalIgnoreCase))
                continue;

            list.Add(new NeighborEntry { Address = address, Mac = mac, InterfaceName = dev, State = state });
        }

        return list;
    }

    private static IReadOnlyList<NeighborEntry> ParseWindowsArp(string text, string? iface)
    {
        var list = new List<NeighborEntry>();
        string? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Interface:", StringComparison.OrdinalIgnoreCase))
            {
                current = line;
                continue;
            }

            var m = Regex.Match(line, @"(\d+\.\d+\.\d+\.\d+)\s+([0-9a-f\-]{17}|[0-9a-f:]{17})\s+(\S+)", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            list.Add(new NeighborEntry
            {
                Address = m.Groups[1].Value,
                Mac = m.Groups[2].Value.Replace("-", ":").ToUpperInvariant(),
                InterfaceName = current,
                State = m.Groups[3].Value
            });
        }

        return list;
    }

    private static IReadOnlyList<NeighborEntry> ParseProcNetArp(string? iface)
    {
        var path = "/proc/net/arp";
        if (!File.Exists(path)) return [];
        var list = new List<NeighborEntry>();
        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6) continue;
            if (iface is not null && !parts[5].Equals(iface, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new NeighborEntry
            {
                Address = parts[0],
                Mac = parts[3].Contains("00:00:00:00:00:00") ? null : parts[3].ToUpperInvariant(),
                InterfaceName = parts[5],
                State = parts[2]
            });
        }

        return list;
    }
}

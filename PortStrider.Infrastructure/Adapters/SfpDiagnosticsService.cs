using System.Text.RegularExpressions;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Adapters;

public sealed class SfpDiagnosticsService : ISfpDiagnosticsService
{
    public async Task<SfpDiagnostics> ReadAsync(string adapterName, CancellationToken cancellationToken = default)
    {
        if (!HostOs.IsLinux)
        {
            return new SfpDiagnostics
            {
                Supported = false,
                Summary = "SFP diagnostics are not exposed through Windows NDIS."
            };
        }

        if (!await ProcessUtil.CommandExistsAsync("ethtool", cancellationToken))
        {
            return new SfpDiagnostics { Supported = false, Summary = "ethtool is not installed." };
        }

        var module = await ProcessUtil.RunAsync("ethtool", ["-m", adapterName], cancellationToken);
        var raw = (module?.StandardOutput ?? "") + "\n" + (module?.StandardError ?? "");
        if (module?.ExitCode != 0 || string.IsNullOrWhiteSpace(module.StandardOutput))
        {
            return new SfpDiagnostics
            {
                Supported = false,
                Summary = "This adapter does not expose SFP module EEPROM/DDM data.",
                RawOutput = raw.Trim()
            };
        }

        return new SfpDiagnostics
        {
            Supported = true,
            Summary = "SFP module data read from ethtool -m.",
            Vendor = Match(raw, @"Vendor name\s*:\s*(.+)"),
            PartNumber = Match(raw, @"Vendor PN\s*:\s*(.+)"),
            Serial = Match(raw, @"Vendor SN\s*:\s*(.+)"),
            Wavelength = Match(raw, @"Laser wavelength\s*:\s*(.+)"),
            Temperature = Match(raw, @"Module temperature\s*:\s*(.+)"),
            Voltage = Match(raw, @"Module voltage\s*:\s*(.+)"),
            TxPower = Match(raw, @"Laser output power\s*:\s*(.+)"),
            RxPower = Match(raw, @"Receiver signal average optical power\s*:\s*(.+)"),
            Alarms = Match(raw, @"Alarm flags\s*:\s*(.+)"),
            RawOutput = raw.Trim()
        };
    }

    private static string? Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}

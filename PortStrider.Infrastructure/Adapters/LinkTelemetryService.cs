using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Adapters;

public sealed class LinkTelemetryService : ILinkTelemetryService
{
    public async Task<LinkTelemetry> ReadAsync(AdapterInfo adapter, CancellationToken cancellationToken = default)
    {
        var driver = HostOs.IsLinux ? LinuxSysFs.Read(LinuxSysFs.Net(adapter.Name, "device/driver/module/name")) ?? "" : "";
        var firmware = HostOs.IsLinux ? LinuxSysFs.Read(LinuxSysFs.Net(adapter.Name, "device/physfn/device/phys_switch_id")) ?? "" : "";
        var eee = HostOs.IsLinux && (LinuxSysFs.Read(LinuxSysFs.Net(adapter.Name, "eee_enabled")) ?? "0") != "0";

        var actualMbps = adapter.SpeedBps > 0 ? adapter.SpeedBps / 1_000_000 : 0;
        var fallback = new LinkTelemetry
        {
            ActualSpeed = adapter.SpeedLabel,
            ActualSpeedMbps = actualMbps,
            AdvertisedSpeed = "—",
            PartnerSpeed = "—",
            Duplex = adapter.Duplex ?? "—",
            MdiMode = "—",
            Driver = string.IsNullOrWhiteSpace(driver) ? adapter.Description : driver,
            Firmware = string.IsNullOrWhiteSpace(firmware) ? "—" : firmware,
            EnergyEfficientEthernet = eee,
            Source = HostOs.IsWindows ? "NDIS" : "sysfs"
        };

        if (!HostOs.IsLinux) return fallback;

        var settings = await ReadEthtoolAsync(adapter.Name, cancellationToken);
        if (settings is null) return fallback;

        var actual = settings.SpeedMbps > 0 ? settings.SpeedMbps : actualMbps;
        return new LinkTelemetry
        {
            ActualSpeed = actual > 0 ? EthtoolSettings.FormatMbps(actual) : adapter.SpeedLabel,
            ActualSpeedMbps = actual,
            AdvertisedSpeed = EthtoolSettings.FormatMbps(settings.AdvertisedMaxMbps),
            AdvertisedSpeedMbps = settings.AdvertisedMaxMbps,
            AdvertisedDuplex = settings.AdvertisedDuplex,
            PartnerSpeed = EthtoolSettings.FormatMbps(settings.PartnerMaxMbps),
            PartnerSpeedMbps = settings.PartnerMaxMbps,
            PartnerDuplex = settings.PartnerDuplex,
            Duplex = string.IsNullOrWhiteSpace(settings.Duplex) || settings.Duplex.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)
                ? adapter.Duplex ?? "—"
                : settings.Duplex,
            AutoNegotiation = string.IsNullOrWhiteSpace(settings.AutoNegotiation) ? "—" : settings.AutoNegotiation,
            MdiMode = string.IsNullOrWhiteSpace(settings.MdiX) ? "—" : FormatMdi(settings.MdiX),
            Port = string.IsNullOrWhiteSpace(settings.Port) ? "—" : settings.Port,
            Driver = fallback.Driver,
            Firmware = fallback.Firmware,
            EnergyEfficientEthernet = eee,
            Downshift = settings.IsDownshift,
            Source = "ethtool"
        };
    }

    internal static async Task<EthtoolSettings?> ReadEthtoolAsync(string interfaceName, CancellationToken cancellationToken)
    {
        if (!await ProcessUtil.CommandExistsAsync("ethtool", cancellationToken)) return null;
        var result = await ProcessUtil.RunAsync("ethtool", [interfaceName], cancellationToken);
        if (result is null || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;
        return EthtoolSettings.Parse(result.StandardOutput);
    }

    private static string FormatMdi(string raw)
    {
        // ethtool prints "off (auto)", "on (auto)", "off", "on", "Unknown"
        var lower = raw.ToLowerInvariant();
        var auto = lower.Contains("auto");
        if (lower.StartsWith("on")) return auto ? "MDI-X (auto)" : "MDI-X";
        if (lower.StartsWith("off")) return auto ? "MDI (auto)" : "MDI";
        return raw;
    }

    public Task<PoeMeasurement> ReadPoeAsync(AdapterInfo adapter, SwitchInfo? switchInfo, CancellationToken cancellationToken = default)
    {
        if (switchInfo is null || switchInfo.Poe is "—")
        {
            return Task.FromResult(new PoeMeasurement
            {
                Level = CapabilityLevel.Unavailable,
                Source = "No neighbor PoE TLV",
                Summary = "Advertised PoE unavailable. Loaded PoE requires dedicated hardware."
            });
        }

        return Task.FromResult(new PoeMeasurement
        {
            Level = CapabilityLevel.Supported,
            Source = switchInfo.ProtocolLabel,
            Summary = switchInfo.Poe,
            AdvertisedClass = switchInfo.Poe,
            Pairs = switchInfo.Details.FirstOrDefault(d => d.Contains("pair", StringComparison.OrdinalIgnoreCase))
        });
    }
}

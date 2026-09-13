using CliWrap;
using CliWrap.Buffered;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Wifi;

public sealed class WifiService : IWifiService
{
    public async Task<WifiScan> ScanAsync(AdapterInfo adapter, CancellationToken cancellationToken = default)
    {
        if (adapter.Media != LinkMediaKind.WiFi)
            throw new InvalidOperationException("Select a WiFi adapter in the top toolbar. Ethernet adapters cannot scan WiFi.");
        if (OperatingSystem.IsWindows())
            return await WindowsWifi.ScanAsync(adapter.Id, cancellationToken);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("WiFi scanning is supported on Linux and Windows.");

        var iw = ProcessUtil.FindCommand("iw")
            ?? throw new InvalidOperationException("iw is missing. Open Pre-flight and repair WiFi scan tools.");
        // Capabilities are granted via Pre-flight. Never prompt for elevation on every live refresh.
        _ = PrivilegedProcess.HasAmbientNetCapabilities;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var result = await Cli.Wrap(iw).WithArguments(["dev", adapter.Name, "scan"])
                .WithEnvironmentVariables(e => e.Set("LC_ALL", "C"))
                .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync(timeout.Token);
            if (result.ExitCode != 0)
            {
                var error = result.StandardError.Trim();
                var hint = error.Contains("permitted", StringComparison.OrdinalIgnoreCase) || error.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    ? "Open Pre-flight, repair Capture privileges, and restart PortStrider to enable WiFi scans."
                    : error.Contains("busy", StringComparison.OrdinalIgnoreCase)
                        ? "The WiFi radio is busy scanning. Wait a moment and try again."
                        : "Check that WiFi is enabled, airplane mode is off, and the adapter is up.";
                throw new InvalidOperationException($"{hint} {error}");
            }
            return new WifiScan(DateTimeOffset.Now, IwScanParser.Parse(result.StandardOutput));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("WiFi scan timed out. Check the radio/driver and try again.");
        }
    }
}

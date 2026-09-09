using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Principal;
using System.Reflection;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;
using SharpPcap;

namespace PortStrider.Infrastructure.PreFlight;

public sealed class DependencyManager : IPreFlightService
{
    public bool IsWindows => HostOs.IsWindows;
    public bool IsLinux => HostOs.IsLinux;

    public bool HasAdminPrivileges()
    {
        if (IsWindows && OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        try
        {
            return Mono.Unix.Native.Syscall.geteuid() == 0;
        }
        catch
        {
            return Environment.UserName == "root";
        }
    }

    public async Task<IReadOnlyList<DependencyStatus>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var checks = new[]
        {
            CheckPacketCaptureAsync(cancellationToken),
            CheckIperf3Async(cancellationToken),
            CheckCableToolAsync(cancellationToken),
            CheckTraceToolAsync(cancellationToken),
            CheckPrivilegesAsync(cancellationToken)
        };
        return await Task.WhenAll(checks);
    }

    public async Task<bool> RepairAsync(string dependencyId, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        if (IsLinux)
        {
            if (dependencyId == "privs")
                return await GrantLinuxCaptureCapabilitiesAsync(progress, cancellationToken);

            var package = dependencyId switch
            {
                "pcap" => "libpcap0.8 iperf3 ethtool traceroute",
                "iperf3" => "iperf3",
                "ethtool" => "ethtool",
                "trace" => "traceroute",
                _ => null
            };
            if (package is null) return false;
            return await InstallLinuxPackagesAsync(package, progress, cancellationToken);
        }

        if (IsWindows)
        {
            switch (dependencyId)
            {
                case "iperf3":
                    return await InstallWindowsIperfAsync(progress, cancellationToken);
                case "pcap":
                    return await InstallWindowsNpcapAsync(progress, cancellationToken);
                case "privs":
                    return await ElevateWindowsPrivilegesAsync(progress);
            }
        }

        progress.Report("No automatic repair for this item on this OS.");
        return false;
    }

    private async Task<DependencyStatus> CheckPacketCaptureAsync(CancellationToken cancellationToken)
    {
        if (IsWindows)
        {
            var npcapDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");
            var wpcap = Path.Combine(Environment.SystemDirectory, "wpcap.dll");
            var installed = Directory.Exists(npcapDir) || File.Exists(wpcap);
            return new DependencyStatus
            {
                Id = "pcap",
                Name = "Npcap driver",
                IsReady = installed,
                Details = installed ? "wpcap.dll available" : "Npcap is required for CDP/LLDP and packet capture",
                CanAutoFix = true,
                FixHint = "Click Repair to download and run the Npcap installer"
            };
        }

        var hasLib = await LibPcapPresentAsync(cancellationToken);
        var canOpen = false;
        if (hasLib)
        {
            try
            {
                canOpen = await Task.Run(
                    () => CaptureDeviceList.Instance.Count >= 0,
                    cancellationToken);
            }
            catch
            {
                canOpen = false;
            }
        }

        var ready = hasLib && canOpen;
        return new DependencyStatus
        {
            Id = "pcap",
            Name = "libpcap",
            IsReady = ready,
            Details = ready ? "libpcap loaded" : "Missing libpcap — needed for Layer 2 sniffing",
            CanAutoFix = true,
            FixHint = "Install libpcap via your package manager"
        };
    }

    private async Task<DependencyStatus> CheckIperf3Async(CancellationToken cancellationToken)
    {
        var installed = await ProcessUtil.CommandExistsAsync("iperf3", cancellationToken);
        return new DependencyStatus
        {
            Id = "iperf3",
            Name = "iPerf3",
            IsReady = installed,
            Details = installed ? "iperf3 is available" : "Optional — throughput tests will be skipped",
            CanAutoFix = true,
            FixHint = IsWindows ? "Click Repair to automatically download and set up iperf3" : "Install iperf3"
        };
    }

    private async Task<DependencyStatus> CheckCableToolAsync(CancellationToken cancellationToken)
    {
        if (!IsLinux)
        {
            return new DependencyStatus
            {
                Id = "ethtool",
                Name = "Cable diagnostics (TDR)",
                IsReady = true,
                Details = "Not available on standard Windows NDIS — skipped",
                CanAutoFix = false
            };
        }

        var installed = await ProcessUtil.CommandExistsAsync("ethtool", cancellationToken);
        return new DependencyStatus
        {
            Id = "ethtool",
            Name = "ethtool",
            IsReady = installed,
            Details = installed ? "ethtool available" : "Needed for duplex, PHY, and TDR cable tests",
            CanAutoFix = true,
            FixHint = "Install ethtool"
        };
    }

    private async Task<DependencyStatus> CheckTraceToolAsync(CancellationToken cancellationToken)
    {
        var cmd = IsWindows ? "tracert" : "traceroute";
        var installed = await ProcessUtil.CommandExistsAsync(cmd, cancellationToken);
        if (!installed && IsLinux)
            installed = await ProcessUtil.CommandExistsAsync("tracepath", cancellationToken);

        return new DependencyStatus
        {
            Id = "trace",
            Name = "Traceroute",
            IsReady = installed,
            Details = installed ? "Trace binary available" : "Install traceroute / use Windows tracert",
            CanAutoFix = IsLinux,
            FixHint = "Install traceroute"
        };
    }

    private async Task<DependencyStatus> CheckPrivilegesAsync(CancellationToken cancellationToken)
    {
        var admin = HasAdminPrivileges();
        var hasCaps = IsLinux && await HasLinuxCaptureCapsAsync(cancellationToken);
        var ready = admin || hasCaps;
        var details = admin
            ? "Running with elevated privileges"
            : hasCaps
                ? "Linux capabilities detected on PortStrider binary"
                : IsLinux
                    ? "Raw capture needs CAP_NET_RAW / CAP_NET_ADMIN (setcap) or root"
                    : "Launch PortStrider as Administrator for NDIS packet filters";

        return new DependencyStatus
        {
            Id = "privs",
            Name = "Capture privileges",
            IsReady = ready,
            Details = details,
            CanAutoFix = (IsLinux
                          && ProcessUtil.FindCommand("setcap") is not null
                          && (admin || ProcessUtil.FindCommand("pkexec") is not null))
                         || (IsWindows && !admin),
            FixHint = IsLinux
                ? "Click Repair to grant capture capabilities to this PortStrider binary"
                : "Click Repair to relaunch PortStrider as Administrator"
        };
    }

    private static async Task<bool> HasLinuxCaptureCapsAsync(CancellationToken cancellationToken)
    {
        if (!HostOs.IsLinux) return false;
        try
        {
            var executable = ResolveCaptureExecutable();
            if (string.IsNullOrWhiteSpace(executable)) return false;
            var output = await ProcessUtil.RunAsync("getcap", [executable], cancellationToken);
            return output is not null
                   && output.StandardOutput.Contains("cap_net_raw", StringComparison.OrdinalIgnoreCase)
                   && output.StandardOutput.Contains("cap_net_admin", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> GrantLinuxCaptureCapabilitiesAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var executable = ResolveCaptureExecutable();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            progress.Report("Could not locate the PortStrider executable.");
            return false;
        }

        var setcap = ProcessUtil.FindCommand("setcap");
        if (setcap is null)
        {
            progress.Report("setcap is unavailable. Install the libcap2-bin package.");
            return false;
        }

        var command = setcap;
        string[] arguments = ["cap_net_raw,cap_net_admin,cap_net_bind_service+eip", executable];
        if (!HasAdminPrivileges())
        {
            var pkexec = ProcessUtil.FindCommand("pkexec");
            if (pkexec is null)
            {
                progress.Report("pkexec is unavailable; run scripts/grant-caps.sh in a terminal.");
                return false;
            }

            command = pkexec;
            arguments = [setcap, .. arguments];
        }

        progress.Report("Requesting permission to enable packet capture…");
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
            var result = await Cli.Wrap(command)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeoutCts.Token);
            if (result.ExitCode != 0)
            {
                var error = result.StandardError.Trim();
                progress.Report(string.IsNullOrWhiteSpace(error)
                    ? $"setcap exited with code {result.ExitCode}."
                    : error);
                return false;
            }

            var ready = await HasLinuxCaptureCapsAsync(cancellationToken);
            progress.Report(ready
                ? "Capture privileges enabled. Packet capture is ready."
                : "Capabilities were not retained by the executable's filesystem.");
            return ready;
        }
        catch (OperationCanceledException)
        {
            progress.Report("Privilege request timed out or was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            progress.Report($"Could not enable capture privileges: {ex.Message}");
            return false;
        }
    }

    internal static string? ResolveCaptureExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (!string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;

        var entryName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (!string.IsNullOrWhiteSpace(entryName))
        {
            var appHost = Path.Combine(AppContext.BaseDirectory, entryName);
            if (File.Exists(appHost)) return appHost;
        }

        return processPath;
    }

    private static async Task<bool> LibPcapPresentAsync(CancellationToken cancellationToken)
    {
        string[] paths =
        [
            "/usr/lib/x86_64-linux-gnu/libpcap.so.0.8",
            "/usr/lib/x86_64-linux-gnu/libpcap.so.1",
            "/usr/lib/x86_64-linux-gnu/libpcap.so",
            "/usr/lib/aarch64-linux-gnu/libpcap.so.0.8",
            "/usr/lib/aarch64-linux-gnu/libpcap.so.1",
            "/usr/lib/aarch64-linux-gnu/libpcap.so",
            "/usr/lib64/libpcap.so.0.8",
            "/usr/lib64/libpcap.so.1",
            "/usr/lib64/libpcap.so",
            "/usr/lib/libpcap.so.0.8",
            "/usr/lib/libpcap.so.1",
            "/usr/lib/libpcap.so"
        ];
        if (paths.Any(File.Exists)) return true;

        try
        {
            var output = await ProcessUtil.RunAsync("ldconfig", ["-p"], cancellationToken);
            return output is not null
                   && output.StandardOutput.Contains("libpcap.so", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> InstallLinuxPackagesAsync(string packages, IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report("Requesting administrator privileges…");
        string[]? args = null;
        var apt = ProcessUtil.FindCommand("apt-get");
        var dnf = ProcessUtil.FindCommand("dnf");
        var pacman = ProcessUtil.FindCommand("pacman");
        if (apt is not null)
        {
            var env = ProcessUtil.FindCommand("env") ?? "/usr/bin/env";
            args = [env, "DEBIAN_FRONTEND=noninteractive", apt, "install", "-y", "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold", .. packages.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        }
        else if (dnf is not null)
            args = [dnf, "install", "-y", .. packages.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        else if (pacman is not null)
            args = [pacman, "-S", "--noconfirm", .. packages.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

        if (args is null)
        {
            progress.Report("No supported package manager found (apt, dnf, pacman).");
            return false;
        }

        var pkexec = ProcessUtil.FindCommand("pkexec");
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            Command cmd;
            if (pkexec is not null)
            {
                cmd = Cli.Wrap(pkexec).WithArguments(["--disable-internal-agent", .. args]);
            }
            else
            {
                cmd = Cli.Wrap(args[0]).WithArguments(args.Skip(1));
            }

            cmd = cmd
                .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                {
                    if (!string.IsNullOrWhiteSpace(line)) progress.Report(line.Trim());
                }))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                {
                    if (!string.IsNullOrWhiteSpace(line)) progress.Report(line.Trim());
                }));

            var result = await cmd.WithValidation(CommandResultValidation.None).ExecuteAsync(timeoutCts.Token);
            progress.Report(result.ExitCode == 0 ? "Install finished." : $"Installer exited with code {result.ExitCode}");
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            progress.Report("Installation timed out or was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            progress.Report(ex.Message);
            return false;
        }
    }

    private static readonly string[] WindowsIperfUrls =
    [
        "https://github.com/ar51an/iperf3-win-builds/releases/download/3.21/iperf-3.21-win64.zip",
        "https://github.com/ar51an/iperf3-win-builds/releases/download/3.20/iperf-3.20-win64.zip"
    ];

    private static async Task<bool> InstallWindowsIperfAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "PortStrider",
            "bin");

        try
        {
            Directory.CreateDirectory(targetDir);
            byte[]? zipData = null;

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PortStrider/1.0");
                foreach (var url in WindowsIperfUrls)
                {
                    try
                    {
                        progress.Report($"Downloading iperf3 for Windows ({new Uri(url).Segments[^1]})…");
                        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        if (!resp.IsSuccessStatusCode) continue;

                        var totalBytes = resp.Content.Headers.ContentLength ?? -1;
                        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                        using var ms = new MemoryStream();
                        var buffer = new byte[81920];
                        long totalRead = 0;
                        int read;
                        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            await ms.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            totalRead += read;
                            if (totalBytes > 0)
                            {
                                var pct = (int)((totalRead * 100) / totalBytes);
                                progress.Report($"Downloading iperf3: {pct}% ({totalRead / 1024} KB)");
                            }
                        }
                        zipData = ms.ToArray();
                        break;
                    }
                    catch
                    {
                        // Try next URL mirror
                    }
                }
            }

            if (zipData is null || zipData.Length == 0)
            {
                progress.Report("Direct download failed. Checking if winget is available…");
                var winget = ProcessUtil.FindCommand("winget");
                if (winget is not null)
                {
                    progress.Report("Installing iperf3 via winget…");
                    var result = await ProcessUtil.RunAsync(winget, ["install", "--id=ar51an.iPerf3", "-e", "--silent", "--accept-source-agreements", "--accept-package-agreements"], cancellationToken);
                    if (result is not null && result.ExitCode == 0)
                    {
                        progress.Report("iperf3 installed successfully via winget.");
                        return true;
                    }
                }

                progress.Report("Failed to download iperf3 automatically.");
                return false;
            }

            progress.Report("Extracting iperf3 to PortStrider tools directory…");
            using (var zipStream = new MemoryStream(zipData))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                    var destFile = Path.Combine(targetDir, entry.Name);
                    entry.ExtractToFile(destFile, overwrite: true);
                }
            }

            // Prepend targetDir to current process PATH
            var curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!curPath.Split(Path.PathSeparator).Contains(targetDir, StringComparer.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", targetDir + Path.PathSeparator + curPath);
            }

            progress.Report($"iPerf3 installed successfully to {targetDir}");
            return true;
        }
        catch (Exception ex)
        {
            progress.Report($"iPerf3 installation failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> InstallWindowsNpcapAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        var installerUrl = "https://npcap.com/dist/npcap-1.88.exe";
        try
        {
            progress.Report("Checking for latest Npcap installer…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            try
            {
                var html = await http.GetStringAsync("https://npcap.com/#download", cancellationToken);
                var m = Regex.Match(html, @"href=""(dist/npcap-[\d\.]+\.exe)""", RegexOptions.IgnoreCase);
                if (m.Success)
                    installerUrl = "https://npcap.com/" + m.Groups[1].Value.TrimStart('/');
            }
            catch
            {
                // Fallback to default installerUrl
            }

            var tempInstaller = Path.Combine(Path.GetTempPath(), "npcap-setup.exe");
            progress.Report($"Downloading Npcap installer ({new Uri(installerUrl).Segments[^1]})…");

            using (var resp = await http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                resp.EnsureSuccessStatusCode();
                var totalBytes = resp.Content.Headers.ContentLength ?? -1;
                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                await using var fs = new FileStream(tempInstaller, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;
                    if (totalBytes > 0)
                    {
                        var pct = (int)((totalRead * 100) / totalBytes);
                        progress.Report($"Downloading Npcap: {pct}% ({totalRead / (1024 * 1024)} MB)");
                    }
                }
            }

            progress.Report("Starting Npcap installer (ensure 'WinPcap API-compatible Mode' is checked during setup)…");
            var psi = new ProcessStartInfo
            {
                FileName = tempInstaller,
                UseShellExecute = true
            };
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                await proc.WaitForExitAsync(cancellationToken);
                progress.Report("Npcap installation wizard closed.");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            progress.Report($"Direct download failed ({ex.Message}), opening official download page…");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://npcap.com/#download",
                    UseShellExecute = true
                });
            }
            catch { /* ignore */ }
            return false;
        }
    }

    private static async Task<bool> ElevateWindowsPrivilegesAsync(IProgress<string> progress)
    {
        progress.Report("Requesting administrator privileges to relaunch PortStrider…");
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                progress.Report("Unable to determine current process path.");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            };
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                await Task.Delay(500);
                Environment.Exit(0);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            progress.Report($"Elevation canceled or failed: {ex.Message}");
            return false;
        }
    }
}

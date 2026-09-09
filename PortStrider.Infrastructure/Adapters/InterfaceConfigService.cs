using System.Net;
using System.Text.RegularExpressions;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Adapters;

/// <summary>
/// Linux implementation of the LinkRunner G2 "Connect" settings: user-defined MAC, forced speed/duplex,
/// VLAN ID + priority (as a temporary 802.1Q sub-interface) and static IPv4. Every change is undone when the
/// returned session is disposed. Uses iproute2 and ethtool through <see cref="PrivilegedProcess"/>.
/// </summary>
public sealed partial class InterfaceConfigService : IInterfaceConfigService
{
    private static readonly TimeSpan CarrierWait = TimeSpan.FromSeconds(12);

    public bool IsSupported => HostOs.IsLinux && ProcessUtil.FindCommand("ip") is not null;

    public bool RequiresChanges(TestProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.OverrideMac)
        || profile.LinkSpeed != LinkSpeedSetting.Auto
        || profile.LinkDuplex != DuplexSetting.Auto
        || profile.VlanId is > 0
        || (profile.AddressMode == AddressMode.Static && !string.IsNullOrWhiteSpace(profile.StaticIpv4));

    public async Task<InterfaceConfigSession> ApplyAsync(AdapterInfo adapter, TestProfile profile, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var baseIface = adapter.Name;
        var effective = baseIface;
        var applied = new List<string>();
        var problems = new List<string>();
        var undo = new Stack<Func<CancellationToken, Task>>();

        if (!RequiresChanges(profile))
        {
            return new InterfaceConfigSession(
                new InterfaceConfigResult { Status = TestStatus.Skipped, Summary = "Profile uses adapter defaults.", EffectiveInterface = effective },
                baseIface, effective, _ => Task.CompletedTask);
        }

        if (!IsSupported)
        {
            return new InterfaceConfigSession(
                new InterfaceConfigResult
                {
                    Status = TestStatus.Skipped,
                    Summary = HostOs.IsLinux ? "iproute2 (ip) is not installed." : "Interface reconfiguration is only implemented on Linux.",
                    EffectiveInterface = effective
                },
                baseIface, effective, _ => Task.CompletedTask);
        }

        async Task RestoreAsync(CancellationToken ct)
        {
            while (undo.Count > 0)
            {
                var step = undo.Pop();
                try { await step(ct); }
                catch { /* best effort */ }
            }
        }

        try
        {
            // 1. User-defined MAC
            if (!string.IsNullOrWhiteSpace(profile.OverrideMac))
            {
                var wanted = NormalizeMac(profile.OverrideMac);
                if (wanted is null)
                {
                    problems.Add($"MAC '{profile.OverrideMac}' is not valid");
                }
                else if (!string.Equals(wanted, NormalizeMac(adapter.MacAddress), StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report($"Setting MAC {wanted} on {baseIface}…");
                    var original = NormalizeMac(adapter.MacAddress) ?? LinuxSysFs.Read(LinuxSysFs.Net(baseIface, "address"));
                    var ok = await Ip(["link", "set", "dev", baseIface, "down"], cancellationToken)
                             && await Ip(["link", "set", "dev", baseIface, "address", wanted], cancellationToken)
                             && await Ip(["link", "set", "dev", baseIface, "up"], cancellationToken);
                    if (ok)
                    {
                        applied.Add($"MAC {wanted}");
                        if (original is not null)
                        {
                            undo.Push(async ct =>
                            {
                                await Ip(["link", "set", "dev", baseIface, "down"], ct);
                                await Ip(["link", "set", "dev", baseIface, "address", original], ct);
                                await Ip(["link", "set", "dev", baseIface, "up"], ct);
                            });
                        }

                        await WaitForCarrierAsync(baseIface, cancellationToken);
                    }
                    else
                    {
                        await Ip(["link", "set", "dev", baseIface, "up"], cancellationToken);
                        problems.Add($"MAC override refused ({LastError})");
                    }
                }
            }

            // 2. Forced speed / duplex
            if (profile.LinkSpeed != LinkSpeedSetting.Auto || profile.LinkDuplex != DuplexSetting.Auto)
            {
                if (ProcessUtil.FindCommand("ethtool") is null)
                {
                    problems.Add("ethtool is not installed; cannot force speed/duplex");
                }
                else
                {
                    var speed = SpeedMbps(profile.LinkSpeed);
                    var duplex = profile.LinkDuplex == DuplexSetting.Half ? "half" : "full";
                    List<string> args = ["-s", baseIface, "autoneg", "off"];
                    if (speed > 0) args.AddRange(["speed", speed.ToString()]);
                    args.AddRange(["duplex", duplex]);
                    progress?.Report($"Forcing {(speed > 0 ? speed + " Mb/s " : "")}{duplex} duplex on {baseIface}…");
                    var result = await PrivilegedProcess.RunAsync("ethtool", args, cancellationToken);
                    if (result.Success)
                    {
                        applied.Add($"link {(speed > 0 ? speed + " Mb/s" : "")} {duplex}".Trim());
                        undo.Push(ct => PrivilegedProcess.RunAsync("ethtool", ["-s", baseIface, "autoneg", "on"], ct));
                        await WaitForCarrierAsync(baseIface, cancellationToken);
                    }
                    else
                    {
                        problems.Add($"ethtool -s refused ({Trim(result.Combined)})");
                    }
                }
            }

            // 3. VLAN sub-interface
            if (profile.VlanId is > 0 and <= 4094)
            {
                var vid = profile.VlanId.Value;
                var pri = Math.Clamp(profile.VlanPriority, 0, 7);
                var name = VlanInterfaceName(baseIface, vid);
                progress?.Report($"Creating {name} (VLAN {vid}, priority {pri})…");
                await Ip(["link", "del", name], cancellationToken); // stale from a previous crash
                var qos = string.Join(' ', Enumerable.Range(0, 8).Select(p => $"{p}:{pri}"));
                var ok = await Ip(["link", "add", "link", baseIface, "name", name, "type", "vlan", "id", vid.ToString(), "egress-qos-map", .. qos.Split(' ')], cancellationToken)
                         && await Ip(["link", "set", "dev", name, "up"], cancellationToken);
                if (ok)
                {
                    effective = name;
                    applied.Add($"VLAN {vid} pri {pri} → {name}");
                    undo.Push(ct => Ip(["link", "del", name], ct));
                    await WaitForCarrierAsync(name, cancellationToken);
                }
                else
                {
                    await Ip(["link", "del", name], cancellationToken);
                    problems.Add($"VLAN sub-interface failed ({LastError})");
                }
            }

            // 4. Static IPv4
            if (profile.AddressMode == AddressMode.Static && !string.IsNullOrWhiteSpace(profile.StaticIpv4))
            {
                var cidr = ToCidr(profile.StaticIpv4, profile.StaticSubnet);
                if (cidr is null)
                {
                    problems.Add($"Static address '{profile.StaticIpv4}' / '{profile.StaticSubnet}' is not valid");
                }
                else
                {
                    var iface = effective;
                    progress?.Report($"Adding {cidr} on {iface}…");
                    if (await Ip(["addr", "replace", cidr, "dev", iface], cancellationToken))
                    {
                        applied.Add($"static {cidr}");
                        undo.Push(ct => Ip(["addr", "del", cidr, "dev", iface], ct));

                        if (!string.IsNullOrWhiteSpace(profile.StaticGateway) && IPAddress.TryParse(profile.StaticGateway, out _))
                        {
                            var gw = profile.StaticGateway;
                            // Metric 50 keeps the host's existing default route intact; ours only wins for this NIC.
                            if (await Ip(["route", "replace", "default", "via", gw, "dev", iface, "metric", "50"], cancellationToken))
                            {
                                applied.Add($"gateway {gw}");
                                undo.Push(ct => Ip(["route", "del", "default", "via", gw, "dev", iface, "metric", "50"], ct));
                            }
                            else
                            {
                                problems.Add($"default route via {gw} refused ({LastError})");
                            }
                        }
                    }
                    else
                    {
                        problems.Add($"static address refused ({LastError})");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            await RestoreAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            problems.Add(ex.Message);
        }

        var status = problems.Count == 0 ? TestStatus.Pass : applied.Count > 0 ? TestStatus.Warning : TestStatus.Fail;
        var summary = (applied.Count > 0 ? string.Join(" · ", applied) : "No changes applied")
                      + (problems.Count > 0 ? " — " + string.Join("; ", problems) : "");

        return new InterfaceConfigSession(
            new InterfaceConfigResult { Status = status, Summary = summary, EffectiveInterface = effective, Applied = applied },
            baseIface, effective, RestoreAsync);
    }

    public async Task<string?> ApplyAddressAsync(InterfaceConfigSession session, string address, string? subnetMask, string? gateway, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return "Interface reconfiguration is only implemented on Linux.";
        var cidr = ToCidr(address, subnetMask);
        if (cidr is null) return $"'{address}' / '{subnetMask}' is not a valid IPv4 address";
        var iface = session.EffectiveInterface;

        if (!await Ip(["addr", "replace", cidr, "dev", iface], cancellationToken))
            return $"ip addr replace refused: {LastError}";
        session.AddUndo(ct => Ip(["addr", "del", cidr, "dev", iface], ct));

        if (!string.IsNullOrWhiteSpace(gateway) && IPAddress.TryParse(gateway, out _))
        {
            if (!await Ip(["route", "replace", "default", "via", gateway, "dev", iface, "metric", "50"], cancellationToken))
                return $"default route via {gateway} refused: {LastError}";
            session.AddUndo(ct => Ip(["route", "del", "default", "via", gateway, "dev", iface, "metric", "50"], ct));
        }

        return null;
    }

    public async Task FlashPortAsync(string interfaceName, TimeSpan period, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Flash Port needs Linux with iproute2.");
        // Auto-negotiation takes ~1.5-3 s, so anything under that just looks like the port is down.
        var half = TimeSpan.FromMilliseconds(Math.Max(750, period.TotalMilliseconds / 2));
        var cycles = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await Ip(["link", "set", "dev", interfaceName, "down"], cancellationToken))
                    throw new InvalidOperationException($"Cannot take {interfaceName} down: {LastError}");
                await Task.Delay(half, cancellationToken);
                await Ip(["link", "set", "dev", interfaceName, "up"], cancellationToken);
                cycles++;
                progress?.Report($"Flashing {interfaceName} — {cycles} link cycles");
                await Task.Delay(half, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
        finally
        {
            await Ip(["link", "set", "dev", interfaceName, "up"], CancellationToken.None);
        }
    }

    private string _lastError = "unknown error";
    private string LastError => _lastError;

    private async Task<bool> Ip(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await PrivilegedProcess.RunAsync("ip", args, cancellationToken, TimeSpan.FromSeconds(20));
        if (!result.Success) _lastError = Trim(result.Combined.Length == 0 ? $"exit {result.ExitCode}" : result.Combined);
        return result.Success;
    }

    internal static async Task<bool> WaitForCarrierAsync(string iface, CancellationToken cancellationToken, TimeSpan? maxWait = null)
    {
        var deadline = DateTime.UtcNow + (maxWait ?? CarrierWait);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var carrier = LinuxSysFs.Read(LinuxSysFs.Net(iface, "carrier"));
            var oper = LinuxSysFs.Read(LinuxSysFs.Net(iface, "operstate"));
            if (carrier == "1" && (oper is "up" or "unknown")) return true;
            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    internal static string VlanInterfaceName(string baseIface, int vid)
    {
        var name = $"{baseIface}.{vid}";
        return name.Length <= 15 ? name : $"psv{vid}";
    }

    internal static string? NormalizeMac(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hex = MacStrip().Replace(text, "");
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit)) return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToLowerInvariant()));
    }

    internal static string? ToCidr(string address, string? subnet)
    {
        address = address.Trim();
        if (address.Contains('/'))
        {
            var parts = address.Split('/');
            return IPAddress.TryParse(parts[0], out _) && int.TryParse(parts[1], out var p) && p is >= 0 and <= 32 ? $"{parts[0]}/{p}" : null;
        }

        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        var prefix = 24;
        if (!string.IsNullOrWhiteSpace(subnet))
        {
            if (int.TryParse(subnet.TrimStart('/'), out var p) && p is >= 0 and <= 32)
                prefix = p;
            else if (IPAddress.TryParse(subnet, out var mask))
            {
                var bits = mask.GetAddressBytes().Sum(b => System.Numerics.BitOperations.PopCount(b));
                prefix = bits;
            }
            else return null;
        }

        return $"{ip}/{prefix}";
    }

    internal static int SpeedMbps(LinkSpeedSetting setting) => setting switch
    {
        LinkSpeedSetting.Mbps10 => 10,
        LinkSpeedSetting.Mbps100 => 100,
        LinkSpeedSetting.Mbps1000 => 1000,
        LinkSpeedSetting.Mbps2500 => 2500,
        LinkSpeedSetting.Mbps5000 => 5000,
        LinkSpeedSetting.Mbps10000 => 10000,
        _ => 0
    };

    private static string Trim(string text)
    {
        text = text.Replace('\n', ' ').Trim();
        return text.Length <= 100 ? text : text[..97] + "…";
    }

    [GeneratedRegex("[^0-9A-Fa-f]")]
    private static partial Regex MacStrip();
}

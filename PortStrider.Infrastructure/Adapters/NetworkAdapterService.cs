using System.Net.NetworkInformation;
using System.Net.Sockets;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;
using PortStrider.Infrastructure.Platform;
using SharpPcap;
using SharpPcap.LibPcap;

namespace PortStrider.Infrastructure.Adapters;

public sealed class NetworkAdapterService : IAdapterService
{
    public IReadOnlyList<AdapterInfo> ListAdapters(bool includeDown = true)
    {
        var pcapMap = MapPcapDevices();
        var list = new List<AdapterInfo>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback && !includeDown)
                continue;

            var media = AdapterMediaClassifier.Classify(ni);
            if (media == LinkMediaKind.Loopback)
                continue;

            var props = ni.GetIPProperties();
            string? ipv4 = null;
            string? mask = null;
            string? ipv6 = null;
            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork && ipv4 is null)
                {
                    ipv4 = addr.Address.ToString();
                    mask = addr.IPv4Mask.ToString();
                }
                else if (addr.Address.AddressFamily == AddressFamily.InterNetworkV6
                         && !addr.Address.IsIPv6LinkLocal
                         && ipv6 is null)
                {
                    ipv6 = addr.Address.ToString();
                }
            }

            var gw = props.GatewayAddresses
                .Select(g => g.Address.ToString())
                .FirstOrDefault(a => a is not "0.0.0.0");

            pcapMap.TryGetValue(ni.Name, out var pcapName);
            if (pcapName is null)
            {
                foreach (var kv in pcapMap)
                {
                    if (ni.Description.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)
                        || kv.Key.Contains(ni.Name, StringComparison.OrdinalIgnoreCase)
                        || kv.Value.Contains(ni.Description, StringComparison.OrdinalIgnoreCase)
                        || kv.Value.Contains(ni.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        pcapName = kv.Value;
                        break;
                    }
                }
            }

            var duplex = HostOs.IsLinux ? LinuxSysFs.Read(LinuxSysFs.Net(ni.Name, "duplex")) : null;
            var sysSpeed = HostOs.IsLinux ? LinuxSysFs.ReadInt64(LinuxSysFs.Net(ni.Name, "speed")) : null;
            var speedBps = sysSpeed is > 0 ? sysSpeed.Value * 1_000_000 : ni.Speed;
            if (speedBps < 0) speedBps = 0;

            var dhcpEnabled = false;
            if (OperatingSystem.IsWindows())
            {
                try { dhcpEnabled = props.GetIPv4Properties()?.IsDhcpEnabled ?? false; }
                catch { dhcpEnabled = false; }
            }
            else if (HostOs.IsLinux)
            {
                dhcpEnabled = LinuxSysFs.Read(LinuxSysFs.Net(ni.Name, "operstate")) is not null
                              && File.Exists($"/var/lib/dhcp/dhclient.{ni.Name}.leases");
            }

            var info = new AdapterInfo
            {
                Id = ni.Id,
                Name = ni.Name,
                PcapName = pcapName,
                Description = string.IsNullOrWhiteSpace(ni.Description) ? ni.Name : ni.Description,
                MacAddress = FormatMac(ni.GetPhysicalAddress()),
                Ipv4 = ipv4,
                Ipv6 = ipv6,
                SubnetMask = mask,
                Gateway = gw,
                DnsServers = props.DnsAddresses.Select(a => a.ToString()).Distinct().ToArray(),
                DhcpServers = SafeDhcp(props),
                DhcpEnabled = dhcpEnabled,
                Status = MapStatus(ni.OperationalStatus),
                SpeedBps = speedBps,
                Mtu = props.GetIPv4Properties()?.Mtu ?? 0,
                Media = media,
                Duplex = string.IsNullOrWhiteSpace(duplex) ? null : Capitalize(duplex)
            };

            if (!includeDown && !info.IsUp) continue;
            list.Add(info);
        }

        return list
            .OrderByDescending(a => a.IsUp)
            .ThenBy(a => a.Media == LinkMediaKind.Virtual)
            .ThenBy(a => a.Name)
            .ToArray();
    }

    public AdapterInfo? Find(string nameOrId) =>
        ListAdapters().FirstOrDefault(a =>
            a.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase)
            || a.Id.Equals(nameOrId, StringComparison.OrdinalIgnoreCase)
            || (a.PcapName?.Contains(nameOrId, StringComparison.OrdinalIgnoreCase) ?? false));

    public InterfaceSnapshot? Snapshot(string adapterId)
    {
        var ni = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.Id == adapterId || n.Name.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
        if (ni is null) return null;

        var stats = ni.GetIPStatistics();
        long rx = stats.BytesReceived;
        long tx = stats.BytesSent;
        long rxErr = stats.IncomingPacketsWithErrors;
        long txErr = stats.OutgoingPacketsWithErrors;
        long rxDrop = 0;
        long txDrop = 0;
        try { rxDrop = stats.IncomingPacketsDiscarded; } catch { /* platform */ }
        if (!OperatingSystem.IsMacOS())
        {
            try { txDrop = stats.OutgoingPacketsDiscarded; } catch { /* platform */ }
        }

        long rxPkts = stats.UnicastPacketsReceived;
        long txPkts = stats.UnicastPacketsSent;
        if (!OperatingSystem.IsLinux())
        {
            try
            {
                rxPkts += stats.NonUnicastPacketsReceived;
                txPkts += stats.NonUnicastPacketsSent;
            }
            catch { /* platform */ }
        }

        if (HostOs.IsLinux)
        {
            rx = LinuxSysFs.ReadInt64(LinuxSysFs.Stat(ni.Name, "rx_bytes")) ?? rx;
            tx = LinuxSysFs.ReadInt64(LinuxSysFs.Stat(ni.Name, "tx_bytes")) ?? tx;
            rxErr = LinuxSysFs.ReadInt64(LinuxSysFs.Stat(ni.Name, "rx_errors")) ?? rxErr;
            txErr = LinuxSysFs.ReadInt64(LinuxSysFs.Stat(ni.Name, "tx_errors")) ?? txErr;
            rxDrop = LinuxSysFs.ReadInt64(LinuxSysFs.Stat(ni.Name, "rx_dropped")) ?? rxDrop;
            txDrop = LinuxSysFs.ReadInt64(LinuxSysFs.Stat(ni.Name, "tx_dropped")) ?? txDrop;
        }

        return new InterfaceSnapshot
        {
            AdapterId = ni.Id,
            RxBytes = rx,
            TxBytes = tx,
            RxPackets = rxPkts,
            TxPackets = txPkts,
            RxErrors = rxErr,
            TxErrors = txErr,
            RxDropped = rxDrop,
            TxDropped = txDrop
        };
    }

    private static Dictionary<string, string> MapPcapDevices()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var device in CaptureDeviceList.Instance)
            {
                map[device.Name] = device.Name;
                if (device is LibPcapLiveDevice live)
                {
                    if (!string.IsNullOrWhiteSpace(live.Interface.FriendlyName))
                        map[live.Interface.FriendlyName] = live.Name;
                    if (!string.IsNullOrWhiteSpace(live.Description))
                        map[live.Description] = live.Name;
                }
            }
        }
        catch
        {
            // libpcap / Npcap missing — adapter list still works without capture names.
        }

        return map;
    }

    private static string[] SafeDhcp(IPInterfaceProperties props)
    {
        try
        {
            if (!OperatingSystem.IsMacOS())
                return props.DhcpServerAddresses.Select(a => a.ToString()).Where(a => a is not "255.255.255.255").ToArray();
        }
        catch
        {
            return [];
        }
        return [];
    }

    private static OperationalStatusKind MapStatus(OperationalStatus status) => status switch
    {
        OperationalStatus.Up => OperationalStatusKind.Up,
        OperationalStatus.Down => OperationalStatusKind.Down,
        OperationalStatus.Testing => OperationalStatusKind.Testing,
        OperationalStatus.Dormant => OperationalStatusKind.Dormant,
        OperationalStatus.NotPresent => OperationalStatusKind.NotPresent,
        OperationalStatus.LowerLayerDown => OperationalStatusKind.LowerLayerDown,
        _ => OperationalStatusKind.Unknown
    };

    private static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 0) return "—";
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}

using System.Net.NetworkInformation;
using PortStrider.Core.Enums;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Adapters;

internal static class AdapterMediaClassifier
{
    public static LinkMediaKind Classify(NetworkInterface adapter) =>
        Classify(adapter.NetworkInterfaceType, adapter.Name, adapter.Description,
            HostOs.IsLinux ? "/sys/class/net" : null);

    internal static LinkMediaKind Classify(NetworkInterfaceType type, string name, string description,
        string? linuxSysfsNetRoot)
    {
        // Linux managed WiFi interfaces use ARPHRD_ETHER, which .NET can report as Ethernet.
        // The kernel's wireless/PHY metadata identifies the radio even when disconnected or renamed.
        if (type == NetworkInterfaceType.Wireless80211
            || linuxSysfsNetRoot is not null
            && (Directory.Exists(Path.Combine(linuxSysfsNetRoot, name, "wireless"))
                || Directory.Exists(Path.Combine(linuxSysfsNetRoot, name, "phy80211"))))
            return LinkMediaKind.WiFi;

        return type switch
        {
            NetworkInterfaceType.Loopback => LinkMediaKind.Loopback,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Ethernet3Megabit => LinkMediaKind.Ethernet,
            NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp => LinkMediaKind.Virtual,
            _ => description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                 || name.StartsWith("br", StringComparison.OrdinalIgnoreCase)
                 || name.StartsWith("docker", StringComparison.OrdinalIgnoreCase)
                 || name.StartsWith("veth", StringComparison.OrdinalIgnoreCase)
                ? LinkMediaKind.Virtual
                : LinkMediaKind.Other
        };
    }
}

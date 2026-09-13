using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Core.Services;

namespace PortStrider.Infrastructure.Capabilities;

public sealed class CapabilityService : ICapabilityService
{
    public Task<AdapterCapabilities> EvaluateAsync(AdapterInfo adapter, CancellationToken cancellationToken = default)
    {
        var features = new List<FeatureCapability>
        {
            Feature("link", "Physical link", CapabilityLevel.Supported, "Uses host NIC link state."),
            Feature("wifi", "WiFi spectrum / channel analyzer", adapter.Media == LinkMediaKind.WiFi
                    && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && HasTool("iw"))
                    ? CapabilityLevel.Degraded : CapabilityLevel.Unavailable,
                "Scanned access-point channels and RSSI; band support depends on the radio. Does not measure raw RF energy or non-WiFi interference.",
                "Select a WiFi adapter; Linux needs iw and CAP_NET_ADMIN; Windows needs WLAN AutoConfig and location access."),
            Feature("lldp", "LLDP/CDP/EDP discovery", adapter.PcapName is null ? CapabilityLevel.Degraded : CapabilityLevel.Supported,
                adapter.PcapName is null ? "No capture device mapped — install libpcap/Npcap." : "Passive decode from wire."),
            Feature("capture", "Packet capture", adapter.PcapName is null ? CapabilityLevel.Unavailable : CapabilityLevel.Supported,
                "Requires libpcap/Npcap and capture privileges.", adapter.PcapName is null ? "Install libpcap/Npcap." : null),
            Feature("dhcp", "Active DHCP (Discover/Offer/Request/Ack)", OperatingSystem.IsLinux() ? CapabilityLevel.Supported : CapabilityLevel.Degraded,
                OperatingSystem.IsLinux()
                    ? "Full DORA with Option 55/60/43/150, bound to the adapter (SO_BINDTODEVICE)."
                    : "Full DORA; bound to the adapter address, broadcast egress follows the Windows routing table."),
            Feature("link-adv", "Advertised vs actual speed / MDI-X", OperatingSystem.IsLinux() && HasTool("ethtool") ? CapabilityLevel.Supported : CapabilityLevel.Degraded,
                OperatingSystem.IsLinux() ? "Parsed from ethtool link settings; flags downshift." : "NDIS reports actual speed only."),
            Feature("link-force", "Forced speed / duplex", OperatingSystem.IsLinux() && HasTool("ethtool") ? CapabilityLevel.Supported : CapabilityLevel.Unavailable,
                OperatingSystem.IsLinux() ? "ethtool -s during AutoTest, auto-negotiation restored afterwards." : "Not exposed through Windows NDIS."),
            Feature("mac", "User-defined MAC / static IP", OperatingSystem.IsLinux() && HasTool("ip") ? CapabilityLevel.Supported : CapabilityLevel.Unavailable,
                OperatingSystem.IsLinux() ? "iproute2 during AutoTest, restored afterwards." : "Requires iproute2 (Linux)."),
            Feature("vlan", "VLAN tagging (ID + priority)", OperatingSystem.IsLinux() && HasTool("ip") ? CapabilityLevel.Supported : CapabilityLevel.Unavailable,
                OperatingSystem.IsLinux() ? "Temporary 802.1Q sub-interface; DHCP and targets run tagged." : "Requires iproute2 (Linux)."),
            Feature("8021x", "802.1X authentication", OperatingSystem.IsLinux() && HasTool("wpa_supplicant") ? CapabilityLevel.Supported : CapabilityLevel.Unavailable,
                OperatingSystem.IsLinux() ? "wpa_supplicant wired driver (PEAP, TTLS, TLS, MD5)." : "Requires wpa_supplicant (Linux).",
                OperatingSystem.IsLinux() && !HasTool("wpa_supplicant") ? "Install wpasupplicant." : null),
            Feature("flash", "Flash switch port LED", OperatingSystem.IsLinux() && HasTool("ip") ? CapabilityLevel.Supported : CapabilityLevel.Unavailable,
                "Cycles the link so the switch port LED blinks."),
            Feature("reflector", "Packet reflector", adapter.PcapName is null ? CapabilityLevel.Unavailable : CapabilityLevel.Supported,
                "Swaps MAC (+IP/ports) and re-injects via libpcap/Npcap."),
            Feature("poe-loaded", "Loaded PoE measurement", CapabilityLevel.Unavailable,
                "Requires dedicated PoE load hardware.", "Use a PoE load meter or powered-device tester."),
            Feature("poe-adv", "Advertised PoE (LLDP/CDP)", CapabilityLevel.Supported, "Decoded from neighbor frames only."),
            Feature("cable-tdr", "Cable TDR", OperatingSystem.IsLinux() ? CapabilityLevel.Degraded : CapabilityLevel.Unavailable,
                OperatingSystem.IsLinux() ? "Uses ethtool when driver exposes cable test." : "Windows NDIS does not expose pair TDR."),
            Feature("wiremap", "Wiremap / remote IDs", CapabilityLevel.Unavailable,
                "Requires dedicated cable-test port and remote identifiers.", "Use an external wire mapper."),
            Feature("tone", "Tone generation", CapabilityLevel.Unavailable,
                "Requires dedicated tone hardware.", "Use an external tone probe."),
            Feature("sfp", "SFP diagnostics", OperatingSystem.IsLinux() ? CapabilityLevel.Degraded : CapabilityLevel.Unavailable,
                OperatingSystem.IsLinux() ? "Uses ethtool module EEPROM/DDM when exposed." : "Not exposed through Windows NDIS."),
            Feature("iperf", "Throughput (iperf3)", CapabilityLevel.Supported, "External iperf3 client/server."),
            Feature("reports", "Local reports & bundles", CapabilityLevel.Supported, "JSON, CSV, PDF, PCAP, ZIP bundles.")
        };

        return Task.FromResult(new AdapterCapabilities
        {
            AdapterId = adapter.Id,
            AdapterName = adapter.Name,
            Features = features
        });
    }

    private static bool HasTool(string name) => Platform.ProcessUtil.FindCommand(name) is not null;

    private static FeatureCapability Feature(string id, string name, CapabilityLevel level, string summary, string? prerequisite = null) =>
        new() { Id = id, Name = name, Level = level, Summary = summary, Prerequisite = prerequisite };
}

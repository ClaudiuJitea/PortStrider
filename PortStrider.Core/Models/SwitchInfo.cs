using PortStrider.Core.Enums;

namespace PortStrider.Core.Models;

public sealed class SwitchInfo
{
    public DiscoveryProtocol Protocol { get; init; } = DiscoveryProtocol.None;
    public string SwitchName { get; init; } = "—";
    public string PortId { get; init; } = "—";
    public string PortDescription { get; init; } = "—";
    public string VlanId { get; init; } = "—";
    public string NativeVlan { get; init; } = "—";
    /// <summary>Voice VLAN from LLDP-MED Network Policy (voice application) or CDP VoIP VLAN Reply.</summary>
    public string VoiceVlan { get; init; } = "—";
    public string ManagementIp { get; init; } = "—";
    public string Model { get; init; } = "—";
    public string ChassisId { get; init; } = "—";
    public string Capabilities { get; init; } = "—";
    public string Poe { get; init; } = "—";
    public string Duplex { get; init; } = "—";
    public string MauType { get; init; } = "—";
    public string SoftwareVersion { get; init; } = "—";
    public string ProtocolLabel => Protocol switch
    {
        DiscoveryProtocol.Lldp => "IEEE 802.1AB LLDP",
        DiscoveryProtocol.Cdp => "Cisco CDP",
        DiscoveryProtocol.Edp => "Extreme EDP",
        _ => "No neighbor frames yet"
    };

    public IReadOnlyList<int> ObservedVlans { get; init; } = [];
    public IReadOnlyList<string> Details { get; init; } = [];

    public static SwitchInfo Idle() => new()
    {
        SwitchName = "Awaiting frames",
        PortId = "—",
        Model = "Plug into a switch port and listen"
    };
}

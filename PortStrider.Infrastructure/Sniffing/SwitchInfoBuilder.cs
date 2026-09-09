using PortStrider.Core.Enums;
using PortStrider.Core.Models;

namespace PortStrider.Infrastructure.Sniffing;

internal sealed class SwitchInfoBuilder
{
    public DiscoveryProtocol Protocol { get; set; }
    public string SwitchName { get; set; } = "—";
    public string PortId { get; set; } = "—";
    public string PortDescription { get; set; } = "—";
    public string VlanId { get; set; } = "—";
    public string NativeVlan { get; set; } = "—";
    public string VoiceVlan { get; set; } = "—";
    public string ManagementIp { get; set; } = "—";
    private readonly HashSet<int> _observedVlans = [];
    private readonly object _vlanGate = new();

    public void AddObservedVlan(int vid)
    {
        lock (_vlanGate) _observedVlans.Add(vid);
    }

    public IReadOnlyList<int> ObservedVlans
    {
        get { lock (_vlanGate) return _observedVlans.OrderBy(v => v).ToArray(); }
    }
    public string Model { get; set; } = "—";
    public string ChassisId { get; set; } = "—";
    public string Capabilities { get; set; } = "—";
    public string Poe { get; set; } = "—";
    public string Duplex { get; set; } = "—";
    public string MauType { get; set; } = "—";
    public string SoftwareVersion { get; set; } = "—";
    private readonly List<string> _details = [];

    public void AddDetail(string line)
    {
        if (!string.IsNullOrWhiteSpace(line) && !_details.Contains(line))
            _details.Add(line);
    }

    public SwitchInfo Build() => new()
    {
        Protocol = Protocol,
        SwitchName = EmptyToDash(SwitchName),
        PortId = EmptyToDash(PortId),
        PortDescription = EmptyToDash(PortDescription),
        VlanId = EmptyToDash(VlanId),
        NativeVlan = EmptyToDash(NativeVlan),
        VoiceVlan = EmptyToDash(VoiceVlan),
        ManagementIp = EmptyToDash(ManagementIp),
        ObservedVlans = ObservedVlans,
        Model = EmptyToDash(Model),
        ChassisId = EmptyToDash(ChassisId),
        Capabilities = EmptyToDash(Capabilities),
        Poe = EmptyToDash(Poe),
        Duplex = EmptyToDash(Duplex),
        MauType = EmptyToDash(MauType),
        SoftwareVersion = EmptyToDash(SoftwareVersion),
        Details = _details.ToArray()
    };

    private static string EmptyToDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}

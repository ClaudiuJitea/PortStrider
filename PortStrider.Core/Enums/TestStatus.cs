namespace PortStrider.Core.Enums;

public enum TestStatus
{
    Pending,
    Running,
    Pass,
    Fail,
    Warning,
    Skipped
}

public enum AppSection
{
    AutoTest,
    Switch,
    Capture,
    Tools,
    Reflector,
    Cable,
    Neighbors,
    Reports,
    Profiles,
    VlanMonitor,
    Capabilities,
    PreFlight
}

public enum DiscoveryProtocol
{
    None,
    Lldp,
    Cdp,
    Edp
}

public enum LinkMediaKind
{
    Unknown,
    Ethernet,
    WiFi,
    Virtual,
    Loopback,
    Other
}

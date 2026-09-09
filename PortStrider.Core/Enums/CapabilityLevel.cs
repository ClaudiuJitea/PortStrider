namespace PortStrider.Core.Enums;

public enum CapabilityLevel
{
    Supported,
    Degraded,
    Unavailable
}

public enum AddressMode
{
    Dhcp,
    Static
}

public enum TargetKind
{
    Ping,
    TcpPort,
    Http
}

public enum CableUnit
{
    Meters,
    Feet
}

public enum EapType
{
    Peap,
    Ttls,
    Md5,
    Tls
}

public enum DhcpOptionKind
{
    None,
    Option43,
    Option60,
    Option150
}

public enum ReflectorSwapMode
{
    MacOnly,
    MacAndIp
}

public enum ReflectorFilterMode
{
    OwnMac,
    OwnMacAndNetAlly,
    All
}

public enum LinkSpeedSetting
{
    Auto,
    Mbps10,
    Mbps100,
    Mbps1000,
    Mbps2500,
    Mbps5000,
    Mbps10000
}

public enum DuplexSetting
{
    Auto,
    Full,
    Half
}

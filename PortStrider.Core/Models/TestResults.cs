using PortStrider.Core.Enums;

namespace PortStrider.Core.Models;

public sealed class DhcpTestResult
{
    public bool Supported { get; init; }
    public TestStatus Status { get; init; }
    public string Summary { get; init; } = "";
    public long DiscoverMs { get; init; }
    public long OfferMs { get; init; }
    public long RequestMs { get; init; }
    public long AckMs { get; init; }
    public string? ServerIp { get; init; }
    public string? OfferedIp { get; init; }
    public string? Subnet { get; init; }
    public string? Gateway { get; init; }
    public string? Dns { get; init; }
    public string? Option43 { get; init; }
    public string? Option60 { get; init; }
    public string? Option150 { get; init; }
    public string? LeaseTime { get; init; }
    public string? DomainName { get; init; }
    public bool RequestSent { get; init; }
    public bool AckReceived { get; init; }
    public string? InterfaceName { get; init; }
}

public sealed class DnsServerResult
{
    public required string Server { get; init; }
    public IReadOnlyList<long?> RoundtripsMs { get; init; } = [];
    public bool Success => RoundtripsMs.Any(r => r.HasValue);
    public string? ResolvedAddress { get; init; }
    public string? Error { get; init; }
}

public sealed class DnsTestResult
{
    public TestStatus Status { get; init; }
    public string Summary { get; init; } = "";
    public string QueryName { get; init; } = "";
    public IReadOnlyList<DnsServerResult> Servers { get; init; } = [];
}

public sealed class VlanTestResult
{
    public int ConfiguredVlan { get; init; }
    public int Priority { get; init; }
    public string? InterfaceName { get; init; }
    public TestStatus Status { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<int> SeenVlans { get; init; } = [];
}

public sealed class Dot1xResult
{
    public TestStatus Status { get; init; }
    public string Summary { get; init; } = "";
    public string EapMethod { get; init; } = "";
    public string Identity { get; init; } = "";
    public long ElapsedMs { get; init; }
    public string RawLog { get; init; } = "";
}

public sealed class InterfaceConfigResult
{
    public TestStatus Status { get; init; }
    public string Summary { get; init; } = "";
    public string EffectiveInterface { get; init; } = "";
    public IReadOnlyList<string> Applied { get; init; } = [];
}

public sealed class ReflectorStatistics
{
    public long FramesReceived { get; init; }
    public long FramesReflected { get; init; }
    public long BytesReceived { get; init; }
    public long BytesReflected { get; init; }
    public long FramesFiltered { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed class ReflectorOptions
{
    public ReflectorSwapMode Swap { get; init; } = ReflectorSwapMode.MacAndIp;
    public ReflectorFilterMode Filter { get; init; } = ReflectorFilterMode.OwnMacAndNetAlly;
    public string OwnMac { get; init; } = "";
}

public sealed class PoeMeasurement
{
    public CapabilityLevel Level { get; init; } = CapabilityLevel.Unavailable;
    public string Source { get; init; } = "Unavailable";
    public string Summary { get; init; } = "Loaded PoE measurement requires dedicated hardware.";
    public string? AdvertisedClass { get; init; }
    public string? AdvertisedWatts { get; init; }
    public string? Pairs { get; init; }
}

public sealed class LinkTelemetry
{
    public string ActualSpeed { get; init; } = "—";
    /// <summary>Highest speed advertised by the local NIC (from ethtool "Advertised link modes").</summary>
    public string AdvertisedSpeed { get; init; } = "—";
    public string AdvertisedDuplex { get; init; } = "—";
    /// <summary>Highest speed advertised by the link partner (switch) when the driver reports it.</summary>
    public string PartnerSpeed { get; init; } = "—";
    public string PartnerDuplex { get; init; } = "—";
    public string Duplex { get; init; } = "—";
    public string AutoNegotiation { get; init; } = "—";
    public string MdiMode { get; init; } = "—";
    public string Port { get; init; } = "—";
    public string Driver { get; init; } = "—";
    public string Firmware { get; init; } = "—";
    public bool EnergyEfficientEthernet { get; init; }
    /// <summary>True when the negotiated speed is lower than what both ends advertise (G2 "downshift").</summary>
    public bool Downshift { get; init; }
    public long ActualSpeedMbps { get; init; }
    public long AdvertisedSpeedMbps { get; init; }
    public long PartnerSpeedMbps { get; init; }
    public string Source { get; init; } = "OS";
}

public sealed class VlanObservation
{
    public int VlanId { get; init; }
    public long Frames { get; init; }
    public long Bytes { get; init; }
    public double SharePercent { get; init; }
}

public sealed class SfpDiagnostics
{
    public bool Supported { get; init; }
    public string Summary { get; init; } = "";
    public string? Vendor { get; init; }
    public string? PartNumber { get; init; }
    public string? Serial { get; init; }
    public string? Wavelength { get; init; }
    public string? Temperature { get; init; }
    public string? Voltage { get; init; }
    public string? TxPower { get; init; }
    public string? RxPower { get; init; }
    public string? Alarms { get; init; }
    public string RawOutput { get; init; } = "";
}

public sealed class TargetTestResult
{
    public required string TargetId { get; init; }
    public required string Label { get; init; }
    public required TestStatus Status { get; init; }
    public required string Details { get; init; }
    public long ElapsedMs { get; init; }
}

public sealed class AttachmentInfo
{
    public required Guid Id { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required string StoredPath { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.Now;
}

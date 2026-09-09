using PortStrider.Core.Enums;

namespace PortStrider.Core.Models;

public sealed class TestStepUpdate
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required TestStatus Status { get; init; }
    public required string Details { get; init; }
    /// <summary>Host this step probed, when the step can be re-run continuously (gateway, targets).</summary>
    public string? ContinuousHost { get; init; }
    /// <summary>TCP port for continuous re-run; null means ICMP ping.</summary>
    public int? ContinuousPort { get; init; }
}

public sealed class DependencyStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool IsReady { get; init; }
    public required string Details { get; init; }
    public bool CanAutoFix { get; init; }
    public string? FixHint { get; init; }
}

public sealed record CapturedFrame
{
    public required long Number { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required int Length { get; init; }
    public string SourceMac { get; init; } = "";
    public string DestinationMac { get; init; } = "";
    public string EtherType { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    public byte[] Raw { get; init; } = [];
    public string TimeLabel => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
}

public sealed record PingSample
{
    public required int Sequence { get; init; }
    public required bool Success { get; init; }
    public long RoundtripMs { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

public sealed class PingSummary
{
    public int Sent { get; init; }
    public int Received { get; init; }
    public int Lost => Math.Max(0, Sent - Received);
    public double LossPercent => Sent == 0 ? 0 : Lost * 100d / Sent;
    public double MinMs { get; init; }
    public double AvgMs { get; init; }
    public double MaxMs { get; init; }
}

public sealed class TraceHop
{
    public required int Ttl { get; init; }
    public string Address { get; init; } = "*";
    public string Rtt { get; init; } = "*";
    public bool TimedOut { get; init; }
}

public sealed class PortProbeResult
{
    public required int Port { get; init; }
    public required string Service { get; init; }
    public required bool Open { get; init; }
    public required long ElapsedMs { get; init; }
    public string? Error { get; init; }
    public string StatusLabel => Open ? "Open" : "Closed";
}

public sealed class HttpProbeResult
{
    public required string Url { get; init; }
    public int StatusCode { get; init; }
    public long ElapsedMs { get; init; }
    public string? TlsProtocol { get; init; }
    public string? CertificateSubject { get; init; }
    public string? CertificateIssuer { get; init; }
    public DateTimeOffset? CertificateNotAfter { get; init; }
    public string? Error { get; init; }
    public bool Success => Error is null && StatusCode is >= 200 and < 400;
}

public sealed class CablePairResult
{
    public required string Pair { get; init; }
    public required string Status { get; init; }
    public string? Distance { get; init; }
}

public sealed class CableTestResult
{
    public bool Supported { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<CablePairResult> Pairs { get; init; } = [];
    public string RawOutput { get; init; } = "";
}

public sealed class NeighborEntry
{
    public required string Address { get; init; }
    public string? Mac { get; init; }
    public string? InterfaceName { get; init; }
    public string State { get; init; } = "";
}

public sealed class IperfResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = "";
    public double BitsPerSecond { get; init; }
    public string? Error { get; init; }
    public string RawOutput { get; init; } = "";
}

public sealed record SessionReport
{
    public required Guid Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public required string AdapterName { get; init; }
    public string ProfileName { get; init; } = "";
    public string JobLabel { get; init; } = "";
    public string SiteLabel { get; init; } = "";
    public string TechnicianNotes { get; init; } = "";
    public string AssetSerial { get; init; } = "";
    public string CableLabel { get; init; } = "";
    public string WallJackLabel { get; init; } = "";
    public string Overall { get; init; } = "";
    public string Ipv4 { get; init; } = "";
    public string Ipv6 { get; init; } = "";
    public string Gateway { get; init; } = "";
    public SwitchInfo? Switch { get; init; }
    public DhcpTestResult? Dhcp { get; init; }
    public LinkTelemetry? Link { get; init; }
    public PoeMeasurement? Poe { get; init; }
    public DnsTestResult? Dns { get; init; }
    public VlanTestResult? Vlan { get; init; }
    public Dot1xResult? Dot1x { get; init; }
    public InterfaceConfigResult? InterfaceConfig { get; init; }
    public IReadOnlyList<TestStepUpdate> Steps { get; init; } = [];
    public IReadOnlyList<TargetTestResult> Targets { get; init; } = [];
    public IReadOnlyList<VlanObservation> ObservedVlans { get; init; } = [];
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
}

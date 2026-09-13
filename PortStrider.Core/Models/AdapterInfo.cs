using PortStrider.Core.Enums;

namespace PortStrider.Core.Models;

public sealed class AdapterInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? PcapName { get; init; }
    public string Description { get; init; } = "";
    public string MacAddress { get; init; } = "—";
    public string? Ipv4 { get; init; }
    public string? Ipv6 { get; init; }
    public string? SubnetMask { get; init; }
    public string? Gateway { get; init; }
    public IReadOnlyList<string> DnsServers { get; init; } = [];
    public IReadOnlyList<string> DhcpServers { get; init; } = [];
    public bool DhcpEnabled { get; init; }
    public OperationalStatusKind Status { get; init; }
    public long SpeedBps { get; init; }
    public int Mtu { get; init; }
    public LinkMediaKind Media { get; init; }
    public string? Duplex { get; init; }
    public bool IsUp => Status == OperationalStatusKind.Up;
    public string SpeedLabel => FormatSpeed(SpeedBps);
    public string DisplayLabel => $"{Name}  ·  {(Ipv4 ?? "no IPv4")}";
    public string CaptureDevice => PcapName ?? Name;
    public override string ToString() => DisplayLabel;

    public static string FormatSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "—";
        if (bitsPerSecond >= 1_000_000_000)
            return $"{bitsPerSecond / 1_000_000_000d:0.##} Gbps";
        if (bitsPerSecond >= 1_000_000)
            return $"{bitsPerSecond / 1_000_000d:0.##} Mbps";
        if (bitsPerSecond >= 1_000)
            return $"{bitsPerSecond / 1_000d:0.##} kbps";
        return $"{bitsPerSecond} bps";
    }
}

public enum OperationalStatusKind
{
    Unknown,
    Down,
    Up,
    Testing,
    Dormant,
    NotPresent,
    LowerLayerDown
}

public sealed class InterfaceSnapshot
{
    public required string AdapterId { get; init; }
    public long RxBytes { get; init; }
    public long TxBytes { get; init; }
    public long RxPackets { get; init; }
    public long TxPackets { get; init; }
    public long RxErrors { get; init; }
    public long TxErrors { get; init; }
    public long RxDropped { get; init; }
    public long TxDropped { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

public sealed class InterfaceRates
{
    public double RxMbps { get; init; }
    public double TxMbps { get; init; }
    public long RxErrors { get; init; }
    public long TxErrors { get; init; }
    public long RxDropped { get; init; }
    public long TxDropped { get; init; }
    public string RxLabel => $"{RxMbps:0.00} Mbps ↓";
    public string TxLabel => $"{TxMbps:0.00} Mbps ↑";
    public string RxValueLabel => $"{RxMbps:0.00} Mbps";
    public string TxValueLabel => $"{TxMbps:0.00} Mbps";
}

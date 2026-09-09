using PortStrider.Core.Enums;

namespace PortStrider.Core.Models;

public sealed class TestTarget
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required TargetKind Kind { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 443;
    public string Url { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

public sealed record TestProfile
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? AdapterId { get; init; }
    public AddressMode AddressMode { get; init; } = AddressMode.Dhcp;
    public bool EnableIpv6 { get; init; } = true;
    public bool EnablePoeAdvertised { get; init; } = true;
    public bool Enable8021X { get; init; }
    public EapType EapType { get; init; } = EapType.Peap;
    public string? Username8021X { get; init; }
    public string? Password8021X { get; init; }
    public string? AnonymousIdentity8021X { get; init; }
    public string? CaCertPath8021X { get; init; }
    public string? ClientCertPath8021X { get; init; }
    public string? ClientKeyPath8021X { get; init; }
    public string? StaticIpv4 { get; init; }
    public string? StaticSubnet { get; init; }
    public string? StaticGateway { get; init; }
    public string? StaticDns { get; init; }
    public int? VlanId { get; init; }
    public int VlanPriority { get; init; }
    public string? OverrideMac { get; init; }
    public DhcpOptionKind DhcpOption { get; init; } = DhcpOptionKind.None;
    public string? DhcpVendorClass { get; init; }
    /// <summary>Forced link speed for the test (G2 "Speed/Duplex" connect setting). Auto keeps auto-negotiation.</summary>
    public LinkSpeedSetting LinkSpeed { get; init; } = LinkSpeedSetting.Auto;
    public DuplexSetting LinkDuplex { get; init; } = DuplexSetting.Auto;
    public string? ProxyUrl { get; init; }
    /// <summary>Step id after which AutoTest stops: link, 8021x, vlan, dhcp, gw, dns, target-&lt;id&gt;, poe. Empty runs everything.</summary>
    public string StopAfterStepId { get; init; } = "";
    public string DnsQueryName { get; init; } = "cloudflare.com";
    public int PingCount { get; init; } = 3;
    public CableUnit CableUnit { get; init; } = CableUnit.Meters;
    public IReadOnlyList<TestTarget> Targets { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public static TestProfile Default() => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Name = "Default",
        Targets =
        [
            new TestTarget { Id = "gw", Label = "Gateway", Kind = TargetKind.Ping, Host = "" },
            new TestTarget { Id = "dns", Label = "DNS", Kind = TargetKind.Ping, Host = "1.1.1.1" },
            new TestTarget { Id = "wan", Label = "WAN", Kind = TargetKind.Ping, Host = "8.8.8.8" },
            new TestTarget { Id = "https", Label = "HTTPS", Kind = TargetKind.Http, Host = "", Url = "https://1.1.1.1" }
        ]
    };
}

using PortStrider.Infrastructure.Adapters;
using PortStrider.Infrastructure.Cable;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public sealed class CablePresentationTests
{
    private const string Settings = """
        Settings for enp0s13f0u1:
            Supported ports: [ TP MII ]
            Supported link modes: 10baseT/Half 10baseT/Full
                                  100baseT/Half 100baseT/Full
                                  1000baseT/Full
            Advertised link modes: 10baseT/Half 10baseT/Full
                                   100baseT/Half 100baseT/Full
                                   1000baseT/Full
            Link partner advertised link modes: 10baseT/Half 10baseT/Full
                                                100baseT/Half 100baseT/Full
            Speed: 100Mb/s
            Duplex: Full
            Auto-negotiation: on
            Port: MII
            MDI-X: on (auto)
            Link detected: yes
        """;

    [Fact]
    public void MapsRawPhySettingsToVisualSummary()
    {
        var info = CableTestService.ToPhyInfo(EthtoolSettings.Parse(Settings));
        Assert.True(info.LinkDetected);
        Assert.Equal("LINK UP", info.LinkLabel);
        Assert.Equal("100 Mbps", info.SpeedLabel);
        Assert.Equal("Full", info.Duplex);
        Assert.Equal("AUTO", info.NegotiationLabel);
        Assert.Equal("MII", info.Port);
        Assert.Equal("1 Gbps", info.LocalMaximum);
        Assert.Equal("100 Mbps", info.PartnerMaximum);
        Assert.False(info.IsDownshift);
    }
}

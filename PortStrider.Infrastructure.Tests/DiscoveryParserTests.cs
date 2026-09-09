using PortStrider.Core.Enums;
using PortStrider.Infrastructure.Sniffing;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public class DiscoveryParserTests
{
    [Fact]
    public void CaptureDeviceLease_PreventsConcurrentUseAndReleases()
    {
        using (CaptureDeviceLease.Acquire("test-device"))
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => CaptureDeviceLease.Acquire("test-device"));
            Assert.Contains("already being used", error.Message);
        }

        using var reacquired = CaptureDeviceLease.Acquire("test-device");
    }

    [Fact]
    public void EdpParser_DetectsExtremeFrame()
    {
        var frame = new byte[]
        {
            0x01, 0x80, 0xC2, 0x00, 0x00, 0x0E,
            0x00, 0x11, 0x22, 0x33, 0x44, 0x55,
            0x88, 0xB6,
            0x00, 0xE0, 0x2B, 0x01, 0x00, 0x00, 0x00, 0x10,
            0x00, 0x01, 0x00, 0x0A,
            (byte)'E', (byte)'x', (byte)'t', (byte)'r', (byte)'e', (byte)'m', (byte)'e', (byte)'1', (byte)'-', (byte)'1'
        };

        Assert.True(EdpParser.IsEdpFrame(frame));
        var builder = new SwitchInfoBuilder();
        EdpParser.Parse(frame, builder);
        var info = builder.Build();
        Assert.Equal(DiscoveryProtocol.Edp, info.Protocol);
        Assert.NotEqual("—", info.SwitchName);
    }

    [Fact]
    public void LldpParser_SetsProtocol()
    {
        var payload = new byte[]
        {
            0x04, 0x04, (byte)'p', (byte)'1', (byte)'/', (byte)'1',
            0x0A, 0x06, (byte)'s', (byte)'w', (byte)'i', (byte)'t', (byte)'c', (byte)'h',
            0x00, 0x00
        };
        var builder = new SwitchInfoBuilder();
        LldpParser.Parse(payload, builder);
        var info = builder.Build();
        Assert.Equal(DiscoveryProtocol.Lldp, info.Protocol);
        Assert.Equal("switch", info.SwitchName);
        Assert.NotEqual("—", info.PortId);
    }
}

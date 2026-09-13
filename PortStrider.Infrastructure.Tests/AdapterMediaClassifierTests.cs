using System.Net.NetworkInformation;
using PortStrider.Core.Enums;
using PortStrider.Infrastructure.Adapters;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public sealed class AdapterMediaClassifierTests : IDisposable
{
    private readonly string _sysfs = Directory.CreateTempSubdirectory("portstrider-sysfs-").FullName;

    [Theory]
    [InlineData("wlp0s20f3", "wireless")]
    [InlineData("wlp0s20f3", "phy80211")]
    [InlineData("renamed-radio", "wireless")]
    [InlineData("renamed-radio", "phy80211")]
    public void LinuxWirelessMetadataOverridesReportedEthernet(string name, string marker)
    {
        Directory.CreateDirectory(Path.Combine(_sysfs, name, marker));

        Assert.Equal(LinkMediaKind.WiFi,
            AdapterMediaClassifier.Classify(NetworkInterfaceType.Ethernet, name, name, _sysfs));
    }

    [Theory]
    [InlineData("eth0")]
    [InlineData("wlan0")]
    [InlineData("wlp0s20f3")]
    public void NameAloneDoesNotTurnEthernetIntoWifi(string name)
    {
        Directory.CreateDirectory(Path.Combine(_sysfs, name));

        Assert.Equal(LinkMediaKind.Ethernet,
            AdapterMediaClassifier.Classify(NetworkInterfaceType.Ethernet, name, name, _sysfs));
    }

    [Fact]
    public void NativeWirelessTypeStillWorksWithoutSysfs()
    {
        Assert.Equal(LinkMediaKind.WiFi,
            AdapterMediaClassifier.Classify(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Wi-Fi adapter", null));
    }

    [Fact]
    public void NonLinuxClassificationDoesNotReadLinuxMetadata()
    {
        Directory.CreateDirectory(Path.Combine(_sysfs, "eth0", "wireless"));

        Assert.Equal(LinkMediaKind.Ethernet,
            AdapterMediaClassifier.Classify(NetworkInterfaceType.Ethernet, "eth0", "Ethernet adapter", null));
    }

    public void Dispose() => Directory.Delete(_sysfs, recursive: true);
}

using System.Runtime.InteropServices;
using PortStrider.Core.Enums;
using PortStrider.Core.Models;
using PortStrider.Infrastructure.Wifi;
using Xunit;

namespace PortStrider.Infrastructure.Tests;

public sealed class WifiTests
{
    [Theory]
    [InlineData(2412, 1, "2.4 GHz")]
    [InlineData(2484, 14, "2.4 GHz")]
    [InlineData(4920, 184, "5 GHz")]
    [InlineData(5180, 36, "5 GHz")]
    [InlineData(5825, 165, "5 GHz")]
    [InlineData(5935, 2, "6 GHz")]
    [InlineData(5955, 1, "6 GHz")]
    [InlineData(7115, 233, "6 GHz")]
    [InlineData(900, 0, "Other")]
    public void MapsFrequencyWithoutConfusingBands(int frequency, int channel, string band)
    {
        Assert.Equal(channel, WifiChannels.Channel(frequency));
        Assert.Equal(band, WifiChannels.Band(frequency));
    }

    [Fact]
    public void ParsesBssBlocksHiddenSsidsAndSecurityWithoutMergingSameSsid()
    {
        var result = IwScanParser.Parse("""
            BSS aa:bb:cc:dd:ee:01(on wlan0) -- associated
                freq: 2412
                signal: -42.50 dBm
                SSID: Office: West
                RSN:
                    * Authentication suites: PSK SAE
                HT operation:
                    * secondary channel offset: above
            BSS aa:bb:cc:dd:ee:02(on wlan0)
                freq: 5180
                signal: -65.00 dBm
                SSID: Office: West
                RSN:
                    * Authentication suites: IEEE 802.1X
            BSS aa:bb:cc:dd:ee:03(on wlan0)
                freq: 5955
                signal: -81.00 dBm
                SSID:
            """);
        Assert.Equal(3, result.Count);
        Assert.Equal("Office: West", result[0].Name);
        Assert.Equal(-42.5, result[0].SignalDbm);
        Assert.Equal("WPA2 / WPA3", result[0].Security);
        Assert.Equal(40, result[0].WidthMhz);
        Assert.Equal(2422, result[0].CenterFrequencyMhz);
        Assert.Equal("WPA2", result[1].Security);
        Assert.Null(result[1].WidthMhz);
        Assert.Equal("Hidden network", result[2].Name);
        Assert.Equal("Open", result[2].Security);
    }

    [Theory]
    [InlineData(1, 42, 0, 80, 5210)]
    [InlineData(1, 42, 50, 160, 5250)]
    [InlineData(2, 50, 0, 160, 5250)]
    [InlineData(1, 42, 155, null, null)]
    public void UsesOperatingWidthAndCenterInsteadOfPrimaryChannel(int code, int seg1, int seg2, int? width, int? center)
    {
        var result = IwScanParser.Parse($"""
            BSS aa:bb:cc:dd:ee:01(on wlan0)
                freq: 5180
                signal: -52.00 dBm
                SSID: Wide network
                HT operation:
                    * secondary channel offset: above
                VHT operation:
                    * channel width: {code}
                    * center freq segment 1: {seg1}
                    * center freq segment 2: {seg2}
            """);
        var network = Assert.Single(result);
        Assert.Equal(width, network.WidthMhz);
        Assert.Equal(center, network.CenterFrequencyMhz);
        Assert.Equal(36, network.Channel);
    }

    [Fact]
    public void SkipsMalformedOrMissingMeasurementsAndDeduplicatesBssids()
    {
        var result = IwScanParser.Parse("""
            BSS aa:bb:cc:dd:ee:01(on wlan0)
                freq: 2412
                signal: -73.00 dBm
            BSS aa:bb:cc:dd:ee:01(on wlan0)
                freq: 2412
                signal: -53.00 dBm
            BSS aa:bb:cc:dd:ee:02(on wlan0)
                freq: 5180
                signal: 100/100
            BSS not-a-mac
                freq: 5180
                signal: -40 dBm
            BSS aa:bb:cc:dd:ee:03(on wlan0)
                signal: -40 dBm
            """);
        Assert.Equal(-53, Assert.Single(result).SignalDbm);
        Assert.Empty(IwScanParser.Parse(""));
    }

    [Fact]
    public void NativeBssLayoutMatchesWindowsAbi()
    {
        Assert.Equal(360, Marshal.SizeOf<WindowsWifi.BssEntry>());
        Assert.Equal(56, Marshal.OffsetOf<WindowsWifi.BssEntry>(nameof(WindowsWifi.BssEntry.Rssi)).ToInt32());
        Assert.Equal(92, Marshal.OffsetOf<WindowsWifi.BssEntry>(nameof(WindowsWifi.BssEntry.FrequencyKhz)).ToInt32());
        Assert.Equal(352, Marshal.OffsetOf<WindowsWifi.BssEntry>(nameof(WindowsWifi.BssEntry.IeOffset)).ToInt32());
    }

    [Fact]
    public async Task RejectsEthernetBeforeInvokingAnyPlatformTool()
    {
        var adapter = new AdapterInfo { Id = "eth0", Name = "eth0", Media = LinkMediaKind.Ethernet };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WifiService().ScanAsync(adapter));
        Assert.Contains("WiFi adapter", error.Message);
    }
}

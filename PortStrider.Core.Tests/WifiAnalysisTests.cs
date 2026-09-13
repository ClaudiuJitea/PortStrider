using PortStrider.Core.Models;
using Xunit;

namespace PortStrider.Core.Tests;

public sealed class WifiAnalysisTests
{
    [Fact]
    public void BondedChannelsOverlapDespiteDifferentPrimaryChannels()
    {
        var a = new WifiNetwork("A", "01", 5180, -50, "WPA2", 80, 5210);
        var b = new WifiNetwork("B", "02", 5240, -60, "WPA2", 20, 5240);
        Assert.True(WifiAnalysis.Overlaps(a, b));
        Assert.True(WifiAnalysis.Overlaps(b, a));
        Assert.False(WifiAnalysis.Overlaps(a, a));
    }

    [Fact]
    public void TouchingEdgesAndDifferentBandsDoNotOverlap()
    {
        var a = new WifiNetwork("A", "01", 2412, -50, "Open", 20);
        Assert.False(WifiAnalysis.Overlaps(a, new("B", "02", 2432, -60, "Open", 20)));
        Assert.False(WifiAnalysis.Overlaps(a, new("C", "03", 5955, -60, "Open", 20)));
    }

    [Fact]
    public void UnknownWidthUsesTwentyMhzGuide()
    {
        var a = new WifiNetwork("A", "01", 2412, -50, "Open");
        Assert.True(WifiAnalysis.Overlaps(a, new("B", "02", 2417, -60, "Open")));
        Assert.False(WifiAnalysis.Overlaps(a, new("C", "03", 2437, -60, "Open")));
    }
}

public sealed class CablePairVisualStateTests
{
    [Theory]
    [InlineData("OK", "PASS")]
    [InlineData("Normal at 12 m", "PASS")]
    [InlineData("Open Circuit at 5 m", "OPEN")]
    [InlineData("Short Circuit at 2 m", "SHORT")]
    [InlineData("Cross-pair fault", "CHECK")]
    public void ClassifiesPairStateForVisualDisplay(string status, string expected)
    {
        var pair = new CablePairResult { Pair = "Pair A", Status = status };
        Assert.Equal(expected, pair.StateLabel);
    }
}

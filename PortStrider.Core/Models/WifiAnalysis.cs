namespace PortStrider.Core.Models;

public static class WifiAnalysis
{
    // This is channel-footprint overlap, not measured airtime or interference.
    public static bool Overlaps(WifiNetwork a, WifiNetwork b) => a.Key != b.Key && a.Band == b.Band
        && Math.Abs(a.PlotCenterMhz - b.PlotCenterMhz) < (a.PlotWidthMhz + b.PlotWidthMhz) / 2d;
}

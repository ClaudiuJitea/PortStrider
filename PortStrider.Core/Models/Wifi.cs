namespace PortStrider.Core.Models;

public sealed record WifiNetwork(string Ssid, string Bssid, int FrequencyMhz, double SignalDbm,
    string Security, int? WidthMhz = null, int? CenterFrequencyMhz = null)
{
    public string Name => string.IsNullOrWhiteSpace(Ssid) ? "Hidden network" : Ssid;
    public string Band => WifiChannels.Band(FrequencyMhz);
    public int Channel => WifiChannels.Channel(FrequencyMhz);
    public string SignalLabel => $"{SignalDbm:0} dBm";
    public string WidthLabel => WidthMhz is { } width ? $"{width} MHz" : "Unknown";
    public int PlotWidthMhz => WidthMhz ?? 20;
    public int PlotCenterMhz => CenterFrequencyMhz ?? FrequencyMhz;
    public string Key => $"{Bssid}/{FrequencyMhz}";
}

public sealed record WifiScan(DateTimeOffset Timestamp, IReadOnlyList<WifiNetwork> Networks);

public static class WifiChannels
{
    public static string Band(int frequency) => frequency switch
    {
        >= 2412 and <= 2484 => "2.4 GHz",
        >= 4910 and < 5925 => "5 GHz",
        >= 5925 and <= 7125 => "6 GHz",
        _ => "Other"
    };

    public static int Channel(int frequency) => frequency switch
    {
        2484 => 14,
        >= 2412 and <= 2472 => (frequency - 2407) / 5,
        5935 => 2,
        >= 5955 and <= 7115 => (frequency - 5950) / 5,
        >= 4910 and <= 4980 => (frequency - 4000) / 5,
        >= 5000 and < 5925 => (frequency - 5000) / 5,
        _ => 0
    };
}

using System.Globalization;
using PortStrider.Infrastructure.Platform;

namespace PortStrider.Infrastructure.Platform;

public static class LinuxSysFs
{
    public static string? Read(string path)
    {
        if (!HostOs.IsLinux) return null;
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public static long? ReadInt64(string path)
    {
        var text = Read(path);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    public static string Net(string iface, string file) => $"/sys/class/net/{iface}/{file}";

    public static string Stat(string iface, string file) => $"/sys/class/net/{iface}/statistics/{file}";
}

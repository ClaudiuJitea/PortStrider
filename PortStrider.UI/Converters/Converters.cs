using Avalonia.Data.Converters;
using Avalonia.Media;
using PortStrider.Core.Enums;
using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PortStrider.UI.Converters;

public sealed class StatusToBrushConverter : IValueConverter
{
    public static readonly StatusToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is TestStatus s ? s : TestStatus.Pending;
        var hex = status switch
        {
            TestStatus.Pass => "#3DFFA8",
            TestStatus.Fail => "#FF5D73",
            TestStatus.Warning => "#FFC44D",
            TestStatus.Running => "#5B8CFF",
            TestStatus.Skipped => "#6B7789",
            _ => "#4A5568"
        };
        return SolidColorBrush.Parse(hex);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StatusToLabelConverter : IValueConverter
{
    public static readonly StatusToLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TestStatus s ? s.ToString().ToUpperInvariant() : "PENDING";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Ready = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        SolidColorBrush.Parse(value is true ? "#3DFFA8" : "#FF5D73");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolAndMultiConverter : IMultiValueConverter
{
    public static readonly BoolAndMultiConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count > 0 && values.All(value => value is true);
}

public sealed class BoolOrMultiConverter : IMultiValueConverter
{
    public static readonly BoolOrMultiConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Any(value => value is true);
}

public sealed class CountPositiveConverter : IValueConverter
{
    public static readonly CountPositiveConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var positive = value switch
        {
            int count => count > 0,
            ICollection collection => collection.Count > 0,
            _ => false
        };
        return string.Equals(parameter?.ToString(), "invert", StringComparison.OrdinalIgnoreCase)
            ? !positive
            : positive;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EnumLabelConverter : IValueConverter
{
    public static readonly EnumLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "";
        var text = value.ToString() ?? "";
        text = Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2");
        return text
            .Replace("Mac And Ip", "MAC + IP", StringComparison.Ordinal)
            .Replace("Mac Only", "MAC only", StringComparison.Ordinal)
            .Replace("Own Mac And Net Ally", "Own MAC + test traffic", StringComparison.Ordinal)
            .Replace("Own Mac", "Own MAC", StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class HexDumpConverter : IValueConverter
{
    public static readonly HexDumpConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] data || data.Length == 0) return "Select a frame to inspect the payload.";
        return Format(data, 16 * 24);
    }

    public static string Format(byte[] data, int maxBytes)
    {
        var take = Math.Min(data.Length, maxBytes);
        var lines = new List<string>();
        for (var i = 0; i < take; i += 16)
        {
            var slice = data.AsSpan(i, Math.Min(16, take - i));
            var hex = string.Join(" ", slice.ToArray().Select(b => b.ToString("X2")));
            var ascii = new string(slice.ToArray().Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
            lines.Add($"{i:X4}  {hex,-48}  {ascii}");
        }

        if (data.Length > take) lines.Add($"… {data.Length - take} more bytes");
        return string.Join('\n', lines);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PortStrider.Core.Models;

namespace PortStrider.UI.Converters;

public sealed class CablePairBrushConverter : IValueConverter
{
    public static readonly CablePairBrushConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pair = value as CablePairResult;
        return SolidColorBrush.Parse(pair?.IsHealthy is true ? "#3DFFA8"
            : pair?.IsOpen is true ? "#FFC44D" : "#FF5D73");
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CableResultBrushConverter : IValueConverter
{
    public static readonly CableResultBrushConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var result = value as CableTestResult;
        return SolidColorBrush.Parse(result?.Supported is true ? "#3DFFA8" : "#FFC44D");
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

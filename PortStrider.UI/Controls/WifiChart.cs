using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using PortStrider.Core.Models;

namespace PortStrider.UI.Controls;

public sealed class WifiChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<WifiNetwork>> NetworksProperty =
        AvaloniaProperty.Register<WifiChart, IReadOnlyList<WifiNetwork>>(nameof(Networks), Array.Empty<WifiNetwork>());
    public static readonly StyledProperty<IReadOnlyList<WifiScan>> HistoryProperty =
        AvaloniaProperty.Register<WifiChart, IReadOnlyList<WifiScan>>(nameof(History), Array.Empty<WifiScan>());
    public static readonly StyledProperty<string> BandProperty = AvaloniaProperty.Register<WifiChart, string>(nameof(Band), "2.4 GHz");
    public static readonly StyledProperty<string> SelectedKeyProperty = AvaloniaProperty.Register<WifiChart, string>(nameof(SelectedKey), "");
    public static readonly StyledProperty<bool> IsHistoryProperty = AvaloniaProperty.Register<WifiChart, bool>(nameof(IsHistory));
    public event EventHandler<WifiNetwork>? NetworkSelected;
    public IReadOnlyList<WifiNetwork> Networks { get => GetValue(NetworksProperty); set => SetValue(NetworksProperty, value); }
    public IReadOnlyList<WifiScan> History { get => GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public string Band { get => GetValue(BandProperty); set => SetValue(BandProperty, value); }
    public string SelectedKey { get => GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }
    public bool IsHistory { get => GetValue(IsHistoryProperty); set => SetValue(IsHistoryProperty, value); }
    private static readonly IBrush Muted = Brush.Parse("#8B96A8");
    private static readonly IBrush GridBrush = Brush.Parse("#23313F");
    private static readonly string[] Palette = ["#2EE6D6", "#5B8CFF", "#C28CFF", "#FFC44D", "#FF7B97", "#71D991", "#63C5FF", "#E9A5FF"];

    static WifiChart() => AffectsRender<WifiChart>(NetworksProperty, HistoryProperty, BandProperty, SelectedKeyProperty, IsHistoryProperty);

    public static Color NetworkColor(string key)
    {
        uint hash = 2166136261;
        foreach (var c in key) hash = unchecked((hash ^ c) * 16777619);
        return Color.Parse(Palette[hash % Palette.Length]);
    }

    private double _low = 2397, _high = 2499;
    private bool _autoFit = true;
    private Point? _pressed;
    private double _dragLow, _dragHigh;
    private bool _dragging;
    private Rect Plot => new(44, 23, Math.Max(1, Bounds.Width - 62), Math.Max(1, Bounds.Height - 58));
    public (double Low, double High) VisibleFrequencies => (_low, _high);
    private (double Low, double High) FullBand => Band switch
    {
        "5 GHz" => (4900, 5925), "6 GHz" => (5925, 7125), _ => (2397, 2499)
    };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BandProperty) { _autoFit = true; FitNetworks(); }
        if (change.Property == NetworksProperty && _autoFit) FitNetworks();
    }

    public void FitNetworks()
    {
        _autoFit = true;
        if (Networks.Count == 0) { SetRange(FullBand.Low, FullBand.High); return; }
        var left = Networks.Min(n => n.PlotCenterMhz - n.PlotWidthMhz / 2d);
        var right = Networks.Max(n => n.PlotCenterMhz + n.PlotWidthMhz / 2d);
        var padding = Math.Max(8, (right - left) * .12);
        SetRange(left - padding, right + padding);
    }

    public void ShowFullBand() { _autoFit = false; SetRange(FullBand.Low, FullBand.High); }
    public void FocusSelected()
    {
        var n = Networks.FirstOrDefault(n => n.Key == SelectedKey);
        if (n is null) return;
        _autoFit = false;
        SetRange(n.PlotCenterMhz - n.PlotWidthMhz, n.PlotCenterMhz + n.PlotWidthMhz);
    }
    public void ZoomIn() => Zoom(.65, .5);
    public void ZoomOut() => Zoom(1 / .65, .5);
    private void Zoom(double factor, double anchor)
    {
        _autoFit = false;
        var span = Math.Clamp((_high - _low) * factor, 20, FullBand.High - FullBand.Low);
        var frequency = _low + (_high - _low) * anchor;
        SetRange(frequency - span * anchor, frequency + span * (1 - anchor));
    }
    private void SetRange(double low, double high)
    {
        var band = FullBand;
        var span = Math.Clamp(high - low, 20, band.High - band.Low);
        _low = Math.Clamp((low + high - span) / 2, band.Low, band.High - span);
        _high = _low + span;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (IsHistory || !Plot.Contains(e.GetPosition(this))) return;
        Zoom(Math.Pow(.8, e.Delta.Y), Math.Clamp((e.GetPosition(this).X - Plot.Left) / Plot.Width, 0, 1));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsHistory || !Plot.Contains(e.GetPosition(this)) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = e.GetPosition(this);
        _dragLow = _low; _dragHigh = _high; _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (IsHistory) return;
        var point = e.GetPosition(this);
        if (_pressed is { } start)
        {
            if (Math.Abs(point.X - start.X) > 4) _dragging = true;
            if (_dragging)
            {
                _autoFit = false;
                var delta = (start.X - point.X) / Plot.Width * (_dragHigh - _dragLow);
                SetRange(_dragLow + delta, _dragHigh + delta);
            }
            e.Handled = true;
        }
        else
        {
            var network = HitNetworks(point).FirstOrDefault();
            ToolTip.SetTip(this, network is null ? "Scroll to zoom · drag to pan · click a curve to inspect"
                : $"{network.Name} · {network.Bssid}\nChannel {network.Channel} · {network.SignalLabel} · {network.WidthLabel}\nClick to inspect; repeated clicks cycle coincident curves.");
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed is null) return;
        if (!_dragging)
        {
            var candidates = HitNetworks(e.GetPosition(this));
            if (candidates.Length > 0)
            {
                var current = Array.FindIndex(candidates, n => n.Key == SelectedKey);
                NetworkSelected?.Invoke(this, candidates[(current + 1) % candidates.Length]);
            }
        }
        _pressed = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _pressed = null;
        base.OnPointerCaptureLost(e);
    }

    private WifiNetwork[] HitNetworks(Point point)
    {
        var plot = Plot;
        if (!plot.Contains(point)) return [];
        var frequency = _low + (point.X - plot.Left) / plot.Width * (_high - _low);
        var distances = Networks.Where(n => Math.Abs(frequency - n.PlotCenterMhz) <= n.PlotWidthMhz / 2d)
            .Select(n => (Network: n, Distance: Math.Abs(point.Y - (plot.Bottom - (Math.Clamp(n.SignalDbm, -100, -20) + 100) / 80 * plot.Height))))
            .OrderBy(n => n.Distance).ToArray();
        // Prefer the curve nearest the clicked signal level; allow cycling when curves coincide.
        return distances.Where(n => n.Distance <= distances[0].Distance + 5).Select(n => n.Network).ToArray();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var plot = Plot;
        if (plot.Width < 60 || plot.Height < 30) return;
        context.DrawRectangle(Brush.Parse("#0C131C"), null, new Rect(Bounds.Size), 8, 8);
        double Y(double dbm) => plot.Bottom - (Math.Clamp(dbm, -100, -20) + 100) / 80 * plot.Height;
        for (var dbm = -100; dbm <= -20; dbm += 20)
        {
            context.DrawLine(new Pen(GridBrush), new Point(plot.Left, Y(dbm)), new Point(plot.Right, Y(dbm)));
            Label(context, dbm.ToString(), new Point(8, Y(dbm) - 7), Muted);
        }
        Label(context, "dBm", new Point(7, 4), Muted, 10);
        if (IsHistory) DrawHistory(context, plot, Y);
        else DrawSpectrum(context, plot, Y);
    }

    private void DrawSpectrum(DrawingContext context, Rect plot, Func<double, double> y)
    {
        var low = _low; var high = _high;
        var frequencies = Band switch
        {
            "5 GHz" => new[] { 4920, 4940, 4960, 4980 }.Concat(Enumerable.Range(0, 28).Select(i => 5180 + i * 20))
                .Concat(Enumerable.Range(0, 9).Select(i => 5745 + i * 20)),
            "6 GHz" => new[] { 5935 }.Concat(Enumerable.Range(0, 60).Select(i => 5955 + i * 20)),
            _ => Enumerable.Range(0, 13).Select(i => 2412 + i * 5).Append(2484)
        };
        double X(double f) => plot.Left + (f - low) / (high - low) * plot.Width;
        var lastTick = double.NegativeInfinity;
        foreach (var f in frequencies.Where(f => f >= low && f <= high))
        {
            if (X(f) - lastTick < 43) continue;
            lastTick = X(f);
            context.DrawLine(new Pen(GridBrush, 1), new Point(X(f), plot.Top), new Point(X(f), plot.Bottom));
            Label(context, WifiChannels.Channel(f).ToString(), new Point(X(f) - 7, plot.Bottom + 7), Muted);
        }
        Label(context, $"CHANNEL · {low:0}–{high:0} MHz · {(_autoFit ? "AUTO FIT" : "MANUAL ZOOM")}", new Point(plot.Left, Bounds.Height - 13), Muted, 9);
        using (context.PushClip(plot))
        {
            foreach (var network in Networks.OrderBy(n => n.Key == SelectedKey).ThenBy(n => n.SignalDbm))
            {
                var color = NetworkColor(network.Key);
                var middle = X(network.PlotCenterMhz);
                var half = network.PlotWidthMhz / 2d / (high - low) * plot.Width;
                var top = y(network.SignalDbm);
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(new Point(middle - half, plot.Bottom), true);
                    path.CubicBezierTo(new Point(middle - half * .65, plot.Bottom), new Point(middle - half * .75, top), new Point(middle - half * .25, top));
                    path.LineTo(new Point(middle + half * .25, top));
                    path.CubicBezierTo(new Point(middle + half * .75, top), new Point(middle + half * .65, plot.Bottom), new Point(middle + half, plot.Bottom));
                    path.EndFigure(true);
                }
                var selected = network.Key == SelectedKey;
                context.DrawGeometry(new SolidColorBrush(Color.FromArgb(selected ? (byte)65 : (byte)24, color.R, color.G, color.B)),
                    new Pen(new SolidColorBrush(color), selected ? 2.6 : 1.2), geometry);
                if (selected)
                {
                    var name = network.Name.Length > 24 ? network.Name[..21] + "…" : network.Name;
                    Label(context, $"{name}  {network.SignalLabel}", new Point(Math.Clamp(middle - 40, plot.Left + 3, Math.Max(plot.Left + 3, plot.Right - 210)), top - 18), new SolidColorBrush(color), 11);
                }
            }
        }
        if (Networks.Count == 0) Empty(context, plot, $"No {Band} access points in the current scan");
    }

    private void DrawHistory(DrawingContext context, Rect plot, Func<double, double> y)
    {
        if (History.Count == 0 || string.IsNullOrEmpty(SelectedKey)) { Empty(context, plot, "Signal history appears after a scan"); return; }
        var first = History[0].Timestamp;
        var last = History[^1].Timestamp;
        var seconds = Math.Max(1, (last - first).TotalSeconds);
        double X(DateTimeOffset t) => plot.Left + (t - first).TotalSeconds / seconds * plot.Width;
        var brush = new SolidColorBrush(NetworkColor(SelectedKey));
        Point? previous = null;
        using (context.PushClip(plot.Inflate(3)))
        {
            foreach (var sample in History)
            {
                var network = sample.Networks.FirstOrDefault(n => n.Key == SelectedKey);
                if (network is null) { previous = null; continue; }
                var point = new Point(X(sample.Timestamp), y(network.SignalDbm));
                if (previous is { } p) context.DrawLine(new Pen(brush, 2.3), p, point);
                context.DrawEllipse(brush, null, point, 3, 3);
                previous = point;
            }
        }
        Label(context, first.ToString("HH:mm:ss"), new Point(plot.Left, plot.Bottom + 8), Muted);
        Label(context, last.ToString("HH:mm:ss"), new Point(plot.Right - 51, plot.Bottom + 8), Muted);
    }

    private static void Empty(DrawingContext context, Rect plot, string text)
    {
        var formatted = Text(text, Muted, 12);
        context.DrawText(formatted, new Point(plot.Left + Math.Max(4, (plot.Width - formatted.Width) / 2), plot.Top + plot.Height / 2 - 8));
    }
    private static FormattedText Text(string value, IBrush brush, double size) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter, Segoe UI, sans-serif"), size, brush);
    private static void Label(DrawingContext context, string value, Point position, IBrush brush, double size = 11) =>
        context.DrawText(Text(value, brush, size), position);
}

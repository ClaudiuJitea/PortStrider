using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PortStrider.Core.Models;

namespace PortStrider.UI.Controls;

public sealed class CableDiagram : Control
{
    public static readonly StyledProperty<CableTestResult?> ResultProperty =
        AvaloniaProperty.Register<CableDiagram, CableTestResult?>(nameof(Result));
    public CableTestResult? Result { get => GetValue(ResultProperty); set => SetValue(ResultProperty, value); }
    private static readonly IBrush Muted = Brush.Parse("#8B96A8");
    private static readonly IBrush Dim = Brush.Parse("#253343");
    private static readonly IBrush Surface = Brush.Parse("#0C131C");
    static CableDiagram() => AffectsRender<CableDiagram>(ResultProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Surface, new Pen(Dim), bounds, 10, 10);
        if (Result is null) { Center(context, "Run a test to map the cable and PHY", Muted, 13); return; }

        var left = new Rect(30, 46, 112, Math.Max(92, Bounds.Height - 86));
        var right = new Rect(Bounds.Width - 142, 46, 112, Math.Max(92, Bounds.Height - 86));
        DrawPort(context, left, "PORTSTRIDER", Result.Phy.Port);
        DrawPort(context, right, "LINK PARTNER", Result.Phy.PartnerMaximum);
        var start = left.Right + 14;
        var end = right.Left - 14;

        if (Result.Pairs.Count > 0)
        {
            var pairs = Result.Pairs.Take(4).ToArray();
            for (var i = 0; i < pairs.Length; i++)
            {
                var y = 63 + i * Math.Max(28, (Bounds.Height - 105) / 4);
                var pair = pairs[i];
                var color = Brush.Parse(pair.IsHealthy ? "#3DFFA8" : pair.IsOpen ? "#FFC44D" : "#FF5D73");
                context.DrawLine(new Pen(color, pair.IsHealthy ? 3 : 4), new Point(start, y), new Point(end, y));
                Dot(context, new Point(start, y), color); Dot(context, new Point(end, y), color);
                Label(context, pair.Pair.ToUpperInvariant(), new Point(start + 10, y - 22), color, 10, FontWeight.Bold);
                var state = pair.StateLabel + (string.IsNullOrWhiteSpace(pair.Distance) ? "" : $" · {pair.Distance}");
                var label = Text(state, color, 11, FontWeight.SemiBold);
                context.DrawText(label, new Point(Math.Max(start + 80, (Bounds.Width - label.Width) / 2), y - 19));
            }
        }
        else
        {
            var y = Bounds.Height / 2 + 10;
            context.DrawLine(new Pen(Brush.Parse("#FFC44D"), 4, dashStyle: DashStyle.Dash), new Point(start, y), new Point(end, y));
            Dot(context, new Point(start, y), Brush.Parse("#FFC44D")); Dot(context, new Point(end, y), Brush.Parse("#FFC44D"));
            var headline = Text("PAIR MAP UNAVAILABLE", Brush.Parse("#FFC44D"), 13, FontWeight.Bold);
            context.DrawText(headline, new Point((Bounds.Width - headline.Width) / 2, y - 42));
            var explanation = Text("The link works, but this PHY cannot report pair faults or distance.", Muted, 11);
            context.DrawText(explanation, new Point(Math.Max(start, (Bounds.Width - explanation.Width) / 2), y + 15));
        }
        Label(context, Result.Phy.LinkLabel, new Point(31, 17), Result.LinkUp ? Brush.Parse("#3DFFA8") : Brush.Parse("#FFC44D"), 11, FontWeight.Bold);
        var tdr = Text(Result.TdrLabel, Result.Supported ? Brush.Parse("#3DFFA8") : Brush.Parse("#FFC44D"), 11, FontWeight.Bold);
        context.DrawText(tdr, new Point(Bounds.Width - tdr.Width - 30, 17));
    }

    private static void DrawPort(DrawingContext context, Rect rect, string title, string subtitle)
    {
        context.DrawRectangle(Brush.Parse("#151F2A"), new Pen(Brush.Parse("#344458"), 2), rect, 9, 9);
        var socket = new Rect(rect.X + 27, rect.Y + 18, 58, 45);
        context.DrawRectangle(Brush.Parse("#080C11"), new Pen(Brush.Parse("#536477")), socket, 5, 5);
        for (var i = 0; i < 8; i++)
            context.DrawRectangle(Brush.Parse("#D5A928"), null, new Rect(socket.X + 7 + i * 6, socket.Y + 7, 3, 14));
        Label(context, title, new Point(rect.X + 10, rect.Bottom - 43), Brush.Parse("#F2F5F8"), 10, FontWeight.Bold);
        Label(context, subtitle, new Point(rect.X + 10, rect.Bottom - 25), Muted, 10);
    }
    private static void Dot(DrawingContext context, Point p, IBrush brush) => context.DrawEllipse(brush, null, p, 5, 5);
    private void Center(DrawingContext context, string value, IBrush brush, double size)
    {
        var text = Text(value, brush, size);
        context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
    }
    private static FormattedText Text(string value, IBrush brush, double size, FontWeight weight = default) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, weight.Equals(default(FontWeight)) ? FontWeight.Normal : weight), size, brush);
    private static void Label(DrawingContext context, string value, Point point, IBrush brush, double size, FontWeight weight = default) =>
        context.DrawText(Text(value, brush, size, weight), point);
}

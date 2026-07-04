using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace McServerLauncher.Controls;

/// <summary>
/// Tiny line chart (sparkline) for the CPU/RAM history in the status cards. Renders the values as
/// a polyline with a translucent fill underneath; newest sample at the right edge.
/// </summary>
public class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke), Brushes.LimeGreen);

    /// <summary>
    /// Fixed scale ceiling (e.g. 100 for a CPU %). 0 = auto-scale to the data. When set, the scale
    /// still grows if a sample exceeds it, so the line never clips.
    /// </summary>
    public static readonly StyledProperty<double> MaxValueProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(MaxValue));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, MaxValueProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double MaxValue
    {
        get => GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    /// <summary>
    /// Maps samples to pixel points inside a width×height box: X spreads the samples evenly
    /// (newest at the right edge), Y scales 0..scale to bottom..top. Kept static and pure so the
    /// scaling math can be exercised in tests without any rendering infrastructure.
    /// </summary>
    public static Point[] BuildPoints(IReadOnlyList<double> values, double width, double height, double maxValue)
    {
        if (values.Count == 0 || width <= 0 || height <= 0)
            return Array.Empty<Point>();

        var dataMax = values.Max();
        var scale = maxValue > 0 ? Math.Max(maxValue, dataMax) : dataMax;
        if (scale <= 0) scale = 1; // all-zero series: flat line at the bottom

        var points = new Point[values.Count];
        var stepX = values.Count > 1 ? width / (values.Count - 1) : 0;
        for (var i = 0; i < values.Count; i++)
        {
            var clamped = Math.Clamp(values[i], 0, scale);
            var x = values.Count > 1 ? i * stepX : width; // a single sample sits at the right edge
            var y = height - clamped / scale * height;
            points[i] = new Point(x, y);
        }
        return points;
    }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        if (values is null || values.Count < 2) return;

        var points = BuildPoints(values, Bounds.Width, Bounds.Height, MaxValue);
        if (points.Length < 2) return;

        var stroke = Stroke ?? Brushes.LimeGreen;

        // Translucent fill under the line, closed down to the bottom edge.
        var fill = new StreamGeometry();
        using (var g = fill.Open())
        {
            g.BeginFigure(new Point(points[0].X, Bounds.Height), isFilled: true);
            foreach (var p in points) g.LineTo(p);
            g.LineTo(new Point(points[^1].X, Bounds.Height));
            g.EndFigure(isClosed: true);
        }
        if (stroke is ISolidColorBrush solid)
            context.DrawGeometry(new SolidColorBrush(solid.Color, 0.18), null, fill);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], isFilled: false);
            for (var i = 1; i < points.Length; i++) g.LineTo(points[i]);
            g.EndFigure(isClosed: false);
        }
        context.DrawGeometry(null, new Pen(stroke, 1.5), line);
    }
}

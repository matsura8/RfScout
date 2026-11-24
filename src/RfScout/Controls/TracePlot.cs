using System.Windows;
using System.Windows.Media;

namespace RfScout.Controls;

/// <summary>
/// A small line plot of one analyze trace in the detail pane (zoomed spectrum, envelope or
/// instantaneous frequency). The points are raw values; the plot scales them to its own height
/// between their minimum and maximum, and breaks the line at non-finite values.
/// </summary>
public sealed class TracePlot : FrameworkElement
{
    /// <summary>Width reported when the layout offers unbounded width.</summary>
    internal const double DefaultWidth = 300d;

    /// <summary>Height reported when the layout offers unbounded height.</summary>
    internal const double DefaultHeight = 60d;

    /// <summary>The trace's values, oldest first. Null or fewer than two points draws an empty frame.</summary>
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<double>),
        typeof(TracePlot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(12, 22, 38)));
    private static readonly Pen FramePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(42, 62, 88)), 1));
    private static readonly Pen LinePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(64, 224, 140)), 1.2));

    /// <summary>The trace's values (see <see cref="PointsProperty"/>).</summary>
    public IReadOnlyList<double>? Points
    {
        get => (IReadOnlyList<double>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? DefaultWidth : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? DefaultHeight : availableSize.Height);

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 4 || height < 4)
        {
            return;
        }

        drawingContext.DrawRectangle(Background, FramePen, new Rect(0.5, 0.5, width - 1, height - 1));
        IReadOnlyList<double>? points = Points;
        if (points is null || points.Count < 2)
        {
            return;
        }

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (double v in points)
        {
            if (double.IsFinite(v))
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        }

        if (!double.IsFinite(min))
        {
            return;
        }

        double span = max - min;
        if (span < 1e-12)
        {
            // A flat trace sits on the middle line rather than dividing by zero.
            min -= 0.5;
            span = 1;
        }

        const double Pad = 3;
        double plotHeight = height - (2 * Pad);
        double step = (width - (2 * Pad)) / (points.Count - 1);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            bool open = false;
            for (int i = 0; i < points.Count; i++)
            {
                double v = points[i];
                if (!double.IsFinite(v))
                {
                    open = false;
                    continue;
                }

                var p = new Point(Pad + (i * step), Pad + plotHeight - ((v - min) / span * plotHeight));
                if (!open)
                {
                    ctx.BeginFigure(p, isFilled: false, isClosed: false);
                    open = true;
                }
                else
                {
                    ctx.LineTo(p, isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, LinePen, geometry);
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

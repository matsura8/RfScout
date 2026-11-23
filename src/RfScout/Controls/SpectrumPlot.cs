using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RfScout.Core.Display;

namespace RfScout.Controls;

/// <summary>
/// The live spectrum plot: the current sweep, the peak-hold trace and the noise floor, on a dB
/// grid with frequency labels. It paints whatever <see cref="SpectrumPlotModel"/> it is handed and
/// turns pointer input into requests (zoom, pan, focus, hover) that the view model acts on.
/// </summary>
public sealed class SpectrumPlot : FrameworkElement
{
    /// <summary>Width reported when the layout offers unbounded width.</summary>
    internal const double DefaultWidth = 480d;

    /// <summary>Height reported when the layout offers unbounded height.</summary>
    internal const double DefaultHeight = 200d;

    private const double LeftAxis = 46d;
    private const double BottomAxis = 18d;

    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(12, 22, 38)));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(42, 62, 88)), 1));
    private static readonly Pen LivePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(64, 224, 140)), 1.4));
    private static readonly Pen HoldPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(255, 196, 64)), 1));
    private static readonly Pen FloorPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(140, 150, 170)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(170, 185, 205)));
    private static readonly Typeface LabelFace = new("Segoe UI");

    private SpectrumPlotModel _model = SpectrumPlotModel.Empty;
    private Point? _dragFrom;

    /// <summary>Wheel zoom: the fraction across the plot the pointer is at, and the zoom factor.</summary>
    public event Action<double, double>? ZoomRequested;

    /// <summary>Drag pan: pixels moved and the plot width in pixels.</summary>
    public event Action<double, double>? PanRequested;

    /// <summary>Double-click: the fraction across the plot to centre on.</summary>
    public event Action<double>? FocusRequested;

    /// <summary>Pointer over the plot: fractions across and up the plot area.</summary>
    public event Action<double, double>? HoverRequested;

    /// <summary>Paint a new model.</summary>
    public void SetModel(SpectrumPlotModel model)
    {
        _model = model ?? SpectrumPlotModel.Empty;
        InvalidateVisual();
    }

    /// <inheritdoc />
    /// <remarks>Fills the space offered, with a default when a side is unbounded (an infinite
    /// DesiredSize throws in WPF layout).</remarks>
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? DefaultWidth : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? DefaultHeight : availableSize.Height);

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        double width = ActualWidth;
        double height = ActualHeight;
        drawingContext.DrawRectangle(Background, null, new Rect(0, 0, width, height));
        Rect plot = PlotArea();
        if (plot.Width < 10 || plot.Height < 10)
        {
            return;
        }

        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawGrid(drawingContext, plot, dip);
        DrawTrace(drawingContext, plot, _model.Floor, FloorPen);
        DrawTrace(drawingContext, plot, _model.PeakHold, HoldPen);
        DrawTrace(drawingContext, plot, _model.Live, LivePen);
        if (!_model.HasData)
        {
            Label(drawingContext, "No sweep yet: press Start (or Space).", plot.Left + 8, plot.Top + 6, dip);
        }
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseWheel(e);
        ZoomRequested?.Invoke(FractionAcross(e.GetPosition(this)), e.Delta > 0 ? 0.8 : 1.25);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);
        Point point = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            FocusRequested?.Invoke(FractionAcross(point));
            e.Handled = true;
            return;
        }

        _dragFrom = point;
        CaptureMouse();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragFrom = null;
        ReleaseMouseCapture();
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        Rect plot = PlotArea();
        if (_dragFrom is Point from && e.LeftButton == MouseButtonState.Pressed && plot.Width > 0)
        {
            PanRequested?.Invoke(point.X - from.X, plot.Width);
            _dragFrom = point;
        }

        if (plot.Width > 0 && plot.Height > 0)
        {
            HoverRequested?.Invoke(
                Math.Clamp((point.X - plot.Left) / plot.Width, 0, 1),
                Math.Clamp(1 - ((point.Y - plot.Top) / plot.Height), 0, 1));
        }
    }

    private static T Freeze<T>(T item)
        where T : Freezable
    {
        item.Freeze();
        return item;
    }

    private static void DrawTrace(DrawingContext dc, Rect plot, IReadOnlyList<PlotPoint> points, Pen pen)
    {
        if (points.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(ToPixel(plot, points[0]), false, false);
            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(ToPixel(plot, points[i]), true, false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void Label(DrawingContext dc, string text, double x, double y, double dip)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 11, LabelBrush, dip);
        dc.DrawText(formatted, new Point(Math.Max(0, x), Math.Max(0, y)));
    }

    private static Point ToPixel(Rect plot, PlotPoint p) =>
        new(plot.Left + (p.XFraction * plot.Width), plot.Top + ((1 - p.YFraction) * plot.Height));

    private void DrawGrid(DrawingContext dc, Rect plot, double dip)
    {
        double top = _model.TopDb;
        double bottom = SpectrumPlotModel.FixedBottomDb;
        if (top > bottom)
        {
            double step = top - bottom > 100 ? 20 : 10;
            for (double db = Math.Ceiling(bottom / step) * step; db <= top; db += step)
            {
                double y = plot.Top + ((1 - ((db - bottom) / (top - bottom))) * plot.Height);
                dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                Label(dc, db.ToString("F0", CultureInfo.InvariantCulture), 4, y - 8, dip);
            }
        }

        double start = _model.ViewStartHz;
        double stop = _model.ViewStopHz;
        if (stop > start)
        {
            const int divisions = 8;
            for (int i = 0; i <= divisions; i++)
            {
                double x = plot.Left + (plot.Width * i / divisions);
                dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                double mhz = (start + ((stop - start) * i / divisions)) / 1e6;
                string text = mhz.ToString(stop - start < 2e6 ? "F3" : "F1", CultureInfo.InvariantCulture);
                Label(dc, text, Math.Min(x - 16, plot.Right - 44), plot.Bottom + 2, dip);
            }
        }
    }

    private Rect PlotArea() =>
        new(LeftAxis, 6, Math.Max(0, ActualWidth - LeftAxis - 8), Math.Max(0, ActualHeight - BottomAxis - 10));

    private double FractionAcross(Point point)
    {
        Rect plot = PlotArea();
        return plot.Width > 0 ? Math.Clamp((point.X - plot.Left) / plot.Width, 0, 1) : 0.5;
    }
}

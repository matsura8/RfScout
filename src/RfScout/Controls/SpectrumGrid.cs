// SpectrumGrid.cs - WPF control hosting the SkiaSharp waterfall (M1).
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RfScout.Controls;

/// <summary>
/// FrameworkElement that renders the waterfall with SkiaSharp and forwards pointer
/// events as reference-level (frequency, dBFS) coordinates.
/// </summary>
public class SpectrumGrid : FrameworkElement
{
    private readonly SkiaHost _host;
    private WriteableBitmap? _bitmap;
    private long _centerHz;
    private long _spanHz = 1_000_000L;
    private double _refLevel = -100d;
    private double _dbRange = 120d;
    private IReadOnlyList<Core.Display.PeakMarker> _markers = [];

    /// <summary>Upper bound on waterfall columns; the view model clamps the grid to this.</summary>
    public const int MaxBins = 4096;

    /// <summary>Raised for a pointer event with reference-level coordinates.</summary>
    public event Action<double, double>? ProbeRequested;

    public SpectrumGrid()
    {
        _host = new SkiaHost(this);
        _host.MouseLeftButtonDown += (_, _) => RaiseProbe(_host.LastPoint);
    }

    /// <summary>Replaces the waterfall bitmap (owner transfers the instance to the control).</summary>
    public void SetBitmap(WriteableBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        WriteableBitmap? old = Interlocked.Exchange(ref _bitmap, bitmap);
        if (!ReferenceEquals(old, bitmap))
        {
            old?.Freeze();
        }

        Dispatcher.InvokeAsync(() => _host.InvalidateVisual(), DispatcherPriority.Render);
    }

    /// <summary>Replace the peak markers drawn as vertical ticks across the waterfall.</summary>
    public void SetMarkers(IReadOnlyList<Core.Display.PeakMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        _markers = markers;
    }

    /// <summary>Updates the axis mapping used to translate pixels to Hz / dBFS.</summary>
    public void SetAxes(long centerHz, long spanHz, double refLevel, double dbRange)
    {
        _centerHz = centerHz;
        _spanHz = spanHz <= 0 ? 1_000_000L : spanHz;
        _refLevel = refLevel;
        _dbRange = dbRange <= 0 ? 120d : dbRange;
    }

    /// <summary>Frequency in Hz at a horizontal pixel of the current bitmap width.</summary>
    public double FrequencyAtPixel(double x)
    {
        WriteableBitmap? bitmap = _bitmap;
        double width = bitmap is null ? ActualWidth : bitmap.Width;
        if (width <= 0)
        {
            return _centerHz;
        }

        double fraction = x / width;
        return _centerHz + ((fraction - 0.5d) * _spanHz);
    }

    /// <summary>Reference level in dBFS at a vertical pixel of the current bitmap height.</summary>
    public double LevelAtPixel(double y)
    {
        WriteableBitmap? bitmap = _bitmap;
        double height = bitmap is null ? ActualHeight : bitmap.Height;
        if (height <= 0)
        {
            return _refLevel;
        }

        return _refLevel + ((1d - (y / height)) * _dbRange);
    }

    /// <inheritdoc />
    protected override int VisualChildrenCount => 1;

    /// <inheritdoc />
    protected override Visual GetVisualChild(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(index, 0);

        return _host;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The control fills whatever space the layout offers. Inside an auto-sized row, a StackPanel
    /// or a ScrollViewer that space is unbounded, and WPF rejects an infinite DesiredSize
    /// (InvalidOperationException at startup), so an unbounded side falls back to a default.
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? DefaultWidth : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? DefaultHeight : availableSize.Height);

    /// <summary>Width reported when the layout offers unbounded width.</summary>
    internal const double DefaultWidth = 480d;

    /// <summary>Height reported when the layout offers unbounded height.</summary>
    internal const double DefaultHeight = 160d;

    private void RaiseProbe(Point point)
    {
        ProbeRequested?.Invoke(FrequencyAtPixel(point.X), LevelAtPixel(point.Y));
    }

    /// <summary>Inner visual that paints the waterfall bitmap and remembers the last pointer position.</summary>
    private sealed class SkiaHost : FrameworkElement
    {
        private readonly SpectrumGrid _owner;

        internal Point LastPoint { get; private set; }

        internal SkiaHost(SpectrumGrid owner)
        {
            _owner = owner;
            MouseMove += (_, e) => LastPoint = e.GetPosition(this);
        }

        /// <inheritdoc />
        protected override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(Brushes.Black, null, new Rect(0d, 0d, ActualWidth, ActualHeight));
            WriteableBitmap? bitmap = _owner._bitmap;
            if (bitmap is not null)
            {
                context.DrawImage(bitmap, new Rect(0d, 0d, ActualWidth, ActualHeight));
            }

            DrawMarkers(context);
            DrawAxes(context);
        }

        private void DrawMarkers(DrawingContext context)
        {
            IReadOnlyList<Core.Display.PeakMarker> markers = _owner._markers;
            if (markers.Count == 0)
            {
                return;
            }

            double width = ActualWidth;
            double height = ActualHeight;
            foreach (Core.Display.PeakMarker marker in markers)
            {
                double fraction = (marker.CenterHz - (_owner._centerHz - (_owner._spanHz / 2))) / _owner._spanHz;
                if (fraction < 0 || fraction > 1)
                {
                    continue;
                }

                double x = fraction * width;
                var pen = new Pen(marker.Selected ? Brushes.Yellow : Brushes.Lime, marker.Selected ? 2d : 1d);
                context.DrawLine(pen, new Point(x, 0d), new Point(x, Math.Min(12d, height)));
            }
        }

        private void DrawAxes(DrawingContext context)
        {
            Typeface typeface = new("Segoe UI");
            FormattedText title = new(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F3} MHz span {1:F3} MHz",
                    _owner._centerHz / 1e6,
                    _owner._spanHz / 1e6),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                12d,
                Brushes.LimeGreen,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            context.DrawText(title, new Point(6d, 2d));

            FormattedText bottom = new(
                string.Format(CultureInfo.InvariantCulture, "{0:F0} dBFS", _owner._refLevel),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                12d,
                Brushes.LimeGreen,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            context.DrawText(bottom, new Point(6d, Math.Max(0d, ActualHeight - bottom.Height - 2d)));
        }
    }
}

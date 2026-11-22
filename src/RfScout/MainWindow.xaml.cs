using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RfScout.Controls;
using RfScout.Core;
using RfScout.Core.Display;

namespace RfScout;

/// <summary>
/// Main window. Logic lives in the view model and Core, not here: this code-behind only owns the
/// render loop (a dispatcher timer at the view model's refresh rate) and hands the waterfall
/// control the bitmap the Core model produced.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _renderTimer;
    private WriteableBitmap? _bitmap;
    private byte[] _rowScratch = [];
    private long _lastSequence;
    private long _lastWaterfallWrite;
    private bool _waterfallPainted;
    private int _markerCount;

    public MainWindow()
    {
        InitializeComponent();

        // The view model is created in XAML; the render loop is owned here because only the view
        // knows about the dispatcher.
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ViewModels.MainViewModel.RefreshIntervalMs) };
        _renderTimer.Tick += (_, _) => RenderFrame();
        _renderTimer.Start();

        Loaded += (_, _) => RenderFrame();
        Closed += (_, _) => _renderTimer.Stop();
    }

    /// <summary>Arrow keys move between peaks; Space and Enter keep their sweep/analyze meaning.</summary>
    private void OnPeakTableKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel model)
        {
            return;
        }

        switch (e.Key)
        {
            case System.Windows.Input.Key.Up:
                model.MovePeakSelection(-1);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Down:
                model.MovePeakSelection(+1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>A header click: route the sort through the view model so the filter stays applied.</summary>
    private void OnPeakTableSorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel model && e.Column?.Header is string header)
        {
            string column = header switch
            {
                "ID" => "Id",
                "Frequency" => "Frequency",
                "Power" => "Power",
                "SNR" => "Snr",
                "BW" => "Bandwidth",
                "Duty" => "Duty",
                "Type" => "Type",
                "Band" => "Band",
                "Best guess" => "BestGuess",
                "Last seen" => "LastSeen",
                _ => "Power",
            };

            model.TogglePeakSort(column);
            e.Handled = true;
        }
    }

    /// <summary>A click on the waterfall: report the frequency and level under the pointer.</summary>
    private void OnWaterfallProbe(double frequencyHz, double levelDb)
    {
        if (DataContext is ViewModels.MainViewModel model)
        {
            model.WaterfallProbe(frequencyHz, levelDb);
        }
    }

    /// <summary>
    /// Pull the newest frame from the view model and repaint. Runs on the UI thread, so the only
    /// work here is copying the newest waterfall row into the bitmap and updating the axes.
    /// </summary>
    private void RenderFrame()
    {
        if (DataContext is not ViewModels.MainViewModel model)
        {
            return;
        }

        long before = Stopwatch.GetTimestamp();

        if (!model.TryRefreshDisplay(_lastSequence, out _, out _, out _))
        {
            return;
        }

        _lastSequence = model.Frames.Sequence;

        SpectrumViewport view = model.View;
        DbRange range = model.ColourRange;
        Waterfall.SetAxes((long)Math.Round(view.CenterHz), (long)Math.Round(view.SpanHz), range.MinDb, range.SpanDb);

        WaterfallModel strip = model.Waterfall;
        if (strip.Columns > 0 && strip.Writes != _lastWaterfallWrite)
        {
            _lastWaterfallWrite = strip.Writes;
            EnsureBitmap(strip.Columns, strip.Rows);
            if (CopyNewestRow(strip) is WriteableBitmap bitmap)
            {
                Waterfall.SetBitmap(bitmap);
                _waterfallPainted = true;
            }
        }

        IReadOnlyList<PeakMarker> markers = model.PeekMarkers();
        if (markers.Count != _markerCount || markers.Count > 0)
        {
            Waterfall.SetMarkers(markers);
            _markerCount = markers.Count;
        }

        if (_waterfallPainted || markers.Count > 0)
        {
            Waterfall.InvalidateVisual();
        }

        // Feed the paint back so the responsiveness figure in the status bar reflects real UI work.
        model.RecordUiPaint(ElapsedMs(before));
    }

    /// <summary>Size the shared bitmap for the current grid (recreated when the span changes).</summary>
    private void EnsureBitmap(int columns, int rows)
    {
        if (_bitmap is not null && _bitmap.PixelWidth == columns && _bitmap.PixelHeight == rows)
        {
            return;
        }

        _bitmap = new WriteableBitmap(columns, rows, 96, 96, PixelFormats.Bgra32, null);
        _rowScratch = new byte[columns * 4];
    }

    /// <summary>Blit the newest row into the bitmap and return it for the control, or null on failure.</summary>
    private WriteableBitmap? CopyNewestRow(WaterfallModel strip)
    {
        WriteableBitmap? bitmap = _bitmap;
        if (bitmap is null || _rowScratch.Length < bitmap.PixelWidth * 4)
        {
            return null;
        }

        if (!strip.TryCopyRow(strip.NewestRow, _rowScratch))
        {
            return null;
        }

        bitmap.Lock();
        try
        {
            bitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, 1), _rowScratch, bitmap.PixelWidth * 4, 0);
        }
        finally
        {
            bitmap.Unlock();
        }

        return bitmap;
    }

    private static double ElapsedMs(long startTicks) =>
        Stopwatch.GetElapsedTime(startTicks, Stopwatch.GetTimestamp()).TotalMilliseconds;

    /// <summary>Format a millisecond gap for the status bar (kept here so tests can check it).</summary>
    internal static string FormatGap(double ms) =>
        string.Format(CultureInfo.InvariantCulture, "{0:F0} ms", Math.Max(0d, ms));
}


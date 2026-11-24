using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RfScout.Controls;
using RfScout.Core;
using RfScout.Core.Display;
using RfScout.Core.Locate;

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
    private DispatcherTimer? _settingsTimer;
    private SpectrumFrame? _plotFrame;
    private SpectrumFrame? _plotHold;
    private IReadOnlyList<double> _plotFloor = [];

    public MainWindow()
    {
        InitializeComponent();

        SourceBox.ItemsSource = Enum.GetValues<ViewModels.SweepSourceKind>();
        if (DataContext is ViewModels.MainViewModel plotModel)
        {
            // The real radio: without a factory the view model only ever had the synthetic source.
            plotModel.RadioFactory = kind => kind == ViewModels.SweepSourceKind.HackRf ? OpenHackRf(plotModel) : null;

            // Pointer input on the spectrum becomes view-model requests; any change to the view
            // (zoom, pan, range, peak hold) repaints the plot even when no new frame arrives.
            Spectrum.ZoomRequested += plotModel.Zoom;
            Spectrum.PanRequested += plotModel.Pan;
            Spectrum.FocusRequested += plotModel.FocusAt;
            Spectrum.HoverRequested += plotModel.ReportSpectrumHover;
            plotModel.NeedsRedraw += (_, _) => Dispatcher.BeginInvoke(new Action(RepaintPlot));

            // Start/Stop, Analyze and Locate enable themselves from view-model state that can
            // change off the UI thread, so ask WPF to re-check the commands when it does.
            plotModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ViewModels.MainViewModel.IsSweeping) or nameof(ViewModels.MainViewModel.IsLocating)
                    or nameof(ViewModels.MainViewModel.IsAnalyzing) or nameof(ViewModels.MainViewModel.SelectedPeak))
                {
                    Dispatcher.BeginInvoke(new Action(System.Windows.Input.CommandManager.InvalidateRequerySuggested));
                }
            };

            // RF amp and antenna power must be confirmed by the operator (hard rule 2).
            plotModel.NeedsConfirmation += message => MessageBox.Show(
                this, message, "RfScout", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        // The view model is created in XAML; the render loop is owned here because only the view
        // knows about the dispatcher.
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ViewModels.MainViewModel.RefreshIntervalMs) };
        _renderTimer.Tick += (_, _) => RenderFrame();
        _renderTimer.Start();

        Loaded += (_, _) => RenderFrame();
        Closed += (_, _) => _renderTimer.Stop();

        // M6: load the per-user settings once the view model exists, watch the declared
        // HackRF for unplug/replug, and autosave every few seconds when something changed.
        if (DataContext is ViewModels.MainViewModel settingsModel)
        {
            settingsModel.LoadSettings();
            settingsModel.StartUnplugWatch();
            _settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _settingsTimer.Tick += (_, _) => settingsModel.SaveSettingsIfDirty();
            _settingsTimer.Start();
            Closed += (_, _) =>
            {
                _settingsTimer.Stop();
                settingsModel.StopUnplugWatch();
                settingsModel.SaveSettingsIfDirty();
            };
        }

        // M5 locate: show the strength panel while locate owns the radio, and redraw its
        // graph whenever a window arrives. The view model raises the event from the source
        // thread, so the work goes through the dispatcher.
        if (DataContext is ViewModels.MainViewModel locateModel)
        {
            locateModel.LocateUpdated += (_, _) => Dispatcher.BeginInvoke(new Action(PaintLocate));
            locateModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModels.MainViewModel.IsLocating))
                {
                    Dispatcher.BeginInvoke(new Action(() => LocatePanel.Visibility = locateModel.IsLocating
                        ? Visibility.Visible
                        : Visibility.Collapsed));
                }
            };

            // The panel is collapsed until locate starts, so the graph canvas never gets a
            // real size from layout; paint once the window is up so the first windows land.
            ContentRendered += (_, _) =>
            {
                if (locateModel.IsLocating)
                {
                    PaintLocate();
                }
            };
        }
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
            case System.Windows.Input.Key.Enter:
                if (model.AnalyzePeakCommand.CanExecute(null))
                {
                    model.AnalyzePeakCommand.Execute(null);
                }

                e.Handled = true;
                break;
            case System.Windows.Input.Key.L:
                if (!model.IsLocating)
                {
                    // A muted emitter is silenced on purpose, so L refuses until the
                    // operator unmutes it rather than hunting a deliberately quiet signal.
                    if (model.SelectedPeak?.IsMuted == true)
                    {
                        model.ReportMutedLocateAttempt();
                    }
                    else
                    {
                        _ = model.LocateSelectedPeakAsync();
                    }

                    e.Handled = true;
                }

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

    /// <summary>Escape leaves locate from anywhere in the window; Space starts or stops the sweep
    /// unless the keyboard is in a text box, button or list that uses Space itself.</summary>
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (DataContext is not ViewModels.MainViewModel model)
        {
            return;
        }

        if (e.Key == System.Windows.Input.Key.Escape && model.IsLocating)
        {
            model.ExitLocate();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Space && !e.Handled
            && System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.ComboBox))
        {
            if (model.ToggleSweepCommand.CanExecute(null))
            {
                model.ToggleSweepCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Redraw the locate 60 s graph from the view model snapshot. Kept tiny: one polyline
    /// across the canvas, dB on the vertical axis (the meter range), seconds on the
    /// horizontal (the rolling window). The meter itself is data-bound.
    /// </summary>
    private void PaintLocate()
    {
        if (DataContext is not ViewModels.MainViewModel model)
        {
            return;
        }

        Canvas canvas = LocateGraphCanvas;
        canvas.Children.Clear();

        // The canvas sits inside a fixed-height Border, so it has a real size even before
        // the window has ever measured it; fall back to the host's layout size.
        double width = canvas.ActualWidth > 0 ? canvas.ActualWidth : LocateGraphHost.ActualWidth;
        double height = canvas.ActualHeight > 0 ? canvas.ActualHeight : LocateGraphHost.ActualHeight;
        if (width < 10 || height < 10)
        {
            width = 240;
            height = 88;
        }

        IReadOnlyList<(int Second, double Db)> graph = model.LocateGraph();
        if (graph.Count < 2)
        {
            return;
        }

        const double minDb = ViewModels.MainViewModel.MeterFloorDb;
        const double maxDb = ViewModels.MainViewModel.MeterTopDb;
        int newest = graph[^1].Second;
        int oldest = Math.Max(0, newest - (LocateController.DefaultGraphSeconds - 1));

        var points = new System.Windows.Media.PointCollection();
        foreach ((int second, double db) in graph)
        {
            if (second < oldest)
            {
                continue;
            }

            double x = (second - oldest) / (double)(LocateController.DefaultGraphSeconds - 1) * width;
            double y = height - ((db - minDb) / (maxDb - minDb) * height);
            points.Add(new System.Windows.Point(x, Math.Clamp(y, 0, height)));
        }

        if (points.Count < 2)
        {
            return;
        }

        var line = new System.Windows.Shapes.Polyline
        {
            Points = points,
            Stroke = System.Windows.Media.Brushes.DeepSkyBlue,
            StrokeThickness = 2,
        };
        canvas.Children.Add(line);
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

        if (!model.TryRefreshDisplay(_lastSequence, out SpectrumFrame? latest, out SpectrumFrame? hold, out IReadOnlyList<double> floor))
        {
            return;
        }

        _lastSequence = model.Frames.Sequence;
        _plotFrame = latest;
        _plotHold = hold;
        _plotFloor = floor;
        RepaintPlot();

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

    /// <summary>Open the first HackRF on the USB bus, or record why it failed and return null.</summary>
    private static Services.HackRf.HackRfDevice? OpenHackRf(ViewModels.MainViewModel model)
    {
        try
        {
            var device = new Services.HackRf.HackRfDevice();
            model.LastOpenError = null;
            return device;
        }
        catch (Exception ex) when (ex is Services.HackRf.HackRfException or DllNotFoundException
            or BadImageFormatException or InvalidOperationException or EntryPointNotFoundException)
        {
            model.LastOpenError = ex.Message;
            return null;
        }
    }

    /// <summary>Rebuild the spectrum plot from the last frame and the current view and range.</summary>
    private void RepaintPlot()
    {
        if (DataContext is ViewModels.MainViewModel model)
        {
            Spectrum.SetModel(SpectrumPlotModel.Build(_plotFrame, _plotHold, _plotFloor, model.View, model.ColourRange));
        }
    }

    /// <summary>Size the shared bitmap for the current grid (recreated when the span changes).</summary>
    private void EnsureBitmap(int columns, int rows)
    {
        if (_bitmap is not null && _bitmap.PixelWidth == columns && _bitmap.PixelHeight == rows)
        {
            return;
        }

        _bitmap = new WriteableBitmap(columns, rows, 96, 96, PixelFormats.Bgra32, null);
        _rowScratch = new byte[columns * 4 * rows];
    }

    /// <summary>
    /// Repaint the whole strip into the bitmap, newest sweep at the top and older ones below, so
    /// the waterfall scrolls down. Returns the bitmap for the control, or null on failure.
    /// </summary>
    private WriteableBitmap? CopyNewestRow(WaterfallModel strip)
    {
        WriteableBitmap? bitmap = _bitmap;
        if (bitmap is null)
        {
            return null;
        }

        int rows = bitmap.PixelHeight;
        int stride = bitmap.PixelWidth * 4;
        if (_rowScratch.Length < stride * rows || strip.Rows != rows)
        {
            return null;
        }

        int newest = strip.NewestRow;
        for (int r = 0; r < rows; r++)
        {
            int source = (newest - r + rows) % rows;
            if (!strip.TryCopyRow(source, _rowScratch.AsSpan(r * stride, stride)))
            {
                return null;
            }
        }

        bitmap.Lock();
        try
        {
            bitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, rows), _rowScratch, stride, 0);
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


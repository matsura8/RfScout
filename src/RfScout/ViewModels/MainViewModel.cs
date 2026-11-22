using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using RfScout.Core;
using RfScout.Core.Display;
using RfScout.Core.Fakes;
using RfScout.Core.Streaming;
using RfScout.Core.Spectrum;
using System.Windows.Threading;
using RfScout.Services.HackRf;

namespace RfScout.ViewModels;

/// <summary>Which radio the app is talking to: the real HackRF, or the built-in demo signal.</summary>
public enum SweepSourceKind
{
    /// <summary>The HackRF One on the USB bus.</summary>
    HackRf = 0,

    /// <summary><see cref="SyntheticRadioSource"/>, so the UI can be driven with no radio attached.</summary>
    Synthetic = 1,
}

/// <summary>
/// State for the main window: the sweep settings, the running source, and everything the plots and
/// the status bar show.
///
/// The rules this class follows are the ones the brief sets for the whole app. All the arithmetic
/// lives in Core (viewport maths, colour mapping, noise-floor estimation, peak hold), so a test can
/// drive the sweep with a synthetic source and check the numbers without a window. The views are
/// thin: they paint what they are handed and call these methods when the operator touches them.
/// Nothing here touches libhackrf directly - the radio arrives through
/// <see cref="IRadioSource"/>, which is how the same view model runs against
/// <see cref="SyntheticRadioSource"/> when no HackRF is plugged in.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>How often the plots repaint, in milliseconds. The brief asks for about 30 fps.</summary>
    public const int RefreshIntervalMs = 33;

    /// <summary>Default waterfall colour range in dBFS before the first sweep or an Auto press.</summary>
    public const double DefaultDbMin = -110;
    public const double DefaultDbMax = -20;

    private readonly Lock _gate = new();
    private readonly SpectrumFrameBuffer _frames = new();
    /// <summary>Floor line for the trace: the display estimator (min-hold, updates every refresh).
    /// The peak detector's statistical floor is the Spectrum one, fed only by completed sweeps.</summary>
    private readonly RfScout.Core.Display.NoiseFloorEstimator _floor = new();
    private readonly WaterfallModel _waterfall = new();
    private readonly Core.Spectrum.NoiseFloorEstimator _peakFloor = new();
    private PeakDetector _peakDetector = new();
    private readonly PeakTracker _peakTracker = new();
    private BandPlan? _bandPlan = TryLoadBandPlan();
    private readonly List<PeakRow> _peakRows = [];
    private PeakRow? _selectedPeak;
    private double _peakThresholdDb = PeakDetector.DefaultThresholdDb;
    private string _peakFilterText = string.Empty;
    private List<PeakRow> _visiblePeakRows = [];
    private string _peakSortColumn = "Power";
    private bool _peakSortDescending = true;
    private int _floorDirtyBins;
    private long _lastPeakFloorTicks;
    private long _lastPeakFloorSequence;
    private readonly DbRange _dbRange = new(DefaultDbMin, DefaultDbMax);

    private IRadioSource? _radio;
    private SpectrumViewport _viewport = new(SweepPreset.MinTunableHz, SweepPreset.MaxTunableHz);
    private SweepSourceKind _sourceKind = SweepSourceKind.HackRf;
    private bool _isSweeping;
    private string _statusText = "Idle. Press Start (or Space) to sweep.";
    private string _hoverText = "No sweep yet";
    private string _waterfallHoverText = "No sweep yet";
    private string _spectrumHelp = "Waiting for the first sweep.";
    private string _waterfallHelp = "Waiting for the first sweep.";
    private string _deviceText = "No radio opened";
    private string _statsText = "sweeps/s 0.00 | dropped 0";
    private double? _worstUiGapMs;
    private string _startMHzText = "1.000";
    private string _stopMHzText = "6000.000";
    private int _binWidthHz = 625_000;
    private int _lnaDb = RadioGains.DefaultLnaDb;
    private int _vgaDb = RadioGains.DefaultVgaDb;
    private bool _rfAmp;
    private bool _antennaPower;
    private bool _autoScale = true;
    private string _dbMinText = DefaultDbMin.ToString("F0", CultureInfo.InvariantCulture);
    private string _dbMaxText = DefaultDbMax.ToString("F0", CultureInfo.InvariantCulture);
    private SweepPreset? _selectedPreset = SweepPresetLibrary.All[0];
    private SweepPreset _customPreset;
    private bool _disposed;

    /// <summary>Created in the field initialiser so the setter can be a plain property.</summary>
    public MainViewModel()
    {
        _customPreset = new SweepPreset("Custom", 1_000_000, 6_000_000_000, 625_000);

        // Wired here rather than in XAML so a headless test can press the same commands the
        // toolbar buttons and the Space shortcut trigger.
        StartCommand = new RelayCommand(Start, _ => !IsSweeping);
        StopCommand = new RelayCommand(Stop, _ => IsSweeping);
        ToggleSweepCommand = new RelayCommand(ToggleSweep);
        ApplyCommand = new RelayCommand(ApplyNow);
        AutoScaleCommand = new RelayCommand(AutoRangeNow);
        ClearPeakHoldCommand = new RelayCommand(ClearPeakHold);
        ResetViewCommand = new RelayCommand(ResetView);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the view should repaint. The dispatcher timer does this on a tick, and
    /// a synthetic sweep raises it straight away so a test sees a frame without waiting.</summary>
    public event EventHandler? NeedsRedraw;

    /// <summary>Raised with true when the operator must confirm before antenna power or the amp goes on.</summary>
    public event Func<string, bool>? NeedsConfirmation;

    /// <summary>
    /// Factory for the radio, so the app can hand over the real HackRF wrapper and a test can hand
    /// over a fake. Returns null when no source of the requested kind is available.
    /// </summary>
    public Func<SweepSourceKind, IRadioSource?>? RadioFactory { get; set; }

    public string Title { get; } =
        $"RfScout {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]}";

    public static IReadOnlyList<SweepPreset> Presets => SweepPresetLibrary.All;

    public static IReadOnlyList<int> LnaSteps => RadioGains.LnaSteps;

    public static IReadOnlyList<int> VgaSteps => RadioGains.VgaSteps;

    public static IReadOnlyList<int> BinWidthsHz => SweepPreset.CommonBinWidthsHz;

    public ICommand StartCommand { get; private set; } = null!;
    public ICommand StopCommand { get; private set; } = null!;
    public ICommand ToggleSweepCommand { get; private set; } = null!;
    public ICommand ApplyCommand { get; private set; } = null!;
    public ICommand AutoScaleCommand { get; private set; } = null!;
    public ICommand ClearPeakHoldCommand { get; private set; } = null!;
    public ICommand ResetViewCommand { get; private set; } = null!;

    /// <summary>True while a sweep is running.</summary>
    public bool IsSweeping
    {
        get
        {
            lock (_gate)
            {
                return _isSweeping;
            }
        }
    }

    public SweepSourceKind SourceKind
    {
        get
        {
            lock (_gate)
            {
                return _sourceKind;
            }
        }

        set
        {
            bool changed;
            lock (_gate)
            {
                changed = _sourceKind != value;
                _sourceKind = value;
            }

            if (changed)
            {
                OnPropertyChanged();
                Stop();
            }
        }
    }

    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                return _statusText;
            }
        }

        private set
        {
            if (!SetField(ref _statusText, value))
            {
                return;
            }
        }
    }

    public string HoverText
    {
        get
        {
            lock (_gate)
            {
                return _hoverText;
            }
        }

        private set
        {
            if (!SetField(ref _hoverText, value))
            {
                return;
            }
        }
    }

    public string WaterfallHoverText
    {
        get
        {
            lock (_gate)
            {
                return _waterfallHoverText;
            }
        }

        private set
        {
            if (!SetField(ref _waterfallHoverText, value))
            {
                return;
            }
        }
    }

    /// <summary>Automation help for the spectrum: the same information the trace carries, in words.</summary>
    public string SpectrumHelp
    {
        get
        {
            lock (_gate)
            {
                return _spectrumHelp;
            }
        }

        private set
        {
            if (!SetField(ref _spectrumHelp, value))
            {
                return;
            }
        }
    }

    /// <summary>Automation help for the waterfall.</summary>
    public string WaterfallHelp
    {
        get
        {
            lock (_gate)
            {
                return _waterfallHelp;
            }
        }

        private set
        {
            if (!SetField(ref _waterfallHelp, value))
            {
                return;
            }
        }
    }

    public string DeviceText
    {
        get
        {
            lock (_gate)
            {
                return _deviceText;
            }
        }

        private set
        {
            if (!SetField(ref _deviceText, value))
            {
                return;
            }
        }
    }

    /// <summary>Status bar line: sweeps per second and the dropped-block counter.</summary>
    public string StatsText
    {
        get
        {
            lock (_gate)
            {
                return _statsText;
            }
        }

        private set
        {
            if (!SetField(ref _statsText, value))
            {
                return;
            }
        }
    }

    /// <summary>Worst gap between UI paints while sweeping, or null before the first measurement.</summary>
    public double? WorstUiGapMs
    {
        get
        {
            lock (_gate)
            {
                return _worstUiGapMs;
            }
        }

        private set
        {
            if (!SetField(ref _worstUiGapMs, value))
            {
                return;
            }
        }
    }

    public string StartMHzText
    {
        get
        {
            lock (_gate)
            {
                return _startMHzText;
            }
        }

        set
        {
            if (!SetField(ref _startMHzText, value))
            {
                return;
            }

            SelectedPreset = null;
        }
    }

    public string StopMHzText
    {
        get
        {
            lock (_gate)
            {
                return _stopMHzText;
            }
        }

        set
        {
            if (!SetField(ref _stopMHzText, value))
            {
                return;
            }

            SelectedPreset = null;
        }
    }

    public int BinWidthHz
    {
        get
        {
            lock (_gate)
            {
                return _binWidthHz;
            }
        }

        set
        {
            if (!SetField(ref _binWidthHz, value))
            {
                return;
            }

            SelectedPreset = null;
        }
    }

    public int LnaDb
    {
        get
        {
            lock (_gate)
            {
                return _lnaDb;
            }
        }

        set
        {
            int clamped = RadioGains.ClampLna(value);
            if (!SetField(ref _lnaDb, clamped))
            {
                return;
            }

            PushGains();
        }
    }

    public int VgaDb
    {
        get
        {
            lock (_gate)
            {
                return _vgaDb;
            }
        }

        set
        {
            int clamped = RadioGains.ClampVga(value);
            if (!SetField(ref _vgaDb, clamped))
            {
                return;
            }

            PushGains();
        }
    }

    public bool RfAmp
    {
        get
        {
            lock (_gate)
            {
                return _rfAmp;
            }
        }

        set
        {
            if (!SetField(ref _rfAmp, value))
            {
                return;
            }

            if (value && !Confirm("Turn on the 14 dB RF amplifier? Strong signals near the antenna can overload or damage the front end."))
            {
                SetField(ref _rfAmp, false);
                OnPropertyChanged();
                return;
            }

            ApplySwitches();
        }
    }

    public bool AntennaPower
    {
        get
        {
            lock (_gate)
            {
                return _antennaPower;
            }
        }

        set
        {
            if (!SetField(ref _antennaPower, value))
            {
                return;
            }

            if (value && !Confirm("Turn on antenna (bias-tee) power? This feeds up to 5 V up the coax to an active antenna or amplifier."))
            {
                SetField(ref _antennaPower, false);
                OnPropertyChanged();
                return;
            }

            ApplySwitches();
        }
    }

    public bool AutoScale
    {
        get
        {
            lock (_gate)
            {
                return _autoScale;
            }
        }

        set
        {
            if (!SetField(ref _autoScale, value))
            {
                return;
            }

            if (value)
            {
                AutoRangeNow();
            }
        }
    }

    public string DbMinText
    {
        get
        {
            lock (_gate)
            {
                return _dbMinText;
            }
        }

        set
        {
            if (!SetField(ref _dbMinText, value))
            {
                return;
            }

            ApplyDbRangeFromText();
        }
    }

    public string DbMaxText
    {
        get
        {
            lock (_gate)
            {
                return _dbMaxText;
            }
        }

        set
        {
            if (!SetField(ref _dbMaxText, value))
            {
                return;
            }

            ApplyDbRangeFromText();
        }
    }

    public SweepPreset? SelectedPreset
    {
        get
        {
            lock (_gate)
            {
                return _selectedPreset;
            }
        }

        set
        {
            SweepPreset? picked;
            lock (_gate)
            {
                if (_selectedPreset == value)
                {
                    return;
                }

                _selectedPreset = value;
                picked = value;
            }

            OnPropertyChanged();
            if (picked is not null)
            {
                ApplyPreset(picked);
            }
        }
    }

    /// <summary>Colour range in force for the waterfall, read by the plot on every paint.</summary>
    public DbRange ColourRange
    {
        get
        {
            lock (_gate)
            {
                return _dbRange;
            }
        }
    }

    /// <summary>The visible slice, read by both plots on every paint.</summary>
    public SpectrumViewport View
    {
        get
        {
            lock (_gate)
            {
                return _viewport;
            }
        }
    }

    /// <summary>Waterfall rows and colours, read by the waterfall on every paint.</summary>
    public WaterfallModel Waterfall
    {
        get
        {
            lock (_gate)
            {
                return _waterfall;
            }
        }
    }

    /// <summary>Frames held for the display, with the peak-hold merge.</summary>
    public SpectrumFrameBuffer Frames => _frames;

    /// <summary>Rows the peak table binds to: filtered and sorted view of every tracked peak.</summary>
    public IReadOnlyList<PeakRow> PeakRows => _visiblePeakRows;

    /// <summary>The selected peak, shared between the table row and the plot markers.</summary>
    public PeakRow? SelectedPeak
    {
        get
        {
            lock (_gate)
            {
                return _selectedPeak;
            }
        }

        set
        {
            PeakRow? previous;
            lock (_gate)
            {
                if (ReferenceEquals(_selectedPeak, value))
                {
                    return;
                }

                previous = _selectedPeak;
                _selectedPeak = value;
            }

            if (previous is not null)
            {
                previous.IsSelected = false;
            }

            if (value is not null)
            {
                value.IsSelected = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPeakText));
            RaiseRedraw();
        }
    }

    /// <summary>Detail-pane text for the selected peak, empty when none is selected.</summary>
    public string SelectedPeakText
    {
        get
        {
            lock (_gate)
            {
                return _selectedPeak is null
                    ? "No peak selected. Pick a row or click a marker."
                    : _selectedPeak.AutomationName + ". Best guess: " + _selectedPeak.BestGuess + ".";
            }
        }
    }

    /// <summary>Detection threshold in dB above the noise floor; the toolbar slider binds here.</summary>
    public double PeakThresholdDb
    {
        get
        {
            lock (_gate)
            {
                return _peakThresholdDb;
            }
        }

        set
        {
            double clamped = Math.Clamp(value, 0, 60);
            bool changed;
            lock (_gate)
            {
                changed = clamped != _peakThresholdDb;
                if (changed)
                {
                    _peakThresholdDb = clamped;
                    _peakDetector = new PeakDetector(clamped);
                }
            }

            if (changed)
            {
                OnPropertyChanged();
                RebuildPeakRows();
            }
        }
    }

    /// <summary>Free-text filter for the peak table (matches frequency, band, type or guess).</summary>
    public string PeakFilterText
    {
        get
        {
            lock (_gate)
            {
                return _peakFilterText;
            }
        }

        set
        {
            bool changed;
            lock (_gate)
            {
                changed = _peakFilterText != value;
                if (changed)
                {
                    _peakFilterText = value;
                }
            }

            if (changed)
            {
                OnPropertyChanged();
                RebuildPeakRows();
            }
        }
    }

    /// <summary>Column the table sorts by ("Id", "Frequency", "Power", "Snr", "Bandwidth", "Duty", "Band", "LastSeen").</summary>
    public string PeakSortColumn
    {
        get
        {
            lock (_gate)
            {
                return _peakSortColumn;
            }
        }

        set
        {
            bool changed;
            lock (_gate)
            {
                changed = _peakSortColumn != value;
                if (changed)
                {
                    _peakSortColumn = value;
                }
            }

            if (changed)
            {
                OnPropertyChanged();
                RebuildPeakRows();
            }
        }
    }

    /// <summary>True when the sort runs high-to-low; the header toggles it.</summary>
    public bool PeakSortDescending
    {
        get
        {
            lock (_gate)
            {
                return _peakSortDescending;
            }
        }

        set
        {
            bool changed;
            lock (_gate)
            {
                changed = _peakSortDescending != value;
                if (changed)
                {
                    _peakSortDescending = value;
                }
            }

            if (changed)
            {
                OnPropertyChanged();
                RebuildPeakRows();
            }
        }
    }

    /// <summary>Sort a column and flip the direction when it is already the active one.</summary>
    public void TogglePeakSort(string column)
    {
        lock (_gate)
        {
            if (string.Equals(_peakSortColumn, column, StringComparison.OrdinalIgnoreCase))
            {
                _peakSortDescending = !_peakSortDescending;
            }
            else
            {
                _peakSortColumn = column;
                _peakSortDescending = true;
            }
        }

        OnPropertyChanged(nameof(PeakSortColumn));
        OnPropertyChanged(nameof(PeakSortDescending));
        RebuildPeakRows();
    }

    /// <summary>Arrow keys in the peak table: move the selection by one row of the visible list.</summary>
    public void MovePeakSelection(int delta)
    {
        List<PeakRow> rows = _visiblePeakRows;
        if (rows.Count == 0)
        {
            return;
        }

        int index = -1;
        lock (_gate)
        {
            if (_selectedPeak is not null)
            {
                index = rows.IndexOf(_selectedPeak);
            }
        }

        int next = index < 0
            ? (delta < 0 ? rows.Count - 1 : 0)
            : Math.Clamp(index + delta, 0, rows.Count - 1);

        SelectedPeak = rows[next];
    }

    /// <summary>Band plan the table consults; null only when the shipped file failed to load.</summary>
    public BandPlan? BandPlan => _bandPlan;

    /// <summary>
    /// Marker list for the plots: one per visible tracked peak, with the selected one flagged.
    /// Frequencies are absolute Hz; the control maps them through its own axes.
    /// </summary>
    public IReadOnlyList<RfScout.Core.Display.PeakMarker> PeekMarkers()
    {
        List<PeakRow> rows = _visiblePeakRows;
        PeakRow? selected = SelectedPeak;
        var markers = new List<RfScout.Core.Display.PeakMarker>(rows.Count);
        foreach (PeakRow row in rows)
        {
            markers.Add(new RfScout.Core.Display.PeakMarker(row.Id, row.CenterHz, row.PowerDb, ReferenceEquals(row, selected)));
        }

        return markers;
    }

    /// <summary>Build the plan the current settings describe, or null when they are not usable.</summary>
    public SweepPlan? CurrentPlan
    {
        get
        {
            lock (_gate)
            {
                return BuildPlan(_startMHzText, _stopMHzText, _binWidthHz, _lnaDb, _vgaDb, _rfAmp, _antennaPower);
            }
        }
    }

    /// <summary>Start sweeping, using whatever the controls say now.</summary>
    public void Start()
    {
        SweepPlan? plan = CurrentPlan;
        if (plan is null)
        {
            StatusText = "Start and stop must be a valid span between 1 and 6000 MHz.";
            return;
        }

        lock (_gate)
        {
            if (_isSweeping)
            {
                return;
            }
        }

        IRadioSource? source = OpenSource();
        if (source is null)
        {
            StatusText = SourceKind == SweepSourceKind.HackRf
                ? "No HackRF found. Plug it in, or switch the source to Synthetic."
                : "No source available.";
            return;
        }

        try
        {
            source.SpectrumAvailable += OnSpectrumAvailable;
            source.Stopped += OnStopped;
            source.StartSweep(plan);
        }
        catch (Exception ex) when (ex is HackRfException or InvalidOperationException or ArgumentException)
        {
            source.Dispose();
            StatusText = $"Could not start the sweep: {ex.Message}";
            return;
        }

        lock (_gate)
        {
            _radio = source;
            _isSweeping = true;
            _frames.Reset();
            _floor.Reset();
            _peakFloor.Reset();
            _peakTracker.Reset();
            ClearPeakRows();
            _waterfall.Clear();
            _customPreset = new SweepPreset(
                "Custom",
                (long)(double.Parse(_startMHzText, CultureInfo.InvariantCulture) * 1e6),
                (long)(double.Parse(_stopMHzText, CultureInfo.InvariantCulture) * 1e6),
                _binWidthHz);
            _viewport = new SpectrumViewport(_customPreset.StartHz, _customPreset.StopHz);
            _viewport.SetMinSpanFromBinWidth(plan.DisplayBinWidthHz);
            _waterfall.Resize(CalculateColumns(_customPreset.StartHz, _customPreset.StopHz, plan.DisplayBinWidthHz));
            _statusText = $"Sweeping {_customPreset.StartHz / 1e6:F3}-{_customPreset.StopHz / 1e6:F3} MHz at {FrequencyFormat.BinWidth(plan.DisplayBinWidthHz)} bins.";
            OnPropertyChanged(nameof(IsSweeping));
            OnPropertyChanged(nameof(View));
        }

        RadioDeviceInfo? info = source.DeviceInfo;
        DeviceText = info is null ? "Radio open" : $"{info.BoardName} {info.ShortSerial} | fw {info.FirmwareVersion} | api {info.UsbApiVersionText}";
        RaiseRedraw();
    }

    /// <summary>Stop sweeping and give the radio back.</summary>
    public void Stop()
    {
        IRadioSource? source;
        lock (_gate)
        {
            _isSweeping = false;
            source = _radio;
            _radio = null;
            OnPropertyChanged(nameof(IsSweeping));
            _statusText = "Stopped.";
        }

        if (source is null)
        {
            return;
        }

        source.SpectrumAvailable -= OnSpectrumAvailable;
        source.Stopped -= OnStopped;
        try
        {
            source.StopStream();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Already stopped; the dispose below is what matters.
            _ = ex;
        }

        source.Dispose();
    }

    /// <summary>Space bar and the Start/Stop button: start when idle, stop when sweeping.</summary>
    public void ToggleSweep()
    {
        if (IsSweeping)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    /// <summary>Apply the toolbar settings to a sweep that is already running.</summary>
    public void ApplyNow()
    {
        if (IsSweeping)
        {
            Stop();
            Start();
            return;
        }

        SweepPlan? plan = CurrentPlan;
        if (plan is not null)
        {
            lock (_gate)
            {
                _customPreset = new SweepPreset(
                    "Custom",
                    (long)(double.Parse(_startMHzText, CultureInfo.InvariantCulture) * 1e6),
                    (long)(double.Parse(_stopMHzText, CultureInfo.InvariantCulture) * 1e6),
                    _binWidthHz);
                _viewport = new SpectrumViewport(_customPreset.StartHz, _customPreset.StopHz);
                _viewport.SetMinSpanFromBinWidth(plan.DisplayBinWidthHz);
                _waterfall.Resize(CalculateColumns(_customPreset.StartHz, _customPreset.StopHz, plan.DisplayBinWidthHz));
                OnPropertyChanged(nameof(View));
            }

            StatusText = $"Span set to {_customPreset.StartHz / 1e6:F3}-{_customPreset.StopHz / 1e6:F3} MHz. Press Start to sweep.";
        }
        else
        {
            StatusText = "Start and stop must be a valid span between 1 and 6000 MHz.";
        }
    }

    /// <summary>Recall a preset into the controls (and restart a running sweep).</summary>
    public void ApplyPreset(SweepPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        StartMHzText = (preset.StartHz / 1e6).ToString("F3", CultureInfo.InvariantCulture);
        StopMHzText = (preset.StopHz / 1e6).ToString("F3", CultureInfo.InvariantCulture);
        BinWidthHz = preset.BinWidthHz;

        if (IsSweeping)
        {
            Stop();
            Start();
        }
        else
        {
            ApplyNow();
        }
    }

    /// <summary>Feed one frame from a source thread. Public so a test can drive the pipeline directly.</summary>
    public void OfferFrame(SpectrumFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frames.Offer(frame);
    }

    /// <summary>
    /// Produce the frame the plots paint now, folding in the noise floor and the waterfall row.
    /// Returns false when nothing new has arrived since the last call, which keeps an idle window
    /// from repainting two hand-drawn plots sixty times a second.
    /// </summary>
    public bool TryRefreshDisplay(long sinceSequence, out SpectrumFrame? latest, out SpectrumFrame? peakHold, out IReadOnlyList<double> floorDb)
    {
        latest = null;
        peakHold = null;
        floorDb = [];

        long sequence = _frames.Sequence;
        if (sequence <= sinceSequence)
        {
            return false;
        }

        if (!_frames.TryGetSnapshot(out latest, out peakHold) || latest is null)
        {
            return false;
        }

        floorDb = _floor.Update(latest);

        if (latest.BinCount > 0)
        {
            EnsureWaterfallColumns(latest.BinCount);
            _waterfall.WriteRow(latest.PowerDb, ColourRange);
        }

        UpdatePeakPipeline(latest);

        lock (_gate)
        {
            if (_autoScale)
            {
                _dbRange.AutoRange(latest.PowerDb, latest.FilledBins);
                _dbMinText = _dbRange.MinDb.ToString("F0", CultureInfo.InvariantCulture);
                _dbMaxText = _dbRange.MaxDb.ToString("F0", CultureInfo.InvariantCulture);
            }

            _spectrumHelp = PlotReadout.SpanSummary(latest);
            _waterfallHelp = PlotReadout.SpanSummary(latest);
            RefreshStatsLocked();

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpectrumHelp)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WaterfallHelp)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DbMinText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DbMaxText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatsText)));
        }

        return true;
    }

    /// <summary>Ask for the colour range to follow the energy on screen.</summary>
    public void AutoRangeNow()
    {
        if (_frames.TryGetSnapshot(out SpectrumFrame? frame, out _) && frame is not null)
        {
            _dbRange.AutoRange(frame.PowerDb, frame.FilledBins);
            lock (_gate)
            {
                _dbMinText = _dbRange.MinDb.ToString("F0", CultureInfo.InvariantCulture);
                _dbMaxText = _dbRange.MaxDb.ToString("F0", CultureInfo.InvariantCulture);
            }

            OnPropertyChanged(nameof(DbMinText));
            OnPropertyChanged(nameof(DbMaxText));
            RaiseRedraw();
        }
    }

    /// <summary>Wheel zoom about the pointer, given where across the plot the pointer is.</summary>
    public void Zoom(double viewFraction, double factor)
    {
        SpectrumViewport view = View;
        lock (_gate)
        {
            view.ZoomAt(viewFraction, factor);
        }

        RaiseRedraw();
    }

    /// <summary>Drag by <paramref name="pixelDelta"/> pixels across a plot <paramref name="pixelWidth"/> wide.</summary>
    public void Pan(double pixelDelta, double pixelWidth)
    {
        SpectrumViewport view = View;
        lock (_gate)
        {
            view.PanPixels(pixelDelta, pixelWidth);
        }

        RaiseRedraw();
    }

    /// <summary>Double-click: put the clicked frequency in the middle and zoom in hard around it.</summary>
    public void FocusAt(double viewFraction)
    {
        SpectrumViewport view = View;
        lock (_gate)
        {
            view.Focus(viewFraction, PlotReadout.DoubleClickSpanHz(view));
        }

        RaiseRedraw();
    }

    /// <summary>Undo the zoom.</summary>
    public void ResetView()
    {
        View.ResetToFullSpan();
        RaiseRedraw();
    }

    /// <summary>Update the readout while the pointer is over the spectrum.</summary>
    public void ReportSpectrumHover(double xFraction, double yFraction)
    {
        _frames.TryGetSnapshot(out SpectrumFrame? frame, out _);
        HoverText = PlotReadout.Hover(frame, View, xFraction, yFraction);
    }

    /// <summary>Update the readout while the pointer is over the waterfall.</summary>
    public void ReportWaterfallHover(double xFraction)
    {
        _frames.TryGetSnapshot(out SpectrumFrame? frame, out _);
        WaterfallHoverText = PlotReadout.Hover(frame, View, xFraction, 0);
    }

    /// <summary>
    /// A pointer event on the waterfall control, already translated to reference-level coordinates
    /// (Hz, dBFS) by the control. Public so a test can press the same path the click triggers.
    /// </summary>
    public void WaterfallProbe(double frequencyHz, double levelDb)
    {
        WaterfallHoverText = string.Format(
            CultureInfo.InvariantCulture,
            "{0}  {1}",
            FrequencyFormat.MHz(frequencyHz),
            FrequencyFormat.Db(levelDb));
        RaiseRedraw();
    }

    /// <summary>Record a UI paint so the responsiveness number can be reported and asserted.</summary>
    public void RecordUiPaint(double gapMs)
    {
        if (gapMs > 0 && (!WorstUiGapMs.HasValue || gapMs > WorstUiGapMs.Value))
        {
            WorstUiGapMs = gapMs;
        }
    }

    /// <summary>Drop the peak-hold trace without stopping the sweep.</summary>
    public void ClearPeakHold()
    {
        _frames.Reset(clearPeakHold: true);
        RaiseRedraw();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }

    private IRadioSource? OpenSource()
    {
        IRadioSource? created = RadioFactory?.Invoke(SourceKind);
        if (created is not null)
        {
            return created;
        }

        // No factory wired (a test, or the app starting before the device layer is ready): fall
        // back to the synthetic source so the UI still has something to show.
        SweepPlan? plan = CurrentPlan;
        if (plan is null)
        {
            return null;
        }

        return new SyntheticRadioSource(plan.Ranges[0].StartHz, plan.Ranges[0].StopHz);
    }

    private void OnSpectrumAvailable(object? sender, SpectrumFrameEventArgs e) => _frames.Offer(e.Frame);

    private void OnStopped(object? sender, RadioStoppedEventArgs e)
    {
        StatusText = e.Reason;
        lock (_gate)
        {
            _isSweeping = false;
            OnPropertyChanged(nameof(IsSweeping));
        }
    }

    /// <summary>
    /// Fold the newest frame into the M2 peak pipeline: the statistical floor takes only
    /// completed sweeps (throttled so a 24000-bin percentile sort does not run sixty times a
    /// second on a partial frame), the detector runs against that floor, and the tracker gives
    /// the detections stable IDs. The table rows are then rebuilt from the tracker snapshot.
    /// </summary>
    private void UpdatePeakPipeline(SpectrumFrame frame)
    {
        if (!frame.SweepCompleted)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        bool due = (now - _lastPeakFloorTicks) >= TimeSpan.TicksPerSecond;
        if (!due && frame.BinCount != _floorDirtyBins)
        {
            // A completed sweep on a grid the floor already covers: fold it in silently so the
            // percentile window keeps filling even inside the throttle window.
            _peakFloor.Update(frame);
            return;
        }

        if (!due && _lastPeakFloorSequence == Frames.Sequence)
        {
            return;
        }

        _lastPeakFloorTicks = now;
        _lastPeakFloorSequence = Frames.Sequence;
        _floorDirtyBins = frame.BinCount;

        IReadOnlyList<double> floor = _peakFloor.Update(frame);
        IReadOnlyList<DetectedPeak> detections = _peakDetector.Detect(frame, floor);
        IReadOnlyList<TrackedPeak> tracks = _peakTracker.Update(detections, frame.BinWidthHz, frame.UtcTimestamp);

        RebuildPeakRows(tracks);
    }

    /// <summary>Rebuild the visible row list from the tracker's current snapshot.</summary>
    private void RebuildPeakRows(IReadOnlyList<TrackedPeak>? tracks = null)
    {
        tracks ??= _peakTracker.Snapshot();

        var rows = new List<PeakRow>(tracks.Count);
        lock (_gate)
        {
            var byId = new Dictionary<int, PeakRow>(_peakRows.Count);
            foreach (PeakRow row in _peakRows)
            {
                byId[row.Id] = row;
            }

            foreach (TrackedPeak track in tracks)
            {
                if (!byId.TryGetValue(track.Id, out PeakRow? row))
                {
                    row = new PeakRow { Id = track.Id };
                    _peakRows.Add(row);
                    byId[track.Id] = row;
                }

                string band = _bandPlan?.BandName(track.CenterHz) ?? "unallocated";
                string guess = BestGuessFor(track, band);
                double confidence = GuessConfidence(track, guess);
                row.Apply(track, band, guess, confidence);
                rows.Add(row);
            }

            _peakRows.RemoveAll(row => !rows.Contains(row));
            if (_selectedPeak is not null && !rows.Contains(_selectedPeak))
            {
                _selectedPeak.IsSelected = false;
                _selectedPeak = null;
            }
        }

        ApplyFilterAndSort(rows);
    }

    /// <summary>Drop every row (new sweep plan).</summary>
    private void ClearPeakRows()
    {
        lock (_gate)
        {
            _peakRows.Clear();
            _selectedPeak = null;
        }

        ApplyFilterAndSort(Array.Empty<PeakRow>());
    }

    /// <summary>Apply the filter text and sort order, then publish the visible list.</summary>
    private void ApplyFilterAndSort(IReadOnlyList<PeakRow> rows)
    {
        string filter;
        string column;
        bool descending;
        PeakRow? selected;
        lock (_gate)
        {
            filter = _peakFilterText;
            column = _peakSortColumn;
            descending = _peakSortDescending;
            selected = _selectedPeak;
        }

        IEnumerable<PeakRow> visible = rows;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            visible = rows.Where(row =>
                row.FrequencyText.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.Band.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.Activity.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.BestGuess.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.Id.ToString(CultureInfo.InvariantCulture).Contains(filter, StringComparison.Ordinal));
        }

        List<PeakRow> sorted = visible.ToList();
        int direction = descending ? -1 : 1;
        sorted.Sort((a, b) =>
        {
            int result = column switch
            {
                "Id" => a.Id.CompareTo(b.Id),
                "Frequency" => a.CenterHz.CompareTo(b.CenterHz),
                "Snr" => a.SnrDb.CompareTo(b.SnrDb),
                "Bandwidth" => a.BandwidthHz.CompareTo(b.BandwidthHz),
                "Duty" => a.DutyCycle.CompareTo(b.DutyCycle),
                "Band" => string.CompareOrdinal(a.Band, b.Band),
                "Type" => string.CompareOrdinal(a.Activity, b.Activity),
                "BestGuess" => string.CompareOrdinal(a.BestGuess, b.BestGuess),
                "LastSeen" => a.LastSeenUtc.CompareTo(b.LastSeenUtc),
                _ => a.PowerDb.CompareTo(b.PowerDb),
            };
            return result * direction;
        });

        _visiblePeakRows = sorted;
        _ = selected;
        OnPropertyChanged(nameof(PeakRows));
        RaiseRedraw();
    }

    /// <summary>
    /// M2's "best guess" is the band plan plus the measured shape: M3's classifier refines it.
    /// The band name is the honest answer for now, qualified by the bandwidth class.
    /// </summary>
    private static string BestGuessFor(TrackedPeak track, string band)
    {
        string shape = track.BandwidthClass switch
        {
            PeakBandwidthClass.Narrow => "carrier",
            PeakBandwidthClass.Medium => "channel",
            PeakBandwidthClass.Wide => "wideband",
            _ => "unknown width",
        };

        string activity = track.Activity.ToString().ToLowerInvariant();
        return string.Format(CultureInfo.InvariantCulture, "{0} ({1}, {2})", band, shape, activity);
    }

    /// <summary>Confidence the shape evidence carries: a known band plus a clear class is strong.</summary>
    private static double GuessConfidence(TrackedPeak track, string guess)
    {
        double score = 0.4;
        if (!string.Equals(track.BandwidthClass.ToString(), "Unknown", StringComparison.Ordinal))
        {
            score += 0.2;
        }

        if (track.DutyCycle >= 0.5 || track.DutyCycle <= 0.3)
        {
            score += 0.1; // a consistent duty pattern is a real emitter, not a fluke
        }

        if (guess.Contains("unallocated", StringComparison.OrdinalIgnoreCase))
        {
            score -= 0.1;
        }

        return Math.Clamp(score, 0, 1);
    }

    /// <summary>Load the shipped band plan, or null (the table then says "unallocated").</summary>
    private static BandPlan? TryLoadBandPlan()
    {
        try
        {
            return BandPlan.LoadShipped();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void PushGains()
    {
        IRadioSource? source;
        int lna;
        int vga;
        lock (_gate)
        {
            source = _radio;
            lna = _lnaDb;
            vga = _vgaDb;
        }

        if (source is null)
        {
            return;
        }

        try
        {
            source.SetGains(lna, vga);

            // The floor line belongs to these gains, so an old gain's floor must not be drawn
            // against the new trace. The peak floor and tracker follow: their statistics are
            // meaningless across a gain change.
            _floor.Reset();
            _peakFloor.Reset();
            _peakTracker.Reset();
            ClearPeakRows();
        }
        catch (ArgumentOutOfRangeException)
        {
            StatusText = "The radio rejected those gains.";
        }
    }

    private void ApplySwitches()
    {
        IRadioSource? source;
        bool amp;
        bool ant;
        lock (_gate)
        {
            source = _radio;
            amp = _rfAmp;
            ant = _antennaPower;
        }

        if (source is null)
        {
            return;
        }

        try
        {
            source.SetRfAmplifier(amp);
            source.SetAntennaPower(ant);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            StatusText = "The radio rejected the gain switches.";
        }
    }

    private void RefreshStatsLocked()
    {
        IRadioSource? source = _radio;
        RadioStats stats = source?.ReadStats() ?? RadioStats.Empty;
        string sweeps = stats.SweepsPerSecond.ToString("F2", CultureInfo.InvariantCulture);
        _statsText = $"sweeps/s {sweeps} | dropped {stats.DroppedBlocks}";
    }

    private void ApplyDbRangeFromText()
    {
        bool hasMin = double.TryParse(_dbMinText, NumberStyles.Float, CultureInfo.InvariantCulture, out double min);
        bool hasMax = double.TryParse(_dbMaxText, NumberStyles.Float, CultureInfo.InvariantCulture, out double max);
        if (!hasMin || !hasMax)
        {
            return;
        }

        _dbRange.Set(min, max);
        AutoScale = false;
        RaiseRedraw();
    }

    private static SweepPlan? BuildPlan(string startText, string stopText, int binWidthHz, int lnaDb, int vgaDb, bool amp, bool ant)
    {
        if (!double.TryParse(startText, NumberStyles.Float, CultureInfo.InvariantCulture, out double startMHz)
            || !double.TryParse(stopText, NumberStyles.Float, CultureInfo.InvariantCulture, out double stopMHz))
        {
            return null;
        }

        if (startMHz <= 0 || stopMHz <= startMHz)
        {
            return null;
        }

        var preset = new SweepPreset(
            "Custom",
            (long)Math.Round(startMHz * 1e6),
            (long)Math.Round(stopMHz * 1e6),
            binWidthHz);

        return preset.IsValid ? preset.ToPlan(lnaDb, vgaDb, amp, ant) : null;
    }

    private static int CalculateColumns(long startHz, long stopHz, double binWidthHz)
    {
        int bins = (int)Math.Ceiling((stopHz - startHz) / Math.Max(1, binWidthHz));
        return Math.Clamp(bins, 1, SpectrumGrid.MaxBins);
    }

    private void EnsureWaterfallColumns(int binCount)
    {
        if (_waterfall.Columns == 0)
        {
            _waterfall.Resize(binCount);
        }
    }

    private bool Confirm(string message) => NeedsConfirmation?.Invoke(message) ?? true;

    private void RaiseRedraw() => NeedsRedraw?.Invoke(this, EventArgs.Empty);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}


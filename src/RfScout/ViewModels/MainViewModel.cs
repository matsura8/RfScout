using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Threading.Tasks;
using RfScout.Core;
using RfScout.Core.Analysis;
using RfScout.Core.Decoders;
using RfScout.Core.Display;
using RfScout.Core.Locate;
using RfScout.Core.Fakes;
using RfScout.Core.Recording;
using RfScout.Core.Streaming;
using RfScout.Core.Spectrum;
using RfScout.Core.Export;
using RfScout.Core.Settings;
using RfScout.Services;
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
    private string _region = AppSettings.Defaults.Region;
    private string _colorMapName = AppSettings.Defaults.ColorMap;
    private string? _settingsDirectory;
    private bool _settingsLoaded;
    private bool _settingsDirty;
    private UnplugMonitor? _unplugMonitor;
    private bool _deviceLostBanner;
    private string _bannerText = string.Empty;
    private string _exportStatusText = string.Empty;
    private ProcessCpuTracker? _cpuTracker;
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

    // M5 locate state: the controller is Core logic (meter, 60 s graph, peak hold, tone map);
    // the source and the flag are the locate session, which owns the radio while it runs.
    private readonly LocateController _locate = new();
    private IRadioSource? _locateSource;
    private bool _isLocating;
    private bool _locateWasSweeping;
    private int _locatePeakId;
    private bool _locateTone;
    private System.Windows.Media.MediaPlayer? _locateToneSource;

    /// <summary>Meter range the graph and the tone share: floor to strong.</summary>
    public const double MeterFloorDb = LocateController.MeterMinDb;
    public const double MeterTopDb = LocateController.MeterMaxDb;

    /// <summary>Locate always runs at the radio's minimum capture rate: narrowest filter,
    /// most time per measurement, and the rate the strength meter is calibrated for.</summary>
    private const int LocateSampleRateHz = 2_000_000;

    /// <summary>IQ samples per locate window: 500 ms at 2 Msps, so the graph gets a point
    /// about twice a second and each window is short enough to follow walking with an
    /// antenna. The device layer enforces the 2048-sample floor.</summary>
    private const int LocateWindowSamples = 1_000_000;
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

    // ---- M3 analyze mode -------------------------------------------------------------
    // The analyze state machine (one-at-a-time rule, plan bounds) lives in AnalyzeController;
    // these fields hold the results the detail pane binds to and the analyze's own settings.
    private readonly AnalyzeController _analyze = new();
    private CaptureRecorder _recorder = new();
    private bool _autoAnalyze;
    private int _analyzeRoundRobinIndex;
    private bool _autoAnalyzeTickActive;
    // The peak whose analyze runs from inside the sweep's own completion callback, so the sweep
    // keeps the radio and the analyze borrows it instead of opening a second source.
    private PeakRow? _analyzeSourceMarker;
    private string _analyzeStatus = "Idle. Select a peak and press Enter (or click Analyze peak) to analyze it.";
    private string _classificationText = string.Empty;
    private string _featuresText = string.Empty;
    private string _reasonsText = string.Empty;
    private string _decodeLogText = string.Empty;
    private string _capturePathText = string.Empty;
    private string _zoomedSpectrumText = string.Empty;
    private string _envelopeText = string.Empty;
    private string _instFreqText = string.Empty;
    private SignalFeatures? _lastFeatures;
    private ClassificationResult? _lastClassification;
    private int _lastAnalyzedPeakId = -1;
    private IReadOnlyList<DeviceIdentification> _lastIdentifications = [];
    private readonly DecoderPipeline _decoders = DecoderPipeline.Default();

    /// <summary>
    /// Capture limits an analyze must respect. Public so tests and the settings UI can read and
    /// change them; the analyze plan clamps its window to both (see <see cref="AnalyzeController.PlanFor"/>).
    /// </summary>
    public long MaxCaptureBytes { get; set; } = 64L * 1024 * 1024;

    public double MaxCaptureSeconds { get; set; } = AnalyzeController.MaxCaptureSeconds;

    /// <summary>
    /// Test seam: completes a capture synchronously on the calling thread instead of waiting for
    /// the source's capture event, so a headless test can drive the whole analyze path.
    /// </summary>
    public bool CompleteCapturesInlineForTests { get; set; }

    /// <summary>
    /// Test seam: where an analyze writes its .cs8/.sigmf-meta pair. Null (the default) keeps the
    /// recorder's %LOCALAPPDATA%\RfScout\captures; a test points it at a throwaway folder.
    /// </summary>
    public string? CaptureDirectoryOverride { get; set; }

    /// <summary>
    /// Test seam: given the plan's sample rate, produce the IQ bytes the analyze should classify
    /// instead of the fake source's generic tone. Lets a headless test drive the whole analyze
    /// path over a known waveform (see the M3 analyze tests).
    /// </summary>
    public Func<int, byte[]>? CaptureOverrideForTests { get; set; }

    private TaskCompletionSource<IqCapture>? _captureTcs;
    private string? _captureDirOverrideApplied;

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
        AnalyzePeakCommand = new RelayCommand(
            _ => _ = AnalyzeSelectedPeakAsync(),
            _ => SelectedPeak is not null && !IsAnalyzing);
        LocatePeakCommand = new RelayCommand(
            _ => _ = LocateSelectedPeakAsync(),
            _ => SelectedPeak is not null && !IsLocating && !IsAnalyzing);
        ExitLocateCommand = new RelayCommand(ExitLocate, _ => IsLocating);
        ResetLocatePeakCommand = new RelayCommand(static _ => { }, _ => IsLocating);
        ExportCsvCommand = new RelayCommand(ExportCsv);
        ExportJsonCommand = new RelayCommand(ExportJson);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the view should repaint. The dispatcher timer does this on a tick, and
    /// a synthetic sweep raises it straight away so a test sees a frame without waiting.</summary>
    public event EventHandler? NeedsRedraw;

    /// <summary>Raised when locate windows arrive, so the view repaints the meter and graph.</summary>
    public event EventHandler? LocateUpdated;

    /// <summary>Raised with true when the operator must confirm before antenna power or the amp goes on.</summary>
    public event Func<string, bool>? NeedsConfirmation;

    /// <summary>
    /// Factory for the radio, so the app can hand over the real HackRF wrapper and a test can hand
    /// over a fake. Returns null when no source of the requested kind is available.
    /// </summary>
    public Func<SweepSourceKind, IRadioSource?>? RadioFactory { get; set; }

    /// <summary>Why the last attempt to open the radio failed, shown in the status line; set by
    /// the <see cref="RadioFactory"/>.</summary>
    public string? LastOpenError { get; set; }

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

    /// <summary>M3: analyze the selected peak (the detail pane's button and Enter in the peak table).</summary>
    public ICommand AnalyzePeakCommand { get; private set; } = null!;

    /// <summary>M5: stop the sweep and stay tuned to the selected peak in narrowband locate mode.</summary>
    public ICommand LocatePeakCommand { get; private set; } = null!;

    /// <summary>M5: leave locate and restart the sweep that was running before it.</summary>
    public ICommand ExitLocateCommand { get; private set; } = null!;

    /// <summary>M5: clear the locate peak-hold number.</summary>
    public ICommand ResetLocatePeakCommand { get; private set; } = null!;

    /// <summary>M6: write the peak table and identifications to a CSV file.</summary>
    public ICommand ExportCsvCommand { get; private set; } = null!;

    /// <summary>M6: write the peak table and identifications to a JSON file.</summary>
    public ICommand ExportJsonCommand { get; private set; } = null!;

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
            MarkSettingsDirty();
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
            MarkSettingsDirty();
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
            MarkSettingsDirty();
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

            MarkSettingsDirty();
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

            MarkSettingsDirty();
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

    /// <summary>True while an analyze owns the radio (the sweep is paused for its duration).</summary>
    public bool IsAnalyzing => _analyze.IsBusy;

    /// <summary>Round-robin auto-analyze: after every completed sweep, analyze the next peak.</summary>
    public bool AutoAnalyze
    {
        get => _autoAnalyze;
        set
        {
            if (!SetField(ref _autoAnalyze, value))
            {
                return;
            }

            if (value)
            {
                AnalyzeStatus = "Auto-analyze armed: the next completed sweep starts analyzing the strongest peak.";
            }
            else
            {
                AnalyzeStatus = "Auto-analyze off.";
            }
        }
    }

    /// <summary>Analyze-mode status line shown in the detail pane.</summary>
    public string AnalyzeStatus
    {
        get => _analyzeStatus;
        private set => SetField(ref _analyzeStatus, value);
    }

    /// <summary>The winning class, confidence and headline, e.g. "WFM 87%: ...". Empty until an analyze runs.</summary>
    public string ClassificationText
    {
        get => _classificationText;
        private set => SetField(ref _classificationText, value);
    }

    /// <summary>The measured features, one readable line (AM depth, deviation, OBW, symbol rate, ...).</summary>
    public string FeaturesText
    {
        get => _featuresText;
        private set => SetField(ref _featuresText, value);
    }

    /// <summary>The rules that fired and the evidence each one quoted - why the classifier decided what it did.</summary>
    public string ReasonsText
    {
        get => _reasonsText;
        private set => SetField(ref _reasonsText, value);
    }

    /// <summary>The decode log; empty for M3 (decoding is M4), kept so the pane shape is final.</summary>
    public string DecodeLogText
    {
        get => _decodeLogText;
        private set => SetField(ref _decodeLogText, value);
    }

    /// <summary>Path of the .cs8/.sigmf-meta pair the last analyze recorded, if any.</summary>
    public string CapturePathText
    {
        get => _capturePathText;
        private set => SetField(ref _capturePathText, value);
    }

    /// <summary>One-line description of the zoomed-spectrum trace for the detail pane.</summary>
    public string ZoomedSpectrumText
    {
        get => _zoomedSpectrumText;
        private set => SetField(ref _zoomedSpectrumText, value);
    }

    /// <summary>One-line description of the envelope trace for the detail pane.</summary>
    public string EnvelopeText
    {
        get => _envelopeText;
        private set => SetField(ref _envelopeText, value);
    }

    /// <summary>One-line description of the instantaneous-frequency trace for the detail pane.</summary>
    public string InstFreqText
    {
        get => _instFreqText;
        private set => SetField(ref _instFreqText, value);
    }

    /// <summary>The features of the last analyze, or null before one ran. Public for tests and the detail pane.</summary>
    public SignalFeatures? LastFeatures => _lastFeatures;

    /// <summary>The classification of the last analyze, or null before one ran.</summary>
    public ClassificationResult? LastClassification => _lastClassification;

    /// <summary>Detail-pane text for the selected peak, empty when none is selected.</summary>
    public string SelectedPeakText
    {
        get
        {
            lock (_gate)
            {
                return _selectedPeak is null
                    ? "No peak selected. Pick a row or click a marker."
                    : _selectedPeak.AutomationName + ". Best guess: " + _selectedPeak.BestGuess + "." + Environment.NewLine + IdentificationPaneText(_selectedPeak);
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
                MarkSettingsDirty();
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
                ? $"Could not open the HackRF{(string.IsNullOrEmpty(LastOpenError) ? string.Empty : $" ({LastOpenError})")}. Check it is plugged in and not in use, or switch the source to Synthetic."
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

        OnPropertyChanged(nameof(StatusText));

        RadioDeviceInfo? info = source.DeviceInfo;
        DeviceText = info is null ? "Radio open" : $"{info.BoardName} {info.ShortSerial} | fw {info.FirmwareVersion} | api {info.UsbApiVersionText}";
        DeviceLostBanner = false;
        BannerText = string.Empty;
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

        OnPropertyChanged(nameof(StatusText));

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

            // QA defect 1: the label above the plot showed "No sweep yet" while a live sweep was
            // already filling the grid. The label is HoverText, which only ever came from pointer
            // moves; refresh it from the newest frame so it tracks the sweep state on its own.
            if (_hoverText is "No sweep yet" or "Sweeping" or "Sweep running"
                || _hoverText.StartsWith("Sweeping ", StringComparison.Ordinal))
            {
                _hoverText = latest.SweepCompleted
                    ? _spectrumHelp
                    : "Sweeping " + Core.Display.FrequencyFormat.MHz(latest.StartHz)
                        + " to " + Core.Display.FrequencyFormat.MHz(latest.StopHz);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpectrumHelp)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HoverText)));
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
            // AutoRange marks the range auto-scaled, which is what lets the plot's dB axis
            // follow the display range instead of the fixed -160 floor (QA defect 3).
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
        ExitLocate();
        StopUnplugWatch();
        SaveSettingsIfDirty();
    }

    private IRadioSource? OpenSource()
    {
        if (RadioFactory is { } factory)
        {
            IRadioSource? created = factory(SourceKind);

            // The operator picked the HackRF: if it cannot be opened, say so rather than quietly
            // showing synthetic data that looks like a working radio.
            if (created is not null || SourceKind == SweepSourceKind.HackRf)
            {
                return created;
            }
        }

        // Synthetic chosen, or no factory wired (a test): use the synthetic source so the UI
        // still has something to show.
        SweepPlan? plan = CurrentPlan;
        if (plan is null)
        {
            return null;
        }

        return new SyntheticRadioSource(plan.Ranges[0].StartHz, plan.Ranges[0].StopHz);
    }

    private void OnSpectrumAvailable(object? sender, SpectrumFrameEventArgs e) => _frames.Offer(e.Frame);

    // ---------------------------------------------------------------------------
    // M5 locate: stay tuned to one peak and watch its strength.
    //
    // The rules the sweep already follows apply here too: one mode at a time, the
    // radio is never owned by two paths at once, and leaving a mode gives the radio
    // back to whatever was running before. Locate borrows the analyze sequencing
    // (stop the sweep, open the source, stream narrowband windows, then restart the
    // sweep), but instead of one bounded capture it keeps streaming: the HackRF
    // device raises LocateSampleAvailable with a fresh window every ~0.25 s, the
    // Core LocateController folds each into the meter, the 60 s graph and the peak
    // hold, and the tone pitch follows strength when the operator turns it on.
    // ---------------------------------------------------------------------------

    /// <summary>True while the radio is tuned to one peak for locate.</summary>
    public bool IsLocating
    {
        get
        {
            lock (_gate)
            {
                return _isLocating;
            }
        }
    }

    /// <summary>Live strength of the located peak, dBFS, for the big meter.</summary>
    public double LocateCurrentDb
    {
        get
        {
            lock (_gate)
            {
                return _locate.CurrentDb;
            }
        }
    }

    /// <summary>Loudest strength since locate started (or since the last reset).</summary>
    public double LocatePeakDb
    {
        get
        {
            lock (_gate)
            {
                return _locate.PeakDb;
            }
        }
    }

    /// <summary>Meter text, e.g. "-42.3 dBFS".</summary>
    public string LocateCurrentText
    {
        get
        {
            lock (_gate)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:F1} dBFS", _locate.CurrentDb);
            }
        }
    }

    /// <summary>Peak-hold text.</summary>
    public string LocatePeakText
    {
        get
        {
            lock (_gate)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:F1} dBFS", _locate.PeakDb);
            }
        }
    }

    /// <summary>Current strength mapped to 0..1 for the meter bar.</summary>
    public double LocateMeterFraction
    {
        get
        {
            lock (_gate)
            {
                return LocateController.ToneGainFor(_locate.CurrentDb);
            }
        }
    }

    /// <summary>Graph snapshot for the 60 s strength plot (seconds since locate, dBFS).</summary>
    public IReadOnlyList<(int Second, double Db)> LocateGraph()
    {
        lock (_gate)
        {
            return _locate.SnapshotGraph();
        }
    }

    /// <summary>Audio tone follows strength, off by default per the brief.</summary>
    public bool LocateToneEnabled
    {
        get
        {
            lock (_gate)
            {
                return _locateTone;
            }
        }

        set
        {
            bool on;
            lock (_gate)
            {
                if (_locateTone == value)
                {
                    return;
                }

                _locateTone = value;
                on = value;
            }

            if (on)
            {
                // MediaPlayer is a dispatcher object; marshal so any caller thread is safe.
                System.Windows.Application.Current?.Dispatcher.Invoke(StartLocateTone);
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(StopLocateTone);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Permanent note: what a single HackRF can and cannot tell you.</summary>
    public static string LocateCaveatText =>
        "A single HackRF gives frequency location and relative signal strength, not direction or position.";

    /// <summary>Tone frequency for the current strength, when the tone is on.</summary>
    public double LocateToneHz
    {
        get
        {
            lock (_gate)
            {
                return LocateController.ToneHzFor(_locate.CurrentDb);
            }
        }
    }

    /// <summary>Tone gain for the current strength, when the tone is on.</summary>
    public double LocateToneGain
    {
        get
        {
            lock (_gate)
            {
                return LocateController.ToneGainFor(_locate.CurrentDb);
            }
        }
    }

    /// <summary>Locate the selected peak: stop the sweep and stay tuned to it.</summary>
    public Task LocateSelectedPeakAsync() => LocatePeakAsync(SelectedPeak);

    /// <summary>
    /// Enter locate on one peak row. The sweep is stopped first (one mode at a time);
    /// leaving locate restarts it. The locate stream runs on the source, so this call
    /// returns once the stream is running, not when locate ends.
    /// </summary>
    public async Task LocatePeakAsync(PeakRow? peak)
    {
        if (peak is null)
        {
            StatusText = "Nothing to locate: select a peak first.";
            return;
        }

        lock (_gate)
        {
            if (_isLocating || _analyze.IsBusy)
            {
                return;
            }
        }

        bool wasSweeping = IsSweeping;
        try
        {
            _analyze.BeginAnalyze();
        }
        catch (InvalidOperationException)
        {
            StatusText = "Analyze is busy: one at a time.";
            return;
        }

        try
        {
            if (wasSweeping)
            {
                Stop();
            }

            IRadioSource? source = OpenSource();
            if (source is null)
            {
                StatusText = "No radio available for locate.";
                return;
            }

            try
            {
                source.LocateSampleAvailable += OnLocateSample;
                source.Stopped += OnStopped;
                source.SetGains(LnaDb, VgaDb);
                source.SetRfAmplifier(RfAmp);
                source.SetAntennaPower(AntennaPower);
                source.StartLocate((long)peak.CenterHz, LocateSampleRateHz, LocateWindowSamples);

                lock (_gate)
                {
                    _locateSource = source;
                    _locateWasSweeping = wasSweeping;
                    _locatePeakId = peak.Id;
                    _isLocating = true;
                    _locate.Start(DateTimeOffset.UtcNow);
                }

                StatusText = string.Format(
                    CultureInfo.InvariantCulture,
                    "Locating peak {0} at {1}: {2}",
                    peak.Id,
                    peak.FrequencyText,
                    LocateCaveatText);
                OnPropertyChanged(nameof(IsLocating));
                RaiseLocateUpdated();
            }
            catch (Exception ex) when (ex is HackRfException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                source.LocateSampleAvailable -= OnLocateSample;
                source.Stopped -= OnStopped;
                source.Dispose();
                StatusText = $"Could not start locate: {ex.Message}";
                return;
            }
        }
        finally
        {
            _analyze.EndAnalyze();
        }
    }

    /// <summary>Leave locate and give the radio back to the sweep that was running before.</summary>
    public void ExitLocate()
    {
        IRadioSource? source;
        bool restartSweep;
        lock (_gate)
        {
            if (!_isLocating)
            {
                return;
            }

            _isLocating = false;
            source = _locateSource;
            _locateSource = null;
            restartSweep = _locateWasSweeping;
            _locateWasSweeping = false;
        }

        StopLocateTone();

        if (source is not null)
        {
            source.LocateSampleAvailable -= OnLocateSample;
            source.Stopped -= OnStopped;
            try
            {
                source.StopStream();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                _ = ex;
            }

            source.Dispose();
        }

        StatusText = "Left locate.";
        OnPropertyChanged(nameof(IsLocating));
        RaiseLocateUpdated();

        if (restartSweep && !IsSweeping)
        {
            Start();
        }
    }

    /// <summary>Clear the peak-hold number; the live reading and graph are untouched.</summary>
    public void ResetLocatePeak()
    {
        lock (_gate)
        {
            _locate.ResetPeakHold();
        }

        OnPropertyChanged(nameof(LocatePeakDb));
        OnPropertyChanged(nameof(LocatePeakText));
        RaiseLocateUpdated();
    }

    private void OnLocateSample(object? sender, IqCaptureEventArgs e)
    {
        lock (_gate)
        {
            if (!_isLocating)
            {
                return; // a late window from a locate already left
            }

            _locate.Offer(e.Capture.Samples, DateTimeOffset.UtcNow);
        }

        OnPropertyChanged(nameof(LocateCurrentDb));
        OnPropertyChanged(nameof(LocatePeakDb));
        OnPropertyChanged(nameof(LocateCurrentText));
        OnPropertyChanged(nameof(LocatePeakText));
        OnPropertyChanged(nameof(LocateMeterFraction));
        OnPropertyChanged(nameof(LocateToneHz));
        OnPropertyChanged(nameof(LocateToneGain));
        UpdateLocateTone();
        RaiseLocateUpdated();
    }

    private void RaiseLocateUpdated() => LocateUpdated?.Invoke(this, EventArgs.Empty);

    private void StartLocateTone()
    {
        if (_locateToneSource is not null)
        {
            return;
        }

        try
        {
            var wave = new System.Windows.Media.MediaPlayer();
            wave.Open(new Uri("pack://application:,,,/RfScout;component/Assets/tone.wav", UriKind.Absolute));
            _locateToneSource = wave;
            UpdateLocateTone();
            wave.Play();
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or NotSupportedException)
        {
            // No tone asset on this machine: the meter and graph still work, so log it in
            // the status line rather than failing locate.
            _locateToneSource = null;
            StatusText = $"Locate tone unavailable: {ex.Message}";
        }
    }

    private void UpdateLocateTone()
    {
        System.Windows.Media.MediaPlayer? wave = _locateToneSource;
        if (wave is null)
        {
            return;
        }

        double speed, volume;
        lock (_gate)
        {
            if (!_locateTone)
            {
                return;
            }

            speed = Math.Clamp(LocateController.ToneHzFor(_locate.CurrentDb) / 440.0, 0.25, 4.0);
            volume = Math.Clamp(LocateController.ToneGainFor(_locate.CurrentDb), 0.05, 1.0);
        }

        // MediaPlayer touches the audio device: keep every call on the dispatcher.
        void Apply()
        {
            try
            {
                wave.SpeedRatio = speed;
                wave.Volume = volume;
            }
            catch (InvalidOperationException ex)
            {
                _ = ex;
            }
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(Apply));
    }

    private void StopLocateTone()
    {
        System.Windows.Media.MediaPlayer? wave = Interlocked.Exchange(ref _locateToneSource, null);
        if (wave is null)
        {
            return;
        }

        void Close()
        {
            try
            {
                wave.Stop();
                wave.Close();
            }
            catch (InvalidOperationException ex)
            {
                _ = ex;
            }
        }

        if (System.Windows.Application.Current is { } app)
        {
            if (app.Dispatcher.CheckAccess())
            {
                Close();
            }
            else
            {
                app.Dispatcher.Invoke(new Action(Close));
            }
        }
        else
        {
            Close();
        }
    }

    /// <summary>Capture completion arrives on the source thread; hand it to the waiting analyze.</summary>
    private void OnCaptureAvailable(object? sender, IqCaptureEventArgs e)
    {
        TaskCompletionSource<IqCapture>? tcs = Interlocked.Exchange(ref _captureTcs, null);
        tcs?.TrySetResult(e.Capture);
    }

    /// <summary>
    /// M3 analyze one peak: pause the sweep, retune to a 2-20 Msps window sized from the peak's
    /// bandwidth, capture 0.5-2 s of IQ, then classify it and resume the sweep. Sweep and analyze
    /// never own the radio together: <see cref="AnalyzeController"/> enforces one analyze at a
    /// time, and the sweep is stopped before the capture starts and restarted afterwards.
    /// </summary>
    public Task AnalyzeSelectedPeakAsync() => AnalyzePeakAsync(SelectedPeak);

    /// <summary>
    /// Analyze one peak row. Split out from <see cref="AnalyzeSelectedPeakAsync"/> so auto-analyze
    /// can pass the row its tick picked instead of re-reading the selection.
    /// </summary>
    public async Task AnalyzePeakAsync(PeakRow? peak)
    {
        if (peak is null)
        {
            AnalyzeStatus = "Nothing to analyze: select a peak first.";
            return;
        }

        try
        {
            _analyze.BeginAnalyze();
        }
        catch (InvalidOperationException)
        {
            AnalyzeStatus = "Analyze is busy: one at a time.";
            return;
        }

        bool wasSweeping = IsSweeping;
        try
        {
            AnalyzeStatus = $"Analyzing peak {peak.Id} at {peak.FrequencyText}: pausing the sweep.";
            OnPropertyChanged(nameof(IsAnalyzing));

            // Release the radio before the analyze opens it: the sweep and the analyze never own
            // it together. When the analyze runs from a sweep callback (auto-analyze after a
            // completed sweep), the source is still inside its own StartSweep call on this thread,
            // so Stop() would hit the "already running" guard; in that case the sweep is left
            // running and the finally block below stops and restarts it cleanly instead.
            bool releasedUpFront = wasSweeping && !ReferenceEquals(peak, _analyzeSourceMarker);
            if (releasedUpFront)
            {
                Stop();
            }

            AnalyzePlan plan = AnalyzeController.PlanFor(
                (long)peak.CenterHz,
                peak.BandwidthHz,
                MaxCaptureBytes,
                MaxCaptureSeconds);

            IRadioSource? source;
            if (releasedUpFront)
            {
                source = OpenSource();
            }
            else
            {
                // The sweep still holds the radio (the analyze runs from its completion callback).
                // Hand the running source to the capture instead of opening a second one: the
                // fake sources accept a capture while streaming, and the real device rejects a
                // second open. The finally block below restarts the sweep afterwards.
                lock (_gate)
                {
                    source = _radio;
                }
            }

            if (source is null)
            {
                AnalyzeStatus = "No radio available for the analyze.";
                return;
            }

            IqCapture capture;
            try
            {
                source.CaptureAvailable += OnCaptureAvailable;
                var tcs = new TaskCompletionSource<IqCapture>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _captureTcs, tcs);
                try
                {
                    source.StartCapture(plan.CenterHz, plan.SampleRateHz, TimeSpan.FromSeconds(plan.DurationSeconds));
                    if (CompleteCapturesInlineForTests)
                    {
                        // The fake sources raise CaptureAvailable synchronously, so the task is
                        // already settled; this just keeps a source that never fires from hanging a test.
                        capture = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                    else
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        capture = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    source.CaptureAvailable -= OnCaptureAvailable;
                    Interlocked.Exchange(ref _captureTcs, null);
                }
            }
            catch (Exception ex) when (ex is HackRfException or InvalidOperationException or ArgumentException or TimeoutException)
            {
                AnalyzeStatus = $"Capture failed: {ex.Message}";
                return;
            }
            finally
            {
                try
                {
                    source.StopStream();
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    _ = ex;
                }

                if (releasedUpFront)
                {
                    // The analyze opened this source, so the analyze owns its lifetime.
                    source.Dispose();
                }
                // Otherwise the sweep still owns the source: it is stopped here and restarted by
                // the outer finally, which must not dispose the radio the sweep is about to reuse.
            }

            AnalyzeStatus = $"Captured {capture.SampleCount} samples at {plan.SampleRateHz / 1e6:F1} Msps; analyzing.";

            if (CaptureOverrideForTests is not null)
            {
                // Test seam: replace the fake source's generic tone with the signal under test, so
                // the whole analyze path (capture -> features -> classify -> record -> pane text)
                // runs headless over a known waveform.
                capture = capture with { Samples = CaptureOverrideForTests(capture.SampleRateHz) };
            }

            SignalFeatures features = FeatureExtractor.Extract(capture.Samples, capture.SampleRateHz);
            ClassificationResult result = ModulationClassifier.Classify(features);
            _lastFeatures = features;
            _lastClassification = result;
            _lastAnalyzedPeakId = peak.Id;

            // M4: run the band-plan decoders over the same capture (identifiers only, hard rule 3).
            // The pipeline always returns at least the classification-only label, so the detail pane
            // never goes blank after an analyze.
            DecoderContext decodeCtx = DecoderContext.ForPeak(
                capture.Samples,
                capture.CenterHz,
                capture.SampleRateHz,
                _bandPlan ?? BandPlan.LoadShipped(),
                result,
                features);
            IReadOnlyList<DeviceIdentification> identifications = _decoders.Identify(decodeCtx);
            lock (_gate)
            {
                _lastIdentifications = identifications;
            }

            PlotTrace zoom = CapturePlots.ZoomedSpectrum(capture.Samples, capture.SampleRateHz);
            PlotTrace envelope = CapturePlots.Envelope(capture.Samples, capture.SampleRateHz);
            PlotTrace instFreq = CapturePlots.InstantaneousFrequency(capture.Samples, capture.SampleRateHz);

            string capturePath = string.Empty;
            try
            {
                // The recorder is built once, so pointing the override at a new folder means a
                // new recorder over that folder (a test's throwaway directory). The first time
                // the override is applied, drop anything the default folder already held.
                if (CaptureDirectoryOverride is string dir && !string.Equals(dir, _captureDirOverrideApplied, StringComparison.Ordinal))
                {
                    _recorder = new CaptureRecorder(dir);
                    _captureDirOverrideApplied = dir;
                    _recorder.Rotate();
                }

                RecordedCapture recorded = _recorder.Save(
                    capture,
                    new ClassificationSummary(result.ClassText, result.Confidence));
                capturePath = recorded.Cs8Path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnalyzeStatus = $"Capture could not be recorded: {ex.Message}";
            }

            ClassificationText = result.Describe();
            FeaturesText = features.Describe();
            ReasonsText = result.DescribeReasons();
            DecodeLogText = string.Join(Environment.NewLine, identifications.Select(static id => id.Describe()));
            CapturePathText = capturePath;
            ZoomedSpectrumText = zoom.Describe();
            EnvelopeText = envelope.Describe();
            InstFreqText = instFreq.Describe();
            AnalyzeStatus = $"Peak {peak.Id}: {result.ClassText} {result.Confidence:P0}.";
        }
        finally
        {
            _analyze.EndAnalyze();
            OnPropertyChanged(nameof(IsAnalyzing));
            if (wasSweeping && !IsSweeping && !IsAnalyzing)
            {
                Start();
            }
            else if (wasSweeping && IsSweeping)
            {
                // The sweep was never released (analyze ran from its own callback thread): give
                // the radio back to it by stopping and restarting outside the callback stack.
                _analyzeSourceMarker = null;
                Stop();
                Start();
            }
        }
    }

    /// <summary>
    /// Round-robin auto-analyze: called after each completed sweep while <see cref="AutoAnalyze"/>
    /// is on; analyzes the next visible peak in turn. Returns the running task, or null when there
    /// is nothing to do. Re-entrancy is guarded so a slow analyze never stacks.
    /// </summary>
    public Task? TickAutoAnalyze()
    {
        if (!AutoAnalyze || IsAnalyzing || _autoAnalyzeTickActive)
        {
            return null;
        }

        List<PeakRow> rows = _visiblePeakRows;
        if (rows.Count == 0)
        {
            return null;
        }

        PeakRow target = rows[Math.Abs(_analyzeRoundRobinIndex++) % rows.Count];
        SelectedPeak = target;
        // The tick runs from the sweep's completion callback: mark the target so the analyze
        // borrows the running sweep's radio instead of trying to stop and re-open it.
        _analyzeSourceMarker = target;
        _autoAnalyzeTickActive = true;
        return RunAutoAnalyzeAsync(target);
    }

    private async Task RunAutoAnalyzeAsync(PeakRow target)
    {
        try
        {
            // Analyze the row this tick picked, not whatever happens to be selected by the time
            // the previous analyze finishes: the round-robin index and the selection are set
            // together here, and a later tick must never have its target overwritten by an
            // earlier analyze still unwinding.
            await AnalyzePeakAsync(target).ConfigureAwait(false);
        }
        finally
        {
            _autoAnalyzeTickActive = false;
        }
    }


    private void OnStopped(object? sender, RadioStoppedEventArgs e)
    {
        StatusText = e.Reason;
        lock (_gate)
        {
            _isSweeping = false;
            OnPropertyChanged(nameof(IsSweeping));
        }

        // M6 unplug handling: a lost device raises the banner as well as the status line, so
        // the operator sees it even if the status text scrolls away. The UnplugMonitor (or a
        // replug-driven Start) clears it once the radio answers again.
        if (e.DeviceLost)
        {
            DeviceLostBanner = true;
            BannerText = "The radio stopped streaming: it may have been unplugged. Plug it back in to reconnect.";
            DeviceText = "No radio: unplugged";
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

        // Temporal floor (each bin's own history) combined with a spatial floor (the bins around
        // it), so steady carriers such as FM stations are found as well as intermittent signals.
        IReadOnlyList<double> floor = SpatialFloor.Combine(
            _peakFloor.Update(frame),
            SpatialFloor.Compute(frame, SpatialFloor.HalfWindowBins(frame.BinWidthHz)));
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
                string guess = BestGuessFor(track, band, row);
                double confidence = GuessConfidence(track, guess);
                if (track.Id == _lastAnalyzedPeakId)
                {
                    row.Identifications = _lastIdentifications;
                }
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

    /// <summary>
    /// Called when the operator presses L on a muted peak. The status line explains why
    /// locate did not start; the setter is private so only the view model writes it.
    /// </summary>
    public void ReportMutedLocateAttempt()
    {
        ExportStatusText = "That peak is muted. Clear Mute in the peak table to locate it.";
    }

    /// <summary>
    /// How many tracked peaks the operator has muted. Shown next to the peak table so a
    /// greyed row is never mistaken for a disappeared emitter.
    /// </summary>
    public int MutedPeakCount
    {
        get
        {
            IReadOnlyList<PeakRow> rows = PeakRows;
            int count = 0;
            foreach (PeakRow row in rows)
            {
                if (row.IsMuted)
                {
                    count++;
                }
            }

            return count;
        }
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
        OnPropertyChanged(nameof(MutedPeakCount));
        RaiseRedraw();
    }

    /// <summary>
    /// M2's "best guess" is the band plan plus the measured shape: M3's classifier refines it.
    /// The band name is the honest answer for now, qualified by the bandwidth class.
    /// </summary>
    private static string BestGuessFor(TrackedPeak track, string band, PeakRow? peak = null)
    {
        // M4: when a decoder identified this peak, the most confident identification is the best guess.
        if (peak is not null && peak.Identifications is { Count: > 0 } identIds)
        {
            foreach (DeviceIdentification ident in identIds)
            {
                if (ident.Protocol != DeviceIdentification.ProtocolClassification && ident.Protocol != PeakEnricher.ProtocolPeak)
                {
                    return ident.ShortText;
                }
            }
        }

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

    /// <summary>
    /// M4 detail-pane block: every identification from the last analyze, most confident first.
    /// Empty when no analyze has run yet (the pane then keeps its M3 text).
    /// </summary>
    private string IdentificationPaneText(PeakRow? row)
    {
        IReadOnlyList<DeviceIdentification> ids;
        lock (_gate)
        {
            ids = row is not null && row.Id == _lastAnalyzedPeakId ? _lastIdentifications : [];
        }

        if (ids.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Identifications:");
        foreach (DeviceIdentification id in ids)
        {
            sb.AppendLine("  " + id.Describe());
        }

        return sb.ToString();
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

        // M6 status bar: sample rate, sweeps/s, dropped blocks and process CPU. The serial is
        // carried by DeviceText, which Start() and the unplug watcher set together with it.
        InitCpuTrackerLocked();
        // QA defect 2: the status bar read "CPU 153929%". The tracker already divides CPU time by
        // wall time, but it must also divide by the processor count, and it must not be re-armed
        // every refresh (that made the window one paint long and amplified the cost of this call).
        double cpu = _cpuTracker!.ReadPercent() / Environment.ProcessorCount;
        // BinWidth already appends " MHz"/" kHz", so the unit text is just "sps"; adding "s"
        // here produced "20.00 MHzsps" on air (QA status-bar polish).
        string rate = stats.SamplesPerSecond > 0
            ? FrequencyFormat.BinWidth(stats.SamplesPerSecond) + "sps"
            : "idle";
        _statsText = string.Format(
            CultureInfo.InvariantCulture,
            "{0} | sweeps/s {1} | dropped {2} | CPU {3:F0}%",
            rate,
            sweeps,
            stats.DroppedBlocks,
            cpu);
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
        MarkSettingsDirty();
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

    /// <summary>Ask the operator. With nobody listening the answer is no: an RF switch must never
    /// turn on unconfirmed (hard rule 2).</summary>
    private bool Confirm(string message) => NeedsConfirmation?.Invoke(message) ?? false;


    // ---------------------------------------------------------------------------
    // M6: per-user settings, CSV/JSON export, unplug banner + reconnect, status-bar CPU.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Load %LOCALAPPDATA%\RfScout\settings.json and apply it. A missing or corrupt file
    /// silently keeps the built-in defaults (AppSettingsStore falls back for us). Idempotent;
    /// tests point SettingsDirectory at a temp folder.
    /// </summary>
    public void LoadSettings()
    {
        AppSettings s = AppSettingsStore.Load(SettingsDirectory);
        lock (_gate)
        {
            _settingsLoaded = true;
            _region = s.Region;
            _colorMapName = s.ColorMap;
            _startMHzText = s.StartMHz.ToString("F3", CultureInfo.InvariantCulture);
            _stopMHzText = s.StopMHz.ToString("F3", CultureInfo.InvariantCulture);
            _binWidthHz = s.BinWidthHz;
            _lnaDb = RadioGains.ClampLna(s.LnaGainDb);
            _vgaDb = RadioGains.ClampVga(s.VgaGainDb);
            _peakThresholdDb = s.PeakThresholdDb;
            _peakDetector = new PeakDetector(_peakThresholdDb);
            _dbRange.Set(s.DbMin, s.DbMax);
            _dbMinText = _dbRange.MinDb.ToString("F0", CultureInfo.InvariantCulture);
            _dbMaxText = _dbRange.MaxDb.ToString("F0", CultureInfo.InvariantCulture);
            _settingsDirty = false;
        }

        OnPropertyChanged(nameof(StartMHzText));
        OnPropertyChanged(nameof(StopMHzText));
        OnPropertyChanged(nameof(BinWidthHz));
        OnPropertyChanged(nameof(LnaDb));
        OnPropertyChanged(nameof(VgaDb));
        OnPropertyChanged(nameof(PeakThresholdDb));
        OnPropertyChanged(nameof(DbMinText));
        OnPropertyChanged(nameof(DbMaxText));
        OnPropertyChanged(nameof(Region));
        OnPropertyChanged(nameof(ColorMapName));
        OnPropertyChanged(nameof(ColourRange));
        RaiseRedraw();
    }

    /// <summary>Capture the current UI state and write it to the settings file.</summary>
    public bool SaveSettingsNow()
    {
        AppSettings s = CurrentSettings();
        return AppSettingsStore.Save(s, SettingsDirectory);
    }

    /// <summary>The settings object the current state describes (public for tests).</summary>
    public AppSettings CurrentSettings()
    {
        lock (_gate)
        {
            double.TryParse(_startMHzText, NumberStyles.Float, CultureInfo.InvariantCulture, out double start);
            double.TryParse(_stopMHzText, NumberStyles.Float, CultureInfo.InvariantCulture, out double stop);
            return new AppSettings
            {
                StartMHz = start,
                StopMHz = stop,
                BinWidthHz = _binWidthHz,
                LnaGainDb = _lnaDb,
                VgaGainDb = _vgaDb,
                PeakThresholdDb = _peakThresholdDb,
                ColorMap = _colorMapName,
                Region = _region,
                DbMin = _dbRange.MinDb,
                DbMax = _dbRange.MaxDb,
            };
        }
    }

    private void MarkSettingsDirty()
    {
        lock (_gate)
        {
            if (!_settingsLoaded)
            {
                // Do not record a save for a change made before the file was read: that would
                // overwrite the stored span with the built-in defaults.
                return;
            }

            _settingsDirty = true;
        }
    }

    /// <summary>Save when something changed since the last write. Cheap; call from a timer.</summary>
    public bool SaveSettingsIfDirty()
    {
        lock (_gate)
        {
            if (!_settingsDirty)
            {
                return false;
            }
        }

        if (SaveSettingsNow())
        {
            lock (_gate)
            {
                _settingsDirty = false;
            }

            return true;
        }

        return false;
    }

    /// <summary>Band-plan region from the settings file ("US" by default).</summary>
    public string Region
    {
        get
        {
            lock (_gate)
            {
                return _region;
            }
        }

        set
        {
            string trimmed = value?.Trim() ?? string.Empty;
            if (trimmed.Length == 0 || trimmed.Length > 32)
            {
                return;
            }

            if (!SetField(ref _region, trimmed))
            {
                return;
            }

            MarkSettingsDirty();
        }
    }

    /// <summary>Waterfall colour map name from the settings file ("Viridis" or "Grayscale").</summary>
    public string ColorMapName
    {
        get
        {
            lock (_gate)
            {
                return _colorMapName;
            }
        }

        set
        {
            string normalized = ColorMaps.Normalize(value);
            if (!SetField(ref _colorMapName, normalized))
            {
                return;
            }

            MarkSettingsDirty();
            RaiseRedraw();
        }
    }

    /// <summary>The colour map the waterfall draws with, resolved from the settings name.</summary>
    public ColorMapKind ColorMapKind => ColorMaps.KindFor(ColorMapName);

    /// <summary>Settings folder override for tests; null means %LOCALAPPDATA%\RfScout.</summary>
    public string? SettingsDirectory
    {
        get
        {
            lock (_gate)
            {
                return _settingsDirectory;
            }
        }

        set
        {
            lock (_gate)
            {
                _settingsDirectory = value;
            }
        }
    }

    /// <summary>
    /// Export the current peak table (with identifications) as CSV and JSON into
    /// <paramref name="directory"/>. Returns the two file paths, or null when there are no
    /// peaks to export. CSV cells are formula-guarded (CsvBuilder) because decoder text
    /// arrives over the air.
    /// </summary>
    public (string CsvPath, string JsonPath)? ExportPeaks(string directory)
    {
        List<PeakExportRow> rows = ExportRows();
        if (rows.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(directory);
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string csvPath = Path.Combine(directory, "rfscout-peaks-" + stamp + ".csv");
        string jsonPath = Path.Combine(directory, "rfscout-peaks-" + stamp + ".json");

        string csv = PeakExport.PeaksToCsv(rows) + "\r\n# identifications\r\n" + PeakExport.IdentificationsToCsv(rows);
        File.WriteAllText(csvPath, csv);
        File.WriteAllText(jsonPath, PeakExport.ToJson(rows));
        return (csvPath, jsonPath);
    }

    /// <summary>Last export result for the status line ("Nothing to export yet", paths, errors).</summary>
    public string ExportStatusText
    {
        get
        {
            lock (_gate)
            {
                return _exportStatusText;
            }
        }

        private set
        {
            if (!SetField(ref _exportStatusText, value))
            {
                return;
            }
        }
    }

    /// <summary>Export the peak table to CSV in the export folder (default: settings folder).</summary>
    public void ExportCsv() => RunExport("csv");

    /// <summary>Export the peak table and identifications to JSON in the export folder.</summary>
    public void ExportJson() => RunExport("json");

    private void RunExport(string kind)
    {
        List<PeakExportRow> rows = ExportRows();
        if (rows.Count == 0)
        {
            ExportStatusText = "Nothing to export yet: no peaks tracked.";
            return;
        }

        try
        {
            string dir = SettingsDirectory ?? AppSettingsStore.DefaultDirectory;
            Directory.CreateDirectory(dir);
            string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string baseName = Path.Combine(dir, "rfscout-peaks-" + stamp);

            if (kind == "csv")
            {
                string csv = PeakExport.PeaksToCsv(rows) + "\r\n# identifications\r\n" + PeakExport.IdentificationsToCsv(rows);
                File.WriteAllText(baseName + ".csv", csv);
                ExportStatusText = "Exported " + rows.Count + " peaks to " + baseName + ".csv";
            }
            else
            {
                File.WriteAllText(baseName + ".json", PeakExport.ToJson(rows));
                ExportStatusText = "Exported " + rows.Count + " peaks to " + baseName + ".json";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ExportStatusText = "Export failed: " + ex.Message;
        }
    }

    /// <summary>The visible peak rows as plain export records (public for tests).</summary>
    public List<PeakExportRow> ExportRows()
    {
        IReadOnlyList<PeakRow> rows = PeakRows;
        var result = new List<PeakExportRow>(rows.Count);
        foreach (PeakRow row in rows)
        {
            result.Add(new PeakExportRow(
                row.Id,
                row.CenterHz,
                row.PowerDb,
                row.SnrDb,
                row.BandwidthHz,
                row.DutyCycle,
                row.Activity,
                row.Band,
                row.BestGuess,
                row.Confidence,
                row.LastSeenUtc,
                row.Identifications,
                row.IsMuted));
        }

        return result;
    }

    /// <summary>True while the "radio unplugged" banner should be shown.</summary>
    public bool DeviceLostBanner
    {
        get
        {
            lock (_gate)
            {
                return _deviceLostBanner;
            }
        }

        private set
        {
            if (!SetField(ref _deviceLostBanner, value))
            {
                return;
            }
        }
    }

    /// <summary>Banner text shown while the radio is unplugged.</summary>
    public string BannerText
    {
        get
        {
            lock (_gate)
            {
                return _bannerText;
            }
        }

        private set
        {
            if (!SetField(ref _bannerText, value))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Start watching the declared hackrf device (ids from devflow.json via DevFlow's
    /// DEVFLOW_USB_DEVICES convention, never hard-coded here). On unplug: stop cleanly and
    /// show the banner; on replug: re-open and resume the sweep that was running.
    /// </summary>
    public void StartUnplugWatch()
    {
        (ushort Vid, ushort Pid)? id = DeclaredHackRfId();
        if (id is null)
        {
            return;
        }

        UnplugMonitor monitor = new(
            (vid, pid) => UsbEnumerator.ListDevices().Any(d => d.Vid == vid && d.Pid == pid),
            id.Value.Vid,
            id.Value.Pid);
        monitor.DeviceLost += OnMonitorDeviceLost;
        monitor.DeviceReturned += OnMonitorDeviceReturned;
        lock (_gate)
        {
            _unplugMonitor?.Dispose();
            _unplugMonitor = monitor;
        }

        monitor.Start();
    }

    /// <summary>Stop the unplug watcher (called from Dispose).</summary>
    public void StopUnplugWatch()
    {
        UnplugMonitor? monitor;
        lock (_gate)
        {
            monitor = _unplugMonitor;
            _unplugMonitor = null;
        }

        monitor?.Dispose();
    }

    /// <summary>
    /// The HackRF vid:pid from the environment DevFlow gives hardware tests
    /// (DEVFLOW_USB_DEVICES, e.g. "hackrf=1d50:6089"). Null when nothing is declared, which
    /// keeps the watcher off rather than guessing an id.
    /// </summary>
    internal static (ushort Vid, ushort Pid)? DeclaredHackRfId()
    {
        foreach ((string name, UsbDeviceId id) in Usb.ParseDeviceList(Environment.GetEnvironmentVariable("DEVFLOW_USB_DEVICES")))
        {
            if (name.Equals("hackrf", StringComparison.OrdinalIgnoreCase))
            {
                return (id.Vid, id.Pid);
            }
        }

        return null;
    }

    private void OnMonitorDeviceLost(object? sender, EventArgs e)
    {
        bool wasSweeping;
        lock (_gate)
        {
            wasSweeping = _isSweeping;
        }

        if (wasSweeping)
        {
            Stop();
        }

        DeviceLostBanner = true;
        BannerText = "The HackRF was unplugged. Sweeping stopped; plug it back in to reconnect.";
        DeviceText = "No radio: unplugged";
    }

    private void OnMonitorDeviceReturned(object? sender, EventArgs e)
    {
        DeviceLostBanner = false;
        BannerText = string.Empty;
        StatusText = "HackRF reconnected.";
        lock (_gate)
        {
            _unplugMonitor?.NotifyReconnected();
        }

        Start();
    }

    /// <summary>Percent of one core this process is using, from cumulative CPU time deltas.</summary>
    public double CpuPercent
    {
        get
        {
            lock (_gate)
            {
                return _cpuTracker?.ReadPercent() ?? 0.0;
            }
        }
    }

    private void InitCpuTrackerLocked()
    {
        // Created once per view model. Re-creating it per status refresh reset the baseline every
        // time, so the percent was measured over a near-zero window and read absurdly high.
        _cpuTracker ??= new ProcessCpuTracker();
    }

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


using System.IO;
using RfScout.Core.Spectrum;

namespace RfScout.Core.Recording;

/// <summary>
/// Plays a recorded .cs8 + .sigmf-meta pair back through <see cref="IRadioSource"/>, so the
/// analyze and (later) decode paths can be exercised on known captures instead of live air.
///
/// The reader is defensive: a truncated .cs8 plays the whole samples it holds, a garbage or
/// missing sidecar falls back to the file name (rfscout_&lt;stamp&gt;_&lt;MHz&gt;MHz) for the
/// centre frequency, and a file over <see cref="MaxPlaybackBytes"/> is refused rather than
/// swallowed. Nothing here trusts the disk.
/// </summary>
public sealed class FileRadioSource : IRadioSource
{
    /// <summary>A .cs8 larger than this is refused (the recorder's cap is 2 GiB; playback is for analysis snippets).</summary>
    public const long MaxPlaybackBytes = 256L * 1024 * 1024;

    private readonly string _cs8Path;
    private readonly SigMfMeta _meta;
    private readonly byte[] _iq;
    private readonly Lock _gate = new();
    private bool _running;
    private IqCapture? _lastCapture;
    private int _lnaDb = 24;
    private int _vgaDb = 34;

    public FileRadioSource(string cs8Path)
    {
        ArgumentNullException.ThrowIfNull(cs8Path);
        if (!File.Exists(cs8Path))
        {
            throw new FileNotFoundException("capture file not found", cs8Path);
        }

        long length = new FileInfo(cs8Path).Length;
        if (length > MaxPlaybackBytes)
        {
            throw new InvalidDataException($"capture is {length} bytes, over the {MaxPlaybackBytes} playback limit.");
        }

        _cs8Path = cs8Path;
        _meta = ReadMeta(Path.ChangeExtension(cs8Path, ".sigmf-meta"));
        _iq = File.ReadAllBytes(cs8Path);
        if (_iq.Length % 2 != 0)
        {
            // A truncated write that lands on an odd byte loses the dangling half-sample.
            _iq = _iq[..(_iq.Length - 1)];
        }
    }

    /// <summary>Centre frequency of the recording in Hz.</summary>
    public long CenterHz => (long)Math.Round(_meta.Core);

    /// <summary>Sample rate of the recording in Hz.</summary>
    public int SampleRateHz => (int)Math.Round(_meta.SampleRate);

    /// <summary>Samples the file really holds (may be fewer than the meta claims when truncated).</summary>
    public int SampleCount => _iq.Length / 2;

    /// <summary>The parsed sidecar, or a synthesised one when the sidecar was garbage.</summary>
    public SigMfMeta Meta => _meta;

    /// <summary>The path being played.</summary>
    public string Path2 => _cs8Path;

    public event EventHandler<SpectrumFrameEventArgs>? SpectrumAvailable;

    public event EventHandler<IqCaptureEventArgs>? CaptureAvailable;

    public event EventHandler<RadioStoppedEventArgs>? Stopped;

    public RadioDeviceInfo? DeviceInfo => new(
        SerialNumber: "REPLAY000000000000000000000",
        BoardName: "File replay (" + System.IO.Path.GetFileName(_cs8Path) + ")",
        FirmwareVersion: "n/a",
        UsbApiVersion: 0x0108,
        LibraryVersion: "none");

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public IqCapture? LastCapture
    {
        get
        {
            lock (_gate)
            {
                return _lastCapture;
            }
        }
    }

    public RadioStats ReadStats() => new(0, 0, 0, SampleRateHz);

    /// <summary>
    /// Sweep mode on a recording replays the capture as one frame: the file holds a narrowband
    /// window, so the "spectrum" is the capture's own FFT folded into the plan's grid is M4
    /// work; for M3 playback the useful mode is <see cref="StartCapture"/>, and a sweep simply
    /// raises Stopped so callers fall back honestly rather than pretending.
    /// </summary>
    public void StartSweep(SweepPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Enter();
        try
        {
            Stopped?.Invoke(this, new RadioStoppedEventArgs("file playback supports capture replay, not sweep", deviceLost: false));
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Replay the stored IQ as one capture. The requested centre/rate are validated against the
    /// recording: asking for a different tune than the file holds is an error, not a silent lie.
    /// </summary>
    public void StartCapture(long centerHz, int sampleRateHz, TimeSpan duration)
    {
        Enter();
        try
        {
            if (centerHz != CenterHz)
            {
                throw new InvalidOperationException(
                    $"recording is centred at {CenterHz} Hz, not {centerHz} Hz.");
            }

            if (sampleRateHz != SampleRateHz)
            {
                throw new InvalidOperationException(
                    $"recording is at {SampleRateHz} Hz/s, not {sampleRateHz} Hz/s.");
            }

            int wanted = (int)Math.Clamp(duration.TotalSeconds * sampleRateHz, 0, SampleCount);
            int take = wanted <= 0 ? SampleCount : Math.Min(wanted, SampleCount);
            IqCapture capture;
            lock (_gate)
            {
                capture = new IqCapture(
                    CenterHz,
                    SampleRateHz,
                    _lnaDb,
                    _vgaDb,
                    RfAmpEnabled: false,
                    AntennaPower: false,
                    ParseStartUtc(_meta.CaptureStart),
                    _iq.AsMemory(0, take * 2));
                _lastCapture = capture;
            }

            CaptureAvailable?.Invoke(this, new IqCaptureEventArgs(capture));
        }
        finally
        {
            Exit();
        }
    }

    public void SetGains(int lnaGainDb, int vgaGainDb)
    {
        lock (_gate)
        {
            _lnaDb = lnaGainDb;
            _vgaDb = vgaGainDb;
        }
    }

    public void SetRfAmplifier(bool enabled)
    {
    }

    public void SetAntennaPower(bool enabled)
    {
    }

    public void StopStream() => Exit();

    public void Dispose() => Exit();

    private void Enter()
    {
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException("a replay is already running");
            }

            _running = true;
        }
    }

    private void Exit()
    {
        lock (_gate)
        {
            _running = false;
        }
    }

    /// <summary>
    /// Read the sidecar, falling back to a synthesised one when it is missing or garbage. The
    /// fallback centre comes from the file name (rfscout_&lt;stamp&gt;_&lt;value&gt;MHz), the
    /// rate from a 2 Msps default; a file that yields neither is refused.
    /// </summary>
    private static SigMfMeta ReadMeta(string metaPath)
    {
        if (File.Exists(metaPath))
        {
            try
            {
                return SigMfMeta.Parse(File.ReadAllText(metaPath));
            }
            catch (InvalidDataException)
            {
                // fall through to the name-based fallback
            }
            catch (IOException)
            {
            }
        }

        string name = System.IO.Path.GetFileNameWithoutExtension(metaPath);
        string[] parts = name.Split('_');
        if (parts.Length >= 3 &&
            parts[^1].EndsWith("MHz", StringComparison.Ordinal) &&
            double.TryParse(
                parts[^1][..^3],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double mhz))
        {
            return SigMfMeta.Parse(
                System.Text.Json.JsonSerializer.Serialize(new SigMfMeta
                {
                    Core = mhz * 1e6,
                    SampleRate = 2_000_000,
                    CaptureStart = "1970-01-01T00:00:00.000Z",
                    Captures = [new CaptureRange { Core = 0, Length = 0 }],
                }));
        }

        throw new InvalidDataException($"cannot recover capture parameters from {metaPath}");
    }

    private static DateTimeOffset ParseStartUtc(string text) =>
        DateTimeOffset.TryParse(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset utc)
            ? utc
            : DateTimeOffset.UnixEpoch;
}

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RfScout.Core;
using RfScout.Core.Spectrum;
using RfScout.Core.Streaming;

namespace RfScout.Services.HackRf;

/// <summary>
/// The HackRF One behind <see cref="IRadioSource"/>: open it, sweep it, or tune it and capture a
/// narrowband IQ window. Receive only, one device, and never both streams at once.
///
/// Threading follows the brief:
///  * libhackrf calls <see cref="OnSampleBlock"/> on its own transfer thread. That callback only
///    copies bytes into <see cref="ByteRingBuffer"/> and returns - no parsing, no allocation, no
///    UI call - so the transfer queue never starves.
///  * one worker thread drains the ring, frames sweep blocks, runs the FFT and publishes frames.
///    It also stops a finished capture: hackrf_stop_rx must never run inside the callback,
///    because libhackrf holds its transfer lock there and stop_rx waits on that same lock.
///  * frames reach the UI on the worker thread; the view model marshals them onto the dispatcher.
///    Nothing in this class references WPF.
///
/// Sweep and capture are mutually exclusive: <see cref="StartSweep"/> and <see cref="StartCapture"/>
/// claim the device with an interlocked mode switch and refuse a second stream rather than
/// silently sharing it.
/// </summary>
public sealed unsafe class HackRfDevice : IRadioSource
{
    /// <summary>Lowest capture sample rate the HackRF's baseband filter chain supports cleanly.</summary>
    public const int MinCaptureSampleRateHz = 2_000_000;

    /// <summary>Highest sample rate the radio produces, and the rate sweep mode always uses.</summary>
    public const int MaxSampleRateHz = SweepMath.SampleRateHz;

    /// <summary>Largest capture held in memory: 256 MiB, about 6.7 s at 20 Msps.</summary>
    public const int MaxCaptureBytes = 1 << 28;

    /// <summary>After starting a stream, wait this long before trusting <c>hackrf_is_streaming</c>.</summary>
    private const long StreamGraceMs = 750;

    /// <summary>How often an idle DSP worker asks libhackrf whether the stream is still alive.</summary>
    private const long StreamCheckIntervalMs = 250;

    /// <summary>How many times to look for the radio before reporting it missing, and how long
    /// to wait between looks. Together about a second and a half, which is how long a HackRF
    /// takes to come back after closing a stream.</summary>
    private const int MaxOpenAttempts = 6;
    private const int OpenRetryPauseMs = 250;

    private const long ModeIdle = 0;
    private const long ModeSweep = 1;
    private const long ModeCapture = 2;
    private const long ModeLocate = 3;

    /// <summary>
    /// libhackrf hands back the context pointer passed to <c>hackrf_start_rx</c>, which is a
    /// pinned GCHandle to this instance; the device pointer is registered too as a fallback. The
    /// table exists because the callback is static and must find the instance without a managed
    /// this pointer, and because it keeps the instance alive while a transfer can still fire.
    /// </summary>
    private static readonly ConcurrentDictionary<IntPtr, HackRfDevice> Streams = new();

    private readonly HackRfHandle _handle;
    private readonly ByteRingBuffer _ring;
    private readonly SweepBlockParser _parser = new();
    private readonly Thread _worker;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ManualResetEventSlim _idle = new(true);
    private readonly Lock _gate = new();

    // Held by the DSP worker around each read-and-process step, and by ResetStreamState, so a
    // reset never lands in the middle of a ring read or a parser feed.
    private readonly Lock _drainGate = new();

    // Serialises stopping, which the API caller, the DSP worker (finished capture, lost stream)
    // and Dispose can all do.
    private readonly Lock _streamGate = new();

    private SpectrumAssembler? _assembler;
    private CaptureWindow? _capture;
    private LocateWindow? _locate;
    private SweepPlan? _plan;
    private long _mode = ModeIdle;
    private long _tunedHz;
    private long _blocks;
    private long _sweeps;
    private long _samples;
    private int _sampleRateHz = SweepMath.SampleRateHz;
    private int _lnaDb = RadioGains.DefaultLnaDb;
    private int _vgaDb = RadioGains.DefaultVgaDb;
    private bool _ampOn;
    private bool _antennaPowerOn;
    private long _rateWindowStartMs;
    private long _rateWindowSweeps;
    private long _rateWindowSamples;
    private double _sweepsPerSecond;
    private double _samplesPerSecond;
    // Ticks, not DateTimeOffset: Interlocked.Exchange on a long is atomic everywhere, whereas a
    // 16-byte struct field would be a torn read on x86.
    private long _lastSweepUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
    private bool _disposed;
    private int _captureDone;
    private int _locateDone;
    private long _locateActive;
    private long _locateRestartArmed;
    private int _streamLive;
    private long _streamStartedMs;
    private long _lastStreamCheckMs;

    /// <summary>The GCHandle pinning this instance for the running stream, or IntPtr.Zero.</summary>
    private IntPtr _streamContext;

    /// <summary>
    /// Open the first HackRF present, or a specific one by serial. Reads the board id, firmware
    /// version and USB API version, and refuses firmware older than the 1.02 API sweep mode needs.
    /// </summary>
    /// <exception cref="HackRfException">The radio is missing, busy, or its firmware is too old.</exception>
    public HackRfDevice(string? serialNumber = null)
    {
        LibHackRf.Initialize();

        // Sized from the library's own transfer geometry, so it always holds more than the
        // transfers libhackrf keeps in flight and a slow DSP thread costs whole sweeps rather
        // than partial blocks the framer would have to discard. Both calls need only
        // hackrf_init, so they run before the try and _ring is definitely assigned on every path.
        int transferBytes = Math.Max(262_144, NativeMethods.hackrf_get_transfer_buffer_size());
        int queueDepth = Math.Max(2, NativeMethods.hackrf_get_transfer_queue_depth());
        _ring = new ByteRingBuffer(transferBytes * queueDepth * 2);

        HackRfHandle? handle = null;
        try
        {
            string wanted = (serialNumber ?? string.Empty).Trim();

            // A radio that was streaming a moment ago can take a few hundred milliseconds to come
            // back: closing a stream resets the USB side of the firmware, and a freshly
            // initialised libusb enumerates the bus before the device has re-appeared. That is not
            // a missing radio, and calling it one is how stop-then-start or unplug-then-replug
            // fails in front of the user, so look for it several times over about a second and a
            // half before giving up. The last attempt's error is the one reported.
            string serial = wanted;
            string boardFromList = string.Empty;
            (string Serial, string Board, bool SharesBus, HackRfHandle Handle) opened = default;
            HackRfException? lastError = null;

            for (int attempt = 0; ; attempt++)
            {
                bool more = true;
                try
                {
                    opened = TryOpen(wanted);
                    break;
                }
                catch (HackRfException ex) when (ex.DeviceMissing)
                {
                    lastError = ex;
                    more = attempt < MaxOpenAttempts - 1;
                }

                if (!more)
                {
                    throw lastError!;
                }

                Thread.Sleep(OpenRetryPauseMs);
            }

            serial = opened.Serial;
            boardFromList = opened.Board;
            BusSharesUsb = opened.SharesBus;
            handle = opened.Handle;
            _handle = handle;

            DeviceInfo = ReadDeviceInfo(boardFromList, serial);
            if (!DeviceInfo.SupportsSweep)
            {
                throw new HackRfException(
                    "hackrf_start_rx_sweep",
                    NativeMethods.ErrorUsbApiVersion,
                    $"firmware API {DeviceInfo.UsbApiVersionText} is older than 1.02, which sweep mode needs");
            }

            ConfigureGains();

            _worker = new Thread(DrainLoop)
            {
                Name = "RfScout DSP",
                IsBackground = true,
            };
            _worker.Start();
        }
        catch
        {
            // handle stays null when the failure happened before the open, so there is
            // nothing to close and only the library reference to release.
            handle?.Dispose();
            LibHackRf.Shutdown();
            throw;
        }
    }

    /// <summary>
    /// One look for the radio: enumerate, pick the serial (or the first HackRF), open it, and
    /// report what the list said. Throws <see cref="HackRfException"/> when it is not there, which
    /// the constructor retries.
    /// </summary>
    private static (string Serial, string Board, bool SharesBus, HackRfHandle Handle) TryOpen(string wanted)
    {
        string serial = wanted;
        string board = string.Empty;
        bool sharesBus;

        IntPtr list = NativeMethods.hackrf_device_list();
        try
        {
            NativeMethods.HackrfDeviceList devices = NativeMethods.ReadDeviceList(list);
            if (devices.DeviceCount == 0)
            {
                // An empty list is how libhackrf reports "no HackRF on the bus", which is exactly
                // what the open would fail with anyway. Say it here, where the list count is known.
                throw new HackRfException("hackrf_open", NativeMethods.ErrorNotFound, "HackRF not found");
            }

            int index = 0;
            if (wanted.Length > 0)
            {
                for (int i = 0; i < devices.DeviceCount; i++)
                {
                    if (string.Equals(devices.SerialNumber(i), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }
            }

            serial = devices.SerialNumber(index);
            board = BoardNameFor(devices.UsbBoardId(index));
            sharesBus = NativeMethods.hackrf_device_list_bus_sharing(list, index) == 1;
        }
        finally
        {
            if (list != IntPtr.Zero)
            {
                NativeMethods.hackrf_device_list_free(list);
            }
        }

        int result = wanted.Length == 0
            ? NativeMethods.hackrf_open(out IntPtr raw)
            : NativeMethods.hackrf_open_by_serial(wanted, out raw);
        NativeMethods.ThrowForError(result, "hackrf_open");
        return (serial, board, sharesBus, new HackRfHandle(raw));
    }

    public event EventHandler<SpectrumFrameEventArgs>? SpectrumAvailable;

    public event EventHandler<IqCaptureEventArgs>? CaptureAvailable;

    public event EventHandler<IqCaptureEventArgs>? LocateSampleAvailable;

    public event EventHandler<RadioStoppedEventArgs>? Stopped;

    /// <summary>What the radio reported about itself when it was opened.</summary>
    public RadioDeviceInfo DeviceInfo { get; }

    RadioDeviceInfo? IRadioSource.DeviceInfo => DeviceInfo;

    /// <summary>
    /// True when the HackRF shares its USB hub with other devices. Laptop hubs often also feed
    /// card readers, Bluetooth and other internal devices, and that is the usual cause of
    /// dropped blocks at 20 Msps - worth knowing before blaming the code.
    /// </summary>
    public bool BusSharesUsb { get; private set; }

    public bool IsRunning => Interlocked.Read(ref _mode) != ModeIdle;

    /// <summary>True while a narrowband capture is running.</summary>
    public bool IsCapturing => Interlocked.Read(ref _mode) == ModeCapture;

    /// <summary>True while a locate stream (repeating strength windows) is running.</summary>
    public bool IsLocating => Interlocked.Read(ref _mode) == ModeLocate;

    /// <summary>Plan of the running sweep, null when idle or capturing.</summary>
    public SweepPlan? ActivePlan
    {
        get
        {
            lock (_gate)
            {
                return _plan;
            }
        }
    }

    /// <summary>Current tuned centre in capture mode, or the sweep's first range start.</summary>
    public long TunedHz => Interlocked.Read(ref _tunedHz);

    /// <summary>Realised sample rate of the running stream.</summary>
    public int SampleRateHz => Volatile.Read(ref _sampleRateHz);

    /// <summary>Sweeps (published frames) per second over the last rate window.</summary>
    public double SweepsPerSecond => Volatile.Read(ref _sweepsPerSecond);

    /// <summary>Samples per second over the last rate window.</summary>
    public double SamplesPerSecond => Volatile.Read(ref _samplesPerSecond);

    /// <summary>When the last frame was published, for the "stream stalled" watchdog.</summary>
    public DateTimeOffset LastSweepUtc => new(Interlocked.Read(ref _lastSweepUtcTicks), TimeSpan.Zero);

    /// <summary>Bytes the ring could not hold: RF time lost because the DSP thread fell behind.</summary>
    public long DroppedBytes => _ring.DroppedBytes;

    /// <summary>Sweep blocks whose header was rejected, plus ring drops, for the status bar.</summary>
    public long DroppedBlocks => _parser.HeaderErrors + _ring.DroppedChunks;

    /// <summary>The capture that finished most recently, or null.</summary>
    public IqCapture? CurrentCapture
    {
        get
        {
            lock (_gate)
            {
                return _capture?.Completed;
            }
        }
    }

    /// <summary>
    /// Start a sweep. The radio always runs at its fixed 20 Msps in sweep mode; the bin width
    /// comes from the plan's FFT length and the firmware sweep list is programmed from its ranges.
    /// </summary>
    public void StartSweep(SweepPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfDisposed();
        if (!plan.IsValid)
        {
            throw new ArgumentException("Sweep plan is not valid.", nameof(plan));
        }

        EnterMode(ModeSweep);
        try
        {
            long startHz = long.MaxValue;
            long stopHz = 0;
            foreach (FrequencyRange range in plan.Ranges)
            {
                startHz = Math.Min(startHz, range.StartHz);
                stopHz = Math.Max(stopHz, range.StopHz);
            }

            SpectrumAssembler assembler = new(startHz, stopHz, plan.FftLength, plan.DisplayBinWidthHz);

            lock (_gate)
            {
                _assembler = assembler;
                _capture = null;
                _plan = plan;
            }

            Volatile.Write(ref _sampleRateHz, SweepMath.SampleRateHz);
            Interlocked.Exchange(ref _tunedHz, startHz);
            _idle.Reset();

            // The plan records the gains and RF switches the sweep was made with, so the radio
            // must actually be set to them, not left on whatever an earlier call configured.
            ApplyRadioSettings(plan.LnaGainDb, plan.VgaGainDb, plan.RfAmpEnabled, plan.AntennaPower);

            NativeMethods.ThrowForError(
                NativeMethods.hackrf_set_sample_rate(_handle.DangerousGetHandle(), SweepMath.SampleRateHz),
                "hackrf_set_sample_rate");

            ProgramSweep(plan);
            ResetStreamState();
            StartStream(sweepMode: true);
        }
        catch
        {
            Interlocked.Exchange(ref _mode, ModeIdle);
            _idle.Set();
            throw;
        }
    }

    /// <summary>
    /// Tune to <paramref name="centerHz"/> and capture <paramref name="duration"/> of IQ at
    /// <paramref name="sampleRateHz"/>, then stop the stream. The baseband filter is set to the
    /// widest valid width at or below 0.75 of the rate, which is also libhackrf's own cap.
    /// </summary>
    public void StartCapture(long centerHz, int sampleRateHz, TimeSpan duration)
    {
        ThrowIfDisposed();
        if (centerHz < SweepPreset.MinTunableHz || centerHz > SweepPreset.MaxTunableHz)
        {
            throw new ArgumentOutOfRangeException(nameof(centerHz), centerHz, "Frequency is outside the HackRF's range.");
        }

        if (sampleRateHz is < MinCaptureSampleRateHz or > MaxSampleRateHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRateHz),
                sampleRateHz,
                $"Capture rate must be {MinCaptureSampleRateHz}-{MaxSampleRateHz} Hz.");
        }

        if (duration <= TimeSpan.Zero || duration.TotalSeconds > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A capture must be between 0 and 30 seconds.");
        }

        if (Math.Ceiling(duration.TotalSeconds * sampleRateHz) * 2 > MaxCaptureBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                $"At {sampleRateHz / 1e6:0.###} Msps a capture can be at most {MaxCaptureSeconds(sampleRateHz):0.##} s ({MaxCaptureBytes >> 20} MiB).");
        }

        EnterMode(ModeCapture);
        try
        {
            int samples = (int)Math.Ceiling(duration.TotalSeconds * sampleRateHz);
            var window = new CaptureWindow(samples, centerHz, sampleRateHz, _lnaDb, _vgaDb, _ampOn, _antennaPowerOn);

            lock (_gate)
            {
                _capture = window;
                _assembler = null;
                _plan = null;
            }

            Volatile.Write(ref _sampleRateHz, sampleRateHz);
            Interlocked.Exchange(ref _tunedHz, centerHz);
            _idle.Reset();

            IntPtr handle = _handle.DangerousGetHandle();
            NativeMethods.ThrowForError(NativeMethods.hackrf_set_freq(handle, (ulong)centerHz), "hackrf_set_freq");
            NativeMethods.ThrowForError(
                NativeMethods.hackrf_set_sample_rate(handle, sampleRateHz),
                "hackrf_set_sample_rate");

            uint filterHz = NativeMethods.hackrf_compute_baseband_filter_bw_round_down_lt(
                (uint)(sampleRateHz * 3 / 4));
            if (filterHz != 0)
            {
                NativeMethods.ThrowForError(
                    NativeMethods.hackrf_set_baseband_filter_bandwidth(handle, filterHz),
                    "hackrf_set_baseband_filter_bandwidth");
            }

            ResetStreamState();
            StartStream(sweepMode: false);
        }
        catch
        {
            Interlocked.Exchange(ref _mode, ModeIdle);
            _idle.Set();
            throw;
        }
    }

    /// <summary>
    /// M5 locate: tune to <paramref name="centerHz"/> and stream continuously, raising
    /// <see cref="LocateSampleAvailable"/> with a fresh <paramref name="windowSamples"/>-sample
    /// IQ window about every <paramref name="windowSamples"/>/rate seconds. The stream stops
    /// between windows (libhackrf cannot deliver a bounded window in sweep mode), which makes
    /// each reading a clean, independent measurement of the same frequency. Runs until
    /// <see cref="StopStream"/>.
    /// </summary>
    public void StartLocate(long centerHz, int sampleRateHz, int windowSamples)
    {
        ThrowIfDisposed();
        if (centerHz < SweepPreset.MinTunableHz || centerHz > SweepPreset.MaxTunableHz)
        {
            throw new ArgumentOutOfRangeException(nameof(centerHz), centerHz, "Frequency is outside the HackRF's range.");
        }

        if (sampleRateHz is < MinCaptureSampleRateHz or > MaxSampleRateHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRateHz),
                sampleRateHz,
                $"Locate rate must be {MinCaptureSampleRateHz}-{MaxSampleRateHz} Hz.");
        }

        if (windowSamples is < 2048 or > (MaxCaptureBytes / 2))
        {
            throw new ArgumentOutOfRangeException(nameof(windowSamples), windowSamples, "Locate window size is out of range.");
        }

        EnterMode(ModeLocate);
        try
        {
            lock (_gate)
            {
                _locate = new LocateWindow(windowSamples, centerHz, sampleRateHz, _lnaDb, _vgaDb, _ampOn, _antennaPowerOn);
                _capture = null;
                _assembler = null;
                _plan = null;
            }

            Volatile.Write(ref _sampleRateHz, sampleRateHz);
            Interlocked.Exchange(ref _tunedHz, centerHz);
            Interlocked.Exchange(ref _locateActive, 1);
            _idle.Reset();
            StartLocateWindow();
        }
        catch
        {
            Interlocked.Exchange(ref _locateActive, 0);
            Interlocked.Exchange(ref _mode, ModeIdle);
            _idle.Set();
            throw;
        }
    }

    /// <summary>Arm the native stream for the next locate window.</summary>
    private void StartLocateWindow()
    {
        IntPtr handle = _handle.DangerousGetHandle();
        NativeMethods.ThrowForError(NativeMethods.hackrf_set_freq(handle, (ulong)TunedHz), "hackrf_set_freq");
        NativeMethods.ThrowForError(
            NativeMethods.hackrf_set_sample_rate(handle, SampleRateHz),
            "hackrf_set_sample_rate");

        uint filterHz = NativeMethods.hackrf_compute_baseband_filter_bw_round_down_lt(
            (uint)(SampleRateHz * 3 / 4));
        if (filterHz != 0)
        {
            NativeMethods.ThrowForError(
                NativeMethods.hackrf_set_baseband_filter_bandwidth(handle, filterHz),
                "hackrf_set_baseband_filter_bandwidth");
        }

        ResetStreamState();
        StartStream(sweepMode: false);
    }

    /// <summary>
    /// Wait for the running capture to fill its window. Returns the capture, or null on timeout.
    /// Call from a worker thread, never from the UI thread, or the window stops painting.
    /// </summary>
    public IqCapture? WaitForCapture(TimeSpan timeout) => _idle.Wait(timeout) ? CurrentCapture : null;

    /// <summary>Longest capture <see cref="StartCapture"/> accepts at a given rate.</summary>
    public static double MaxCaptureSeconds(int sampleRateHz) => MaxCaptureBytes / 2.0 / sampleRateHz;

    /// <summary>Stop the running stream without closing the device.</summary>
    public void StopStream()
    {
        if (_disposed)
        {
            return;
        }

        StopStreamCore();
    }

    /// <summary>
    /// Stop the native stream, release the callback registration and go idle. Runs during
    /// <see cref="Dispose"/> too (after the disposed flag is set), so a disposed device never
    /// leaves its GCHandle and buffers in <see cref="Streams"/>. Never call it from
    /// <see cref="OnSampleBlock"/>.
    /// </summary>
    private void StopStreamCore()
    {
        lock (_streamGate)
        {
            // Stop the native stream once: a finished capture is stopped by the worker, and
            // calling hackrf_stop_rx again on a stopped stream (from Dispose) is not safe.
            // The registration below is released either way.
            if (Interlocked.Read(ref _mode) != ModeIdle && !_handle.IsClosed)
            {
                _ = NativeMethods.hackrf_stop_rx(_handle.DangerousGetHandle());
            }

            Volatile.Write(ref _streamLive, 0);
            UnmapStream();
            Interlocked.Exchange(ref _mode, ModeIdle);
            Volatile.Write(ref _captureDone, 0);
            Volatile.Write(ref _locateDone, 0);
        }

        _idle.Set();

        if (Interlocked.Read(ref _locateActive) == 1 && Interlocked.Read(ref _locateRestartArmed) == 0)
        {
            // A locate session survives the stop between its windows; a deliberate StopStream
            // (or a device-lost teardown, which never re-arms) ends it and drops the window.
            Interlocked.Exchange(ref _locateActive, 0);
            lock (_gate)
            {
                _locate = null;
            }
        }
    }

    public void SetGains(int lnaGainDb, int vgaGainDb)
    {
        if (!RadioGains.IsValidLna(lnaGainDb))
        {
            throw new ArgumentOutOfRangeException(nameof(lnaGainDb), lnaGainDb, "LNA must be 0-40 dB in 8 dB steps.");
        }

        if (!RadioGains.IsValidVga(vgaGainDb))
        {
            throw new ArgumentOutOfRangeException(nameof(vgaGainDb), vgaGainDb, "VGA must be 0-62 dB in 2 dB steps.");
        }

        lock (_gate)
        {
            _lnaDb = lnaGainDb;
            _vgaDb = vgaGainDb;
            if (_disposed || _handle.IsClosed)
            {
                return;
            }

            ConfigureGains();
        }
    }

    public void SetRfAmplifier(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || _handle.IsClosed)
            {
                return;
            }

            NativeMethods.ThrowForError(
                NativeMethods.hackrf_set_amp_enable(_handle.DangerousGetHandle(), (byte)(enabled ? 1 : 0)),
                "hackrf_set_amp_enable");

            _ampOn = enabled;
        }
    }

    public void SetAntennaPower(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || _handle.IsClosed)
            {
                return;
            }

            NativeMethods.ThrowForError(
                NativeMethods.hackrf_set_antenna_enable(_handle.DangerousGetHandle(), (byte)(enabled ? 1 : 0)),
                "hackrf_set_antenna_enable");

            _antennaPowerOn = enabled;
        }
    }

    /// <summary>LNA and VGA in dB, for the detail pane's gain line.</summary>
    public (int LnaDb, int VgaDb, bool AmpOn, bool AntennaPower) Gains
    {
        get
        {
            lock (_gate)
            {
                return (_lnaDb, _vgaDb, _ampOn, _antennaPowerOn);
            }
        }
    }

    /// <summary>Counters for the status bar. No device round trip, so a UI timer can call it.</summary>
    public RadioStats ReadStats() =>
        new(
            BlocksReceived: Interlocked.Read(ref _blocks),
            DroppedBlocks: DroppedBlocks,
            SweepsPerSecond: SweepsPerSecond,
            SamplesPerSecond: SamplesPerSecond,
            DroppedBytes: DroppedBytes);

    /// <summary>Stop any stream and close the device. Idempotent, safe from any thread.</summary>
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

        _cancel.Cancel();

        try
        {
            StopStreamCore();
        }
        catch (HackRfException ex)
        {
            Stopped?.Invoke(this, new RadioStoppedEventArgs("Stopping the stream failed.", deviceLost: false, ex));
        }
        finally
        {
            _handle.Dispose();
            _worker.Join(TimeSpan.FromSeconds(2));
            _idle.Dispose();
            _cancel.Dispose();
            LibHackRf.Shutdown();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// The libhackrf RX callback. It runs on a libusb/pthread transfer thread, must return at
    /// once and must not allocate: it copies the transfer into the ring. Returning non-zero stops
    /// the stream, which is how a device-unplug error surfaces.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSampleBlock(IntPtr transfer)
    {
        if (transfer == IntPtr.Zero)
        {
            return 0;
        }

        ref readonly HackrfTransferHeader header = ref Unsafe.AsRef<HackrfTransferHeader>(transfer.ToPointer());

        // The context is the GCHandle we passed in; the device pointer is a fallback in case a
        // library version hands over something else.
        if (!Streams.TryGetValue(header.RxContext, out HackRfDevice? device) &&
            !Streams.TryGetValue(header.Device, out device))
        {
            return 0; // the device was disposed while a transfer was still in flight
        }

        int length = Math.Min(header.ValidLength, header.BufferLength);
        if (length <= 0)
        {
            return 0;
        }

        device.Consume(new ReadOnlySpan<byte>(header.Buffer.ToPointer(), length));
        return 0;
    }

    /// <summary>Producer side, on the libhackrf thread.</summary>
    private void Consume(ReadOnlySpan<byte> bytes)
    {
        long mode = Interlocked.Read(ref _mode);
        if (mode == ModeIdle)
        {
            return; // bytes from a stream that has already been stopped
        }

        Interlocked.Add(ref _samples, bytes.Length / 2);

        if (mode == ModeCapture)
        {
            CaptureWindow? window;
            lock (_gate)
            {
                window = _capture;
            }

            if (window is not null && window.Write(bytes))
            {
                // Only flag it: this runs inside libhackrf's transfer callback with its transfer
                // lock held, and hackrf_stop_rx waits on that lock. The DSP worker stops the
                // stream and raises CaptureAvailable.
                Volatile.Write(ref _captureDone, 1);
            }

            return;
        }

        if (mode == ModeLocate)
        {
            LocateWindow? window;
            lock (_gate)
            {
                window = _locate;
            }

            if (window is not null && window.Write(bytes))
            {
                // Same callback-lock rule as a capture: flag it, the DSP worker stops the
                // stream and raises the next locate window.
                Volatile.Write(ref _locateDone, 1);
            }

            return;
        }

        _ring.Write(bytes);
    }

    /// <summary>Consumer side: the DSP worker thread.</summary>
    private void DrainLoop()
    {
        const int chunkBytes = 1 << 17; // 131072 bytes = 8 sweep blocks at a time
        byte[] buffer = new byte[chunkBytes];

        while (!_cancel.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref _captureDone, 0) == 1)
            {
                FinishCapture();
                continue;
            }

            if (Interlocked.Exchange(ref _locateDone, 0) == 1)
            {
                FinishLocateWindow();
                continue;
            }

            int read;
            SpectrumFrame? frame = null;
            lock (_drainGate)
            {
                read = _ring.Read(buffer);
                if (read > 0)
                {
                    frame = ProcessChunk(buffer.AsSpan(0, read));
                }
            }

            // Raised outside the drain gate, so a handler that starts a new sweep can reset
            // the stream state without waiting on this thread.
            if (frame is not null)
            {
                SpectrumAvailable?.Invoke(this, new SpectrumFrameEventArgs(frame));
            }

            if (read == 0)
            {
                CheckNativeStream();
                Thread.Sleep(2);
            }
        }
    }

    /// <summary>Frame and transform one chunk of sweep bytes; returns a frame when one is due.</summary>
    private SpectrumFrame? ProcessChunk(ReadOnlySpan<byte> chunk)
    {
        SpectrumAssembler? assembler;
        lock (_gate)
        {
            assembler = _assembler;
        }

        if (assembler is null)
        {
            return null; // capture mode: the callback fills the window directly
        }

        _parser.Feed(chunk, block =>
        {
            assembler.AddBlock(block, block.IqSamples.Span);
            Interlocked.Increment(ref _blocks);
        });

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!assembler.IsDue(now))
        {
            return null;
        }

        SpectrumFrame frame = assembler.Publish(now);

        // The status bar promises sweeps per second, so only a frame that closed a pass counts.
        // The watchdog timestamp and the sample rate still move on every frame, because those
        // measure liveness of the stream, not the completion of a sweep.
        if (frame.SweepCompleted)
        {
            Interlocked.Increment(ref _sweeps);
        }

        Interlocked.Exchange(ref _lastSweepUtcTicks, now.UtcTicks);
        UpdateRates(now);
        return frame;
    }

    /// <summary>A capture window filled up in the callback: stop the stream here, off the libhackrf thread.</summary>
    private void FinishCapture()
    {
        IqCapture? done;
        lock (_gate)
        {
            done = _capture?.Completed;
        }

        StopStreamCore();
        if (done is not null)
        {
            CaptureAvailable?.Invoke(this, new IqCaptureEventArgs(done));
        }
    }

    /// <summary>
    /// A locate window filled up in the callback: stop the stream, raise the window, and arm
    /// the next one so the caller can restart the stream for a fresh measurement.
    /// </summary>
    private void FinishLocateWindow()
    {
        IqCapture? done;
        lock (_gate)
        {
            done = _locate?.Completed;
            if (_locate is not null)
            {
                // Re-arm: the next window reuses this buffer.
                _locate.Reset();
            }
        }

        Interlocked.Exchange(ref _locateRestartArmed, 1);
        StopStreamCore();
        Interlocked.Exchange(ref _locateRestartArmed, 0);

        if (done is not null)
        {
            LocateSampleAvailable?.Invoke(this, new IqCaptureEventArgs(done));
        }

        if (Interlocked.Read(ref _locateActive) == 1)
        {
            try
            {
                StartLocateWindow();
            }
            catch (Exception ex) when (ex is HackRfException or InvalidOperationException)
            {
                // The next window could not start (device unplugged mid-locate): the stream-lost
                // watchdog or the caller reports it; do not spin here.
                Interlocked.Exchange(ref _locateActive, 0);
                Interlocked.Exchange(ref _mode, ModeIdle);
                _idle.Set();
            }
        }
    }

    /// <summary>
    /// libhackrf ends a stream without calling the RX callback when a transfer fails, which is
    /// what an unplugged radio looks like. Poll <c>hackrf_is_streaming</c> while no data arrives
    /// and go idle with <see cref="Stopped"/> (device lost) instead of waiting forever.
    /// </summary>
    private void CheckNativeStream()
    {
        // Only a stream libhackrf has actually started can be lost; between EnterMode and a
        // successful start the library rightly says it is not streaming yet.
        if (Volatile.Read(ref _streamLive) == 0 || _handle.IsClosed || Volatile.Read(ref _captureDone) == 1)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _streamStartedMs) < StreamGraceMs || now - _lastStreamCheckMs < StreamCheckIntervalMs)
        {
            return;
        }

        _lastStreamCheckMs = now;
        int state = NativeMethods.hackrf_is_streaming(_handle.DangerousGetHandle());
        if (state == NativeMethods.HackrfTrue || Interlocked.Read(ref _mode) == ModeIdle || Volatile.Read(ref _captureDone) == 1)
        {
            return;
        }

        StopStreamCore();
        Stopped?.Invoke(
            this,
            new RadioStoppedEventArgs($"The radio stopped streaming ({NativeMethods.ErrorName(state)}); it may have been unplugged.", deviceLost: true));
    }

    /// <summary>
    /// Sweeps and samples per second over a one-second window. A running average would hide a
    /// stall for minutes, which is exactly what the status bar is there to catch.
    /// </summary>
    private void UpdateRates(DateTimeOffset now)
    {
        long nowMs = now.ToUnixTimeMilliseconds();
        long sweeps = Interlocked.Read(ref _sweeps);
        long samples = Interlocked.Read(ref _samples);

        if (_rateWindowStartMs == 0)
        {
            _rateWindowStartMs = nowMs;
            _rateWindowSweeps = sweeps;
            _rateWindowSamples = samples;
            return;
        }

        long elapsed = nowMs - _rateWindowStartMs;
        if (elapsed < 1_000)
        {
            return;
        }

        Volatile.Write(ref _sweepsPerSecond, ((_sweeps - _rateWindowSweeps) * 1000.0) / elapsed);
        Volatile.Write(ref _samplesPerSecond, ((_samples - _rateWindowSamples) * 1000.0) / elapsed);
        _rateWindowStartMs = nowMs;
        _rateWindowSweeps = sweeps;
        _rateWindowSamples = samples;
    }

    /// <summary>
    /// Program the firmware sweep list. <c>hackrf_init_sweep</c> wants a packed uint16 array of MHz
    /// start/stop pairs and counts ranges, not entries.
    ///
    /// The step width is the sample rate in Hz - 20 MHz at 20 Msps - which is what
    /// <c>hackrf_sweep.c</c> programs (<c>TUNE_STEP</c> is the sample rate in MHz and it passes
    /// <c>TUNE_STEP * FREQ_ONE_MHZ</c>). With the interleaved style the firmware then alternates
    /// sub-steps of a quarter and three quarters of that, so consecutive block headers advance by
    /// 5 MHz and then 15 MHz: each block's two kept bands fill the 5 MHz the block before it left
    /// out, the pair advances 20 MHz and measures exactly 20 MHz, with no gap and no double
    /// counting.
    ///
    /// Half that step width - 10 MHz, which looks just as reasonable because it is the width of the
    /// two kept bands - puts the headers 2.5 and 7.5 MHz apart, and a pair then covers only
    /// 17.5 MHz of the 20 MHz it advances: a 1.25 MHz hole every 5 MHz of span, which on a 625 kHz
    /// display grid is three empty bins in ten right across the range. The spectrum then looks like
    /// a busy band with a comb through it, and the sweep never reports itself complete because those
    /// bins never fill. This is the one number in the device layer that has to match upstream.
    /// </summary>
    private void ProgramSweep(SweepPlan plan)
    {
        ushort[] pairs = BuildSweepPairs(plan);
        int rangeCount = pairs.Length / 2;

        fixed (ushort* pinned = pairs)
        {
            NativeMethods.ThrowForError(
                NativeMethods.hackrf_init_sweep(
                    _handle.DangerousGetHandle(),
                    pinned,
                    rangeCount,
                    NativeMethods.BytesPerBlock,
                    (uint)SweepMath.SampleRateHz,
                    (uint)SweepMath.SweepOffsetHz,
                    NativeMethods.SweepStyleInterleaved),
                "hackrf_init_sweep");
        }
    }

    /// <summary>
    /// The packed MHz start/stop pairs for every range of <paramref name="plan"/>, in order. A plan
    /// that needs more ranges than the firmware holds is rejected rather than silently cut short.
    /// </summary>
    internal static ushort[] BuildSweepPairs(SweepPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var ranges = new List<(int StartMHz, int StopMHz)>();
        foreach (FrequencyRange range in plan.Ranges)
        {
            ranges.AddRange(SweepMath.BuildSweepRanges(range));
        }

        if (ranges.Count is 0 or > NativeMethods.MaxSweepRanges)
        {
            throw new ArgumentException(
                $"Sweep plan needs {ranges.Count} radio ranges; the firmware holds 1 to {NativeMethods.MaxSweepRanges}.",
                nameof(plan));
        }

        ushort[] pairs = new ushort[ranges.Count * 2];
        for (int i = 0; i < ranges.Count; i++)
        {
            pairs[2 * i] = (ushort)ranges[i].StartMHz;
            pairs[(2 * i) + 1] = (ushort)ranges[i].StopMHz;
        }

        return pairs;
    }

    /// <summary>
    /// Hand libhackrf the static RX callback and start streaming. The callback is a
    /// <c>delegate* unmanaged[Cdecl]</c> pointing straight at <see cref="OnSampleBlock"/>, so no
    /// delegate object is allocated and nothing has to be kept alive for the library's sake.
    /// </summary>
    private void StartStream(bool sweepMode)
    {
        GCHandle box = GCHandle.Alloc(this, GCHandleType.Normal);
        IntPtr context = GCHandle.ToIntPtr(box);
        IntPtr handle = _handle.DangerousGetHandle();
        Volatile.Write(ref _streamContext, context);
        Streams[context] = this;
        Streams[handle] = this;

        int result = sweepMode
            ? NativeMethods.hackrf_start_rx_sweep(handle, &OnSampleBlock, context)
            : NativeMethods.hackrf_start_rx(handle, &OnSampleBlock, context);

        if (result != NativeMethods.Success)
        {
            UnmapStream();
            NativeMethods.ThrowForError(result, sweepMode ? "hackrf_start_rx_sweep" : "hackrf_start_rx");
        }

        Interlocked.Exchange(ref _streamStartedMs, Environment.TickCount64);
        Volatile.Write(ref _streamLive, 1);
    }

    /// <summary>
    /// Detach from the static stream table and release the pin. Only the pointer recorded in
    /// <see cref="_streamContext"/> is ever treated as a GCHandle: the device pointer libhackrf
    /// also uses as a key is raw native memory, and GCHandle.FromIntPtr on it yields a handle
    /// whose Free corrupts the pinning heap - which is how a hardware run died with a
    /// fail-fast instead of a test failure.
    /// </summary>
    private void UnmapStream()
    {
        Streams.TryRemove(_handle.DangerousGetHandle(), out _);

        IntPtr context = Interlocked.Exchange(ref _streamContext, IntPtr.Zero);
        if (context != IntPtr.Zero)
        {
            Streams.TryRemove(context, out _);
            GCHandle.FromIntPtr(context).Free();
        }
    }

    private void ResetStreamState()
    {
        // The DSP worker keeps running between streams; hold its gate so the ring and parser are
        // never reset in the middle of a read or a feed.
        lock (_drainGate)
        {
            _parser.Reset();
            _ring.Reset();
            Volatile.Write(ref _captureDone, 0);
            Volatile.Write(ref _locateDone, 0);
            Interlocked.Exchange(ref _blocks, 0);
            Interlocked.Exchange(ref _sweeps, 0);
            Interlocked.Exchange(ref _samples, 0);
            _rateWindowStartMs = 0;
            _rateWindowSweeps = 0;
            _rateWindowSamples = 0;
            Volatile.Write(ref _sweepsPerSecond, 0);
            Volatile.Write(ref _samplesPerSecond, 0);
        }
    }

    /// <summary>Set gains and the RF amp / antenna power together and remember them.</summary>
    private void ApplyRadioSettings(int lnaDb, int vgaDb, bool ampOn, bool antennaPower)
    {
        lock (_gate)
        {
            _lnaDb = lnaDb;
            _vgaDb = vgaDb;
            _ampOn = ampOn;
            _antennaPowerOn = antennaPower;
            ConfigureGains();
        }
    }

    private void ConfigureGains()
    {
        IntPtr handle = _handle.DangerousGetHandle();
        NativeMethods.ThrowForError(NativeMethods.hackrf_set_lna_gain(handle, (uint)_lnaDb), "hackrf_set_lna_gain");
        NativeMethods.ThrowForError(NativeMethods.hackrf_set_vga_gain(handle, (uint)_vgaDb), "hackrf_set_vga_gain");
        NativeMethods.ThrowForError(NativeMethods.hackrf_set_amp_enable(handle, (byte)(_ampOn ? 1 : 0)), "hackrf_set_amp_enable");
        NativeMethods.ThrowForError(
            NativeMethods.hackrf_set_antenna_enable(handle, (byte)(_antennaPowerOn ? 1 : 0)),
            "hackrf_set_antenna_enable");
    }

    private RadioDeviceInfo ReadDeviceInfo(string boardFromList, string serial)
    {
        IntPtr handle = _handle.DangerousGetHandle();

        byte boardId = 0;
        _ = NativeMethods.hackrf_board_id_read(handle, &boardId);
        string boardName =
            NativeMethods.LibraryOwnedString(NativeMethods.hackrf_board_id_name(boardId)) is { Length: > 0 } named ? named :
            boardFromList.Length > 0 ? boardFromList : "HackRF";

        byte* version = stackalloc byte[NativeMethods.VersionStringLength + 1];
        _ = NativeMethods.hackrf_version_string_read(handle, version, NativeMethods.VersionStringLength);
        string firmware = ParseAscii(new ReadOnlySpan<byte>(version, NativeMethods.VersionStringLength));

        ushort api = 0;
        _ = NativeMethods.hackrf_usb_api_version_read(handle, &api);

        return new RadioDeviceInfo(
            SerialNumber: serial.Length > 0 ? serial : "unknown",
            BoardName: boardName,
            FirmwareVersion: firmware.Length > 0 ? firmware : "unknown",
            UsbApiVersion: api,
            LibraryVersion: NativeMethods.LibraryOwnedString(NativeMethods.hackrf_library_version()) ?? "unknown");
    }

    private static string ParseAscii(ReadOnlySpan<byte> bytes)
    {
        int end = 0;
        while (end < bytes.Length && bytes[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(bytes[..end]);
    }

    private static string BoardNameFor(int usbProductId) => usbProductId switch
    {
        0x6089 => "HackRF One",
        0x604B => "Jawbreaker",
        0xCC15 => "RAD1O",
        _ => "HackRF",
    };

    private void EnterMode(long mode)
    {
        long previous = Interlocked.CompareExchange(ref _mode, mode, ModeIdle);
        if (previous != ModeIdle)
        {
            throw new InvalidOperationException(previous == ModeSweep ? "A sweep is already running." : "A capture is already running.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Mirror of the prefix of <c>hackrf_transfer</c> from hackrf.h (v2026.01.3, line 963). Keep
    /// the field order in step with the header; the callback only reads it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct HackrfTransferHeader
    {
        /// <summary><c>hackrf_device* device</c>.</summary>
        public readonly IntPtr Device;

        /// <summary><c>uint8_t* buffer</c>, interleaved s8 I/Q.</summary>
        public readonly IntPtr Buffer;

        /// <summary><c>int buffer_length</c>.</summary>
        public readonly int BufferLength;

        /// <summary><c>int valid_length</c>, bytes actually transferred.</summary>
        public readonly int ValidLength;

        /// <summary><c>void* rx_ctx</c>, the pointer passed to <c>hackrf_start_rx</c>.</summary>
        public readonly IntPtr RxContext;

        /// <summary><c>void* tx_ctx</c>, unused: RfScout never transmits.</summary>
        public readonly IntPtr TxContext;
    }

    /// <summary>
    /// One narrowband capture: a fixed byte window filled by the RX callback, plus the settings
    /// that produced it so the file and the SigMF header can record them.
    /// </summary>
    private sealed class CaptureWindow
    {
        private readonly byte[] _bytes;
        private readonly long _centerHz;
        private readonly int _sampleRateHz;
        private readonly int _lnaDb;
        private readonly int _vgaDb;
        private readonly bool _ampOn;
        private readonly bool _antennaPower;
        private int _written;

        public CaptureWindow(
            int maxSamples,
            long centerHz,
            int sampleRateHz,
            int lnaDb,
            int vgaDb,
            bool ampOn,
            bool antennaPower)
        {
            MaxSamples = maxSamples;
            // StartCapture has already rejected anything over MaxCaptureBytes, so this never truncates.
            _bytes = new byte[Math.Max(2, maxSamples * 2)];
            _centerHz = centerHz;
            _sampleRateHz = sampleRateHz;
            _lnaDb = lnaDb;
            _vgaDb = vgaDb;
            _ampOn = ampOn;
            _antennaPower = antennaPower;
        }

        /// <summary>Samples the caller asked for.</summary>
        public int MaxSamples { get; }

        /// <summary>Set once the window is full; further writes are ignored.</summary>
        public IqCapture? Completed { get; private set; }

        /// <summary>Fill the window; returns true exactly once, when it becomes complete.</summary>
        public bool Write(ReadOnlySpan<byte> bytes)
        {
            if (Completed is not null || bytes.IsEmpty)
            {
                return false;
            }

            int take = Math.Min(bytes.Length, _bytes.Length - _written);
            if (take > 0)
            {
                bytes[..take].CopyTo(_bytes.AsSpan(_written));
                _written += take;
            }

            if (_written < _bytes.Length)
            {
                return false;
            }

            int samples = _written / 2;
            Completed = new IqCapture(
                _centerHz,
                _sampleRateHz,
                _lnaDb,
                _vgaDb,
                _ampOn,
                _antennaPower,
                DateTimeOffset.UtcNow,
                _bytes.AsMemory(0, samples * 2));

            return true;
        }
    }

    /// <summary>
    /// M5 locate: like a <see cref="CaptureWindow"/>, but reusable. The DSP worker stops the
    /// stream when one window fills, raises it, resets this window and restarts the stream, so
    /// locate becomes a steady train of independent measurements of the same frequency.
    /// </summary>
    private sealed class LocateWindow
    {
        private readonly byte[] _bytes;
        private readonly long _centerHz;
        private readonly int _sampleRateHz;
        private readonly int _lnaDb;
        private readonly int _vgaDb;
        private readonly bool _ampOn;
        private readonly bool _antennaPower;
        private int _written;

        public LocateWindow(
            int maxSamples,
            long centerHz,
            int sampleRateHz,
            int lnaDb,
            int vgaDb,
            bool ampOn,
            bool antennaPower)
        {
            _bytes = new byte[Math.Max(2, maxSamples * 2)];
            _centerHz = centerHz;
            _sampleRateHz = sampleRateHz;
            _lnaDb = lnaDb;
            _vgaDb = vgaDb;
            _ampOn = ampOn;
            _antennaPower = antennaPower;
        }

        /// <summary>Set once the window is full; further writes are ignored until <see cref="Reset"/>.</summary>
        public IqCapture? Completed { get; private set; }

        /// <summary>Fill the window; returns true exactly once, when it becomes complete.</summary>
        public bool Write(ReadOnlySpan<byte> bytes)
        {
            if (Completed is not null || bytes.IsEmpty)
            {
                return false;
            }

            int take = Math.Min(bytes.Length, _bytes.Length - _written);
            if (take > 0)
            {
                bytes[..take].CopyTo(_bytes.AsSpan(_written));
                _written += take;
            }

            if (_written < _bytes.Length)
            {
                return false;
            }

            int samples = _written / 2;
            Completed = new IqCapture(
                _centerHz,
                _sampleRateHz,
                _lnaDb,
                _vgaDb,
                _ampOn,
                _antennaPower,
                DateTimeOffset.UtcNow,
                _bytes.AsMemory(0, samples * 2));

            return true;
        }

        /// <summary>Clear the window for the next round of the locate stream.</summary>
        public void Reset()
        {
            Completed = null;
            _written = 0;
        }
    }
}

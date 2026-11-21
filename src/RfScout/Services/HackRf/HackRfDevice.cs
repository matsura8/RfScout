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

    private const long ModeIdle = 0;
    private const long ModeSweep = 1;
    private const long ModeCapture = 2;

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

    private SpectrumAssembler? _assembler;
    private CaptureWindow? _capture;
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
            string serial = wanted;
            string boardFromList = string.Empty;

            IntPtr list = NativeMethods.hackrf_device_list();
            try
            {
                NativeMethods.HackrfDeviceList devices = NativeMethods.ReadDeviceList(list);
                if (devices.DeviceCount > 0)
                {
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
                    boardFromList = BoardNameFor(devices.UsbBoardId(index));
                    BusSharesUsb = NativeMethods.hackrf_device_list_bus_sharing(list, index) == 1;
                }
            }
            finally
            {
                if (list != IntPtr.Zero)
                {
                    NativeMethods.hackrf_device_list_free(list);
                }
            }

            int openResult = wanted.Length == 0
                ? NativeMethods.hackrf_open(out IntPtr raw)
                : NativeMethods.hackrf_open_by_serial(wanted, out raw);
            NativeMethods.ThrowForError(openResult, "hackrf_open");
            handle = new HackRfHandle(raw);
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

    public event EventHandler<SpectrumFrameEventArgs>? SpectrumAvailable;

    public event EventHandler<IqCaptureEventArgs>? CaptureAvailable;

    public event EventHandler<RadioStoppedEventArgs>? Stopped;

    /// <summary>What the radio reported about itself when it was opened.</summary>
    public RadioDeviceInfo DeviceInfo { get; }

    RadioDeviceInfo? IRadioSource.DeviceInfo => DeviceInfo;

    /// <summary>
    /// True when the HackRF shares its USB hub with other devices. Apache's built-in hub also
    /// feeds the card reader, smart-card reader, Bluetooth and the MSI receiver, and that is the
    /// usual cause of dropped blocks at 20 Msps - worth knowing before blaming the code.
    /// </summary>
    public bool BusSharesUsb { get; private set; }

    public bool IsRunning => Interlocked.Read(ref _mode) != ModeIdle;

    /// <summary>True while a narrowband capture is running.</summary>
    public bool IsCapturing => Interlocked.Read(ref _mode) == ModeCapture;

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
            SpectrumAssembler assembler = new(
                plan.Ranges[0].StartHz,
                plan.Ranges[^1].StopHz,
                plan.FftLength,
                plan.DisplayBinWidthHz);

            lock (_gate)
            {
                _assembler = assembler;
                _capture = null;
                _plan = plan;
            }

            Volatile.Write(ref _sampleRateHz, SweepMath.SampleRateHz);
            Interlocked.Exchange(ref _tunedHz, plan.Ranges[0].StartHz);
            _idle.Reset();

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
    /// Wait for the running capture to fill its window. Returns the capture, or null on timeout.
    /// Call from a worker thread, never from the UI thread, or the window stops painting.
    /// </summary>
    public IqCapture? WaitForCapture(TimeSpan timeout) => _idle.Wait(timeout) ? CurrentCapture : null;

    /// <summary>Stop the running stream without closing the device.</summary>
    public void StopStream()
    {
        if (_disposed || _handle.IsClosed)
        {
            return;
        }

        _ = NativeMethods.hackrf_stop_rx(_handle.DangerousGetHandle());
        UnmapStream();
        Interlocked.Exchange(ref _mode, ModeIdle);
        _idle.Set();
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
            StopStream();
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
                StopStream();
                _idle.Set();
                CaptureAvailable?.Invoke(this, new IqCaptureEventArgs(window.Completed!));
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
            int read = _ring.Read(buffer);
            if (read == 0)
            {
                Thread.Sleep(2);
                continue;
            }

            SpectrumAssembler? assembler;
            lock (_gate)
            {
                assembler = _assembler;
            }

            if (assembler is null)
            {
                continue; // capture mode: the callback fills the window directly
            }

            _parser.Feed(buffer.AsSpan(0, read), block =>
            {
                assembler.AddBlock(block, block.IqSamples.Span);
                Interlocked.Increment(ref _blocks);
            });

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!assembler.IsDue(now))
            {
                continue;
            }

            SpectrumFrame frame = assembler.Publish(now);
            Interlocked.Increment(ref _sweeps);
            Interlocked.Exchange(ref _lastSweepUtcTicks, now.UtcTicks);
            UpdateRates(now);
            SpectrumAvailable?.Invoke(this, new SpectrumFrameEventArgs(frame));
        }
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
    /// Program the firmware sweep list. <c>hackrf_init_sweep</c> wants a packed uint16 array of
    /// MHz start/stop pairs and counts ranges, not entries. The step width is 10 MHz (half the
    /// sample rate, i.e. <c>TUNE_STEP</c> in <c>hackrf_sweep.c</c>); with the interleaved style
    /// the firmware then alternates sub-steps of 7.5 MHz and 2.5 MHz, so block headers advance by
    /// 250 kHz and the two kept quarters tile the span, which is what
    /// <see cref="SweepMath"/> assumes when it stitches them.
    /// </summary>
    private void ProgramSweep(SweepPlan plan)
    {
        IReadOnlyList<(int StartMHz, int StopMHz)> ranges = SweepMath.BuildSweepRanges(plan.Ranges[0]);
        if (ranges.Count is 0 or > NativeMethods.MaxSweepRanges)
        {
            throw new ArgumentException($"Sweep plan produced {ranges.Count} radio ranges.", nameof(plan));
        }

        ushort[] pairs = new ushort[ranges.Count * 2];
        for (int i = 0; i < ranges.Count; i++)
        {
            pairs[(2 * i)] = (ushort)ranges[i].StartMHz;
            pairs[(2 * i) + 1] = (ushort)ranges[i].StopMHz;
        }

        fixed (ushort* pinned = pairs)
        {
            NativeMethods.ThrowForError(
                NativeMethods.hackrf_init_sweep(
                    _handle.DangerousGetHandle(),
                    pinned,
                    ranges.Count,
                    NativeMethods.BytesPerBlock,
                    (uint)(SweepMath.SampleRateHz / 2),
                    (uint)SweepMath.SweepOffsetHz,
                    NativeMethods.SweepStyleInterleaved),
                "hackrf_init_sweep");
        }
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
    }

    private void UnmapStream()
    {
        Streams.TryRemove(_handle.DangerousGetHandle(), out _);
        foreach (IntPtr key in Streams.Keys)
        {
            if (Streams.TryGetValue(key, out HackRfDevice? owner) && ReferenceEquals(owner, this))
            {
                Streams.TryRemove(key, out _);
                if (key != IntPtr.Zero)
                {
                    try
                    {
                        GCHandle box = GCHandle.FromIntPtr(key);
                        if (box.IsAllocated)
                        {
                            box.Free();
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Not a GCHandle we allocated; nothing to free.
                    }
                }
            }
        }
    }

    private void ResetStreamState()
    {
        _parser.Reset();
        _ring.Reset();
        Interlocked.Exchange(ref _blocks, 0);
        Interlocked.Exchange(ref _sweeps, 0);
        Interlocked.Exchange(ref _samples, 0);
        _rateWindowStartMs = 0;
        _rateWindowSweeps = 0;
        _rateWindowSamples = 0;
        Volatile.Write(ref _sweepsPerSecond, 0);
        Volatile.Write(ref _samplesPerSecond, 0);
    }

    private void ConfigureGains()
    {
        IntPtr handle = _handle.DangerousGetHandle();
        NativeMethods.ThrowForError(NativeMethods.hackrf_set_lna_gain(handle, (uint)_lnaDb), "hackrf_set_lna_gain");
        NativeMethods.ThrowForError(NativeMethods.hackrf_set_vga_gain(handle, (uint)_vgaDb), "hackrf_set_vga_gain");
        _ = NativeMethods.hackrf_set_amp_enable(handle, (byte)(_ampOn ? 1 : 0));
        _ = NativeMethods.hackrf_set_antenna_enable(handle, (byte)(_antennaPowerOn ? 1 : 0));
    }

    private RadioDeviceInfo ReadDeviceInfo(string boardFromList, string serial)
    {
        IntPtr handle = _handle.DangerousGetHandle();

        byte boardId = 0;
        _ = NativeMethods.hackrf_board_id_read(handle, &boardId);
        string boardName =
            NativeMethods.hackrf_board_id_name(boardId) is { Length: > 0 } named ? named :
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
            LibraryVersion: NativeMethods.hackrf_library_version() ?? "unknown");
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
            _bytes = new byte[Math.Clamp(maxSamples * 2, 2, 1 << 28)];
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
}

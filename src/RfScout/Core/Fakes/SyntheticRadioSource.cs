using System.Buffers.Binary;
using RfScout.Core.Spectrum;

namespace RfScout.Core.Fakes;

/// <summary>
/// Deterministic sweep data for unit tests and <c>--self-test</c>: no HackRF, no threads, no
/// timing, no dependency on what happens to be on the air. The block helpers are shared by the
/// tests so that no test re-implements sweep framing in its own body.
/// </summary>
public static class SyntheticSignal
{
    /// <summary>
    /// Complex tone as interleaved s8 I/Q: I = a*cos(2*pi*f*i), Q = a*sin(2*pi*f*i).
    /// <paramref name="cyclesPerSample"/> is the tone frequency in cycles per sample relative to
    /// the tuned centre, so a negative value is below it.
    /// </summary>
    public static byte[] IqBytes(int sampleCount, double cyclesPerSample, double amplitude = 1.0)
    {
        byte[] iq = new byte[2 * sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double phase = 2 * Math.PI * cyclesPerSample * i;
            iq[2 * i] = ToS8(amplitude * Math.Cos(phase));
            iq[(2 * i) + 1] = ToS8(amplitude * Math.Sin(phase));
        }

        return iq;
    }

    /// <summary>
    /// One complete 16384-byte sweep block: the <c>0x7F 0x7F</c> header carrying
    /// <paramref name="headerFrequencyHz"/> little-endian in bytes 2..9, then interleaved s8
    /// I/Q. <paramref name="sampleCount"/> must fit in a block (8187 samples).
    /// </summary>
    public static byte[] SweepBlockBytes(long headerFrequencyHz, int sampleCount, double cyclesPerSample, double amplitude = 1.0)
    {
        if (sampleCount is < 0 or > SweepMath.SamplesPerBlock)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleCount),
                sampleCount,
                $"A block holds at most {SweepMath.SamplesPerBlock} samples.");
        }

        byte[] iq = IqBytes(sampleCount, cyclesPerSample, amplitude);
        byte[] block = new byte[SweepMath.BytesPerBlock];
        block[0] = 0x7F;
        block[1] = 0x7F;
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(2, 8), (ulong)headerFrequencyHz);
        iq.CopyTo(block, SweepMath.HeaderBytes);
        return block;
    }

    /// <summary>The same block as a <see cref="SweepBlock"/>, IQ copied out of the header.</summary>
    public static SweepBlock SweepBlock(long headerFrequencyHz, int sampleCount, double cyclesPerSample, double amplitude = 1.0)
    {
        byte[] block = SweepBlockBytes(headerFrequencyHz, sampleCount, cyclesPerSample, amplitude);
        return new SweepBlock(headerFrequencyHz, block.AsMemory(SweepMath.HeaderBytes));
    }

    /// <summary>
    /// A block carrying a tone at <paramref name="rfFrequencyHz"/> as the radio would produce it
    /// when tuned to <paramref name="headerHz"/> + 7.5 MHz, i.e. already expressed in cycles per
    /// sample relative to that tuned centre.
    /// </summary>
    public static SweepBlock BlockWithTone(long headerHz, int fftLength, double rfFrequencyHz, double amplitude = 1.0)
    {
        _ = fftLength; // the FFT length the caller will decode with; the tone is rate-relative
        double tunedCenterHz = headerHz + SweepMath.SweepOffsetHz;
        double cyclesPerSample = (rfFrequencyHz - tunedCenterHz) / SweepMath.SampleRateHz;
        return SweepBlock(headerHz, SweepMath.SamplesPerBlock, cyclesPerSample, amplitude);
    }

    private static byte ToS8(double value)
    {
        int scaled = (int)Math.Round(Math.Clamp(value, -1.0, 1.0) * 127.0);
        return (byte)(sbyte)scaled;
    }
}

/// <summary>
/// An <see cref="IRadioSource"/> that replays synthetic blocks instead of talking to a HackRF, so
/// the whole sweep pipeline - framing, FFT, grid, peak finding, decoders - can be tested and
/// demoed with no radio attached. It also serves as the no-hardware demo mode in the app.
/// </summary>
public sealed class SyntheticRadioSource : IRadioSource
{
    /// <summary>
    /// Header step that tiles a span exactly. A block measures
    /// <c>header + 2.5 MHz .. header + 12.5 MHz</c> (the two quarters either side of its tuned
    /// centre), so advancing the header by half the sample rate makes the bands abut with no gap
    /// and no overlap - the same coverage the real interleaved sweep adds up to, which it reaches
    /// by stepping a quarter of its tune step and then three quarters.
    /// </summary>
    public const long DefaultStepHz = SweepMath.SampleRateHz / 2;

    /// <summary>Identity a fake source reports, so the status bar and About box have something
    /// honest to show when no radio is present.</summary>
    public static RadioDeviceInfo FakeDeviceInfo { get; } = new(
        SerialNumber: "SYNTH000000000000000000000",
        BoardName: "Synthetic source (no radio)",
        FirmwareVersion: "n/a",
        UsbApiVersion: 0x0108,
        LibraryVersion: "none");

    private readonly long _startHz;
    private readonly long _stopHz;
    private readonly Func<long, int, double, SweepBlock> _blockFactory;
    private readonly Lock _gate = new();
    private long _blocksReceived;
    private int _lnaDb = RadioGains.DefaultLnaDb;
    private int _vgaDb = RadioGains.DefaultVgaDb;
    private bool _ampOn;
    private bool _antennaPower;
    private bool _running;
    private IqCapture? _lastCapture;

    /// <summary>
    /// Replay a sweep of <paramref name="startHz"/>..<paramref name="stopHz"/>.
    /// <paramref name="blockFactory"/> builds the block for one header frequency, FFT length and
    /// tone frequency, which lets a test plant a tone wherever it likes.
    /// </summary>
    public SyntheticRadioSource(
        long startHz,
        long stopHz,
        Func<long, int, double, SweepBlock>? blockFactory = null)
    {
        if (stopHz <= startHz)
        {
            throw new ArgumentException("Stop must be above start.", nameof(stopHz));
        }

        _startHz = startHz;
        _stopHz = stopHz;
        _blockFactory = blockFactory ?? ((header, fft, tone) => SyntheticSignal.BlockWithTone(header, fft, tone));
    }

    public event EventHandler<SpectrumFrameEventArgs>? SpectrumAvailable;

    public event EventHandler<IqCaptureEventArgs>? CaptureAvailable;

    public event EventHandler<RadioStoppedEventArgs>? Stopped;

    public RadioDeviceInfo? DeviceInfo => FakeDeviceInfo;

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

    /// <summary>Blocks handed to the assembler so far.</summary>
    public long BlocksReplayed => Interlocked.Read(ref _blocksReceived);

    /// <summary>The capture raised by the last <see cref="StartCapture"/>, or null.</summary>
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

    public SweepPlan? Plan
    {
        get
        {
            lock (_gate)
            {
                return _plan;
            }
        }
    }

    private SweepPlan? _plan;

    public RadioStats ReadStats() =>
        new(
            BlocksReceived: Interlocked.Read(ref _blocksReceived),
            DroppedBlocks: 0,
            SweepsPerSecond: 1,
            SamplesPerSecond: SweepMath.SampleRateHz);

    public void StartSweep(SweepPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Validate before claiming the source, so a rejected plan leaves it idle and startable.
        // The real device does the same, and the UI relies on it: a plan with no ranges would
        // otherwise look "running" with nothing to show.
        if (!plan.IsValid)
        {
            throw new ArgumentException("Sweep plan is not valid.", nameof(plan));
        }

        Enter();
        try
        {
            lock (_gate)
            {
                _plan = plan;
            }

            int fft = plan.FftLength;
            SpectrumFrame frame = ReplayOnce(plan, fft, DefaultStepHz);

            // Raised while still running, like a real source: a handler that starts another
            // stream must get the "already running" error rather than a second stream.
            SpectrumAvailable?.Invoke(this, new SpectrumFrameEventArgs(frame));
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Walk the span once and return the assembled frame, so a test can drive a single pass
    /// without subscribing to events.
    /// </summary>
    public SpectrumFrame ReplayOnce(SweepPlan plan, int fftLength, long stepHz)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (stepHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepHz), stepHz, "Step must be positive.");
        }

        double displayBinWidth = plan.DisplayBinWidthHz;
        var assembler = new SpectrumAssembler(_startHz, _stopHz, fftLength, displayBinWidth);

        foreach (FrequencyRange range in plan.Ranges)
        {
            for (long header = range.StartHz; header < range.StopHz; header += stepHz)
            {
                SweepBlock block = _blockFactory(header, fftLength, header + (SweepMath.SampleRateHz / 4.0));
                assembler.AddBlock(block, block.IqSamples.Span);
                Interlocked.Increment(ref _blocksReceived);
            }
        }

        return assembler.Publish(DateTimeOffset.UtcNow);
    }

    public void StartCapture(long centerHz, int sampleRateHz, TimeSpan duration)
    {
        Enter();
        try
        {
            if (sampleRateHz <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRateHz), sampleRateHz, "Sample rate must be positive.");
            }

            int samples = (int)Math.Clamp(duration.TotalSeconds * sampleRateHz, 1, 1 << 24);
            byte[] iq = SyntheticSignal.IqBytes(samples, cyclesPerSample: 0.05);
            IqCapture capture;
            lock (_gate)
            {
                capture = new IqCapture(
                    centerHz,
                    sampleRateHz,
                    _lnaDb,
                    _vgaDb,
                    _ampOn,
                    _antennaPower,
                    DateTimeOffset.UtcNow,
                    iq);
                _lastCapture = capture;
            }

            // Raised while still running, exactly as the real device does.
            CaptureAvailable?.Invoke(this, new IqCaptureEventArgs(capture));
        }
        finally
        {
            Exit();
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
        }
    }

    public void SetRfAmplifier(bool enabled)
    {
        lock (_gate)
        {
            _ampOn = enabled;
        }
    }

    public void SetAntennaPower(bool enabled)
    {
        lock (_gate)
        {
            _antennaPower = enabled;
        }
    }

    /// <summary>Gains the fake is currently set to, for tests that assert the UI pushed them down.</summary>
    public (int LnaDb, int VgaDb, bool AmpOn, bool AntennaPower) Gains
    {
        get
        {
            lock (_gate)
            {
                return (_lnaDb, _vgaDb, _ampOn, _antennaPower);
            }
        }
    }

    /// <summary>Raise <see cref="Stopped"/> the way a real source does when the device is pulled.</summary>
    public void SimulateDeviceLost() =>
        Stopped?.Invoke(this, new RadioStoppedEventArgs("Synthetic source stopped.", deviceLost: true));

    public void StopStream() => Exit();

    public void Dispose() => Exit();

    private void Enter()
    {
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException("A sweep or capture is already running.");
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
}

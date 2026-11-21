using System.Buffers.Binary;
using RfScout.Core.Spectrum;

namespace RfScout.Core.Fakes;

/// <summary>
/// Deterministic sweep data for unit tests and <c>--self-test</c>: no HackRF, no threads, no
/// timing. The block helpers are shared by the tests so that no test re-implements the sweep
/// framing in its own body.
/// </summary>
public static class SyntheticSignal
{
    /// <summary>
    /// Complex tone as interleaved s8 I/Q: I = a*cos(2*pi*f*i), Q = a*sin(2*pi*f*i), with
    /// <paramref name="cyclesPerSample"/> the tone frequency in cycles per sample (a positive
    /// value is above the tuned centre).
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

    /// <summary>The same block as a <see cref="SweepBlock"/>, IQ copied out of the header-prefixed buffer.</summary>
    public static SweepBlock SweepBlock(long headerFrequencyHz, int sampleCount, double cyclesPerSample, double amplitude = 1.0)
    {
        byte[] block = SweepBlockBytes(headerFrequencyHz, sampleCount, cyclesPerSample, amplitude);
        return new SweepBlock(headerFrequencyHz, block.AsMemory(SweepMath.HeaderBytes));
    }

    /// <summary>
    /// A tone at <paramref name="rfFrequencyHz"/> inside the block a radio would produce when it
    /// tunes to <paramref name="headerHz"/> + 7.5 MHz, i.e. already expressed in cycles per
    /// sample relative to that tuned centre.
    /// </summary>
    public static SweepBlock BlockWithTone(long headerHz, int fftLength, double rfFrequencyHz, double amplitude = 1.0)
    {
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
/// An <see cref="IRadioSource"/> that replays synthetic blocks instead of talking to a HackRF,
/// so the whole sweep pipeline (framing, FFT, grid, peak finding, decoders) can be tested and
/// demoed with no hardware attached.
/// </summary>
public sealed class SyntheticRadioSource : IRadioSource
{
    /// <summary>
    /// Header step that tiles a span exactly: a block keeps [header, header + 5 MHz] and
    /// [header + 10 MHz, header + 15 MHz], so advancing the header by 15 MHz per block leaves no
    /// gap and no overlap.
    /// </summary>
    public const long DefaultStepHz = SweepMath.SampleRateHz * 3 / 4;

    private readonly long _startHz;
    private readonly long _stopHz;
    private readonly Func<long, int, double, SweepBlock> _blockFactory;
    private long _blocksReceived;

    /// <summary>
    /// Replay a sweep of <paramref name="startHz"/>..<paramref name="stopHz"/>.
    /// <paramref name="blockFactory"/> builds the block for one header frequency, FFT length and
    /// tone offset, which lets a test plant a tone at a chosen RF.
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

    public bool IsRunning { get; private set; }

    /// <summary>Blocks handed to the assembler so far.</summary>
    public long BlocksReplayed => Interlocked.Read(ref _blocksReceived);

    public SweepPlan? Plan { get; private set; }

    public RadioStats ReadStats() =>
        new(Interlocked.Read(ref _blocksReceived), 0, 0, SweepMath.SampleRateHz);

    public void StartSweep(SweepPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (IsRunning)
        {
            throw new InvalidOperationException("A sweep or capture is already running.");
        }

        if (!plan.IsValid)
        {
            throw new ArgumentException("Sweep plan is not valid.", nameof(plan));
        }

        Plan = plan;
        IsRunning = true;

        try
        {
            int fft = plan.FftLength;
            SpectrumFrame frame = ReplayOnce(plan, fft, DefaultStepHz);

            // Raised while still running, like a real source: a handler that starts another
            // stream must get the "already running" error rather than a second stream.
            SpectrumAvailable?.Invoke(this, new SpectrumFrameEventArgs(frame));
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// Walk the span once and return the assembled frame. Exposed so tests can drive a single
    /// pass without an event subscription.
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
        if (IsRunning)
        {
            throw new InvalidOperationException("A sweep or capture is already running.");
        }

        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), sampleRateHz, "Sample rate must be positive.");
        }

        IsRunning = true;
        try
        {
            int samples = (int)Math.Clamp(duration.TotalSeconds * sampleRateHz, 1, 1 << 24);
            byte[] iq = SyntheticSignal.IqBytes(samples, cyclesPerSample: 0.05);
            var capture = new IqCapture(
                centerHz,
                sampleRateHz,
                LnaGainDb: 0,
                VgaGainDb: 0,
                RfAmpEnabled: false,
                AntennaPower: false,
                DateTimeOffset.UtcNow,
                iq);

            // Raised while still running, exactly as <see cref="StartSweep"/> does.
            CaptureAvailable?.Invoke(this, new IqCaptureEventArgs(capture));
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Raise <see cref="Stopped"/> the way a real source would when the device is pulled.</summary>
    public void SimulateDeviceLost() =>
        Stopped?.Invoke(this, new RadioStoppedEventArgs("Synthetic source stopped.", deviceLost: true));

    public void Dispose() => IsRunning = false;
}

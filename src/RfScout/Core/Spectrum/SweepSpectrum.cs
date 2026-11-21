using RfScout.Core.Dsp;

namespace RfScout.Core.Spectrum;

/// <summary>
/// Turns one sweep block (8187 samples at one tuned frequency) into power bins: Hann window,
/// FFT, keep the two quarters a HackRF sweep block actually resolves (see
/// <see cref="SweepMath.BinOffsetHz"/>), and average the extra windows when the block holds
/// more than one transform.
///
/// Output is linear power normalised so a full-scale complex sine measures 1.0, i.e. 0 dBFS.
/// Noise therefore reads lower in a narrow bin than in a wide one, as it does in
/// <c>hackrf_sweep</c>.
/// </summary>
public sealed class SweepSpectrumProcessor
{
    private readonly FloatFft _fft;
    private readonly float[] _window;
    private readonly float _powerScale;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly float[] _accum;

    /// <summary>Create a processor for one FFT length; reuse it, the tables are not free.</summary>
    public SweepSpectrumProcessor(int fftLength)
    {
        FftLength = SweepMath.ClampFftLength(fftLength);
        BinsPerBlock = SweepMath.BinsPerWindow(FftLength);
        _fft = new FloatFft(FftLength);
        _re = new float[FftLength];
        _im = new float[FftLength];
        _accum = new float[BinsPerBlock];
        _window = WindowFunctions.HannPeriodic(FftLength);
        WindowsPerBlock = SweepMath.WindowsPerBlock(FftLength);

        // Hann keeps half the amplitude, so power is short by (N * 0.5)^2. Dividing by that makes
        // a full-scale complex sine land on 1.0 = 0 dBFS.
        float coherent = FftLength * (float)WindowFunctions.HannCoherentGain;
        _powerScale = 1f / (coherent * coherent);
    }

    /// <summary>FFT length in use.</summary>
    public int FftLength { get; }

    /// <summary>Bins written per block: the two kept quarters, i.e. half the transform.</summary>
    public int BinsPerBlock { get; }

    /// <summary>Realised bin width in Hz.</summary>
    public double BinWidthHz => SweepMath.BinWidthHz(FftLength);

    /// <summary>How many independent windows of one block are averaged into the output.</summary>
    public int WindowsPerBlock { get; }

    /// <summary>
    /// Process the IQ bytes of one block into linear power bins.
    /// <paramref name="powerOut"/> needs at least <see cref="BinsPerBlock"/> slots.
    /// </summary>
    public void Process(ReadOnlySpan<byte> iq, Span<float> powerOut)
    {
        if (powerOut.Length < BinsPerBlock)
        {
            throw new ArgumentException($"Need {BinsPerBlock} bins, got {powerOut.Length}.", nameof(powerOut));
        }

        _accum.AsSpan().Clear();
        // iq is interleaved bytes, so one FFT window is 2 * FftLength of them, not FftLength.
        int windowBytes = 2 * FftLength;
        int windowsDone = 0;
        for (int offset = 0; offset + windowBytes <= iq.Length && windowsDone < WindowsPerBlock; offset += windowBytes)
        {
            IqMath.ToComplex(iq[offset..], _re, _im);
            WindowFunctions.ApplyInPlace(_window, _re, _im);
            _fft.PowerQuarters(_re, _im, _accum);
            windowsDone++;
        }

        float scale = _powerScale / Math.Max(1, windowsDone);
        for (int i = 0; i < BinsPerBlock; i++)
        {
            powerOut[i] = _accum[i] * scale;
        }
    }

    /// <summary>
    /// Scatter one block's bins into a shared grid. Called by <see cref="SpectrumAssembler"/> and
    /// by tests that want to check where a bin landed.
    /// </summary>
    public void Scatter(SweepBlock block, ReadOnlySpan<float> blockBins, float[] grid, long startHz, double gridBinWidthHz)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (blockBins.Length != BinsPerBlock)
        {
            throw new ArgumentException($"Expected {BinsPerBlock} block bins, got {blockBins.Length}.", nameof(blockBins));
        }

        double blockBinHz = BinWidthHz;
        for (int i = 0; i < blockBins.Length; i++)
        {
            double hz = block.HeaderFrequencyHz + SweepMath.BinOffsetHz(FftLength, i);
            int index = (int)((hz - startHz) / gridBinWidthHz);
            if ((uint)index < (uint)grid.Length && blockBins[i] > grid[index])
            {
                grid[index] = blockBins[i];
            }
        }
    }
}

/// <summary>
/// Picks the display grid for a span so a 6 GHz sweep at 2441 Hz bins does not allocate 2.45
/// million floats per frame. Wider bins are honest: the peak detector runs on the same grid, so
/// the resolution reported is the resolution drawn.
/// </summary>
public static class SpectrumGrid
{
    /// <summary>Most bins one frame may hold.</summary>
    public const int MaxBins = 24_000;

    /// <summary>Bin width the display grid should use for a span and a block resolution.</summary>
    public static double BinWidthFor(IReadOnlyList<FrequencyRange> ranges, double blockBinWidthHz, int maxBins = MaxBins)
    {
        long span = 0;
        foreach (FrequencyRange range in ranges)
        {
            span += Math.Max(0, range.StopHz - range.StartHz);
        }

        if (span <= 0 || blockBinWidthHz <= 0)
        {
            return blockBinWidthHz;
        }

        double needed = span / (double)maxBins;
        return needed > blockBinWidthHz ? needed : blockBinWidthHz;
    }
}

/// <summary>
/// Collects sweep blocks into full-span <see cref="SpectrumFrame"/>s.
///
/// Every block covers one slice of the span, so the assembler scatters block bins into a shared
/// grid and publishes a snapshot on a fixed interval. A sweep need not finish before the display
/// updates - <c>hackrf_sweep</c> behaves the same way - which is what keeps a slow sweep (a
/// 10 kHz-bin pass over 6 GHz takes seconds) usable.
/// </summary>
public sealed class SpectrumAssembler
{
    private readonly SweepSpectrumProcessor _processor;
    private readonly double _binWidthHz;
    private readonly long _startHz;
    private readonly long _stopHz;
    private readonly float[] _scratch;
    private readonly float[] _front;
    private readonly float[] _back;
    private readonly Lock _gate = new();
    private int _activeIndex;
    private long _lastPublishTicks;

    /// <summary>
    /// Assemble frames for <paramref name="startHz"/>..<paramref name="stopHz"/> at
    /// <paramref name="binWidthHz"/>, where blocks come from an FFT of
    /// <paramref name="blockFftLength"/>. Publishes at most once per
    /// <paramref name="publishInterval"/>.
    /// </summary>
    public SpectrumAssembler(
        long startHz,
        long stopHz,
        int blockFftLength,
        double binWidthHz,
        TimeSpan? publishInterval = null)
    {
        if (stopHz <= startHz)
        {
            throw new ArgumentException("Stop must be above start.", nameof(stopHz));
        }

        if (binWidthHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(binWidthHz), binWidthHz, "Bin width must be positive.");
        }

        _startHz = startHz;
        _stopHz = stopHz;
        _binWidthHz = binWidthHz;
        _processor = new SweepSpectrumProcessor(blockFftLength);
        _scratch = new float[_processor.BinsPerBlock];

        int bins = Math.Max(1, (int)Math.Ceiling((stopHz - startHz) / binWidthHz));
        _front = new float[bins];
        _back = new float[bins];
        PublishInterval = publishInterval ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>Minimum time between published frames.</summary>
    public TimeSpan PublishInterval { get; }

    /// <summary>Bins in the assembled grid.</summary>
    public int BinCount => _front.Length;

    /// <summary>Display bin width in Hz.</summary>
    public double BinWidthHz => _binWidthHz;

    /// <summary>Grid lower edge.</summary>
    public long StartHz => _startHz;

    /// <summary>Grid upper edge.</summary>
    public long StopHz => _stopHz;

    /// <summary>Processor used to turn blocks into bins; exposed so tests can feed bins directly.</summary>
    public SweepSpectrumProcessor Processor => _processor;

    /// <summary>Turn one block's IQ bytes into grid bins.</summary>
    public void AddBlock(SweepBlock block, ReadOnlySpan<byte> iq)
    {
        lock (_gate)
        {
            _processor.Process(iq, _scratch);
            float[] target = _activeIndex == 0 ? _front : _back;
            _processor.Scatter(block, _scratch, target, _startHz, _binWidthHz);
        }
    }

    /// <summary>Is the next frame due yet?</summary>
    public bool IsDue(DateTimeOffset now) =>
        (now.UtcTicks - Interlocked.Read(ref _lastPublishTicks)) >= PublishInterval.Ticks;

    /// <summary>
    /// Publish the grid as a frame and start the next one empty. The frame owns its array, so the
    /// UI can keep it while the DSP thread carries on.
    /// </summary>
    public SpectrumFrame Publish(DateTimeOffset now)
    {
        float[] snapshot;
        float[] next;
        lock (_gate)
        {
            snapshot = _activeIndex == 0 ? _front : _back;
            int other = _activeIndex == 0 ? 1 : 0;
            _activeIndex = other;
            next = other == 0 ? _front : _back;
            Interlocked.Exchange(ref _lastPublishTicks, now.UtcTicks);
        }

        float[] db = new float[snapshot.Length];
        int filled = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            float linear = snapshot[i];
            if (linear > 0)
            {
                db[i] = (float)IqMath.PowerToDb(linear);
                filled++;
            }
            else
            {
                db[i] = SpectrumFrame.EmptyBin;
            }
        }

        next.AsSpan().Clear();

        return new SpectrumFrame
        {
            StartHz = _startHz,
            StopHz = _stopHz,
            BinWidthHz = _binWidthHz,
            PowerDb = db,
            FilledBins = filled,
            UtcTimestamp = now,
        };
    }

    /// <summary>Empty the grid (call when the sweep plan changes).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _front.AsSpan().Clear();
            _back.AsSpan().Clear();
            Interlocked.Exchange(ref _lastPublishTicks, 0);
        }
    }
}


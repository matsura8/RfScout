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
    /// Max-merge one block's bins into a shared grid, where 0 means "not measured yet" and any
    /// positive value is a measurement (the same convention <see cref="SpectrumFrame.EmptyBin"/>
    /// converts). Called by <see cref="SpectrumAssembler"/> and by tests that want to check where
    /// a bin landed.
    /// </summary>
    /// <returns>How many grid bins went from unmeasured to measured, which is how the assembler
    /// knows a pass over the span is complete.</returns>
    public int Scatter(SweepBlock block, ReadOnlySpan<float> blockBins, float[] grid, long startHz, double gridBinWidthHz)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (blockBins.Length != BinsPerBlock)
        {
            throw new ArgumentException($"Expected {BinsPerBlock} block bins, got {blockBins.Length}.", nameof(blockBins));
        }

        // A block bin is a bar BinWidthHz wide centred on BinOffsetHz, so it belongs to every grid
        // bin that bar overlaps. Reducing that to "the one bin its centre falls in" loses a quarter
        // of the span when the grid is coarser than the block bins: the block bins sit on a fixed
        // 19531.25 Hz comb, and when the grid pitch is a whole multiple of that comb only every
        // third bin receives a value, so the spectrum shows a regular comb of holes, the peak
        // detector sees a sawtooth noise floor, and a sweep never reports itself complete because
        // the skipped bins never fill.
        double blockBinHz = BinWidthHz;
        double halfBlockBin = blockBinHz / 2;

        int newlyMeasured = 0;
        for (int i = 0; i < blockBins.Length; i++)
        {
            float power = blockBins[i];
            if (power <= 0f)
            {
                continue; // nothing to record, and recording it would claim coverage
            }

            double centreHz = block.HeaderFrequencyHz + SweepMath.BinOffsetHz(FftLength, i);

            // Bin the bar's two edges instead of its centre. That covers every grid bin the bar
            // overlaps: the bar is never wider than the grid bins it can span here, so when the
            // edges land in different bins those bins are neighbours and everything between them
            // lies inside the bar.
            // Floor, not a cast: a plain (int) rounds toward zero, so a bar straddling the grid's
            // lower edge (the first kept bin sits exactly on the header) would lose the half of
            // itself that lies below it, and a bar straddling a bin boundary above zero would be
            // credited to the bin above only.
            int first = (int)Math.Floor((centreHz - halfBlockBin - startHz) / gridBinWidthHz);
            int last = (int)Math.Floor((centreHz + halfBlockBin - startHz) / gridBinWidthHz);

            for (int index = first; index <= last; index++)
            {
                if ((uint)index >= (uint)grid.Length)
                {
                    continue;
                }

                if (grid[index] <= 0f)
                {
                    newlyMeasured++;
                }

                if (power > grid[index])
                {
                    grid[index] = power;
                }
            }
        }

        return newlyMeasured;
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
/// Every block covers one slice of the span, so the assembler scatters block bins into one grid
/// that keeps accumulating and publishes a snapshot on a fixed interval: a sweep need not finish
/// before the display updates, which is what keeps a slow pass usable (hackrf_sweep behaves the
/// same way). What matters is when that grid resets, and the answer is "when a pass is complete",
/// not "every refresh". A 1-6000 MHz pass at 20 Msps is about 600 blocks and a quarter of a second,
/// so clearing on every 100 ms publish would have shown a third of the span and thrown the rest
/// away, and a narrow span would be reset dozens of times per pass. Until a pass is whole, frames
/// report <see cref="SpectrumFrame.SweepCompleted"/> = false, and anything that needs complete
/// sweeps - the noise-floor percentile, the sweep rate in the status bar - waits for one.
/// </summary>
public sealed class SpectrumAssembler
{
    private readonly SweepSpectrumProcessor _processor;
    private readonly double _binWidthHz;
    private readonly long _startHz;
    private readonly long _stopHz;
    private readonly float[] _scratch;
    private readonly float[] _grid;
    private readonly Lock _gate = new();
    private int _measuredBins;
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
        _grid = new float[bins];
        PublishInterval = publishInterval ?? TimeSpan.FromMilliseconds(100);

        // Zero, not "now": the first frame after a start or a plan change should appear as soon
        // as there is something in it rather than after a whole refresh interval of empty screen.
        _lastPublishTicks = 0;
    }

    /// <summary>Minimum time between published frames.</summary>
    public TimeSpan PublishInterval { get; }

    /// <summary>Bins in the assembled grid.</summary>
    public int BinCount => _grid.Length;

    /// <summary>Display bin width in Hz.</summary>
    public double BinWidthHz => _binWidthHz;

    /// <summary>Grid lower edge.</summary>
    public long StartHz => _startHz;

    /// <summary>Grid upper edge.</summary>
    public long StopHz => _stopHz;

    /// <summary>Processor used to turn blocks into bins; exposed so tests can feed bins directly.</summary>
    public SweepSpectrumProcessor Processor => _processor;

    /// <summary>Bins measured since the grid was last cleared, i.e. how far through a pass we are.</summary>
    public int MeasuredBins
    {
        get
        {
            lock (_gate)
            {
                return _measuredBins;
            }
        }
    }

    /// <summary>True once every bin has been measured; the next publish closes the sweep.</summary>
    public bool IsSweepComplete
    {
        get
        {
            lock (_gate)
            {
                return _measuredBins >= _grid.Length;
            }
        }
    }

    /// <summary>Turn one block's IQ bytes into grid bins.</summary>
    public void AddBlock(SweepBlock block, ReadOnlySpan<byte> iq)
    {
        lock (_gate)
        {
            _processor.Process(iq, _scratch);
            _measuredBins += _processor.Scatter(block, _scratch, _grid, _startHz, _binWidthHz);
        }
    }

    /// <summary>
    /// Is the next frame due? Due when the refresh interval has passed and something new was
    /// measured, or as soon as a pass completes - a completed sweep is worth showing immediately
    /// rather than up to a refresh interval late.
    /// </summary>
    public bool IsDue(DateTimeOffset now)
    {
        bool complete;
        lock (_gate)
        {
            complete = _measuredBins >= _grid.Length;
        }

        if (complete)
        {
            return true;
        }

        long last = Interlocked.Read(ref _lastPublishTicks);
        return (now.UtcTicks - last) >= PublishInterval.Ticks;
    }

    /// <summary>
    /// Publish the grid as a frame. The array is copied, so the UI can keep the frame while the
    /// DSP thread carries on filling the same grid. When this frame completes a pass the grid is
    /// cleared, so the next frame is the next sweep rather than a peak hold of the last hour.
    /// </summary>
    public SpectrumFrame Publish(DateTimeOffset now)
    {
        float[] db;
        int filled;
        bool completed;

        lock (_gate)
        {
            Interlocked.Exchange(ref _lastPublishTicks, now.UtcTicks);

            db = new float[_grid.Length];
            filled = 0;
            for (int i = 0; i < _grid.Length; i++)
            {
                float linear = _grid[i];
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

            completed = _measuredBins >= _grid.Length;
            if (completed)
            {
                _grid.AsSpan().Clear();
                _measuredBins = 0;
            }
        }

        return new SpectrumFrame
        {
            StartHz = _startHz,
            StopHz = _stopHz,
            BinWidthHz = _binWidthHz,
            PowerDb = db,
            FilledBins = filled,
            UtcTimestamp = now,
            SweepCompleted = completed,
        };
    }

    /// <summary>Empty the grid (call when the sweep plan changes).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _grid.AsSpan().Clear();
            _measuredBins = 0;
            Interlocked.Exchange(ref _lastPublishTicks, 0);
        }
    }
}

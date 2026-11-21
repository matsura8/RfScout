using RfScout.Core;
using RfScout.Core.Dsp;
using RfScout.Core.Fakes;
using RfScout.Core.Spectrum;

namespace RfScout.Tests;

/// <summary>
/// End-to-end check of the sweep DSP on synthetic blocks: a tone at a known RF must come back
/// at that RF, at the right level, and the tuned centre must not leak into the display. This is
/// the test that catches a wrong bin mapping or wrong window averaging, both of which look
/// plausible on screen.
/// </summary>
public sealed class SweepSpectrumTests
{
    private const long HeaderHz = 100_000_000;
    private const int FftLength = 1024;
    private const long ToneLowHz = 102_000_000;
    private const long ToneHighHz = 112_000_000;

    [Theory]
    [InlineData(ToneLowHz)]      // inside the low kept quarter: header .. header + 5 MHz
    [InlineData(ToneHighHz)]     // inside the high kept quarter: header + 10 .. + 15 MHz
    public void ToneAppearsAtItsOwnFrequencyAtFullScale(long toneHz)
    {
        var (bins, processor) = ProcessTone(toneHz);
        int index = PeakIndexOf(bins);
        double hz = HeaderHz + SweepMath.BinOffsetHz(processor.FftLength, index);

        Assert.True(
            Math.Abs(hz - toneHz) <= SweepMath.BinWidthHz(FftLength),
            $"tone at {toneHz} Hz appeared at {hz:F0} Hz (bin {index})");

        double dbfs = IqMath.PowerToDb(bins[index]);
        Assert.True(dbfs is > -1 and < 1, $"tone read {dbfs:F3} dBFS, expected 0 +/- 1");
    }

    [Fact]
    public void ToneAtTheTunedCentreIsDroppedWithTheDcQuarter()
    {
        // 107.5 MHz is the centre of the sampled band. hackrf_sweep throws away the half of the
        // FFT around DC, where the centre's own spike and the image land, so a tone there must
        // not raise any bin.
        var (bins, _) = ProcessTone(HeaderHz + SweepMath.SweepOffsetHz);
        Assert.DoesNotContain(bins, b => b > 1e-3);
    }

    [Fact]
    public void AveragingUsesEveryWindowInTheBlock()
    {
        const int fft = 1024;

        // Exactly 200 cycles per 1024-sample window: a bin centre, and inside the high kept
        // quarter (signed bins n/8..3n/8-1), which is where the sweep path actually looks. Every
        // window of the block then sees the same spectrum, so averaging must be exact.
        const double cyclesPerSample = 200.0 / 1024.0;
        byte[] iq = SyntheticSignal.IqBytes(SweepMath.SamplesPerBlock, cyclesPerSample);

        var processor = new SweepSpectrumProcessor(fft);
        float[] bins = new float[processor.BinsPerBlock];
        processor.Process(iq, bins);

        // With a tone at an exact bin, every window sees the same spectrum, so averaging must be
        // exact: the peak sits at the tone's bin and no other bin holds anything.
        int expectedBin = ExpectedBinOf(cyclesPerSample, fft);
        int peak = PeakIndexOf(bins);
        Assert.Equal(expectedBin, peak);

        // A periodic Hann window is 0.5 - 0.25z^-1 - 0.25z^+1, so a tone exactly on a bin puts
        // exactly a quarter of its power in each neighbour and nothing anywhere else. Asserting
        // that pattern - rather than "everything else is small" - is what would have caught the
        // accumulate-versus-overwrite bug and a wrong kept-quarter mapping.
        double peakPower = bins[peak];
        Assert.Equal(0.25, bins[peak - 1] / peakPower, 3);
        Assert.Equal(0.25, bins[peak + 1] / peakPower, 3);
        for (int i = 0; i < bins.Length; i++)
        {
            if (i >= peak - 1 && i <= peak + 1)
            {
                continue;
            }

            // What is left away from the main lobe is the s8 quantisation floor of the synthetic
            // block, about -120 dB relative to the peak per bin, so the bound has to sit above
            // that and still far below the 0.25 leakage pattern.
            Assert.True(
                bins[i] < 1e-4 * peakPower,
                $"bin {i} holds {bins[i]:E2} against a peak of {peakPower:E2} (ratio {bins[i] / peakPower:E2})");
        }

        // A full-scale tone, Hann-windowed and normalised, reads 0 dBFS.
        Assert.True(IqMath.PowerToDb(peakPower) is > -1 and < 1);
    }

    [Fact]
    public void ProcessReadsWholeWindowsOnly()
    {
        // 8187 samples at 1024 per window is 7 windows and 119 leftover samples. If the loop
        // stepped by FftLength *bytes* instead of samples it would overlap windows and read past
        // the end of the block.
        const int fft = 1024;
        var processor = new SweepSpectrumProcessor(fft);
        Assert.Equal(SweepMath.SamplesPerBlock / fft, processor.WindowsPerBlock);

        byte[] iq = SyntheticSignal.IqBytes(SweepMath.SamplesPerBlock, 0.03);
        float[] bins = new float[processor.BinsPerBlock];
        processor.Process(iq, bins);
        Assert.True(PeakIndexOf(bins) >= 0);

        // A block shorter than one window still produces a (zero) result rather than throwing.
        byte[] shortIq = SyntheticSignal.IqBytes(fft - 1, 0.03);
        processor.Process(shortIq, bins);
        Assert.All(bins, b => Assert.Equal(0f, b));
    }

    [Fact]
    public void ScatterPutsABlockBinWhereTheFrameSaysItIs()
    {
        const int fft = 1024;
        var processor = new SweepSpectrumProcessor(fft);
        var block = SyntheticSignal.SweepBlock(HeaderHz, SweepMath.SamplesPerBlock, 0.03);

        float[] bins = new float[processor.BinsPerBlock];
        for (int i = 0; i < bins.Length; i++)
        {
            bins[i] = 1f;
        }

        const long gridStartHz = 90_000_000;
        const double gridBinHz = 25_000;
        float[] grid = new float[1_000];
        processor.Scatter(block, bins, grid, gridStartHz, gridBinHz);

        for (int i = 0; i < bins.Length; i++)
        {
            double hz = HeaderHz + SweepMath.BinOffsetHz(fft, i);
            int index = (int)((hz - gridStartHz) / gridBinHz);
            Assert.True(grid[index] > 0, $"bin {i} at {hz:F0} Hz did not land in the grid");
        }

        // Nothing outside the two kept quarters is written.
        int lowStart = (int)((HeaderHz - gridStartHz) / gridBinHz);
        Assert.All(grid[..lowStart], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void AssemblerPublishesFramesAndKeepsTheTwoBuffersIndependent()
    {
        var assembler = new SpectrumAssembler(90_000_000, 120_000_000, FftLength, 25_000);
        var block = SyntheticSignal.BlockWithTone(HeaderHz, FftLength, ToneLowHz);

        assembler.AddBlock(block, block.IqSamples.Span);
        SpectrumFrame frame = assembler.Publish(DateTimeOffset.UtcNow);

        Assert.Equal(90_000_000, frame.StartHz);
        Assert.Equal(120_000_000, frame.StopHz);
        Assert.Equal(assembler.BinCount, frame.BinCount);
        Assert.True(frame.FilledBins > 0);

        int index = IndexOfLoudest(frame);
        double hz = frame.CenterFrequencyOfBin(index);
        Assert.True(Math.Abs(hz - ToneLowHz) <= 2 * 25_000, $"frame peak at {hz:F0} Hz");

        // The frame owns its array, so publishing again must not disturb what the UI is holding.
        float[] held = frame.PowerDb;
        SpectrumFrame next = assembler.Publish(DateTimeOffset.UtcNow.AddMilliseconds(200));
        Assert.NotSame(held, next.PowerDb);
        Assert.True(held[index] > float.MinValue);
    }

    [Fact]
    public void EmptyBinsAreMarkedNotZero()
    {
        var assembler = new SpectrumAssembler(90_000_000, 120_000_000, FftLength, 25_000);
        SpectrumFrame frame = assembler.Publish(DateTimeOffset.UtcNow);
        Assert.Equal(0, frame.FilledBins);
        Assert.All(frame.PowerDb, v => Assert.Equal(SpectrumFrame.EmptyBin, v));
        Assert.True(frame.IsEmpty(0));
    }

    [Fact]
    public void PeakHoldMergeKeepsTheLoudestSighting()
    {
        var assembler = new SpectrumAssembler(90_000_000, 120_000_000, FftLength, 25_000);
        var block = SyntheticSignal.BlockWithTone(HeaderHz, FftLength, ToneLowHz);
        assembler.AddBlock(block, block.IqSamples.Span);
        SpectrumFrame first = assembler.Publish(DateTimeOffset.UtcNow);

        // Second sweep sees nothing: the merge must still show the tone from the first.
        SpectrumFrame empty = assembler.Publish(DateTimeOffset.UtcNow.AddMilliseconds(200));
        SpectrumFrame merged = empty.MergePeakHold(first);

        int index = IndexOfLoudest(first);
        Assert.Equal(first.PowerDb[index], merged.PowerDb[index]);
        Assert.True(merged.FilledBins >= first.FilledBins);
    }

    [Fact]
    public void SyntheticSourceReplaysASweepAndRaisesOneFrame()
    {
        var source = new SyntheticRadioSource(100_000_000, 100_000_000 + (2 * SyntheticRadioSource.DefaultStepHz));
        SpectrumFrame? received = null;
        source.SpectrumAvailable += (_, e) => received = e.Frame;

        var plan = new SweepPlan([new FrequencyRange(100_000_000, 100_000_000 + (2 * SyntheticRadioSource.DefaultStepHz))], BinWidthHz: 25_000);
        source.StartSweep(plan);

        Assert.NotNull(received);
        Assert.True(received!.FilledBins > 0);
        Assert.False(source.IsRunning);
        Assert.Equal(2, source.BlocksReplayed);
    }

    [Fact]
    public void ASecondStreamWhileOneIsRunningIsRejected()
    {
        using var source = new SyntheticRadioSource(100_000_000, 200_000_000);

        // The frame event fires while the source is still running, the way a real one does, so a
        // handler that tries to start something else gets the error rather than two streams.
        source.SpectrumAvailable += (_, __) => Assert.Throws<InvalidOperationException>(
            () => source.StartCapture(150_000_000, 1_000_000, TimeSpan.FromMilliseconds(1)));

        source.StartSweep(new SweepPlan([new FrequencyRange(100_000_000, 105_000_000)], BinWidthHz: 25_000));

        // Once it has finished, the same source can start again.
        source.StartCapture(150_000_000, 1_000_000, TimeSpan.FromMilliseconds(1));
        Assert.False(source.IsRunning);
    }

    [Fact]
    public void SyntheticSourceValidatesItsArguments()
    {
        using var source = new SyntheticRadioSource(100_000_000, 200_000_000);
        Assert.Throws<ArgumentException>(() => new SyntheticRadioSource(200_000_000, 100_000_000));
        Assert.Throws<ArgumentNullException>(() => source.StartSweep(null!));
        Assert.Throws<ArgumentException>(() => source.StartSweep(new SweepPlan([], BinWidthHz: 25_000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.StartCapture(150_000_000, 0, TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void DeviceLostRaisesTheStoppedEvent()
    {
        using var source = new SyntheticRadioSource(100_000_000, 200_000_000);
        RadioStoppedEventArgs? args = null;
        source.Stopped += (_, e) => args = e;

        source.SimulateDeviceLost();

        Assert.NotNull(args);
        Assert.True(args!.DeviceLost);
        Assert.False(string.IsNullOrWhiteSpace(args.Reason));
    }

    private static (float[] Bins, SweepSpectrumProcessor Processor) ProcessTone(long toneHz)
    {
        var processor = new SweepSpectrumProcessor(FftLength);
        var block = SyntheticSignal.BlockWithTone(HeaderHz, FftLength, toneHz);
        float[] bins = new float[processor.BinsPerBlock];
        processor.Process(block.IqSamples.Span, bins);
        return (bins, processor);
    }

    /// <summary>
    /// Bin index in the two-quarter output for a tone at <paramref name="cyclesPerSample"/>
    /// relative to the tuned centre: negative frequencies are the low quarter, positive the high
    /// one. The tone is placed a whole number of bins away so no leakage is involved.
    /// </summary>
    private static int ExpectedBinOf(double cyclesPerSample, int fft)
    {
        int signedBin = (int)Math.Round(cyclesPerSample * fft);
        int quarter = fft / 4;

        // Low quarter: output j holds raw bin 5n/8+j, i.e. signed bin j - 3n/8.
        // High quarter: output quarter+j holds raw bin n/8+j, i.e. signed bin j + n/8.
        return signedBin < 0 ? (signedBin + (3 * fft / 8)) : (signedBin - (fft / 8)) + quarter;
    }

    private static int PeakIndexOf(float[] bins)
    {
        int best = 0;
        for (int i = 1; i < bins.Length; i++)
        {
            if (bins[i] > bins[best])
            {
                best = i;
            }
        }

        return best;
    }

    private static int IndexOfLoudest(SpectrumFrame frame)
    {
        int best = 0;
        for (int i = 1; i < frame.BinCount; i++)
        {
            if (frame.PowerDb[i] > frame.PowerDb[best])
            {
                best = i;
            }
        }

        return best;
    }
}

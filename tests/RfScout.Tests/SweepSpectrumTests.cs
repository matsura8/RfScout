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
    // One tone either side of the tuned centre (header + 7.5 MHz), each exactly 256 bins away so
    // no leakage is involved: 5 MHz at 20 Msps with a 1024-point FFT. Both fall inside the kept
    // quarters, which upstream prints as [header, header + fs/4] and
    // [header + fs/2, header + 3fs/4].
    private const long ToneLowHz = 102_500_000;
    private const long ToneHighHz = 112_500_000;

    [Theory]
    [InlineData(ToneLowHz)]      // header + fs/4: the last kept bin of the low band
    [InlineData(ToneHighHz)]     // header + 3fs/4: the last kept bin of the high band
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

    /// <summary>
    /// A block is sampled over 20 MHz but keeps only two 5 MHz bands, [header, header + fs/4] and
    /// [header + fs/2, header + 3fs/4]. Everything else in the sampled band is thrown away, and the
    /// two discarded regions are easy to mistake for measured air because they are in the middle of
    /// the range the block nominally covers. A tone there must not raise any kept bin: if it did,
    /// one emission would appear twice on the display at two different frequencies, which is the
    /// classic sweep-mode artefact.
    /// </summary>
    [Theory]
    [InlineData(-77)]   // middle gap, 1.5 MHz below the tuned centre
    [InlineData(77)]    // middle gap, 1.5 MHz above it
    [InlineData(-486)]  // bottom of the sampled band, below the first kept band
    [InlineData(486)]   // top of the sampled band, above the second kept band
    public void ToneOutsideTheKeptBandsIsDroppedWithTheImage(int signedBin)
    {
        // Kept bins are signed -384..-129 and 128..255 at this FFT length, so the discarded parts of
        // the sampled band are -512..-385, -128..127 and 256..511. The probes are written in bins
        // rather than MHz because the tone has to land on an exact bin: 1.5 MHz is 76.8 bins, and a
        // tone between bins leaks across the whole transform, which would test the window rather
        // than the discard. On its own bin it is steady in every window of the block, so what comes
        // back is the block's floor. A tone aliased into a kept quarter instead of dropped would
        // read near 0 dBFS, and the display would show one emission twice, 10 MHz apart.
        double cyclesPerSample = (double)signedBin / FftLength;
        byte[] iq = SyntheticSignal.IqBytes(SweepMath.SamplesPerBlock, cyclesPerSample);
        var processor = new SweepSpectrumProcessor(FftLength);
        float[] powers = new float[processor.BinsPerBlock];
        processor.Process(iq, powers);

        // A full-scale s8 tone puts about -65 dBFS in every bin it does not occupy (quantisation
        // spread over 512 bins), which is the floor this assert measures against. A tone that
        // survived the discard instead of being dropped would read near 0 dBFS, so -40 dBFS is a
        // wide margin in both directions: 25 dB above the floor, 40 dB below a real signal.
        double peakDb = IqMath.PowerToDb(powers[PeakIndexOf(powers)]);
        Assert.True(peakDb < -40, $"discarded tone at signed bin {signedBin} raised a bin to {peakDb:F1} dBFS");
    }

    /// <summary>
    /// And the boundaries of the two kept bands are measured, not dropped, up to the bin step
    /// upstream skips to avoid the DC-adjacent bin: the first kept bin of a group is one step
    /// inside the printed band edge.
    /// </summary>
    [Theory]
    [InlineData(HeaderHz + 1_000_000)]    // inside the first kept band
    [InlineData(HeaderHz + 4_000_000)]    // near its top edge
    [InlineData(HeaderHz + 11_000_000)]   // inside the second kept band
    [InlineData(HeaderHz + 14_000_000)]   // near its top edge
    public void ToneInsideEitherKeptBandIsMeasuredAtItsOwnFrequency(long toneHz)
    {
        var (bins, processor) = ProcessTone(toneHz);
        int index = PeakIndexOf(bins);
        double hz = HeaderHz + SweepMath.BinOffsetHz(processor.FftLength, index);

        Assert.True(
            Math.Abs(hz - toneHz) <= SweepMath.BinWidthHz(FftLength),
            $"tone at {toneHz} Hz appeared at {hz:F0} Hz (bin {index})");
        Assert.True(IqMath.PowerToDb(bins[index]) > -1, $"tone at {toneHz} was not at full scale");
    }

    [Fact]
    public void ToneAtTheTunedCentreIsDroppedWithTheDcSpike()
    {
        // The centre of the sampled band is raw FFT bin 0, and each kept group starts one bin
        // above its quarter edge, so the centre itself is dropped along with the DC spike. It is
        // the one frequency a sweep cannot see, which is why the interleaved headers step past it
        // instead of dwelling on it: the odd blocks put their centre on the part the block before
        // discarded.
        var (bins, _) = ProcessTone(HeaderHz + SweepMath.SweepOffsetHz);
        double peakDb = IqMath.PowerToDb(bins[PeakIndexOf(bins)]);
        Assert.True(peakDb < -60, $"centre tone read {peakDb:F1} dBFS, expected dropped");
    }

    [Fact]
    public void EveryKeptBinMapsToTheFrequencyUpstreamWouldPrint()
    {
        // The mapping and the FFT's output order have to agree bin for bin, at both ends of both
        // quarters. A tone is planted at each of those signed bins and must come back at its own
        // frequency - which is the tuned centre plus the signed bin times the bin width.
        var processor = new SweepSpectrumProcessor(FftLength);
        double binWidth = SweepMath.BinWidthHz(FftLength);

        foreach (int signedBin in KeptProbeBins())
        {
            double cyclesPerSample = (double)signedBin / FftLength;
            byte[] iq = SyntheticSignal.IqBytes(SweepMath.SamplesPerBlock, cyclesPerSample);
            float[] powers = new float[processor.BinsPerBlock];
            processor.Process(iq, powers);

            int index = PeakIndexOf(powers);
            Assert.Equal(ExpectedBinOf(cyclesPerSample, FftLength), index);
            Assert.Equal(SweepMath.SweepOffsetHz + (signedBin * binWidth), SweepMath.BinOffsetHz(FftLength, index), 3);
        }
    }

    /// <summary>
    /// The first and last kept bin of each of the two groups, as signed bins about the tuned
    /// centre. These are the bins a wrong mapping moves, so they are the ones worth probing.
    ///
    /// <see cref="FloatFft.PowerQuarters"/> extracts raw bins <c>5N/8 .. 7N/8-1</c> (the negative
    /// frequencies) and <c>N/8 .. 3N/8-1</c> (the positive ones) - two quarters of N/4 bins each,
    /// which is the fs/2 a sweep block measures. Upstream's <c>hackrf_sweep</c> prints the rows one
    /// bin higher, <c>1 + 5N/8 .. 5N/8 + N/4</c> and <c>1 + N/8 .. N/8 + N/4</c>: it skips the bin
    /// on the quarter edge because that bin is next to DC and its skirt is the tuned centre's own
    /// spike, which is also why the centre itself is not measured at all. These probes are written
    /// against the extraction, because that is what the display is built from.
    /// </summary>
    private static int[] KeptProbeBins()
    {
        int n = FftLength;
        return
        [
            -3 * n / 8,                    // low quarter, first kept
            -(n / 8) - 1,                  // low quarter, last kept
            n / 8,                         // high quarter, first kept
            (n / 4) - 1,                   // high quarter, last kept
        ];
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
        float[] grid = new float[1_200];   // 90..120 MHz, so the top edge of the band is inside the grid
        processor.Scatter(block, bins, grid, gridStartHz, gridBinHz);

        for (int i = 0; i < bins.Length; i++)
        {
            double hz = HeaderHz + SweepMath.BinOffsetHz(fft, i);
            int index = (int)((hz - gridStartHz) / gridBinHz);

            // >= 0, not > 0: the first kept bin of the low group sits on the header itself, so it
            // lands on the grid bin that starts there rather than strictly inside it. A strict >
            // would fail on the bin that is exactly on a band edge - which is most of what makes
            // interleaved blocks tile without gaps or overlaps.
            Assert.True(index >= 0, $"bin {i} at {hz:F0} Hz fell below the grid");
            Assert.True(grid[index] > 0, $"bin {i} at {hz:F0} Hz did not land in the grid");
        }

        // Nothing outside the two kept quarters is written. The first kept bar straddles the header
        // - half a block bin either side - so it legitimately reaches the last grid bin below the
        // header, and everything before that bin must stay empty. That one bin is the boundary this
        // assert is really about: give the mapping a half-bin offset and the emptiness starts one
        // bin later, which is how it earns its place.
        double lowEdge = (HeaderHz - gridStartHz) + SweepMath.FirstBinEdgeHz - (SweepMath.BinWidthHz(fft) / 2);
        int firstTouched = (int)Math.Floor(lowEdge / gridBinHz);
        Assert.Equal(399, firstTouched);
        Assert.All(grid[..firstTouched], v => Assert.Equal(0f, v));
        Assert.True(grid[firstTouched] > 0f, "the bar straddling the header did not reach the bin below it");

        // And above the second quarter: the last bar ends half a block bin short of header + 3fs/4,
        // so the first grid bin at or above that edge - and everything past it - is untouched.
        double topEdge = (HeaderHz - gridStartHz) + ((3.0 * SweepMath.SampleRateHz) / 4.0) - (SweepMath.BinWidthHz(fft) / 2);
        int firstAbove = (int)Math.Ceiling(topEdge / gridBinHz);
        Assert.Equal(1_000, firstAbove);
        Assert.All(grid[firstAbove..], v => Assert.Equal(0f, v));
        Assert.True(grid[firstAbove - 1] > 0f, "the last bar did not reach the bin under its top edge");
    }

    /// <summary>
    /// The radio's bins are 19531.25 Hz wide and sit on a fixed comb. A display grid of 625 kHz is
    /// exactly 32 of them wide, and binning each block bin into only the grid bin its centre falls
    /// in then touches every 32nd bin and skips the rest - a comb of holes one quarter of the
    /// display, a sawtooth noise floor in peak-hold, and a sweep that never reports itself
    /// complete. A bar has to fill every grid bin it overlaps.
    /// </summary>
    [Fact]
    public void AGridCoarserThanTheBlockBinsIsFilledAcrossTheWholeBar()
    {
        const double gridBinHz = 625_000;
        var (measured, grid) = ScatterFlatBlock(gridBinHz, 64);

        // A block's first bar straddles the header (its first bin centre sits on that edge), so at
        // 625 kHz it fills bins 0..7 and stops short of bin 8, which starts exactly at 5 MHz. The
        // second bar straddles 10 MHz and runs to half a block bin short of 15 MHz, so bins 15..23
        // are measured.
        AssertFilledRun(grid, 0, 8);
        AssertFilledRun(grid, 15, 9);

        // The discarded middle stays empty apart from that half-bin overlap at the band edge, and so
        // does everything past 15 MHz: filling either would report unmeasured air as a measurement.
        Assert.All(grid[8..14], v => Assert.Equal(0f, v));
        Assert.All(grid[24..], v => Assert.Equal(0f, v));

        // 8 + 9 bins, which is what two 5 MHz bands look like on a 625 kHz grid - not 512 single-bin
        // writes, and not the third-of-the-span comb that binning each bar by its centre alone left.
        Assert.Equal(17, measured);
    }

    /// <summary>
    /// Bin indices must be floored, not truncated toward zero. A plain cast rounds toward zero, so a
    /// bar that stops just short of a grid''s first bin is reported as reaching it - inventing
    /// coverage on the first bin of every sweep, and losing a bin of real coverage at the top edge.
    /// The first kept bin of a block sits exactly on the header, which puts exactly one bar in
    /// exactly that position, so the test isolates that one bar.
    /// </summary>
    [Fact]
    public void ABarShortOfTheGridStartDoesNotLeakIntoTheFirstBin()
    {
        var processor = new SweepSpectrumProcessor(FftLength);
        var block = SyntheticSignal.SweepBlock(HeaderHz, SweepMath.SamplesPerBlock, 0.03);

        // One bar only: the first kept bin, centred on the header and half a block bin wide either
        // side, so it ends 9765.625 Hz above the header.
        float[] oneBar = new float[processor.BinsPerBlock];
        oneBar[0] = 1f;

        // Grid starting 10 kHz above the header: the bar lies entirely below it, so bin 0 - which
        // covers header+10 kHz .. header+35 kHz - must stay empty.
        float[] above = new float[4];
        processor.Scatter(block, oneBar, above, HeaderHz + 10_000, 25_000);
        Assert.Equal(0f, above[0]);

        // Grid starting 3 kHz below the header: the bar straddles that edge, so bin 0 must be
        // measured - the half of the bar below the grid start is still a measurement.
        float[] straddling = new float[4];
        processor.Scatter(block, oneBar, straddling, HeaderHz - 3_000, 25_000);
        Assert.True(straddling[0] > 0f, "the bar straddling the grid start left the first grid bin empty");
    }

    [Fact]
    public void AGridFinerThanTheBlockBinsIsFilledAcrossTheWholeBarToo()
    {
        const double gridBinHz = 250_000;
        var (measured, grid) = ScatterFlatBlock(gridBinHz, 100);

        // Same two bands at 250 kHz: 0..19 and 39..59, with bins 20..38 and 60.. unmeasured. The
        // second band opens half a block bin into bin 39. A finer grid must not invent coverage
        // either - the bars are the same width, only more of them.
        AssertFilledRun(grid, 0, 20);
        AssertFilledRun(grid, 39, 21);
        Assert.All(grid[20..38], v => Assert.Equal(0f, v));
        Assert.All(grid[60..], v => Assert.Equal(0f, v));
        Assert.Equal(41, measured);
    }

    /// <summary>
    /// Scatter one block whose bins are all equal (so every kept bin counts as measured) into a
    /// grid of the given width, and report how many bins were filled.
    /// </summary>
    private static (int Measured, float[] Grid) ScatterFlatBlock(double gridBinHz, int gridBins)
    {
        var processor = new SweepSpectrumProcessor(FftLength);
        var block = SyntheticSignal.SweepBlock(HeaderHz, SweepMath.SamplesPerBlock, 0.03);
        float[] bins = new float[processor.BinsPerBlock];
        bins.AsSpan().Fill(1f);

        float[] grid = new float[gridBins];
        return (processor.Scatter(block, bins, grid, HeaderHz, gridBinHz), grid);
    }

    /// <summary>Every bin of <paramref name="count"/> starting at <paramref name="start"/> holds a measurement.</summary>
    private static void AssertFilledRun(float[] grid, int start, int count)
    {
        for (int i = start; i < (start + count) && i < grid.Length; i++)
        {
            Assert.True(grid[i] > 0f, $"grid bin {i} is a hole inside a measured band");
        }
    }

    [Fact]
    public void PublishedFramesOwnTheirArraysAndSurviveTheNextPublish()
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

        // The UI keeps frames (the peak-hold trace, the waterfall's newest row), so a publish has
        // to hand over an array of its own rather than the one the DSP thread is still filling.
        float[] held = frame.PowerDb;
        SpectrumFrame next = assembler.Publish(DateTimeOffset.UtcNow.AddMilliseconds(200));
        Assert.NotSame(held, next.PowerDb);
        Assert.True(held[index] > float.MinValue);
    }

    /// <summary>
    /// The refresh interval must not delay the first frame of a sweep: an operator who presses
    /// Start should see the spectrum as soon as the first blocks land, not a blank plot for a
    /// whole interval.
    /// </summary>
    [Fact]
    public void TheFirstFrameIsDueImmediatelyRatherThanAfterOneInterval()
    {
        var assembler = new SpectrumAssembler(90_000_000, 120_000_000, FftLength, 25_000);
        DateTimeOffset start = DateTimeOffset.UtcNow;

        Assert.True(assembler.IsDue(start), "a brand new assembler reported itself not due");

        assembler.Publish(start);
        Assert.False(assembler.IsDue(start.AddMilliseconds(99)), "due again before the interval had passed");
        Assert.True(assembler.IsDue(start.AddMilliseconds(101)));
    }

    /// <summary>
    /// A pass over a wide span takes longer than the refresh interval, so frames are published
    /// mid-pass. What must not happen is the grid being cleared at each publish: that is what the
    /// assembler used to do, and it meant a 1-6000 MHz sweep was never seen whole - each frame
    /// threw away the part of the span the frame before had just measured.
    /// </summary>
    [Fact]
    public void APartialPassKeepsEarlierBinsInsteadOfRestartingEmpty()
    {
        // 30 MHz span at 25 kHz bins = 1200 bins. One block measures two 5 MHz bands, so it fills
        // 400 of them: a third of a pass.
        var assembler = new SpectrumAssembler(90_000_000, 120_000_000, FftLength, 25_000);
        Assert.Equal(1_200, assembler.BinCount);

        var first = SyntheticSignal.SweepBlock(90_000_000, SweepMath.SamplesPerBlock, 0.03);
        assembler.AddBlock(first, first.IqSamples.Span);
        SpectrumFrame partial = assembler.Publish(DateTimeOffset.UtcNow);

        Assert.False(partial.SweepCompleted, "a third of the span was reported as a finished sweep");
        Assert.InRange(partial.FilledBins, 398, 402);
        Assert.Equal(partial.FilledBins, assembler.MeasuredBins);
        Assert.False(assembler.IsSweepComplete);

        // Nothing new arrives, and the next frame still shows what was measured before.
        SpectrumFrame again = assembler.Publish(DateTimeOffset.UtcNow.AddMilliseconds(100));
        Assert.Equal(partial.FilledBins, again.FilledBins);
        int peak = IndexOfLoudest(partial);
        Assert.Equal(partial.PowerDb[peak], again.PowerDb[peak]);
    }

    [Fact]
    public void TheFrameThatCoversEveryBinClosesTheSweepAndResetsTheGrid()
    {
        // A block measures [header, header + 5 MHz] and [header + fs/2, header + 3fs/4], so
        // stepping the header by 5 MHz walks the bands along the span with no gap: four blocks
        // from 90 MHz cover 90..110, 95..115, 100..120 and 105..125 MHz, which together fill the
        // 90..120 MHz grid.
        var assembler = new SpectrumAssembler(90_000_000, 120_000_000, FftLength, 25_000);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        SpectrumFrame? closing = null;
        for (int block = 0; block < 4 && closing is null; block++)
        {
            long header = 90_000_000 + (block * 5_000_000);

            // Off-bin-centre tone (0.03 cycles/sample): a tone exactly on a bin leaves far bins at
            // zero, and a zero bin reads as "not measured" rather than as air.
            var next = SyntheticSignal.SweepBlock(header, SweepMath.SamplesPerBlock, 0.03);
            assembler.AddBlock(next, next.IqSamples.Span);

            if (assembler.IsDue(now))
            {
                SpectrumFrame frame = assembler.Publish(now);
                if (frame.SweepCompleted)
                {
                    closing = frame;
                }
            }
        }

        Assert.NotNull(closing);
        Assert.Equal(assembler.BinCount, closing!.FilledBins);
        Assert.All(closing.PowerDb, v => Assert.False(v == SpectrumFrame.EmptyBin, "a completed sweep still held an empty bin"));

        // The pass is over, so the grid starts again: the next frame is the next sweep rather than
        // a peak hold of everything since Start was pressed.
        SpectrumFrame following = assembler.Publish(now.AddMilliseconds(100));
        Assert.False(following.SweepCompleted);
        Assert.Equal(0, following.FilledBins);
    }

    [Fact]
    public void ResetDiscardsAPartialPassAndItsCompletionState()
    {
        var assembler = new SpectrumAssembler(90_000_000, 120_000_000, FftLength, 25_000);
        var block = SyntheticSignal.BlockWithTone(HeaderHz, FftLength, ToneLowHz);
        assembler.AddBlock(block, block.IqSamples.Span);
        Assert.True(assembler.MeasuredBins > 0);

        assembler.Reset();

        Assert.Equal(0, assembler.MeasuredBins);
        Assert.False(assembler.IsSweepComplete);
        SpectrumFrame frame = assembler.Publish(DateTimeOffset.UtcNow);
        Assert.Equal(0, frame.FilledBins);
        Assert.False(frame.SweepCompleted);
    }

    [Fact]
    public void ScatterCountsEachGridBinAsMeasuredOnlyOnce()
    {
        var processor = new SweepSpectrumProcessor(FftLength);
        var block = SyntheticSignal.SweepBlock(HeaderHz, SweepMath.SamplesPerBlock, 0.03);
        float[] bins = new float[processor.BinsPerBlock];
        bins.AsSpan().Fill(1f);

        float[] grid = new float[1_000];
        int firstPass = processor.Scatter(block, bins, grid, 90_000_000, 25_000);
        Assert.True(firstPass > 0, "the first scatter measured nothing");

        // The same block again covers bins that are already measured, so coverage must not grow -
        // otherwise a sweep would report itself complete after a third of the span.
        Assert.Equal(0, processor.Scatter(block, bins, grid, 90_000_000, 25_000));
        Assert.Equal(0, processor.Scatter(block, bins, grid, 90_000_000, 25_000));
    }

    [Fact]
    public void ScatterIgnoresZeroPowerBinsSoTheyDoNotFakeCoverage()
    {
        var processor = new SweepSpectrumProcessor(FftLength);
        var block = SyntheticSignal.SweepBlock(HeaderHz, SweepMath.SamplesPerBlock, 0.03);
        float[] grid = new float[1_000];

        // A block whose bins are all zero carries no measurement, so it must not advance a pass.
        Assert.Equal(0, processor.Scatter(block, new float[processor.BinsPerBlock], grid, 90_000_000, 25_000));
        Assert.All(grid, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void EmptyBinsAreMarkedNotZero()
    {
        var assembler = new SpectrumAssembler(90_000_000, 120_000_000, FftLength, 25_000);
        SpectrumFrame frame = assembler.Publish(DateTimeOffset.UtcNow);
        Assert.Equal(0, frame.FilledBins);
        Assert.All(frame.PowerDb, v => Assert.Equal(SpectrumFrame.EmptyBin, v));
        Assert.True(frame.IsEmpty(0));

        // "Nothing measured" is the opposite of a finished sweep, and the two must never be
        // confused: an empty frame that claimed completion would let the noise floor train on air.
        Assert.False(frame.SweepCompleted);
    }

    [Fact]
    public void PeakHoldMergeKeepsTheLoudestSighting()
    {
        var assembler = new SpectrumAssembler(90_000_000, 120_000_000, FftLength, 25_000);
        var block = SyntheticSignal.BlockWithTone(HeaderHz, FftLength, ToneLowHz);
        assembler.AddBlock(block, block.IqSamples.Span);
        SpectrumFrame first = assembler.Publish(DateTimeOffset.UtcNow);

        // After a reset the next frame really is empty (the grid accumulates otherwise), so this
        // exercises the merge itself: the peak-hold trace must keep showing the tone the sweep
        // only passed over once.
        assembler.Reset();
        SpectrumFrame empty = assembler.Publish(DateTimeOffset.UtcNow.AddMilliseconds(200));
        Assert.Equal(0, empty.FilledBins);

        SpectrumFrame merged = empty.MergePeakHold(first);
        int index = IndexOfLoudest(first);
        Assert.Equal(first.PowerDb[index], merged.PowerDb[index]);
        Assert.Equal(first.FilledBins, merged.FilledBins);

        // A merged frame is a display artefact, not a measurement pass, so it must not claim the
        // completion of either input.
        Assert.False(merged.SweepCompleted);
        SpectrumFrame completed = new()
        {
            StartHz = first.StartHz,
            StopHz = first.StopHz,
            BinWidthHz = first.BinWidthHz,
            PowerDb = (float[])first.PowerDb.Clone(),
            FilledBins = first.FilledBins,
            UtcTimestamp = first.UtcTimestamp,
            SweepCompleted = true,
        };
        Assert.True(empty.MergePeakHold(completed).SweepCompleted);
        Assert.True(completed.MergePeakHold(empty).SweepCompleted);
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

        // PowerQuarters keeps raw bins 5N/8.. (negative frequencies) in output 0.. and raw bins
        // N/8.. in output N/4+ so a tone at signed bin s is output s + 3N/8 in the low quarter and
        // N/4 + s - N/8 in the high one. SweepMath.BinOffsetHz implements the same rule - each
        // group's first bin on its printed band edge, the rest a whole bin step apart - so the two
        // must agree bin for bin. That is what catches a mapping off by half a bin, which nothing
        // else on screen shows.
        return signedBin < 0
            ? signedBin + (3 * fft / 8)
            : (fft / 4) + signedBin - (fft / 8);
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

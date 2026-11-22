using RfScout.Core;
using RfScout.Core.Fakes;
using RfScout.Core.Spectrum;

namespace RfScout.Tests;

/// <summary>
/// Sweep arithmetic: the numbers that decide where every bin on the display lands. These are
/// cheap to get wrong and impossible to see by eye on a waterfall, so they are pinned here.
/// </summary>
public sealed class SweepMathTests
{
    [Theory]
    [InlineData(100_000, 256)]
    [InlineData(78_125, 256)]
    [InlineData(50_000, 512)]
    [InlineData(SweepMath.MinBinWidthHz, 4096)]
    [InlineData(SweepMath.MaxBinWidthHz, 8)]
    [InlineData(1, 4096)]           // below the finest width: clamps to the longest FFT
    [InlineData(10_000_000, 8)]     // above the coarsest: clamps to the shortest
    public void FftLengthForBinWidthPicksAPowerOfTwo(int binWidthHz, int expectedFft) =>
        Assert.Equal(expectedFft, SweepMath.FftLengthForBinWidth(binWidthHz));

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    [InlineData(250_000)]
    [InlineData(1_000_000)]
    public void RealisedBinWidthIsNeverCoarserThanAskedFor(int binWidthHz)
    {
        int fft = SweepMath.FftLengthForBinWidth(Math.Max(binWidthHz, SweepMath.MinBinWidthHz));
        double realised = SweepMath.BinWidthHz(fft);
        Assert.True(realised <= binWidthHz + 1, $"{binWidthHz} Hz asked for, {realised} Hz delivered");
        Assert.True(fft is >= SweepMath.MinFftLength and <= SweepMath.MaxFftLength);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(3, 8)]
    [InlineData(5, 8)]
    [InlineData(8, 8)]
    [InlineData(100, 128)]
    [InlineData(200, 256)]
    [InlineData(4096, 4096)]
    [InlineData(5000, 4096)]
    [InlineData(100_000, 4096)]
    public void ClampFftLengthRoundsUpToPowerOfTwoWithinBounds(int fftLength, int expected) =>
        Assert.Equal(expected, SweepMath.ClampFftLength(fftLength));

    [Fact]
    public void BinWidthAndWindowCountsAreConsistent()
    {
        Assert.Equal(78_125, SweepMath.BinWidthHz(256), 3);
        Assert.Equal(128, SweepMath.BinsPerWindow(256));
        Assert.Equal(SweepMath.SamplesPerBlock / 256, SweepMath.WindowsPerBlock(256));
        Assert.Equal(1, SweepMath.WindowsPerBlock(4096));
        Assert.Equal(8187, SweepMath.SamplesPerBlock);
        Assert.Equal(16_384, SweepMath.BytesPerBlock);
    }

    /// <summary>
    /// The bin mapping, checked against the numbers <c>hackrf_sweep.c</c> actually uses rather
    /// than against a reading of the format. A block is sampled while the radio is tuned to
    /// <c>header + 7.5 MHz</c> but reports only the bare header, so its FFT is centred there. The
    /// tool keeps raw bins <c>1 + 5N/8 .. 1 + 7N/8</c> and prints them under the band edges
    /// <c>[header, header + fs/4]</c>, then raw bins <c>1 + N/8 .. 1 + 3N/8</c> under
    /// <c>[header + fs/2, header + 3fs/4]</c>. The half around DC is dropped because that is where
    /// the tuned centre's own spike and the image land. <c>FloatFft.PowerQuarters</c> emits exactly
    /// those two groups in that order, so the mapping is piecewise with a gap in the middle.
    ///
    /// Getting this wrong does not look like a bug on screen: read the two quarters as one
    /// continuous ramp and every signal above the tuned centre moves up by 5 MHz, so an FM
    /// broadcast at 102.5 MHz in a sweep centred on 107.5 MHz appears at 112.5 MHz - plausible
    /// until the FM band is empty and something unexplained sits at the top of the range.
    /// </summary>
    [Theory]
    [InlineData(256)]
    [InlineData(1024)]
    public void BinOffsetsMatchTheBandsHackrfSweepPrints(int fft)
    {
        int bins = SweepMath.BinsPerWindow(fft);
        double binWidth = SweepMath.BinWidthHz(fft);
        int quarter = bins / 2;

        // A raw bin above N/2 is a negative frequency: it is one bin step below DC, not fs/2 above
        // it. That is what puts the first group in the 5 MHz below the tuned centre and the second
        // in the 5 MHz starting fs/2 above the header.
        //
        // The 5N/8 and N/8 offsets are not needed here: a quarter of the sample rate is exactly
        // 3N/8 bins wide, so the first group's signed bins run from -3N/8 to -N/8-1 and the second
        // from +N/8 to N/4-1 - all of them whole bin steps from the printed band edges whatever FFT
        // length is in use. The first group therefore starts exactly on the header, which is the
        // one frequency a block measures that is not a whole bin above a band edge.
        // Both groups start on their printed band edge: the low group's first bin is raw 5N/8, which
        // is fs/4 below the tuned centre, i.e. the header; the high group's first bin is raw N/8,
        // which is fs/8 above the centre, i.e. header + fs/2. From there each centre is a whole bin
        // step above the last, so the printed band edges are exact rather than approximate.
        double LowEdge(int i) => i * binWidth;
        double HighEdge(int i) => (SweepMath.SampleRateHz / 2.0) + (i * binWidth);

        Assert.Equal(LowEdge(0), SweepMath.BinOffsetHz(fft, 0), 3);
        Assert.Equal(LowEdge(quarter - 1), SweepMath.BinOffsetHz(fft, quarter - 1), 3);
        Assert.Equal(HighEdge(0), SweepMath.BinOffsetHz(fft, quarter), 3);
        Assert.Equal(HighEdge(quarter - 1), SweepMath.BinOffsetHz(fft, bins - 1), 3);

        // What a block really measures, as band edges: each group starts exactly on its printed edge
        // (the header, and header + fs/2) and stops one bin step short of its printed far edge,
        // because the bin on that far edge is the one next to DC and is dropped with the DC spike.
        // Nothing outside those is measured, which is why the assembler must not credit a block with
        // more coverage than two quarters of the sample rate.
        Assert.Equal(SweepMath.FirstBinEdgeHz, SweepMath.BinOffsetHz(fft, 0), 3);
        Assert.Equal((SweepMath.SampleRateHz / 4.0) - binWidth, SweepMath.BinOffsetHz(fft, quarter - 1), 3);
        Assert.Equal(SweepMath.SampleRateHz / 2.0, SweepMath.BinOffsetHz(fft, quarter), 3);
        Assert.Equal((3.0 * SweepMath.SampleRateHz / 4.0) - binWidth, SweepMath.BinOffsetHz(fft, bins - 1), 3);

        // The one step that is not a bin width crosses the discarded middle: fs/4 wide, plus the bin
        // step that the dropped DC-adjacent bin leaves at the top of the low group. That unmeasured
        // stretch is exactly what the odd interleaved block measures, which is the whole point of
        // interleaving.
        double join = SweepMath.BinOffsetHz(fft, quarter) - SweepMath.BinOffsetHz(fft, quarter - 1);
        Assert.Equal((SweepMath.SampleRateHz / 4.0) + binWidth, join, 3);
    }

    [Fact]
    public void BinOffsetIsMonotonicExceptAcrossTheDroppedMiddle()
    {
        const int fft = 256;
        int bins = SweepMath.BinsPerWindow(fft);
        double binWidth = SweepMath.BinWidthHz(fft);
        int quarter = bins / 2;

        for (int i = 1; i < quarter; i++)
        {
            Assert.Equal(binWidth, SweepMath.BinOffsetHz(fft, i) - SweepMath.BinOffsetHz(fft, i - 1), 3);
        }

        for (int i = quarter + 1; i < bins; i++)
        {
            Assert.Equal(binWidth, SweepMath.BinOffsetHz(fft, i) - SweepMath.BinOffsetHz(fft, i - 1), 3);
        }

        // The middle step is the only one that is not a bin width: it crosses the discarded band
        // between the two kept quarters, fs/4 wide plus the bin step left by the DC-adjacent bin
        // that upstream drops at the top of the low group.
        double join = SweepMath.BinOffsetHz(fft, quarter) - SweepMath.BinOffsetHz(fft, quarter - 1);
        Assert.Equal((SweepMath.SampleRateHz / 4.0) + binWidth, join, 3);
    }

    /// <summary>
    /// Why the interleaved style is worth twice the blocks: the firmware advances the header by a
    /// quarter of the tune step on odd dwells and three quarters on even ones
    /// (<c>usb_api_sweep.c</c> lines 203-209), so consecutive blocks alternate sitting fs/8 apart
    /// and each block's discarded middle is exactly the band its neighbour measures. Without the
    /// odd blocks half the spectrum would be missing; without the even ones the same.
    ///
    /// This arithmetic is also what the assembler's completion test has to agree with: a pass is
    /// whole when every bin between the range edges has been measured, not when a fixed number of
    /// blocks have arrived.
    /// </summary>
    [Fact]
    public void InterleavedHeadersCoverATuneStepBetweenThem()
    {
        const int fft = 1024;
        int bins = SweepMath.BinsPerWindow(fft);

        // hackrf_sweep programs step_width = TUNE_STEP * 1 MHz with TUNE_STEP = fs in MHz, i.e. a
        // 20 MHz tune step at 20 Msps, and the firmware splits that step between the two dwells.
        const long tuneStepHz = SweepMath.SampleRateHz;
        long evenHeader = 100_000_000;                  // frequencies[range * 2]
        long oddHeader = evenHeader + (tuneStepHz / 4); // odd dwell: sweep_freq += step_width / 4
        long nextEven = oddHeader + (3 * tuneStepHz / 4); // even dwell: += 3 * step_width / 4

        Assert.Equal(tuneStepHz / 4, oddHeader - evenHeader);
        Assert.Equal(evenHeader + tuneStepHz, nextEven);

        // Each block measures two bands a quarter sample rate wide, so the two interleaved blocks
        // measure exactly the tune step between them - no gap and no double-counting, which is the
        // arithmetic that makes a 1-6000 MHz sweep take about 600 blocks rather than 300 or 1200.
        double measuredPerBlock = 2.0 * (SweepMath.SampleRateHz / 4.0);
        Assert.Equal(tuneStepHz, 2.0 * measuredPerBlock, 3);

        // What one block really covers, from the same constants the display uses: two bands of
        // [header, header + fs/4] and [header + fs/2, header + 3fs/4], so the odd header's first
        // band starts exactly where the even header's first band ends.
        double quarterSpan = SweepMath.SampleRateHz / 4.0;
        Assert.Equal(quarterSpan, SweepMath.FirstBinEdgeHz + quarterSpan - SweepMath.FirstBinEdgeHz);
        Assert.Equal(
            SweepMath.FirstBinEdgeHz + quarterSpan,
            oddHeader - evenHeader + SweepMath.FirstBinEdgeHz);

        // The gap between the two groups of one block is a quarter sample rate plus one bin step,
        // and it is exactly the band the odd block measures: its own first band starts at
        // (header + fs/8) and runs to (header + fs/8 + fs/4), covering the middle of the even
        // block's sampled range.
        int quarter = bins / 2;
        double gap = SweepMath.BinOffsetHz(fft, quarter) - SweepMath.BinOffsetHz(fft, quarter - 1);
        Assert.Equal((SweepMath.SampleRateHz / 4.0) + SweepMath.BinWidthHz(fft), gap, 3);

        // A block's measured stretch runs from the header to one bin step short of 3fs/4 above it:
        // two fs/4 bands with fs/4 missing in the middle. A pass is therefore only whole once both
        // interleaved kinds have arrived, which is why the assembler counts measured bins rather
        // than blocks.
        double outerSpan = (SweepMath.BinOffsetHz(fft, bins - 1) + (0.5 * SweepMath.BinWidthHz(fft))) - SweepMath.FirstBinEdgeHz;
        Assert.Equal((3.0 * SweepMath.SampleRateHz / 4.0) - (0.5 * SweepMath.BinWidthHz(fft)), outerSpan, 3);
    }

    /// <summary>
    /// The whole point of the interleaved style, checked by covering a real span: each block
    /// measures two 5 MHz bands with 5 MHz missing between them, and the quarter-step blocks fill
    /// exactly those gaps. Mark every block's coverage on a 625 kHz grid and a 1-6000 MHz span comes
    /// out whole. The live sweep failed this the moment the device layer programmed half the tune
    /// step - headers 2.5 and 7.5 MHz apart reach only 17.5 MHz of coverage per 20 MHz of span, so
    /// three bins in ten stayed empty across the entire range and the sweep never finished.
    /// </summary>
    [Fact]
    public void InterleavedHeadersTileARealSpanWithNoGaps()
    {
        const long startHz = 1_000_000;
        const long stopHz = 6_000_000_000;
        const double gridBinHz = 625_000;
        int bins = (int)Math.Ceiling((stopHz - startHz) / gridBinHz);
        bool[] covered = new bool[bins];

        int blocks = 0;
        long header;
        do
        {
            header = SweepMath.InterleavedHeaderHz(startHz, blocks);
            MarkBand(covered, header, header + (SweepMath.SampleRateHz / 4), startHz, gridBinHz);
            MarkBand(
                covered,
                header + (SweepMath.SampleRateHz / 2),
                header + ((3 * SweepMath.SampleRateHz) / 4),
                startHz,
                gridBinHz);
            blocks++;
        }
        while (header < stopHz);

        int first = Array.IndexOf(covered, false);
        Assert.True(first < 0, $"bin {first} ({(startHz + (first * gridBinHz) / 1e6):F1} MHz) was never measured after {blocks} blocks");

        // About 600 blocks for the 6 GHz span, which is the block count the display's timing and
        // the sweep-rate readout are built around. Far fewer means the headers are striding too
        // wide; far more means they are not striding wide enough to be worth the interleaving.
        Assert.True(blocks is > 500 and < 700, $"unexpected block count {blocks}");
    }

    [Fact]
    public void InterleavedHeadersAdvanceByAQuarterThenThreeQuartersOfTheTuneStep()
    {
        const long start = 100_000_000;
        long quarter = SweepMath.TuneStepHz / 4;
        long threeQuarters = 3 * SweepMath.TuneStepHz / 4;

        Assert.Equal(quarter, SweepMath.InterleavedHeaderHz(start, 1) - SweepMath.InterleavedHeaderHz(start, 0));
        Assert.Equal(threeQuarters, SweepMath.InterleavedHeaderHz(start, 2) - SweepMath.InterleavedHeaderHz(start, 1));
        Assert.Equal(SweepMath.TuneStepHz, SweepMath.InterleavedHeaderHz(start, 2) - SweepMath.InterleavedHeaderHz(start, 0));
        Assert.Equal(start, SweepMath.InterleavedHeaderHz(start, 0));

        // A pair of blocks advances one tune step and measures one tune step: the second block's
        // first band starts exactly where the first block's first band ends, and its second band
        // ends exactly where the next pair's first band begins. That is the arithmetic that leaves
        // no gap and no double counting, and it only works at this step width.
        long h0 = SweepMath.InterleavedHeaderHz(start, 0);
        long h1 = SweepMath.InterleavedHeaderHz(start, 1);
        long h2 = SweepMath.InterleavedHeaderHz(start, 2);
        Assert.Equal(h0 + SweepMath.TuneStepHz, h2);

        // The four bands a pair measures, sorted, have to butt together with no gap and no overlap
        // and cover exactly the tune step the pair advances. That is the property the display's
        // "sweep complete" test depends on, and it is the property that broke when the step width
        // was halved: the bands then overlapped at one end and left 1.25 MHz uncovered at the other.
        long band = SweepMath.SampleRateHz / 4;
        long[] starts =
        [
            h0,
            h0 + (2 * band),
            h1,
            h1 + (2 * band),
        ];
        Array.Sort(starts);
        for (int i = 0; i < starts.Length; i++)
        {
            long expectedStart = h0 + (i * band);
            Assert.Equal(expectedStart, starts[i]);
        }

        Assert.Equal(h0 + SweepMath.TuneStepHz, starts[^1] + band);

        Assert.Throws<ArgumentOutOfRangeException>(() => SweepMath.InterleavedHeaderHz(start, -1));
    }

    /// <summary>Mark every grid bin the band <paramref name="fromHz"/>..<paramref name="toHz"/> touches.</summary>
    private static void MarkBand(bool[] covered, long fromHz, long toHz, long startHz, double gridBinHz)
    {
        int first = (int)Math.Floor((fromHz - startHz) / gridBinHz);
        int last = (int)Math.Ceiling((toHz - startHz) / gridBinHz) - 1;
        for (int i = Math.Max(first, 0); i <= Math.Min(last, covered.Length - 1); i++)
        {
            covered[i] = true;
        }
    }

    [Fact]
    public void BinOffsetRejectsOutOfRangeIndices()
    {
        const int fft = 256;
        int bins = SweepMath.BinsPerWindow(fft);
        Assert.Throws<ArgumentOutOfRangeException>(() => SweepMath.BinOffsetHz(fft, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SweepMath.BinOffsetHz(fft, bins));
    }

    [Fact]
    public void TunedCentreIsTheHeaderPlusTheSweepOffset()
    {
        Assert.Equal(107_500_000, SweepMath.TunedCenterHz(100_000_000));
        Assert.Equal(7_500_000, SweepMath.SweepOffsetHz);
    }

    [Fact]
    public void HeaderParsingAcceptsAGoodBlock()
    {
        byte[] block = SyntheticSignal.SweepBlockBytes(100_000_000, 64, 0.1);
        Assert.True(SweepMath.TryReadBlockHeader(block, out long hz));
        Assert.Equal(100_000_000, hz);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7_250_000_001)]      // one above the firmware's 7250 MHz ceiling
    [InlineData(long.MaxValue)]
    public void HeaderParsingRejectsImpossibleFrequencies(long headerHz)
    {
        byte[] block = SyntheticSignal.SweepBlockBytes(headerHz, 64, 0.1);
        Assert.False(SweepMath.TryReadBlockHeader(block, out _));
    }

    [Fact]
    public void HeaderParsingRejectsBadMagic()
    {
        byte[] block = SyntheticSignal.SweepBlockBytes(100_000_000, 64, 0.1);
        block[0] = 0x00;
        Assert.False(SweepMath.TryReadBlockHeader(block, out _));

        block = SyntheticSignal.SweepBlockBytes(100_000_000, 64, 0.1);
        block[1] = 0x7E;
        Assert.False(SweepMath.TryReadBlockHeader(block, out _));
    }

    [Fact]
    public void HeaderParsingRejectsShortBuffers() =>
        Assert.False(SweepMath.TryReadBlockHeader(new byte[SweepMath.HeaderBytes - 1], out _));

    [Fact]
    public void BuildSweepRangesSnapsToWholeMHzAndStaysInsideTheRadio()
    {
        var ranges = SweepMath.BuildSweepRanges(new FrequencyRange(88_000_000, 108_000_000));
        Assert.Single(ranges);
        Assert.Equal((88, 108), ranges[0]);

        // A stop that is not a whole MHz rounds up, so nothing requested is left uncovered.
        ranges = SweepMath.BuildSweepRanges(new FrequencyRange(88_123_456, 108_000_001));
        Assert.Equal((88, 109), ranges[0]);

        ranges = SweepMath.BuildSweepRanges(new FrequencyRange(1, 9_000_000_000));
        Assert.True(ranges[0].StopMHz <= SweepMath.MaxTunableHz / 1_000_000);
    }

    [Fact]
    public void BuildSweepRangesRejectsAnEmptyRange() =>
        Assert.Throws<ArgumentException>(() => SweepMath.BuildSweepRanges(new FrequencyRange(100_000_000, 100_000_000)));

    [Fact]
    public void SweepPlanResolvesTheWidthItWillReallyGet()
    {
        var plan = new SweepPlan([new FrequencyRange(88_000_000, 108_000_000)], BinWidthHz: 100_000);
        Assert.True(plan.IsValid);
        Assert.Equal(256, plan.FftLength);
        Assert.Equal(78_125, plan.ResolvedBinWidthHz, 3);
        Assert.True(plan.DisplayBinWidthHz >= plan.ResolvedBinWidthHz);

        Assert.False(new SweepPlan([], BinWidthHz: 100_000).IsValid);
        Assert.False(new SweepPlan([new FrequencyRange(88_000_000, 108_000_000)], BinWidthHz: 10).IsValid);
        Assert.False(new SweepPlan([new FrequencyRange(0, 108_000_000)], BinWidthHz: 100_000).IsValid);
        Assert.False(new SweepPlan([new FrequencyRange(88_000_000, 108_000_000)], BinWidthHz: 100_000, LnaGainDb: 48).IsValid);
    }

    [Fact]
    public void DisplayGridWidensOnlyForWideSpans()
    {
        var narrow = new[] { new FrequencyRange(88_000_000, 108_000_000) };
        Assert.Equal(1_000.0, SpectrumGrid.BinWidthFor(narrow, 1_000), 3);

        var wide = new[] { new FrequencyRange(1_000_000, 6_000_000_000) };
        double widened = SpectrumGrid.BinWidthFor(wide, 78_125);
        Assert.True(widened > 78_125);
        Assert.True((wide[0].StopHz - wide[0].StartHz) / widened <= SpectrumGrid.MaxBins);
    }
}

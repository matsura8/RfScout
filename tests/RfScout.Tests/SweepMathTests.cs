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
    /// The kept quarters sit at [header, header + 5 MHz] and [header + 10 MHz, header + 15 MHz]
    /// relative to the block header, which is what upstream prints next to the same bins.
    /// </summary>
    [Theory]
    [InlineData(256)]
    [InlineData(1024)]
    public void BinOffsetEdgesMatchTheKeptQuarters(int fft)
    {
        int bins = SweepMath.BinsPerWindow(fft);
        double binWidth = SweepMath.BinWidthHz(fft);
        int quarter = bins / 2;
        double quarterSpan = quarter * binWidth;  // one kept quarter is fs/4 wide

        Assert.Equal(0.5 * binWidth, SweepMath.BinOffsetHz(fft, 0), 3);
        Assert.Equal(quarterSpan - (0.5 * binWidth), SweepMath.BinOffsetHz(fft, quarter - 1), 3);
        Assert.Equal(2 * quarterSpan + (0.5 * binWidth), SweepMath.BinOffsetHz(fft, quarter), 3);
        Assert.Equal((3 * quarterSpan) - (0.5 * binWidth), SweepMath.BinOffsetHz(fft, bins - 1), 3);
    }

    [Fact]
    public void BinOffsetIsMonotonicExceptAcrossTheDroppedMiddle()
    {
        const int fft = 256;
        int bins = SweepMath.BinsPerWindow(fft);
        int quarter = bins / 2;
        for (int i = 1; i < quarter; i++)
        {
            Assert.True(SweepMath.BinOffsetHz(fft, i) > SweepMath.BinOffsetHz(fft, i - 1));
        }

        for (int i = quarter + 1; i < bins; i++)
        {
            Assert.True(SweepMath.BinOffsetHz(fft, i) > SweepMath.BinOffsetHz(fft, i - 1));
        }

        // The gap in the middle is exactly where the tuned centre's DC and image land.
        Assert.True(SweepMath.BinOffsetHz(fft, quarter) - SweepMath.BinOffsetHz(fft, quarter - 1) > 4_000_000);
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

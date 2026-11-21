using RfScout.Core;
using RfScout.Core.Fakes;
using RfScout.Core.Spectrum;

namespace RfScout.Tests;

/// <summary>
/// Framing is where a USB stream goes wrong quietly: a transfer that does not end on a block
/// boundary, or one lost block, must not turn into a shifted spectrum or a crash.
/// </summary>
public sealed class SweepBlockParserTests
{
    private const long HeaderHz = 100_000_000;

    [Fact]
    public void OneBlockSplitAcrossThreeFeedsFramesOnce()
    {
        byte[] block = SyntheticSignal.SweepBlockBytes(HeaderHz, SweepMath.SamplesPerBlock, 0.05);
        var parser = new SweepBlockParser();
        var framed = new List<SweepBlock>();

        parser.Feed(block.AsSpan(0, 4096), framed.Add);
        Assert.Empty(framed);
        parser.Feed(block.AsSpan(4096, 12_000 - 4096), framed.Add);
        Assert.Empty(framed);
        parser.Feed(block.AsSpan(12_000), framed.Add);

        Assert.Single(framed);
        Assert.Equal(HeaderHz, framed[0].HeaderFrequencyHz);
        Assert.Equal(SweepMath.SamplesPerBlock, framed[0].SampleCount);
        Assert.Equal(0, parser.HeaderErrors);
        Assert.Equal(1, parser.Blocks);
    }

    [Fact]
    public void TwoBlocksInOneFeedFrameTwice()
    {
        byte[] a = SyntheticSignal.SweepBlockBytes(100_000_000, SweepMath.SamplesPerBlock, 0.05);
        byte[] b = SyntheticSignal.SweepBlockBytes(115_000_000, SweepMath.SamplesPerBlock, 0.05);
        byte[] both = [.. a, .. b];

        var parser = new SweepBlockParser();
        var framed = new List<SweepBlock>();
        parser.Feed(both, framed.Add);

        Assert.Equal(2, framed.Count);
        Assert.Equal(100_000_000, framed[0].HeaderFrequencyHz);
        Assert.Equal(115_000_000, framed[1].HeaderFrequencyHz);
        Assert.Equal(0, parser.HeaderErrors);
    }

    [Fact]
    public void TwoBlocksSplitAtAnOddOffsetStillFrame()
    {
        // A 262144-byte USB transfer is not a multiple of the 16384-byte block once a partial
        // block has been carried over, so the carry has to survive an odd split too.
        byte[] a = SyntheticSignal.SweepBlockBytes(100_000_000, SweepMath.SamplesPerBlock, 0.05);
        byte[] b = SyntheticSignal.SweepBlockBytes(115_000_000, SweepMath.SamplesPerBlock, 0.05);
        byte[] both = [.. a, .. b];

        var parser = new SweepBlockParser();
        var framed = new List<SweepBlock>();
        const int oddSplit = 16_384 + 777;
        parser.Feed(both.AsSpan(0, oddSplit), framed.Add);
        parser.Feed(both.AsSpan(oddSplit), framed.Add);

        Assert.Equal(2, framed.Count);
        Assert.Equal(115_000_000, framed[1].HeaderFrequencyHz);
    }

    [Fact]
    public void ABadHeaderInMiddleIsCountedAndTheStreamKeepsGoing()
    {
        byte[] good1 = SyntheticSignal.SweepBlockBytes(100_000_000, SweepMath.SamplesPerBlock, 0.05);
        byte[] bad = SyntheticSignal.SweepBlockBytes(115_000_000, SweepMath.SamplesPerBlock, 0.05);
        bad[0] = 0x00;                                  // magic lost: a real lost-sync block
        byte[] good2 = SyntheticSignal.SweepBlockBytes(130_000_000, SweepMath.SamplesPerBlock, 0.05);
        byte[] stream = [.. good1, .. bad, .. good2];

        var parser = new SweepBlockParser();
        var framed = new List<SweepBlock>();
        parser.Feed(stream, framed.Add);

        Assert.Equal(2, framed.Count);
        Assert.Equal(100_000_000, framed[0].HeaderFrequencyHz);
        Assert.Equal(130_000_000, framed[1].HeaderFrequencyHz);
        Assert.Equal(1, parser.HeaderErrors);
        Assert.Equal(2, parser.Blocks);
    }

    [Fact]
    public void ResetDropsAPartialBlockAndClearsCounters()
    {
        byte[] block = SyntheticSignal.SweepBlockBytes(HeaderHz, SweepMath.SamplesPerBlock, 0.05);
        var parser = new SweepBlockParser();
        var framed = new List<SweepBlock>();

        parser.Feed(block.AsSpan(0, 8192), framed.Add);
        Assert.Empty(framed);

        parser.Reset();
        parser.Feed(block, framed.Add);

        Assert.Single(framed);
        Assert.Equal(HeaderHz, framed[0].HeaderFrequencyHz);
        Assert.Equal(0, parser.HeaderErrors);
    }

    [Fact]
    public void EmptyFeedsAreHarmless()
    {
        var parser = new SweepBlockParser();
        var framed = new List<SweepBlock>();
        parser.Feed(ReadOnlySpan<byte>.Empty, framed.Add);
        Assert.Empty(framed);
        Assert.Equal(0, parser.Blocks);
    }

    [Fact]
    public void FeedRejectsANullCallback()
    {
        var parser = new SweepBlockParser();
        Assert.Throws<ArgumentNullException>(() => parser.Feed(new byte[16], null!));
    }
}

using RfScout.Core;

namespace RfScout.Tests;

/// <summary>
/// <c>--self-test</c> is the smoke run that <c>dev test</c>, CI, packaging and the Windows
/// Sandbox round all rely on, so it has to be covered by an ordinary test too: a self-test that
/// silently stops checking anything is worse than no self-test.
/// </summary>
public sealed class SelfTestTests
{
    [Fact]
    public void RunPassesOnThisBuild() => Assert.True(SelfTest.Run());

    [Fact]
    public void EachIndividualCheckPasses()
    {
        Assert.True(SelfTest.FftPutsToneInItsOwnBin());
        Assert.True(SelfTest.SweepBlockFrames());
        Assert.True(SelfTest.BinWidthPlanningIsSane());
    }

    [Fact]
    public void SelfTestNeedsNoHardware()
    {
        // The gate that matters for the Sandbox round: nothing here may open a device, read a
        // file or touch the network. Run it twice to prove it is stateless and repeatable.
        Assert.True(SelfTest.Run());
        Assert.True(SelfTest.Run());
    }
}

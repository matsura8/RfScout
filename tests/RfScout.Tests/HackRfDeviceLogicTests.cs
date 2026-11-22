using RfScout.Core;
using RfScout.Services.HackRf;

namespace RfScout.Tests;

/// <summary>Device-side rules that can be checked without a radio attached.</summary>
public sealed class HackRfDeviceLogicTests
{
    [Fact]
    public void EveryRangeOfASweepPlanIsProgrammed()
    {
        var plan = new SweepPlan([new FrequencyRange(88_000_000, 108_000_000), new FrequencyRange(430_000_000, 440_000_000)], 100_000);

        ushort[] pairs = HackRfDevice.BuildSweepPairs(plan);

        Assert.Equal(new ushort[] { 88, 108, 430, 440 }, pairs);
    }

    [Fact]
    public void APlanNeedingMoreRangesThanTheFirmwareHoldsIsRejected()
    {
        var ranges = Enumerable.Range(0, NativeMethods.MaxSweepRanges + 1)
            .Select(i => new FrequencyRange(100_000_000 + (i * 50_000_000L), 110_000_000 + (i * 50_000_000L)))
            .ToList();
        var plan = new SweepPlan(ranges, 100_000);

        Assert.Throws<ArgumentException>(() => HackRfDevice.BuildSweepPairs(plan));
    }

    [Fact]
    public void CaptureLimitIsStatedInSecondsAtTheRequestedRate()
    {
        // 256 MiB of s8 I/Q: 2 bytes per sample.
        Assert.Equal(6.7108864, HackRfDevice.MaxCaptureSeconds(20_000_000), 6);
        Assert.Equal(67.108864, HackRfDevice.MaxCaptureSeconds(2_000_000), 6);
    }
}

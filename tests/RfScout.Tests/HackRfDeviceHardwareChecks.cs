using RfScout.Core;
using RfScout.Services.HackRf;

namespace RfScout.Tests;

/// <summary>
/// The device paths that only show up with a real HackRF: opening it (which reads the
/// library-owned version and board strings), a capture that stops itself, a sweep with
/// non-default settings, and disposing while a stream is running. Run with HARDWARE_TESTS=1
/// and dotnet test --filter-trait Category=Hardware.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class HackRfDeviceHardwareChecks
{
    private static void RequireHardware()
    {
        if (Environment.GetEnvironmentVariable("HARDWARE_TESTS") != "1")
        {
            Assert.Skip("set HARDWARE_TESTS=1 to run hardware tests");
        }
    }

    [Fact]
    public void OpensAndReadsTheLibraryOwnedStrings()
    {
        RequireHardware();
        using var device = new HackRfDevice();

        Assert.False(string.IsNullOrWhiteSpace(device.DeviceInfo.BoardName));
        Assert.NotEqual("unknown", device.DeviceInfo.LibraryVersion);
    }

    [Fact]
    public void ACaptureStopsItselfAndReportsItsSamples()
    {
        RequireHardware();
        using var device = new HackRfDevice();
        IqCapture? raised = null;
        device.CaptureAvailable += (_, e) => raised = e.Capture;

        device.StartCapture(100_000_000, 2_000_000, TimeSpan.FromMilliseconds(200));
        IqCapture? capture = device.WaitForCapture(TimeSpan.FromSeconds(10));

        Assert.NotNull(capture);
        Assert.Equal(400_000, capture.SampleCount);
        Assert.False(device.IsRunning);
        SpinWait.SpinUntil(() => raised is not null, TimeSpan.FromSeconds(2));
        Assert.NotNull(raised);
    }

    [Fact]
    public async Task ASweepAppliesThePlansSettingsAndDisposeWhileRunningReturns()
    {
        RequireHardware();
        var device = new HackRfDevice();
        var plan = new SweepPlan([new FrequencyRange(88_000_000, 108_000_000), new FrequencyRange(430_000_000, 440_000_000)], 100_000, LnaGainDb: 16, VgaGainDb: 20);
        int frames = 0;
        device.SpectrumAvailable += (_, _) => Interlocked.Increment(ref frames);

        device.StartSweep(plan);
        Assert.Equal((16, 20, false, false), device.Gains);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref frames) > 0, TimeSpan.FromSeconds(10)), "no spectrum frame within 10 s");

        // WaitAsync throws TimeoutException if Dispose hangs while the sweep is running.
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await Task.Run(device.Dispose, cancel).WaitAsync(TimeSpan.FromSeconds(10), cancel);
        Assert.False(device.IsRunning);
    }
}

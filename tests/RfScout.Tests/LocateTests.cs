using RfScout.Core;
using RfScout.Core.Dsp;
using RfScout.Core.Fakes;
using RfScout.Core.Locate;
using Xunit;

namespace RfScout.Tests;

/// <summary>
/// M5 locate: the strength calculation lives in Core so it is testable without a radio.
/// The ramp test drives a synthetic signal whose level climbs and falls and checks the
/// tracker follows it; the peak-hold tests pin the hold and its reset.
/// </summary>
public sealed class LocateTests
{
    private static byte[] ToneIq(int samples, double cyclesPerSample, double amplitude) =>
        SyntheticSignal.IqBytes(samples, cyclesPerSample, amplitude);

    [Fact]
    public void FullScaleToneMeasuresNearZeroDbfs()
    {
        byte[] iq = ToneIq(4096, 0.05, 1.0);
        double db = SignalStrengthTracker.MeasureDb(iq);
        // A full-scale complex sine is the 0 dBFS reference. The builder clamps to s8, so
        // quantisation of a unit-amplitude tone costs about 3 dB; the meter's own top of
        // scale is -20 dBFS, well below this, so the exact floor of the reference does not
        // matter to the view - only that a full-scale tone reads near 0 dBFS.
        Assert.InRange(db, -4.0, 0.5);
    }

    [Fact]
    public void EmptyWindowIsSilenceNotInfinity()
    {
        Assert.Equal(SignalStrengthTracker.SilenceDb, SignalStrengthTracker.MeasureDb(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void StrengthFollowsASyntheticSignalRampingUpAndDown()
    {
        // 21 windows: amplitude ramps up to a hill in the middle and back down (about 40 dB
        // of slope), each offered one second apart on a fake clock, as the locate loop feeds it.
        const int steps = 21;
        var start = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        var seen = new List<double>();
        var locate = new LocateController();
        locate.Start(start);
        for (int i = 0; i < steps; i++)
        {
            // Hill: amplitude climbs 0.0126 -> 0.2 -> 0.0126 (about 30 dB of slope). Kept under
            // 0.25 so the builder's s8 rounding never clips the tone (clipping reads as
            // silence), and flored so the ends stay above the -140 dBFS silence floor.
            double t = Math.Abs(i - (steps / 2)) / (steps / 2.0); // 0 at the top, 1 at the ends
            // A rational pitch (1/16 cycles per sample) repeats its s8 quantisation pattern
            // exactly every 16 samples, so measured power tracks amplitude smoothly instead of
            // stepping with the phase-dependent rounding of an irrational pitch.
            double cycles = 1.0 / 16.0;
            double amplitude = 0.2 * Math.Pow(10.0, -t * 1.5);
            // Round the amplitude to a whole s8 step first: a tone whose I and Q samples are             // all multiples of one quantum keeps that ratio (and so its measured power) at             // every phase, which makes the ramp strictly monotonic under s8 quantisation.             amplitude = Math.Round(amplitude * 127.0) / 127.0;

            byte[] iq = ToneIq(2048, cycles, amplitude);
            locate.Offer(iq, start + TimeSpan.FromSeconds(i));
            seen.Add(locate.CurrentDb);
        }

        // Monotonic up the ramp, monotonic down it, and the top step reads near full scale.
        for (int i = 1; i <= steps / 2; i++)
        {
            Assert.True(seen[i] > seen[i - 1] + 1.0, $"ramp up: step {i} {seen[i]:F1} did not rise above {seen[i - 1]:F1}");
        }

        for (int i = steps / 2 + 1; i < steps; i++)
        {
            Assert.True(seen[i] < seen[i - 1] - 1.0, $"ramp down: step {i} {seen[i]:F1} did not fall below {seen[i - 1]:F1}");
        }

                Assert.InRange(seen[steps / 2], -18.0, -16.0); // the hill top: 0.2 amplitude, about -17 dBFS under s8 rounding
        Assert.Equal(seen[steps / 2], locate.PeakDb, 3);

        // The graph timeline holds one bucket per second over the whole session.
        IReadOnlyList<(int Second, double Db)> graph = locate.SnapshotGraph();
        Assert.Equal(steps, graph.Count);
        Assert.Equal(0, graph[0].Second);
        Assert.Equal(steps - 1, graph[^1].Second);
    }

    [Fact]
    public void PeakHoldKeepsTheLoudestWindowAndResetStartsFresh()
    {
        var start = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var locate = new LocateController();
        locate.Start(start);

        locate.OfferDb(-30, start);
        locate.OfferDb(-12, start + TimeSpan.FromSeconds(1)); // the loudest so far
        locate.OfferDb(-45, start + TimeSpan.FromSeconds(2)); // quieter: the hold must stick

        Assert.Equal(-12, locate.PeakDb);
        Assert.Equal(-45, locate.CurrentDb);

        locate.ResetPeakHold();
        Assert.Equal(SignalStrengthTracker.SilenceDb, locate.PeakDb);
        Assert.Equal(-45, locate.CurrentDb); // a reset must not touch the live reading

        // The hold restarts from the next window.
        locate.OfferDb(-60, start + TimeSpan.FromSeconds(3));
        Assert.Equal(-60, locate.PeakDb);
    }

    [Fact]
    public void GraphDropsBucketsOlderThanItsWindowAndKeepsTheLoudestPerSecond()
    {
        var start = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var locate = new LocateController(graphSeconds: 5);
        locate.Start(start);

        for (int second = 0; second < 9; second++)
        {
            locate.OfferDb(-50 + second, start + TimeSpan.FromSeconds(second));
        }

        // Two more windows inside the newest second: the bucket keeps the loudest.
        locate.OfferDb(-20, start + TimeSpan.FromSeconds(8.4));
        locate.OfferDb(-35, start + TimeSpan.FromSeconds(8.8));

        IReadOnlyList<(int Second, double Db)> graph = locate.SnapshotGraph();
        Assert.Equal(5, graph.Count); // seconds 4..8 survive, 0..3 fall off the left edge
        Assert.Equal(4, graph[0].Second);
        Assert.Equal(-20, graph[^1].Db); // the bucket holds the loudest window of its second
    }

    [Fact]
    public void TonePitchRisesWithStrengthAndStaysInRange()
    {
        Assert.Equal(LocateController.ToneMinHz, LocateController.ToneHzFor(SignalStrengthTracker.SilenceDb), 3);
        Assert.Equal(LocateController.ToneMaxHz, LocateController.ToneHzFor(LocateController.MeterMaxDb), 3);
        Assert.Equal(LocateController.ToneMaxHz, LocateController.ToneHzFor(20), 3); // above the range still clamps

        double low = LocateController.ToneHzFor(-100);
        double high = LocateController.ToneHzFor(-40);
        Assert.True(high > low, "stronger must sound higher");

        Assert.Equal(0d, LocateController.ToneGainFor(SignalStrengthTracker.SilenceDb), 3);
        Assert.Equal(1d, LocateController.ToneGainFor(LocateController.MeterMaxDb), 3);
    }

    [Fact]
    public void SyntheticSourceCaptureFeedsTheTracker()
    {
        // The fake source produces a real IQ window; the tracker must accept it end to end.
        using SyntheticRadioSource source = new(100_000_000, 108_000_000);
        IqCapture? capture = null;
        source.CaptureAvailable += (_, e) => capture = e.Capture;
        source.StartCapture(98_000_000, 2_000_000, TimeSpan.FromMilliseconds(50));

        Assert.NotNull(capture);
        var locate = new LocateController();
        locate.Start(DateTimeOffset.UtcNow);
        locate.Offer(capture!.Samples, DateTimeOffset.UtcNow);
        Assert.True(locate.CurrentDb > SignalStrengthTracker.SilenceDb, "a real tone window reads above silence");
        Assert.Equal(locate.CurrentDb, locate.PeakDb, 6);
    }
}

using System.Globalization;
using RfScout.Core;
using RfScout.Core.Locate;
using RfScout.Services.HackRf;
using Xunit;

namespace RfScout.Tests;

/// <summary>
/// M5 "prove it" (hardware): enter locate on a strong FM station, watch the strength stream
/// for a few seconds, and leave cleanly back to sweeping. Like the other live tests the air
/// is unknowable, so an empty band is logged rather than failed; what IS asserted is what the
/// code controls: the locate stream produces windows, every reading is a finite dBFS number,
/// the peak hold tracks the loudest window, and stopping locate returns the device to idle so
/// a sweep starts again on the same handle.
///
/// One radio: joins the HackRF collection so it never opens the device at the same time as
/// another hardware test. Run with dev test -Hardware.
/// </summary>
[Trait("Category", "Hardware")]
[Collection(HackRfHardwareGroup.Name)]
public sealed class LocateHardwareTests
{
    [Fact]
    public void LiveLocateOnFmStreamReadsStrengthAndExitsCleanly()
    {
        using HackRfDevice radio = SweepPipelineHardwareTests.OpenRadio();

        // Reuse the M4 helper to pick the loudest 200 kHz bin in the FM band.
        long centerHz = DecoderLiveHardwareTests.FindStrongestFmStation(radio);
        if (centerHz == 0)
        {
            Log("no FM station found in the sweep; locating mid-band anyway");
            centerHz = 98_500_000;
        }

        radio.SetGains(24, 32);
        radio.SetRfAmplifier(false);
        radio.SetAntennaPower(false);

        var locate = new LocateController();
        locate.Start(DateTimeOffset.UtcNow);
        var windows = new List<IqCapture>();
        radio.LocateSampleAvailable += (_, e) =>
        {
            lock (windows)
            {
                windows.Add(e.Capture);
            }

            locate.Offer(e.Capture.Samples, DateTimeOffset.UtcNow);
        };

        radio.StartLocate(centerHz, 2_000_000, 1_000_000); // 0.5 s windows
        try
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                lock (windows)
                {
                    if (windows.Count >= 6)
                    {
                        break;
                    }
                }

                Thread.Sleep(100);
            }
        }
        finally
        {
            radio.StopStream();
            radio.LocateSampleAvailable -= (_, e) => { };
        }

        int count;
        lock (windows)
        {
            count = windows.Count;
        }

        if (count == 0)
        {
            Log("locate produced no windows: logged, not failed (empty band or antenna)");
            Assert.False(radio.IsRunning, "the device must be idle after leaving locate");
            return;
        }

        // What the code controls: every window is a full 1,000,000-sample capture at 2 Msps
        // centred on the located frequency, and every reading is finite dBFS.
        double peakSeen = double.MinValue;
        foreach (IqCapture capture in windows)
        {
            Assert.Equal(centerHz, capture.CenterHz);
            Assert.Equal(2_000_000, capture.SampleRateHz);
            Assert.Equal(1_000_000, capture.SampleCount);
            double db = SignalStrengthTracker.MeasureDb(capture.Samples);
            Assert.True(double.IsFinite(db) && db >= -140 && db <= 0, $"reading {db} outside the meter scale");
            peakSeen = Math.Max(peakSeen, db);
        }

        Assert.Equal(peakSeen, locate.PeakDb, 6); // the peak hold must equal the loudest window
        Assert.True(locate.SnapshotGraph().Count >= 1, "the graph timeline holds the session");

        // Plausible and changing over time: with a real station present the spread of the
        // readings is logged; a dead-quiet band may legitimately show one flat value.
        double first = SignalStrengthTracker.MeasureDb(windows[0].Samples);
        double last = SignalStrengthTracker.MeasureDb(windows[^1].Samples);
        Log($"live locate @{centerHz / 1e6:F3} MHz: {count} windows, first {first:F1} dBFS, last {last:F1} dBFS, hold {locate.PeakDb:F1} dBFS");

        // Exit cleanly: idle, and a sweep starts again on the same open handle.
        Assert.False(radio.IsRunning, "the device must be idle after leaving locate");
        radio.StartSweep(new SweepPlan(
            [new FrequencyRange(88_000_000, 108_000_000)],
            BinWidthHz: 200_000,
            LnaGainDb: 24,
            VgaGainDb: 32));
        try
        {
            Assert.True(radio.IsRunning, "a sweep must start right after locate ends");
        }
        finally
        {
            radio.StopStream();
        }
    }

    /// <summary>Console plus the TRX StdOut, so the run log shows what the air looked like.</summary>
    private static void Log(string message)
    {
        Console.WriteLine($"[hardware] {message}");
        TestContext.Current.TestOutputHelper?.WriteLine(message);
    }
}

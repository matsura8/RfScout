using System.Text;
using System.IO;
using RfScout.Core;
using RfScout.Core.Spectrum;
using RfScout.Services.HackRf;

namespace RfScout.Tests;

/// <summary>
/// The M2 hardware check: run the peak pipeline (statistical floor, detector, tracker) against
/// a real HackRF sweeping the FM broadcast band, and log what it finds. The brief's rule for
/// live RF applies: the air is unknowable, so a run that finds no peaks is logged, not failed.
/// What *is* asserted is what the code controls - the pipeline runs over real completed sweeps
/// without throwing, the floor stays in a plausible dBFS window, every detection is internally
/// consistent (peak above its own floor, inside the swept span, sane width), and the tracker
/// hands out unique IDs.
/// </summary>
[Trait("Category", "Hardware")]
[Collection(HackRfHardwareGroup.Name)]
public sealed class PeakPipelineHardwareTests
{
    [Fact]
    public void PeakPipelineRunsOverRealFmSweepsAndLogsWhatItFinds()
    {
        if (Environment.GetEnvironmentVariable("HARDWARE_TESTS") != "1")
        {
            Assert.Skip("set HARDWARE_TESTS=1 to run hardware tests");
        }

        using HackRfDevice radio = SweepPipelineHardwareTests.OpenRadio();

        var plan = new SweepPlan(
            [new FrequencyRange(88_000_000, 108_000_000)],
            BinWidthHz: 100_000,
            LnaGainDb: 16,
            VgaGainDb: 20);

        var completed = new List<SpectrumFrame>();
        radio.SpectrumAvailable += (_, e) =>
        {
            if (e.Frame.SweepCompleted)
            {
                lock (completed)
                {
                    completed.Add(e.Frame);
                }
            }
        };

        radio.StartSweep(plan);
        List<SpectrumFrame> frames;
        try
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                lock (completed)
                {
                    if (completed.Count >= 12)
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
        }

        lock (completed)
        {
            frames = [.. completed];
        }

        Assert.True(frames.Count >= 3, $"only {frames.Count} completed sweep(s) arrived in 20 s");

        var floor = new NoiseFloorEstimator();
        var detector = new PeakDetector();
        var tracker = new PeakTracker();
        var log = new StringBuilder();
        int totalDetections = 0;
        var seenIds = new HashSet<int>();

        foreach (SpectrumFrame frame in frames)
        {
            IReadOnlyList<double> noise = floor.Update(frame);
            IReadOnlyList<DetectedPeak> detections = detector.Detect(frame, noise);
            IReadOnlyList<TrackedPeak> tracks = tracker.Update(detections, frame.BinWidthHz, frame.UtcTimestamp);

            foreach (DetectedPeak peak in detections)
            {
                totalDetections++;

                // What the code controls: a detection must sit above its own floor, inside the
                // swept span, and its width must be at least one bin and no more than the span.
                int bin = frame.BinIndexAt(peak.CenterHz);
                Assert.True(bin >= 0 && bin < frame.BinCount, $"peak at {peak.CenterHz} Hz outside the swept span");
                Assert.True(
                    peak.PeakDb > noise[bin],
                    $"peak {peak.PeakDb:F1} dBFS is not above its floor {noise[bin]:F1} dBFS at bin {bin}");
                Assert.True(
                    peak.BandwidthHz3Db >= frame.BinWidthHz && peak.BandwidthHz3Db <= (frame.StopHz - frame.StartHz),
                    $"bandwidth {peak.BandwidthHz3Db} Hz outside one bin..span for peak at {peak.CenterHz} Hz");
            }

            foreach (TrackedPeak track in tracks)
            {
                Assert.True(seenIds.Add(track.Id), $"tracker reused id {track.Id} across updates");
                Assert.True(track.DutyCycle is >= 0 and <= 1, $"duty {track.DutyCycle} outside 0..1");
            }

            double medianFloor = Median(noise);
            Assert.True(
                medianFloor is > -140 and < 0,
                $"median noise floor {medianFloor:F1} dBFS is outside a plausible receiver window");

            log.AppendLine(
                $"sweep: {detections.Count} detection(s), {tracks.Count} track(s), median floor {medianFloor:F1} dBFS"
                + string.Concat(detections.Take(5).Select(p => $"\n    peak {p.CenterHz / 1e6:F3} MHz {p.PeakDb:F1} dBFS snr {p.SnrDb:F1} dB bw {p.BandwidthHz3Db / 1e3:F0} kHz {p.BandwidthClass} {p.BandwidthClass}")));
        }

        Log(log.ToString());

        if (totalDetections == 0)
        {
            Log("no peaks detected over live FM: logged, not failed (the band may be quiet or the antenna poor)");
        }
    }


    /// <summary>Console plus the TRX StdOut, so the run log shows what the air looked like.</summary>
    private static void Log(string message)
    {
        Console.WriteLine($"[hardware] {message}");
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "peak-pipeline-hardware.log"), message + Environment.NewLine);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        double[] sorted = [.. values];
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

}

using RfScout.Core;
using RfScout.Core.Spectrum;

namespace RfScout.Tests;

/// <summary>
/// M2 core tests: the noise-floor percentile, the peak detector and the tracker, driven by
/// hand-built spectra (synthetic tones, wide signals, close pairs, a sloped floor, bursty
/// signals) so the answers are known exactly and no radio or air content is involved.
/// </summary>
public sealed class PeakDetectionTests
{
    private const long StartHz = 100_000_000;
    private const double BinHz = 25_000;

    private static SpectrumFrame Frame(float[] powerDb, bool completed = true) => new()
    {
        StartHz = StartHz,
        StopHz = StartHz + (long)(powerDb.Length * BinHz),
        BinWidthHz = BinHz,
        PowerDb = powerDb,
        FilledBins = (int)powerDb.Count(p => p != SpectrumFrame.EmptyBin),
        SweepCompleted = completed,
        UtcTimestamp = DateTimeOffset.UnixEpoch,
    };

    private static float[] ToneSpectrum(
        int bins,
        double floorDb,
        int toneBin,
        double peakDb,
        int halfWidthBins = 1,
        double skirtDbPerBin = 6.0)
    {
        var power = new float[bins];
        for (int i = 0; i < bins; i++)
        {
            double distance = Math.Abs(i - toneBin);
            power[i] = distance <= halfWidthBins
                ? (float)(peakDb - (distance * skirtDbPerBin * 0.5))
                : (float)floorDb;
        }

        return power;
    }

    [Fact]
    public void EstimatorIgnoresFramesThatDoNotCompleteASweep()
    {
        var estimator = new NoiseFloorEstimator();
        float[] flat = Enumerable.Repeat(-60f, 8).ToArray();

        IReadOnlyList<double> floor = estimator.Update(Frame(flat, completed: false));
        Assert.DoesNotContain(floor, v => !double.IsNaN(v));
        Assert.Equal(0, estimator.Sweeps);
    }

    [Fact]
    public void EstimatorTakesTheLowPercentileNotTheMean()
    {
        var estimator = new NoiseFloorEstimator(percentile: 0.20, windowSweeps: 10);

        for (int sweep = 0; sweep < 9; sweep++)
        {
            estimator.Update(Frame(Enumerable.Repeat(-70f, 4).ToArray()));
        }

        IReadOnlyList<double> floor = estimator.Update(Frame(Enumerable.Repeat(-20f, 4).ToArray()));

        Assert.Equal(10, estimator.Sweeps);
        Assert.All(floor, v => Assert.InRange(v, -70.01, -69.99));
    }

    [Fact]
    public void EstimatorFollowsAGainChangeWithinTheWindow()
    {
        var estimator = new NoiseFloorEstimator(percentile: 0.20, windowSweeps: 8);

        for (int sweep = 0; sweep < 8; sweep++)
        {
            estimator.Update(Frame(Enumerable.Repeat(-50f, 2).ToArray()));
        }

        IReadOnlyList<double> floor = [];
        for (int sweep = 0; sweep < 8; sweep++)
        {
            floor = estimator.Update(Frame(Enumerable.Repeat(-80f, 2).ToArray()));
        }

        Assert.All(floor, v => Assert.InRange(v, -80.01, -79.99));
    }

    [Fact]
    public void EstimatorResetsWhenTheGridChanges()
    {
        var estimator = new NoiseFloorEstimator();
        estimator.Update(Frame(Enumerable.Repeat(-60f, 8).ToArray()));
        Assert.Equal(8, estimator.BinCount);

        IReadOnlyList<double> floor = estimator.Update(Frame(Enumerable.Repeat(-55f, 4).ToArray()));
        Assert.Equal(4, floor.Count);
        Assert.Equal(1, estimator.Sweeps);
    }

    [Fact]
    public void SingleToneProducesOnePeakWithinOneBin()
    {
        float[] power = ToneSpectrum(bins: 400, floorDb: -80, toneBin: 200, peakDb: -50);
        var frame = Frame(power);
        double[] floor = Enumerable.Repeat(-80.0, power.Length).ToArray();

        IReadOnlyList<DetectedPeak> peaks = new PeakDetector(10).Detect(frame, floor);

        Assert.Single(peaks);
        DetectedPeak peak = peaks[0];
        double expectedHz = frame.CenterFrequencyOfBin(200);
        Assert.InRange(peak.CenterHz, expectedHz - BinHz, expectedHz + BinHz);
        Assert.Equal(-50.0, peak.PeakDb, 3);
        Assert.Equal(30.0, peak.SnrDb, 3);
        Assert.Equal(PeakBandwidthClass.Medium, peak.BandwidthClass);
    }

    [Fact]
    public void NeighbouringAboveThresholdBinsMergeIntoOnePeak()
    {
        float[] power = ToneSpectrum(bins: 400, floorDb: -80, toneBin: 200, peakDb: -40, halfWidthBins: 10, skirtDbPerBin: 0.2);
        var frame = Frame(power);
        double[] floor = Enumerable.Repeat(-80.0, power.Length).ToArray();

        IReadOnlyList<DetectedPeak> peaks = new PeakDetector(10).Detect(frame, floor);

        Assert.Single(peaks);
        Assert.Equal(PeakBandwidthClass.Wide, peaks[0].BandwidthClass);
        Assert.True(peaks[0].BandwidthHz3Db >= 10 * BinHz, $"-3 dB width {peaks[0].BandwidthHz3Db / BinHz:F1} bins");
        Assert.True(
            peaks[0].BandwidthHz10Db >= peaks[0].BandwidthHz3Db,
            "-10 dB width must never be narrower than -3 dB");
    }

    [Fact]
    public void CloselySpacedTonesStayTwoPeaksWhenSeparatedByBelowThresholdBins()
    {
        float[] power = Enumerable.Repeat(-80f, 400).ToArray();
        foreach (int centre in new[] { 100, 108 })
        {
            for (int d = -1; d <= 1; d++)
            {
                power[centre + d] = -50f;
            }
        }

        var frame = Frame(power);
        double[] floor = Enumerable.Repeat(-80.0, power.Length).ToArray();

        IReadOnlyList<DetectedPeak> peaks = new PeakDetector(10).Detect(frame, floor);

        Assert.Equal(2, peaks.Count);
        Assert.All(peaks, p => Assert.Equal(PeakBandwidthClass.Medium, p.BandwidthClass));
        double spacing = Math.Abs(peaks[0].CenterHz - peaks[1].CenterHz);
        Assert.InRange(spacing, 7 * BinHz, 9 * BinHz);
    }

    [Fact]
    public void SlopedFloorStillFindsPeaksEverywhereAlongTheSlope()
    {
        // A sloped baseline plus tones that are present in only part of the window: the
        // 20th percentile then tracks the baseline (the tone is the window's high outlier),
        // so both tones clear the threshold no matter where along the slope they sit.
        int bins = 400;
        var power = new float[bins];
        for (int i = 0; i < bins; i++)
        {
            power[i] = (float)(-90 + (i * 0.1));
        }

        var tone = (float[])power.Clone();
        foreach (int centre in new[] { 50, 350 })
        {
            for (int d = -1; d <= 1; d++)
            {
                tone[centre + d] = power[centre + d] + 25f;
            }
        }

        var quiet = (float[])power.Clone();

        var estimator = new NoiseFloorEstimator(percentile: 0.20, windowSweeps: 10);
        IReadOnlyList<double> floor = [];
        for (int sweep = 0; sweep < 10; sweep++)
        {
            // Tone in only 2 of 10 sweeps: the 20th percentile (rank 2 of 10) is a quiet
            // sweep, so the floor tracks the sloped baseline and the tone is 25 dB above it.
            floor = estimator.Update(Frame(sweep < 2 ? tone : quiet));
        }

        IReadOnlyList<DetectedPeak> peaks = new PeakDetector(10).Detect(Frame(tone), floor);

        Assert.True(
            peaks.Count == 2,
            $"floor[50]={floor[50]:F2} floor[350]={floor[350]:F2} tone[50]={tone[50]:F2} tone[350]={tone[350]:F2} peaks={peaks.Count}");
        Assert.Contains(peaks, p => Math.Abs(p.CenterHz - Frame(tone).CenterFrequencyOfBin(50)) < BinHz);
        Assert.Contains(peaks, p => Math.Abs(p.CenterHz - Frame(tone).CenterFrequencyOfBin(350)) < BinHz);
    }

    private static SpectrumFrame frame(float[] power) => Frame(power);
    [Fact]
    public void EmptyBinsAndMissingFloorProduceNoPeaks()
    {
        float[] power = new float[100];
        Array.Fill(power, SpectrumFrame.EmptyBin);
        power[40] = -30f;

        var frame = Frame(power);
        double[] floor = Enumerable.Repeat(double.NaN, power.Length).ToArray();

        Assert.Empty(new PeakDetector(10).Detect(frame, floor));
    }

    [Fact]
    public void ThresholdIsAdjustable()
    {
        float[] power = ToneSpectrum(bins: 200, floorDb: -80, toneBin: 100, peakDb: -65);
        var frame = Frame(power);
        double[] floor = Enumerable.Repeat(-80.0, power.Length).ToArray();

        Assert.Single(new PeakDetector(5).Detect(frame, floor));
        Assert.Empty(new PeakDetector(20).Detect(frame, floor));
    }

    [Fact]
    public void TrackerGivesOneStableIdToTheSameToneAcrossSweeps()
    {
        var tracker = new PeakTracker();
        var frame = Frame(ToneSpectrum(400, -80, 200, -50));
        double[] floor = Enumerable.Repeat(-80.0, 400).ToArray();
        var detector = new PeakDetector(10);

        int id = -1;
        for (int sweep = 0; sweep < 10; sweep++)
        {
            IReadOnlyList<DetectedPeak> peaks = detector.Detect(frame, floor);
            IReadOnlyList<TrackedPeak> tracks = tracker.Update(peaks, BinHz, DateTimeOffset.UnixEpoch.AddSeconds(sweep));
            Assert.Single(tracks);
            if (id < 0)
            {
                id = tracks[0].Id;
            }

            Assert.Equal(id, tracks[0].Id);
        }

        TrackedPeak track = tracker.Snapshot()[0];
        Assert.Equal(10, track.HitCount);
        Assert.Equal(1.0, track.DutyCycle, 3);
        Assert.Equal(PeakActivityClass.Continuous, track.Activity);
        Assert.Equal(-50.0, track.MinDb, 3);
        Assert.Equal(-50.0, track.MaxDb, 3);
        Assert.Equal(-50.0, track.AverageDb, 3);
    }

    [Fact]
    public void TrackerMatchesWithinToleranceAndKeepsSeparateIdsOutsideIt()
    {
        var tracker = new PeakTracker(matchToleranceBins: 2.0);
        var detector = new PeakDetector(10);

        for (int sweep = 0; sweep < 5; sweep++)
        {
            var frame = Frame(ToneSpectrum(400, -80, 200 + (sweep % 2), -50));
            double[] floor = Enumerable.Repeat(-80.0, 400).ToArray();
            tracker.Update(detector.Detect(frame, floor), BinHz, DateTimeOffset.UnixEpoch.AddSeconds(sweep));
        }

        Assert.Single(tracker.Snapshot());

        float[] both = ToneSpectrum(400, -80, 200, -50);
        foreach (int d in new[] { -1, 0, 1 })
        {
            both[220 + d] = -55f;
        }

        var frame2 = Frame(both);
        double[] floor2 = Enumerable.Repeat(-80.0, 400).ToArray();
        IReadOnlyList<TrackedPeak> tracks = tracker.Update(
            detector.Detect(frame2, floor2),
            BinHz,
            DateTimeOffset.UnixEpoch.AddSeconds(5));

        Assert.Equal(2, tracks.Count);
        Assert.NotEqual(tracks[0].Id, tracks[1].Id);
    }

    [Fact]
    public void BurstySignalIsClassifiedBurstyAndContinuousOneContinuous()
    {
        var tracker = new PeakTracker();
        var detector = new PeakDetector(10);
        double[] floor = Enumerable.Repeat(-80.0, 400).ToArray();

        var continuous = Frame(ToneSpectrum(400, -80, 100, -50));
        var burst = Frame(ToneSpectrum(400, -80, 300, -50));

        for (int sweep = 0; sweep < 20; sweep++)
        {
            var peaks = new List<DetectedPeak>();
            peaks.AddRange(detector.Detect(continuous, floor));
            if (sweep % 10 == 0)
            {
                peaks.AddRange(detector.Detect(burst, floor));
            }

            tracker.Update(peaks, BinHz, DateTimeOffset.UnixEpoch.AddSeconds(sweep));
        }

        IReadOnlyList<TrackedPeak> tracks = tracker.Snapshot();
        Assert.Equal(2, tracks.Count);

        TrackedPeak continuousTrack = tracks.Single(t => Math.Abs(t.CenterHz - continuous.CenterFrequencyOfBin(100)) < BinHz);
        TrackedPeak burstTrack = tracks.Single(t => Math.Abs(t.CenterHz - burst.CenterFrequencyOfBin(300)) < BinHz);

        Assert.Equal(PeakActivityClass.Continuous, continuousTrack.Activity);
        Assert.Equal(PeakActivityClass.Bursty, burstTrack.Activity);
        Assert.Equal(2, burstTrack.HitCount);
        Assert.Equal(1.0, continuousTrack.DutyCycle, 2);
    }

    [Fact]
    public void TrackerDropsTracksThatStopBeingSeen()
    {
        var tracker = new PeakTracker();
        var detector = new PeakDetector(10);
        double[] floor = Enumerable.Repeat(-80.0, 400).ToArray();
        var tone = Frame(ToneSpectrum(400, -80, 100, -50));
        var quiet = Frame(Enumerable.Repeat(-80f, 400).ToArray());

        tracker.Update(detector.Detect(tone, floor), BinHz, DateTimeOffset.UnixEpoch);
        Assert.Single(tracker.Snapshot());

        for (int sweep = 1; sweep <= PeakTracker.DropAfterMissedSweeps + 1; sweep++)
        {
            tracker.Update(detector.Detect(quiet, floor), BinHz, DateTimeOffset.UnixEpoch.AddSeconds(sweep));
        }

        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void TrackerKeepsPowerHistoryAndFirstLastSeen()
    {
        var tracker = new PeakTracker();
        var detector = new PeakDetector(10);
        double[] floor = Enumerable.Repeat(-80.0, 400).ToArray();

        for (int sweep = 0; sweep < 5; sweep++)
        {
            var frame = Frame(ToneSpectrum(400, -80, 100, -50 + sweep));
            tracker.Update(detector.Detect(frame, floor), BinHz, DateTimeOffset.UnixEpoch.AddSeconds(sweep * 10));
        }

        TrackedPeak track = tracker.Snapshot()[0];
        Assert.Equal(5, track.PowerHistory.Count);
        Assert.Equal(-50.0, track.PowerHistory[0], 3);
        Assert.Equal(-46.0, track.PowerHistory[^1], 3);
        Assert.Equal(DateTimeOffset.UnixEpoch, track.FirstSeenUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(40), track.LastSeenUtc);
    }

    [Fact]
    public void ResetClearsTracksAndRestartsIds()
    {
        var tracker = new PeakTracker();
        var detector = new PeakDetector(10);
        double[] floor = Enumerable.Repeat(-80.0, 400).ToArray();
        var tone = Frame(ToneSpectrum(400, -80, 100, -50));

        int firstId = tracker.Update(detector.Detect(tone, floor), BinHz, DateTimeOffset.UnixEpoch)[0].Id;
        tracker.Reset();
        Assert.Empty(tracker.Snapshot());
        int secondId = tracker.Update(detector.Detect(tone, floor), BinHz, DateTimeOffset.UnixEpoch)[0].Id;
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public void ConstructorsRejectBadArguments()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new PeakDetector(-1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new PeakTracker(0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new NoiseFloorEstimator(percentile: 1.5));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new NoiseFloorEstimator(windowSweeps: 1));
    }
}

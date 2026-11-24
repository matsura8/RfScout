using System.IO;
using System.Text.Json;
using RfScout.Core;
using RfScout.Core.Analysis;
using RfScout.Core.Fakes;
using RfScout.Core.Recording;

namespace RfScout.Tests;

/// <summary>
/// M3 recording-layer proof: the AnalyzeController plan rules, SigMF round-trips, capture
/// rotation under a size cap, and FileRadioSource playback including truncated and garbage files.
/// Every test uses a temp directory so nothing touches %LOCALAPPDATA%\RfScout\captures.
/// </summary>
public sealed class RecordingTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "rfscout-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static IqCapture MakeCapture(long centerHz, int rateHz, int samples, DateTimeOffset? start = null)
    {
        byte[] iq = SignalGenerator.Cw(samples, rateHz, toneHz: 5_000);
        return new IqCapture(
            centerHz,
            rateHz,
            LnaGainDb: 24,
            VgaGainDb: 34,
            RfAmpEnabled: false,
            AntennaPower: false,
            start ?? new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero),
            iq);
    }

    // ---- AnalyzeController -------------------------------------------------------------

    [Fact]
    public void PlanForNarrowPeakPicksMinimumRateAndClampsLength()
    {
        AnalyzePlan plan = AnalyzeController.PlanFor(162_050_000, 16_000, HackRfCaptureLimitBytes, 5.0);
        Assert.Equal(AnalyzeController.MinSampleRateHz, plan.SampleRateHz);
        Assert.True(plan.DurationSeconds >= AnalyzeController.MinCaptureSeconds);
        Assert.True(plan.DurationSeconds <= AnalyzeController.MaxCaptureSeconds);
        Assert.True(plan.IsSane);
    }

    [Fact]
    public void PlanForWidePeakRaisesRateUpToTheCap()
    {
        // A 200 kHz signal needs at least 400 kHz of half-band: the controller doubles the rate
        // until the signal fits the inner half, capped at 20 Msps.
        AnalyzePlan plan = AnalyzeController.PlanFor(100_000_000, 200_000, HackRfCaptureLimitBytes, 2.0);
        Assert.True(plan.SampleRateHz >= 2_000_000 && plan.SampleRateHz <= AnalyzeController.MaxSampleRateHz);
        Assert.True(200_000 <= (plan.SampleRateHz * AnalyzeController.OccupancyFraction), "signal must fit the inner half");
        Assert.True(plan.IsSane);

        AnalyzePlan huge = AnalyzeController.PlanFor(100_000_000, 12_000_000, HackRfCaptureLimitBytes, 2.0);
        Assert.Equal(AnalyzeController.MaxSampleRateHz, huge.SampleRateHz);
    }

    [Fact]
    public void PlanForBroadcastFmSizedPeakRaisesRateToTenMsps()
    {
        // QA defect 4: a strong FM station measured ~3x its true deviation and occupied
        // bandwidth at the 2 Msps capture this planner used to pick, because the HackRF
        // baseband filter clipped the +/-75 kHz swing and the s8 quantiser added phase noise.
        // A ~200 kHz WFM channel must plan a 10 Msps capture.
        AnalyzePlan wfm = AnalyzeController.PlanFor(106_500_000, 200_000, HackRfCaptureLimitBytes, 2.0);
        Assert.Equal(AnalyzeController.WfmSampleRateHz, wfm.SampleRateHz);
        Assert.True(wfm.IsSane);

        // A narrow signal (AIS 16k) still plans the minimum rate: the raise is WFM-sized only.
        AnalyzePlan narrow = AnalyzeController.PlanFor(162_050_000, 16_000, HackRfCaptureLimitBytes, 2.0);
        Assert.Equal(AnalyzeController.MinSampleRateHz, narrow.SampleRateHz);

        // Below the WFM-sized threshold the raise must not fire.
        AnalyzePlan justUnder = AnalyzeController.PlanFor(100_000_000, AnalyzeController.WfmSignalBandwidthHz - 1, HackRfCaptureLimitBytes, 2.0);
        Assert.True(justUnder.SampleRateHz < AnalyzeController.WfmSampleRateHz, "sub-WFM widths keep the plain doubling plan");

        // The byte cap still wins last: a tiny cap must not plan more bytes than allowed.
        AnalyzePlan capped = AnalyzeController.PlanFor(106_500_000, 200_000, 1L << 20, 2.0);
        Assert.True(capped.SampleCount * 2 <= (1L << 20), "plan must respect the byte cap");
    }

    [Fact]
    public void LiveSizedWfmIsClassifiedWfmAtThePlannedCaptureRate()
    {
        // The end-to-end half of QA defect 4: at the rate the planner now picks for a WFM-sized
        // peak, a broadcast-FM-sized signal (75 kHz deviation, 15 kHz audio) must classify WFM.
        const int Rate = AnalyzeController.WfmSampleRateHz;
        byte[] iq = SignalGenerator.Wfm(2_000_000, Rate, toneHz: 0, devHz: 75_000, modHz: 15_000);
        SignalFeatures features = FeatureExtractor.Extract(iq, Rate);
        ClassificationResult result = ModulationClassifier.Classify(features);
        Assert.Equal(ModulationClass.Wfm, result.Class);
        Assert.True(features.FmDeviationHz is > 20_000 and < 100_000, features.Describe());
        Assert.True(features.OccupiedBandwidthHz is > 30_000 and < 250_000, features.Describe());
    }

    [Fact]
    public void PlanHonoursTheByteCapByShrinkingTheWindowOrRate()
    {
        // 1 MiB cap at 2 Msps s8 = 262144 samples = 0.131 s: below the 0.5 s floor, so the plan
        // must drop the rate until the floor fits rather than produce an impossible window.
        AnalyzePlan plan = AnalyzeController.PlanFor(100_000_000, 10_000, 1 << 20, 2.0);
        Assert.True(plan.SampleCount * 2 <= (1 << 20), "plan must respect the byte cap");
        Assert.True(plan.SampleRateHz >= AnalyzeController.MinSampleRateHz);
    }

    private const long HackRfCaptureLimitBytes = 1L << 28;

    [Fact]
    public void AnalyzeControllerAllowsOnlyOneAnalyzeAtATime()
    {
        var controller = new AnalyzeController();
        Assert.False(controller.IsBusy);
        controller.BeginAnalyze();
        Assert.True(controller.IsBusy);
        Assert.Throws<InvalidOperationException>(() => controller.BeginAnalyze());
        controller.EndAnalyze();
        Assert.False(controller.IsBusy);
        controller.EndAnalyze(); // idempotent
        controller.BeginAnalyze(); // and startable again
    }

    // ---- SigMF round-trip --------------------------------------------------------------

    [Fact]
    public void SigMfMetaRoundTripsThroughJson()
    {
        var summary = new ClassificationSummary("WFM", 0.83);
        SigMfMeta meta = SigMfMeta.ForCapture(
            101_100_000, 20_000_000, 4_000_000,
            new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero),
            24, 34, false, false, "SERIAL123", summary);

        string json = meta.ToJson();
        SigMfMeta back = SigMfMeta.Parse(json);

        Assert.Equal(meta.Core, back.Core);
        Assert.Equal(meta.SampleRate, back.SampleRate);
        Assert.Equal(meta.CaptureStart, back.CaptureStart);
        Assert.Equal(SigMfMeta.SampleFormat, back.Format);
        Assert.Single(back.Captures);
        Assert.Equal(4_000_000, back.Captures[0].Length);
        Assert.Contains(back.Annotations, a => a.Description.Contains("WFM", StringComparison.Ordinal));
        Assert.Equal("SERIAL123", back.Writer?.Serial);
    }

    [Fact]
    public void SigMfMetaRejectsGarbage()
    {
        Assert.Throws<InvalidDataException>(() => SigMfMeta.Parse("not json at all {{{"));
        Assert.Throws<InvalidDataException>(() => SigMfMeta.Parse("""{"core": 1.0}"""));
        Assert.Throws<InvalidDataException>(() => SigMfMeta.Parse("""{"core":1,"sample_rate":0,"captures":[{"core":0,"length":10}]}"""));
    }

    // ---- recorder + rotation -----------------------------------------------------------

    [Fact]
    public void RecorderWritesCs8PlusMetaAndRoundTripsThroughFileRadioSource()
    {
        var recorder = new CaptureRecorder(_dir, maxTotalBytes: 1L << 30);
        IqCapture capture = MakeCapture(101_100_000, 2_000_000, 40_000);
        RecordedCapture saved = recorder.Save(capture, new ClassificationSummary("NFM", 0.7));

        Assert.True(saved.Exists);
        Assert.Equal(new FileInfo(saved.Cs8Path).Length, capture.Samples.Length);

        using var playback = new FileRadioSource(saved.Cs8Path);
        Assert.Equal(101_100_000, playback.CenterHz);
        Assert.Equal(2_000_000, playback.SampleRateHz);
        // The capture carries the interleaved s8 bytes (two per IQ sample), so 40 000 bytes
        // hold 20 000 IQ samples; the file plays back exactly what the radio produced.
        Assert.Equal(capture.SampleCount, playback.SampleCount);

        IqCapture? replayed = null;
        playback.CaptureAvailable += (_, e) => replayed = e.Capture;
        playback.StartCapture(101_100_000, 2_000_000, TimeSpan.FromSeconds(10));
        Assert.NotNull(replayed);
        Assert.Equal(capture.Samples.Length, replayed!.Samples.Length);
        Assert.True(replayed.Samples.Span.SequenceEqual(capture.Samples.Span));
    }

    [Fact]
    public void RecorderDeletesOldestPairsFirstUnderTheCap()
    {
        // Each capture is 40000 samples * 2 bytes = 80000 bytes plus meta. A 200 kB cap fits at
        // most two pairs, so after three saves the oldest must be gone and the newest present.
        var recorder = new CaptureRecorder(_dir, maxTotalBytes: 200_000);
        var baseTime = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        RecordedCapture first = recorder.Save(MakeCapture(100_000_000, 2_000_000, 40_000, baseTime));
        RecordedCapture second = recorder.Save(MakeCapture(101_000_000, 2_000_000, 40_000, baseTime.AddSeconds(1)));
        RecordedCapture third = recorder.Save(MakeCapture(102_000_000, 2_000_000, 40_000, baseTime.AddSeconds(2)));

        Assert.False(File.Exists(first.Cs8Path));
        Assert.False(File.Exists(first.MetaPath));
        Assert.True(third.Exists);
        Assert.True(recorder.TotalBytes() <= 200_000);
        _ = second;
    }

    [Fact]
    public void RecorderRejectsNonPositiveCap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRecorder(_dir, 0));
    }

    // ---- FileRadioSource robustness ----------------------------------------------------

    [Fact]
    public void FileRadioSourcePlaysTruncatedCapture()
    {
        var recorder = new CaptureRecorder(_dir, maxTotalBytes: 1L << 30);
        RecordedCapture saved = recorder.Save(MakeCapture(100_000_000, 2_000_000, 40_000));

        // Chop the .cs8 to an odd, partial length: the reader must keep whole samples only.
        byte[] all = File.ReadAllBytes(saved.Cs8Path);
        File.WriteAllBytes(saved.Cs8Path, all[..(all.Length - 33)]);

        using var playback = new FileRadioSource(saved.Cs8Path);
        Assert.Equal((all.Length - 33) / 2, playback.SampleCount);
    }

    [Fact]
    public void FileRadioSourceRecoversFromGarbageMetaViaFileName()
    {
        var recorder = new CaptureRecorder(_dir, maxTotalBytes: 1L << 30);
        RecordedCapture saved = recorder.Save(MakeCapture(105_500_000, 2_000_000, 40_000));
        File.WriteAllText(saved.MetaPath, "this is not json");

        using var playback = new FileRadioSource(saved.Cs8Path);
        Assert.Equal(105_500_000, playback.CenterHz);
        Assert.Equal(2_000_000, playback.SampleRateHz);
    }

    [Fact]
    public void FileRadioSourceRefusesMismatchedTuneAndOversizedFiles()
    {
        var recorder = new CaptureRecorder(_dir, maxTotalBytes: 1L << 30);
        RecordedCapture saved = recorder.Save(MakeCapture(100_000_000, 2_000_000, 40_000));

        using var playback = new FileRadioSource(saved.Cs8Path);
        Assert.Throws<InvalidOperationException>(() => playback.StartCapture(99_000_000, 2_000_000, TimeSpan.FromMilliseconds(10)));
        Assert.Throws<InvalidOperationException>(() => playback.StartCapture(100_000_000, 8_000_000, TimeSpan.FromMilliseconds(10)));

        // Sweep mode on a replay must raise Stopped honestly rather than pretend.
        bool stopped = false;
        playback.Stopped += (_, _) => stopped = true;
        playback.StartSweep(new SweepPlan([new FrequencyRange(88_000_000, 108_000_000)], BinWidthHz: 100_000));
        Assert.True(stopped);
    }

    [Fact]
    public void FileRadioSourceRejectsMissingFile()
    {
        Assert.Throws<FileNotFoundException>(() => new FileRadioSource(Path.Combine(_dir, "nope.cs8")));
    }
}

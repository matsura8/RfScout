using RfScout.Core;
using RfScout.Core.Fakes;
using RfScout.ViewModels;

namespace RfScout.Tests;

/// <summary>
/// QA findings: the Auto-analyze checkbox did nothing because no code called TickAutoAnalyze, and
/// the detail pane's capture plots were computed but never shown. A completed sweep must now start
/// an analyze when Auto-analyze is on, and the analyze must hand the plots their points.
/// </summary>
public sealed class QaAutoAnalyzeTests
{
    private const int Bins = 800;

    private static SpectrumFrame SweepWithPeak() => new()
    {
        StartHz = 100_000_000,
        StopHz = 100_000_000 + (Bins * 10_000L),
        BinWidthHz = 10_000,
        PowerDb = Enumerable.Range(0, Bins).Select(i => i is >= 398 and <= 402 ? -30f : -90f).ToArray(),
        FilledBins = Bins,
        SweepCompleted = true,
        UtcTimestamp = DateTimeOffset.UtcNow,
    };

    private static MainViewModel Model(SyntheticRadioSource source) => new()
    {
        RadioFactory = _ => source,
        CompleteCapturesInlineForTests = true,
        CaptureDirectoryOverride = Path.Combine(Path.GetTempPath(), "rfscout-qa-auto-" + Guid.NewGuid().ToString("N")),
    };

    private static void FeedSweeps(MainViewModel model, Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !done())
        {
            model.Frames.Offer(SweepWithPeak());
            model.TryRefreshDisplay(0, out _, out _, out _);
            Thread.Sleep(200);
        }
    }

    [Fact]
    public void CompletedSweepStartsAnAutoAnalyzeAndFillsThePlots()
    {
        using var source = new SyntheticRadioSource(100_000_000, 108_000_000);
        using MainViewModel model = Model(source);
        model.StartMHzText = "100";
        model.StopMHzText = "108";
        model.Start();
        model.AutoAnalyze = true;
        try
        {
            FeedSweeps(model, () => model.ZoomedSpectrumPoints.Count > 0);

            Assert.NotEmpty(model.ClassificationText);
            Assert.NotEmpty(model.ZoomedSpectrumPoints);
            Assert.NotEmpty(model.EnvelopePoints);
            Assert.NotEmpty(model.InstFreqPoints);
            Assert.True(model.ZoomedSpectrumPoints.Count <= MainViewModel.TracePlotPoints);
        }
        finally
        {
            model.Stop();
        }
    }

    [Fact]
    public void CompletedSweepDoesNothingWhenAutoAnalyzeIsOff()
    {
        using var source = new SyntheticRadioSource(100_000_000, 108_000_000);
        using MainViewModel model = Model(source);
        model.StartMHzText = "100";
        model.StopMHzText = "108";
        model.Start();
        try
        {
            int sweeps = 0;
            FeedSweeps(model, () => ++sweeps > 12);
            Assert.Empty(model.ClassificationText);
            Assert.Empty(model.ZoomedSpectrumPoints);
        }
        finally
        {
            model.Stop();
        }
    }
}

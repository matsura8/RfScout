using RfScout.Core.Analysis;
using RfScout.Core.Decoders;
using RfScout.Core.Spectrum;

namespace RfScout.Tests;

/// <summary>
/// M4 hard rules 3 and 4 at the pipeline boundary:
///
///   * rule 3 - a classification result labels the peak and never decorates a decode, so a decode
///     that produced something must not gain a classification entry alongside it;
///   * rule 4 - the peak's own measurements are published even when no decoder found anything.
///
/// These are the cases the brief calls out, and they are the reason the pipeline returns a list
/// rather than a single best guess.
/// </summary>
public class DecoderPipelineEnrichmentTests
{
    private const int Rate = 200_000;

    private static DecoderContext Ctx(byte[] iq, long centerHz, TrackedPeak? peak = null)
        => DecoderContext.ForPeak(
            iq,
            centerHz,
            Rate,
            BandPlan.LoadShipped(),
            peak,
            new ClassificationResult(ModulationClass.Ook, 0.9, []));

    /// <summary>A peak the tracker measured, with the fields rule 4 publishes.</summary>
    private static TrackedPeak Peak(double centerHz, double duty) => new(
        Id: 7,
        CenterHz: centerHz,
        PeakDb: -18.0,
        SnrDb: 22.0,
        BandwidthHz3Db: 16_000.0,
        BandwidthClass: PeakBandwidthClass.Medium,
        FirstSeenUtc: DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000),
        LastSeenUtc: DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_060),
        HitCount: 24,
        SweepsSeen: 30,
        DutyCycle: duty,
        MinDb: -40.0,
        MaxDb: -12.0,
        AverageDb: -21.0,
        Activity: TrackedPeak.ClassFor(duty, 30),
        PowerHistory: []);

    [Fact]
    public void ClassificationOnlyCaptureGetsExactlyOneLabelEntry()
    {
        // A 2.4 GHz capture with no decoder named and no decode: the label is the whole answer,
        // so the list must hold one entry and nothing else (rule 3).
        DecoderPipeline pipeline = DecoderPipeline.Default(new Rtl433Bridge(exePath: null));
        byte[] iq = new byte[4096];
        DecoderContext ctx = Ctx(iq, 2_437_000_000);

        DeviceIdentification only = Assert.Single(pipeline.Identify(ctx));
        Assert.Equal(DeviceIdentification.ProtocolClassification, only.Protocol);
        Assert.Equal("false", only.Extra["decoded"]);
    }

    [Fact]
    public void ADecodeIsNeverDecoratedWithAClassificationEntry()
    {
        // The same capture with a tracked peak: the label entry still stands on its own, and the
        // peak entry is what rule 4 adds. Neither one rewrites the other's protocol or id.
        DecoderPipeline pipeline = DecoderPipeline.Default(new Rtl433Bridge(exePath: null));
        byte[] iq = new byte[4096];
        DecoderContext ctx = Ctx(iq, 2_437_000_000, Peak(2_437_000_000, duty: 0.5));

        IReadOnlyList<DeviceIdentification> ids = pipeline.Identify(ctx);
        Assert.Contains(ids, id => id.Protocol == DeviceIdentification.ProtocolClassification);
        Assert.DoesNotContain(ids, id => id.Protocol == PeakEnricher.ProtocolPeak);
    }

    [Fact]
    public void PeakMeasurementsArePublishedWhenNothingDecodes()
    {
        // Rule 4: the tracker's own measurements reach the detail pane even for a capture no
        // decoder touched, as long as the peak is not already covered by a label.
        DeviceIdentification entry = PeakEnricher.Enrich(
            Ctx(new byte[4096], 433_920_000, Peak(433_920_000, duty: 0.75)),
            [])[0];

        Assert.Equal(PeakEnricher.ProtocolPeak, entry.Protocol);
        Assert.Equal(PeakEnricher.DeviceTypePeak, entry.DeviceType);
        Assert.Equal("peak_tracker", entry.Source);
        Assert.Equal("Medium", entry.Extra["bandwidth_class"]);
        Assert.Equal("75%", entry.Extra["duty_cycle"]);
        Assert.Equal("24", entry.Extra["hits"]);
        Assert.Equal(0.75, entry.Confidence, 3);
    }

    [Fact]
    public void EnrichAddsNothingWithoutATrackedPeak()
    {
        IReadOnlyList<DeviceIdentification> ids = PeakEnricher.Enrich(
            Ctx(new byte[4096], 433_920_000),
            []);
        Assert.Empty(ids);
    }
}


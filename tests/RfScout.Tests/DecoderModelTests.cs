// M4 unit tests for the decoder model, selection pipeline and classification-only labels.
// Fixtures here are synthetic (generated in-test); published-vector tests live in the
// per-decoder test files and cite their vector source in a comment.
using RfScout.Core;
using RfScout.Core.Analysis;
using RfScout.Core.Decoders;
using RfScout.Core.Spectrum;
using Xunit;

namespace RfScout.Tests;

public class DecoderModelTests
{
    private static BandPlan Plan() => BandPlan.LoadShipped();

    private static DecoderContext Ctx(byte[] iq, long centerHz, int rate, ClassificationResult? cls = null, SignalFeatures? f = null)
        => DecoderContext.ForPeak(iq, centerHz, rate, Plan(), cls, f);

    [Fact]
    public void ForPeakPicksTheBandPlanAllocationsForTheCentre()
    {
        DecoderContext ctx = Ctx([], 433_920_000, 2_000_000);
        Assert.True(ctx.BandWants("rtl_433"));
        Assert.False(ctx.BandWants("modes"));
        Assert.True(ctx.Bands.Count > 0);
        // Narrowest first: the 433 ISM allocation is the first match.
        Assert.Contains("433", ctx.Bands[0].Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifiedAsHonoursTheClassifierDecision()
    {
        var cls = new ClassificationResult(ModulationClass.Ook, 0.9, []);
        DecoderContext ctx = Ctx([], 433_920_000, 2_000_000, cls);
        Assert.True(ctx.ClassifiedAs(ModulationClass.Ook));
        Assert.False(ctx.ClassifiedAs(ModulationClass.Wfm));
        Assert.False(Ctx([], 433_920_000, 2_000_000).ClassifiedAs(ModulationClass.Ook)); // null classification
    }

    [Fact]
    public void DeviceIdentificationClampsConfidenceAndDescribesItself()
    {
        Assert.Equal(0.0, DeviceIdentification.ClampConfidence(-3));
        Assert.Equal(1.0, DeviceIdentification.ClampConfidence(5));
        Assert.Equal(0.0, DeviceIdentification.ClampConfidence(double.NaN));
        var id = new DeviceIdentification(
            "modes", "aircraft", "A4010E",
            new Dictionary<string, string> { ["callsign"] = "UAE101" }, 0.9, "modes-decoder");
        Assert.Contains("modes: aircraft A4010E", id.ShortText, StringComparison.Ordinal);
        Assert.Contains("callsign: UAE101", id.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineSelectsBandNamedDecodersOnly()
    {
        DecoderPipeline pipeline = DecoderPipeline.Default(new Rtl433Bridge(exePath: null));
        // 433 ISM names rtl_433; with the binary missing the bridge rejects the capture, so
        // nothing protocol-level is selected and the OOK fallback is reachable.
        // The 433 band names rtl_433, so the pipeline selects the bridge even when the binary is
        // missing (Supports=false): Identify() then sees the empty decode and falls back to OOK
        // timing, which is the brief's "rtl_433 missing or finds nothing" path. What must NOT be
        // selected here is a decoder the band does not name.
        DecoderContext ism = Ctx(new byte[8192], 433_920_000, 2_000_000,
            new ClassificationResult(ModulationClass.Ook, 0.9, []));
        Assert.Contains(pipeline.SelectFor(ism), d => d.Key == Rtl433Bridge.Key);
        Assert.DoesNotContain(pipeline.SelectFor(ism), d => d.Key == ModeSDecoder.Key);

        // 1090 names modes and the capture is 2 Msps: ModeS must be selected.
        DecoderContext adsb = Ctx(new byte[2 * 2_000_000], 1_090_000_000, 2_000_000);
        Assert.Contains(pipeline.SelectFor(adsb), d => d.Key == ModeSDecoder.Key);

        // FM band names rds and 2 Msps passes the subcarrier: Rds must be selected.
        DecoderContext fm = Ctx(new byte[2 * 2_000_000], 96_500_000, 2_000_000);
        Assert.Contains(pipeline.SelectFor(fm), d => d.Key == RdsDecoder.Key);

        // Marine band names ais.
        DecoderContext ais = Ctx(new byte[2 * 200_000], 161_975_000, 200_000);
        Assert.Contains(pipeline.SelectFor(ais), d => d.Key == AisDecoder.Key);
    }

    [Fact]
    public void PipelineFallsBackToClassificationLabelWhenNothingDecodes()
    {
        DecoderPipeline pipeline = DecoderPipeline.Default(new Rtl433Bridge(exePath: null));
        // Wi-Fi band, no decoder named, random bytes: classification-only label (hard rule 3).
        var rnd = new Random(20261122);
        byte[] iq = new byte[4096];
        rnd.NextBytes(iq);
        DecoderContext ctx = Ctx(iq, 2_437_000_000, 20_000_000,
            new ClassificationResult(ModulationClass.OfdmWideband, 0.8, []));
        IReadOnlyList<DeviceIdentification> ids = pipeline.Identify(ctx);
        DeviceIdentification only = Assert.Single(ids);
        Assert.Equal(DeviceIdentification.ProtocolClassification, only.Protocol);
        Assert.Contains("Wi-Fi", only.Id, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("likely Wi-Fi", only.Id, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PipelineOokFallbackReportsTimingOnlyForUnkownIsmBursts()
    {
        DecoderPipeline pipeline = DecoderPipeline.Default(new Rtl433Bridge(exePath: null));
        // Synthetic OOK: 128-bit alternating pattern at ~1 kbit/s with 200 kHz rate: pulses of
        // 100 samples on/off, enough edges for the timing decoder.
        byte[] iq = OokPattern(rateHz: 200_000, bitUs: 1000, bits: 256);
        DecoderContext ctx = Ctx(iq, 433_920_000, 200_000,
            new ClassificationResult(ModulationClass.Ook, 0.9, []));
        IReadOnlyList<DeviceIdentification> ids = pipeline.Identify(ctx);
        DeviceIdentification first = ids[0];
        Assert.Equal("ook", first.Protocol);
        Assert.Contains("unknown protocol", first.Id, StringComparison.Ordinal);
        Assert.DoesNotContain("rtl_433", first.Protocol, StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineNeverThrowsOnRandomBytes()
    {
        DecoderPipeline pipeline = DecoderPipeline.Default(new Rtl433Bridge(exePath: null));
        var rnd = new Random(99);
        foreach (long center in new long[] { 96_500_000, 161_975_000, 433_920_000, 1_090_000_000, 2_437_000_000 })
        {
            byte[] iq = new byte[64 * 1024];
            rnd.NextBytes(iq);
            IReadOnlyList<DeviceIdentification> ids = pipeline.Identify(Ctx(iq, center, 2_000_000));
            Assert.NotEmpty(ids); // always at least the classification label
        }
    }

    internal static byte[] OokPattern(int rateHz, int bitUs, int bits)
    {
        int samplesPerBit = rateHz * bitUs / 1_000_000;
        int samples = samplesPerBit * bits;
        byte[] iq = new byte[2 * samples];
        for (int i = 0; i < samples; i++)
        {
            bool on = ((i / samplesPerBit) % 2) == 0;
            iq[2 * i] = (byte)(on ? 100 : 0);
            iq[(2 * i) + 1] = 0;
        }

        return iq;
    }
}

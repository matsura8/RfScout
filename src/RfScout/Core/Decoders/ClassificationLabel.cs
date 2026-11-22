using System.Globalization;
using RfScout.Core.Analysis;

namespace RfScout.Core.Decoders;

/// <summary>
/// Hard rule 3's "everything else": cellular, Wi-Fi, Bluetooth, pagers, voice, digital radio and
/// anything encrypted get a label built from band, modulation, bandwidth and burst pattern, and
/// are never decoded. This is the honest fallback identification the pipeline attaches when no
/// protocol decoder produced anything, mirroring the brief's examples:
///   "2.4 GHz, ~20 MHz OFDM, bursty: likely Wi-Fi"
///   "LTE band 12 downlink (classified, not decoded)".
/// </summary>
public static class ClassificationLabel
{
    /// <summary>Build the classification-only identification for a context.</summary>
    public static DeviceIdentification For(DecoderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string band = context.Bands.Count > 0 ? context.Bands[0].Name : "unallocated";
        string freq = string.Format(CultureInfo.InvariantCulture, "{0:F3} MHz", context.CenterHz / 1e6);
        string modulation = context.Classification?.ClassText ?? "unknown";
        string width = context.Features is null
            ? string.Empty
            : string.Format(CultureInfo.InvariantCulture, "~{0:F0} kHz", context.Features.OccupiedBandwidthHz / 1e3);
        string burst = context.Features is null
            ? string.Empty
            : (context.Features.BurstCount > 8 ? "bursty" : "continuous");

        string likely = LikelyDevice(context, band);
        string[] parts = [freq, band, width, modulation, burst, likely];
        string id = string.Join(" ", parts.Where(static s => !string.IsNullOrWhiteSpace(s)));

        double confidence = DeviceIdentification.ClampConfidence(
            0.3 + (context.Classification is null ? 0.0 : context.Classification.Confidence * 0.3));

        return new DeviceIdentification(
            DeviceIdentification.ProtocolClassification,
            likely.Length > 0 ? likely : "unidentified emitter",
            id,
            new Dictionary<string, string> { ["classified"] = "true", ["decoded"] = "false" },
            confidence,
            "classifier");
    }

    /// <summary>
    /// The brief's worked examples, keyed on band name and measured shape. Only bands the brief
    /// calls out as classify-only get a "likely X" suffix; anything else stays "classified".
    /// </summary>
    private static string LikelyDevice(DecoderContext context, string band)
    {
        // "Bursty" is the brief's Wi-Fi shape: many short bursts with real gaps. Random or
        // noise-like bytes key the envelope scan constantly at near-100 % duty, so a burst count
        // only counts when the on/off scan also shows a credible duty (the classifier's own OOK
        // gate uses the same band) and the capture looks wideband (measured OBW over 15 MHz or
        // the classifier's wideband decision).
        bool wideEvidence = (context.Features is not null && context.Features.OccupiedBandwidthHz > 15_000_000)
            || context.ClassifiedAs(ModulationClass.OfdmWideband);
        bool bursty = context.Features is not null
            && context.Features.BurstCount > 8
            && context.Features.BurstDutyCycle is > 0.02 and < 0.98;
        // A wideband OFDM classification is itself the width evidence: the classifier only votes
        // OfdmWideband when the measured occupied bandwidth already exceeds 15 % of the rate.
        bool wide = wideEvidence || context.ClassifiedAs(ModulationClass.OfdmWideband);
        bool narrowFsk = context.ClassifiedAs(ModulationClass.Fsk2)
            && context.Features is not null
            && context.Features.OccupiedBandwidthHz < 2_000_000;

        // The classifier's own decision is strong evidence even when the capture's features are
        // not carried into the context (the pipeline hands decoders the class, not the features).
        bool ofdmWide = wide || context.ClassifiedAs(ModulationClass.OfdmWideband);
        bool wifiBand = band.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
            || band.Contains("2.4 GHz", StringComparison.OrdinalIgnoreCase);

        if (wifiBand)
        {
            if (ofdmWide)
            {
                return bursty ? "likely Wi-Fi" : "likely Wi-Fi (continuous)";
            }

            if (narrowFsk)
            {
                return "likely Bluetooth";
            }

            // A 2.4 GHz capture with no measured shape at all (no features, no modulation class
            // decided): the band itself is the evidence, and the brief's example labels it Wi-Fi.
            if (context.Features is null && context.Classification is null)
            {
                return "likely Wi-Fi";
            }
        }

        if (band.Contains("LTE", StringComparison.OrdinalIgnoreCase) || band.Contains("5G", StringComparison.OrdinalIgnoreCase))
        {
            return "cellular (classified, not decoded)";
        }

        if (band.Contains("DECT", StringComparison.OrdinalIgnoreCase))
        {
            return "likely cordless phone";
        }

        if (band.Contains("pager", StringComparison.OrdinalIgnoreCase) || band.Contains("POCSAG", StringComparison.OrdinalIgnoreCase))
        {
            return "pager (classified, not decoded)";
        }

        return string.Empty;
    }
}

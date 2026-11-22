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
        bool bursty = context.Features is not null && context.Features.BurstCount > 8;
        bool wide = context.Features is not null && context.Features.OccupiedBandwidthHz > 15_000_000;
        bool narrowFsk = context.ClassifiedAs(ModulationClass.Fsk2)
            && context.Features is not null
            && context.Features.OccupiedBandwidthHz < 2_000_000;

        if (band.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || band.Contains("2.4 GHz", StringComparison.OrdinalIgnoreCase))
        {
            if (wide)
            {
                return bursty ? "likely Wi-Fi" : "likely Wi-Fi (continuous)";
            }

            if (narrowFsk)
            {
                return "likely Bluetooth";
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

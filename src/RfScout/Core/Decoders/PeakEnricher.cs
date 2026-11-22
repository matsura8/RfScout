using System.Globalization;
using RfScout.Core.Analysis;
using RfScout.Core.Spectrum;

namespace RfScout.Core.Decoders;

/// <summary>
/// Adds the peak-level entries to a decode list, following the M4 rules:
///
///   * rule 3 - a classification result labels the peak, it never decorates a decode. The label
///     entry is <see cref="ClassificationLabel"/>, which the pipeline already adds only when no
///     decoder produced anything; this class never rewrites a real decode's protocol or id, and
///     it never adds a second label on top of one;
///   * rule 4 - the peak's own measurements (bandwidth class, duty cycle, burst timing) are worth
///     publishing even when no decoder found anything, because the peak table is the product
///     surface and those fields come from the tracker rather than from a decode.
///
/// The enrichment is pure: it appends at most one entry to the list the pipeline already built
/// and returns it, so the caller keeps ownership of the ordering.
/// </summary>
public static class PeakEnricher
{
    /// <summary>Protocol key for the peak-measurement entry (rule 4).</summary>
    public const string ProtocolPeak = "peak";

    /// <summary>Device type for the peak-measurement entry.</summary>
    public const string DeviceTypePeak = "Peak";

    /// <summary>
    /// Append the peak measurements (rule 4) to <paramref name="found"/> and return it. The entry
    /// is skipped when a classification label is already present, so a peak that only got labelled
    /// keeps exactly one entry (rule 3), and it is skipped when the context carries no tracked peak.
    /// </summary>
    public static IReadOnlyList<DeviceIdentification> Enrich(
        DecoderContext context,
        List<DeviceIdentification> found)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(found);

        if (context.Peak is TrackedPeak peak
            && !found.Exists(static id => id.Protocol == DeviceIdentification.ProtocolClassification))
        {
            found.Add(Measurements(peak));
        }

        return found;
    }

    /// <summary>
    /// The peak's own measurements as an entry (rule 4). The id is the peak's centre frequency,
    /// which is the only handle the peak table has before a decoder names an emitter.
    /// </summary>
    private static DeviceIdentification Measurements(TrackedPeak peak)
    {
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bandwidth_class"] = peak.BandwidthClass.ToString(),
            ["bandwidth_hz"] = peak.BandwidthHz3Db.ToString("F0", CultureInfo.InvariantCulture),
            ["duty_cycle"] = peak.DutyText,
            ["activity"] = peak.Activity.ToString(),
            ["snr_db"] = peak.SnrDb.ToString("F1", CultureInfo.InvariantCulture),
            ["peak_db"] = peak.PeakDb.ToString("F1", CultureInfo.InvariantCulture),
            ["average_db"] = peak.AverageDb.ToString("F1", CultureInfo.InvariantCulture),
            ["first_seen_utc"] = peak.FirstSeenUtc.ToString("O", CultureInfo.InvariantCulture),
            ["last_seen_utc"] = peak.LastSeenUtc.ToString("O", CultureInfo.InvariantCulture),
            ["hits"] = peak.HitCount.ToString(CultureInfo.InvariantCulture),
            ["sweeps_seen"] = peak.SweepsSeen.ToString(CultureInfo.InvariantCulture),
        };

        // A peak that keeps showing up is a stronger claim than a single sweep, so the entry's
        // confidence follows the duty cycle the tracker measured rather than a decode's fit.
        double confidence = Math.Clamp(peak.DutyCycle, 0.0, 1.0);

        return new DeviceIdentification(
            ProtocolPeak,
            DeviceTypePeak,
            peak.CenterText,
            extra,
            DeviceIdentification.ClampConfidence(confidence),
            "peak_tracker");
    }
}

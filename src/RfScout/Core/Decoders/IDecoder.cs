using RfScout.Core;
using RfScout.Core.Analysis;
using RfScout.Core.Spectrum;

namespace RfScout.Core.Decoders;

/// <summary>
/// One decoder's view of one analyzed peak. Decoders are pure functions of the capture plus the
/// band-plan entry and the classifier output (M4: "pick decoders from the band plan entry and the
/// classifier output"), so they run headless over fixtures and fake sources.
///
/// A decoder must never throw on arbitrary bytes and never decode private content (hard rule 3):
/// return zero identifications rather than guessing, and identifiers only.
/// </summary>
public interface IDecoder
{
    /// <summary>Decoder key, matched against the band plan's "decoders" list (e.g. "modes", "rds", "ais", "rtl_433", "ook").</summary>
    string Key { get; }

    /// <summary>
    /// True when this decoder can work on a capture with the given centre frequency, sample rate
    /// and classification. Cheap: no signal processing here, only a sanity check of the inputs.
    /// </summary>
    bool Supports(DecoderContext context);

    /// <summary>
    /// Identify emitters in one capture. Returns zero or more identifications, most confident
    /// first. Must not throw on truncated or random bytes; return an empty list instead.
    /// </summary>
    IReadOnlyList<DeviceIdentification> Decode(DecoderContext context);
}

/// <summary>
/// Everything a decoder is allowed to see: the narrowband IQ capture, its centre frequency and
/// sample rate, the band-plan allocations that contain the peak (narrowest first), and the M3
/// classification with its features. Immutable, so decoders cannot mutate shared state.
/// </summary>
public sealed record DecoderContext(
    ReadOnlyMemory<byte> Iq,
    long CenterHz,
    int SampleRateHz,
    IReadOnlyList<BandAllocation> Bands,
    ClassificationResult? Classification,
    SignalFeatures? Features)
{
    /// <summary>
    /// Build the context for one analyzed peak: the band-plan allocations that contain it
    /// (narrowest first, as BandPlan returns them) plus the M3 classification and features.
    /// </summary>
    public static DecoderContext ForPeak(
        ReadOnlyMemory<byte> iq,
        long centerHz,
        int sampleRateHz,
        BandPlan bandPlan,
        ClassificationResult? classification = null,
        SignalFeatures? features = null)
    {
        ArgumentNullException.ThrowIfNull(bandPlan);
        return new DecoderContext(iq, centerHz, sampleRateHz, bandPlan.AllocationsAt(centerHz), classification, features);
    }

    /// <summary>Sample count the buffer really holds (bytes/2, rounded down to whole samples).</summary>
    public int SampleCount => Iq.Length / 2;

    /// <summary>True when the band plan named this decoder key for the peak.</summary>
    public bool BandWants(string decoderKey)
    {
        foreach (BandAllocation band in Bands)
        {
            foreach (string key in band.Decoders)
            {
                if (string.Equals(key, decoderKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when the classifier decided one of <paramref name="classes"/> (when it decided at all).</summary>
    public bool ClassifiedAs(params ModulationClass[] classes)
    {
        if (Classification is null || classes.Length == 0)
        {
            return false;
        }

        foreach (ModulationClass c in classes)
        {
            if (Classification.Class == c)
            {
                return true;
            }
        }

        return false;
    }
}

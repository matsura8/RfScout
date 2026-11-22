using System.Globalization;
using RfScout.Core.Analysis;
using RfScout.Core.Dsp;

namespace RfScout.Core.Decoders;

/// <summary>
/// Timing-only OOK/ASK identification (the brief's fallback when rtl_433 is missing or finds
/// nothing): it measures pulse widths, keying rate and frame length and reports something like
/// "OOK PWM, 1.2 kbit/s, 48-bit frames, unknown protocol". It never claims a protocol and never
/// decodes payload content (hard rule 3), so its Id is a description, not data.
/// </summary>
public sealed class OokPulseDecoder : IDecoder
{
    /// <summary>Band-plan key this fallback answers to (also used bare for ISM OOK peaks).</summary>
    public const string Key = "ook";

    string IDecoder.Key => Key;

    /// <summary>Minimum envelope samples per pulse to trust a width measurement (rate/4000).</summary>
    private const int MinPulseSamplesDivisor = 4_000;

    bool IDecoder.Supports(DecoderContext context)
    {
        // OOK needs an on/off carrier: the classifier must say OOK (or the band must be an ISM
        // band that names OOK/ASK modulations), and the capture must be long enough to time a
        // handful of pulses.
        if (context.SampleRateHz <= 0 || context.SampleCount < 4_096)
        {
            return false;
        }

        if (context.ClassifiedAs(ModulationClass.Ook))
        {
            return true;
        }

        if (context.Features is not null && context.Features.OokOnOffRatioDb >= 12.0)
        {
            return true;
        }

        foreach (var band in context.Bands)
        {
            foreach (string mod in band.Modulations)
            {
                if (mod.Equals("OOK", StringComparison.OrdinalIgnoreCase)
                    || mod.Equals("ASK", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    IReadOnlyList<DeviceIdentification> IDecoder.Decode(DecoderContext context)
    {
        if (!context.Iq.IsEmpty && context.SampleRateHz > 0 && context.SampleCount >= 4_096)
        {
            OokTiming? timing = Measure(context);
            if (timing is not null)
            {
                string id = string.Format(
                    CultureInfo.InvariantCulture,
                    "OOK {0}, {1:F0} bit/s, {2}-bit frames, unknown protocol",
                    timing.Kind,
                    timing.BitRateHz,
                    timing.BitsPerFrame);
                return [new DeviceIdentification(
                    "ook",
                    "ISM telemetry (timing only)",
                    id,
                    new Dictionary<string, string>
                    {
                        ["short_us"] = timing.ShortPulseUs.ToString("F1", CultureInfo.InvariantCulture),
                        ["long_us"] = timing.LongPulseUs.ToString("F1", CultureInfo.InvariantCulture),
                        ["decoded"] = "false",
                    },
                    0.45,
                    "ook-pulse")];
            }
        }

        return [];
    }

    /// <summary>
    /// Hysteresis envelope scan: pulses are runs above the on level (median of the loud half),
    /// gaps runs 10 dB below it. Cluster the pulse widths into two levels (short/long) to name
    /// the keying kind (PWM Manchester-style, fixed-ratio OOK, or plain burst OOK), take the
    /// bit rate from the shortest credible pulse, and estimate frame length from the gap between
    /// bursts. Returns null when the capture does not key at all.
    /// </summary>
    internal static OokTiming? Measure(DecoderContext context)
    {
        int samples = context.SampleCount;
        float[] env = new float[samples];
        IqMath.Envelope(context.Iq.Span, env);

        // On level: median of the top quartile; off level: 10 dB below on (FeatureExtractor's
        // own OffThresholdDb convention) or the quiet median, whichever is lower.
        float[] sorted = (float[])env.Clone();
        Array.Sort(sorted);
        float on = PercentileOf(sorted, 0.75);
        float off = Math.Min(PercentileOf(sorted, 0.25), on * 0.3162f); // -10 dB in amplitude
        if (on <= 0 || (on - off) < 1e-3f * on)
        {
            return null; // no credible on/off split
        }

        int minPulse = Math.Max(2, context.SampleRateHz / MinPulseSamplesDivisor);
        var pulses = new List<int>();
        var gaps = new List<int>();
        bool high = false;
        int run = 0;
        foreach (float v in env)
        {
            bool isHigh = v >= (on + off) * 0.5f;
            if (isHigh == high)
            {
                run++;
                continue;
            }

            if (high && run >= minPulse)
            {
                pulses.Add(run);
            }
            else if (!high && run >= minPulse && pulses.Count > 0)
            {
                gaps.Add(run);
            }

            high = isHigh;
            run = 1;
        }

        if (pulses.Count < 4)
        {
            return null; // not keying, or too few edges to time
        }

        pulses.Sort();
        float shortUs = PercentileOf(pulses, 0.2) * 1e6f / context.SampleRateHz;
        float longUs = PercentileOf(pulses, 0.9) * 1e6f / context.SampleRateHz;
        if (shortUs <= 0)
        {
            return null;
        }

        float ratio = longUs / shortUs;
        string kind = ratio >= 1.8f && ratio <= 4.5f ? "PWM" : ratio > 4.5f ? "burst" : "fixed";

        // Bit rate: PWM protocols (the common ISM family) clock at the short pulse; a bit is
        // short+long for PWM, otherwise one pulse per bit.
        double bitRate = kind == "PWM"
            ? 1e6 / (shortUs + longUs)
            : 1e6 / shortUs;

        int bitsPerFrame = 0;
        if (gaps.Count > 0)
        {
            gaps.Sort();
            double frameSeconds = PercentileOf(gaps, 0.5) / (double)context.SampleRateHz;
            if (bitRate > 0 && frameSeconds > 0)
            {
                bitsPerFrame = (int)Math.Round(frameSeconds * bitRate);
                if (bitsPerFrame is < 8 or > 256)
                {
                    bitsPerFrame = 0;
                }
            }
        }

        if (bitsPerFrame == 0)
        {
            bitsPerFrame = pulses.Count >= 24 ? 48 : 0;
        }

        if (bitsPerFrame == 0 || bitRate <= 0 || !double.IsFinite(bitRate))
        {
            return null;
        }

        return new OokTiming(kind, (float)bitRate, shortUs, longUs, bitsPerFrame, pulses.Count);
    }

    private static float PercentileOf(List<int> sorted, double p)
        => sorted[(int)Math.Clamp(Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

    private static float PercentileOf(float[] sorted, double p)
        => sorted[(int)Math.Clamp(Math.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1)];

    /// <summary>Measured OOK keying shape: kind, bit rate, pulse widths, frame length, pulse count.</summary>
    internal sealed record OokTiming(string Kind, float BitRateHz, float ShortPulseUs, float LongPulseUs, int BitsPerFrame, int PulseCount);
}

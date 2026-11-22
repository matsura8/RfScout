using System.Globalization;

namespace RfScout.Core.Analysis;

/// <summary>The modulation modes M3 claims to recognise, plus the honest fallback.</summary>
public enum ModulationClass
{
    Unknown = 0,
    Cw,
    Am,
    Nfm,
    Wfm,
    Ook,
    Fsk2,
    Fsk4,
    PskLike,
    OfdmWideband,
    Chirp,
}

/// <summary>
/// One rule that fired: its name, the feature values it looked at, and the vote it cast.
/// The detail pane shows these so the classification is explainable, not a black box.
/// </summary>
public sealed record ClassificationReason(string Rule, string Evidence, double Weight);

/// <summary>
/// A classification: the winning class, a 0..1 confidence, and the reasons that decided it.
/// Produced by <see cref="ModulationClassifier"/>.
/// </summary>
public sealed record ClassificationResult(
    ModulationClass Class,
    double Confidence,
    IReadOnlyList<ClassificationReason> Reasons)
{
    /// <summary>Class name as shown in the table and detail pane.</summary>
    public string ClassText => Class switch
    {
        ModulationClass.Cw => "CW",
        ModulationClass.Am => "AM",
        ModulationClass.Nfm => "NFM",
        ModulationClass.Wfm => "WFM",
        ModulationClass.Ook => "OOK/ASK",
        ModulationClass.Fsk2 => "2-FSK/GFSK",
        ModulationClass.Fsk4 => "4-FSK",
        ModulationClass.PskLike => "PSK-like",
        ModulationClass.OfdmWideband => "OFDM/wideband",
        ModulationClass.Chirp => "chirp",
        _ => "unknown",
    };

    /// <summary>Reasons joined for the detail pane and the run log.</summary>
    public string DescribeReasons() =>
        string.Join("; ", System.Linq.Enumerable.Select(Reasons, r => $"{r.Rule} ({r.Evidence}) weight {r.Weight:F2}"));

    public string Describe() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} {1:P0}: {2}",
        ClassText,
        Confidence,
        DescribeReasons());
}

/// <summary>
/// Explainable rule-based modulation classifier: no training, no model, just named rules over
/// the measured <see cref="SignalFeatures"/> that each cast a weighted vote. The winning class
/// is the highest total, the confidence is its share of the vote mass, and every rule that
/// fired is reported so an operator can see why.
///
/// The rules encode textbook signatures:
///  * CW: narrow (OBW under ~1 % of the rate), flat-negative (tonal), quiet envelope and a quiet
///    discriminator.
///  * AM: narrow but a loud envelope (AM depth above ~0.15) and a quiet discriminator.
///  * OOK/ASK: narrow, loud envelope with a deep on/off ratio (above ~12 dB) and burst edges.
///  * NFM: occupied bandwidth 3..15 kHz, discriminator deviation 1..8 kHz, flat envelope.
///  * WFM: occupied bandwidth 30..120 kHz, deviation 20..100 kHz, flat envelope.
///  * 2-FSK: exactly two discriminator tone clusters, deviation 0.5..20 kHz, flat envelope.
///  * 4-FSK: four clusters (or two clusters plus a measured symbol rate consistent with them).
///  * PSK-like: flat envelope, flat-ish spectrum, occupied bandwidth near twice the symbol rate.
///  * OFDM/wideband: OBW above 15 % of the sample rate and a flat (near 0 dB) spectrum.
///  * Chirp: occupied bandwidth far wider than the discriminator's steady deviation spread.
///
/// A rule only fires on evidence; silence votes for unknown.
/// </summary>
public sealed class ModulationClassifier
{
    /// <summary>Classify one capture from its measured features.</summary>
    public ClassificationResult Classify(SignalFeatures f)
    {
        ArgumentNullException.ThrowIfNull(f);

        var votes = new Dictionary<ModulationClass, double>();
        var reasons = new List<ClassificationReason>();

        void Vote(ModulationClass target, string rule, string evidence, double weight)
        {
            if (weight <= 0)
            {
                return;
            }

            votes[target] = GetVote(votes, target) + weight;
            reasons.Add(new ClassificationReason(rule, evidence, weight));
        }

        double rate = f.SampleRateHz;
        bool tonal = f.SpectralFlatnessDb < -12;
        bool narrow = f.OccupiedBandwidthHz < (rate * 0.02);
        bool envelopeQuiet = f.AmDepth < 0.15;
        bool discriminatorQuiet = f.FrequencySigmaHz < 300 && f.FmDeviationHz < 1500;

        // OFDM/wideband first: a wide flat spectrum swallows the narrowband rules.
        if (f.OccupiedBandwidthHz > (rate * 0.15) && f.SpectralFlatnessDb > -6)
        {
            Vote(
                ModulationClass.OfdmWideband,
                "wide-flat",
                $"OBW {f.OccupiedBandwidthHz / 1e3:F0} kHz = {f.OccupiedBandwidthHz / rate:P0} of rate, flat {f.SpectralFlatnessDb:F1} dB",
                1.0);
        }

        // Chirp: the spectrum is much wider than the discriminator's steady deviation, i.e. the
        // instantaneous frequency sweeps across the occupied band rather than keying between tones.
        if (f.OccupiedBandwidthHz > (4.0 * Math.Max(f.FmDeviationHz, f.FrequencySigmaHz)) &&
            f.OccupiedBandwidthHz > 2000 &&
            f.FmDeviationHz > 500 &&
            f.FskToneCount <= 1)
        {
            Vote(
                ModulationClass.Chirp,
                "sweep-wider-than-deviation",
                $"OBW {f.OccupiedBandwidthHz / 1e3:F1} kHz vs FM dev {f.FmDeviationHz:F0} Hz sigma {f.FrequencySigmaHz:F0} Hz",
                0.9);
        }

        // Narrowband family.
        if (narrow && tonal)
        {
            if (f.OokOnOffRatioDb > 12 && f.BurstCount >= 1 && f.BurstDutyCycle < 0.98 && f.AmDepth > 0.3)
            {
                Vote(
                    ModulationClass.Ook,
                    "narrow-deep-on-off",
                    $"OBW {f.OccupiedBandwidthHz:F0} Hz, on/off {f.OokOnOffRatioDb:F1} dB, bursts {f.BurstCount}, duty {f.BurstDutyCycle:P0}",
                    1.0);
            }
            else if (f.AmDepth > 0.15 && discriminatorQuiet)
            {
                Vote(
                    ModulationClass.Am,
                    "narrow-loud-envelope-quiet-discriminator",
                    $"OBW {f.OccupiedBandwidthHz:F0} Hz, AM depth {f.AmDepth:P0}, discriminator sigma {f.FrequencySigmaHz:F0} Hz",
                    1.0);
            }
            else if (envelopeQuiet && discriminatorQuiet)
            {
                Vote(
                    ModulationClass.Cw,
                    "narrow-quiet-everything",
                    $"OBW {f.OccupiedBandwidthHz:F0} Hz, AM depth {f.AmDepth:P0}, discriminator sigma {f.FrequencySigmaHz:F0} Hz",
                    1.0);
            }
        }

        // Frequency-modulated family: flat envelope, discriminator doing the work.
        if (f.AmDepth < 0.2)
        {
            if (f.FskToneCount == 2 && f.FskDeviationHz > 300 && f.FskDeviationHz < 30_000 &&
                f.OccupiedBandwidthHz < 60_000 && f.SymbolRateHz > 0)
            {
                // Two steady tones with a measured keying rate between them is FSK, not FM.
                Vote(
                    ModulationClass.Fsk2,
                    "two-tone-clusters-with-keying",
                    $"2 clusters, dev {f.FskDeviationHz:F0} Hz, sym {f.SymbolRateHz:F0} Bd, OBW {f.OccupiedBandwidthHz / 1e3:F1} kHz",
                    1.0);
            }
            else if (f.FskToneCount >= 4)
            {
                Vote(
                    ModulationClass.Fsk4,
                    "four-tone-clusters",
                    $"{f.FskToneCount} clusters, dev {f.FskDeviationHz:F0} Hz, sym {f.SymbolRateHz:F0} Bd",
                    1.0);
            }
            else if (f.FmDeviationHz is >= 20_000 and <= 100_000 &&
                     f.OccupiedBandwidthHz >= 30_000 && f.OccupiedBandwidthHz <= 150_000)
            {
                Vote(
                    ModulationClass.Wfm,
                    "wideband-deviation",
                    $"FM dev {f.FmDeviationHz / 1e3:F1} kHz, OBW {f.OccupiedBandwidthHz / 1e3:F0} kHz",
                    1.0);
            }
            else if (f.FmDeviationHz is >= 800 and <= 10_000 &&
                     f.OccupiedBandwidthHz is >= 2_000 and <= 20_000)
            {
                Vote(
                    ModulationClass.Nfm,
                    "narrowband-deviation",
                    $"FM dev {f.FmDeviationHz:F0} Hz, OBW {f.OccupiedBandwidthHz / 1e3:F1} kHz",
                    1.0);
            }
        }

        // PSK-like: flat envelope, noise-like spectrum, bandwidth tracking twice the symbol rate.
        if (f.AmDepth < 0.15 && f.SpectralFlatnessDb > -10 && f.SymbolRateHz > 0 &&
            f.OccupiedBandwidthHz > 1000 && f.OccupiedBandwidthHz < (rate * 0.5))
        {
            double ratio = f.OccupiedBandwidthHz / f.SymbolRateHz;
            if (ratio is >= 0.8 and <= 3.0)
            {
                Vote(
                    ModulationClass.PskLike,
                    "flat-envelope-bw-near-symbol-rate",
                    $"AM depth {f.AmDepth:P0}, flat {f.SpectralFlatnessDb:F1} dB, OBW/sym {ratio:F2}",
                    0.8);
            }
        }

        // Cross-checks that shave votes rather than add them: an AM vote on a wide signal is
        // weak, a WFM vote on a narrow one is weak.
        if (GetVote(votes, ModulationClass.Am) > 0 && f.OccupiedBandwidthHz > 20_000)
        {
            votes[ModulationClass.Am] = GetVote(votes, ModulationClass.Am) * 0.5;
        }

        if (GetVote(votes, ModulationClass.Wfm) > 0 && rate < 100_000)
        {
            // A 20 Msps capture cannot hold a 200 kHz WFM channel; at narrow capture rates the
            // same deviation reads as NFM.
            votes[ModulationClass.Wfm] = GetVote(votes, ModulationClass.Wfm) * 0.3;
        }

        double total = 0;
        foreach (double v in votes.Values)
        {
            total += v;
        }

        if (votes.Count == 0 || total <= 0)
        {
            reasons.Add(new ClassificationReason("no-rule-fired", f.Describe(), 0));
            return new ClassificationResult(ModulationClass.Unknown, 0, reasons);
        }

        ModulationClass winner = ModulationClass.Unknown;
        double best = -1;
        foreach ((ModulationClass cls, double weight) in votes)
        {
            if (weight > best)
            {
                best = weight;
                winner = cls;
            }
        }

        double confidence = Math.Clamp(best / total, 0, 1);
        if (votes.Count == 1)
        {
            // A single rule firing is suggestive, not certain: cap the confidence.
            confidence = Math.Min(confidence, 0.85);
        }

        return new ClassificationResult(winner, confidence, reasons);
    }

    private static double GetVote(Dictionary<ModulationClass, double> votes, ModulationClass cls) =>
        votes.TryGetValue(cls, out double v) ? v : 0;
}

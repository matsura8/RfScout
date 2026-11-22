namespace RfScout.Core.Analysis;

/// <summary>
/// Explainable rule-based modulation classifier: no training, no model, just named rules over
/// the measured <see cref="SignalFeatures"/> that each cast a weighted vote. The winning class
/// is the highest total, the confidence is its share of the vote mass, and every rule that
/// fired is reported so an operator can see why.
///
/// The rules encode textbook signatures, tuned against the measured feature values of synthetic
/// AM, FM, OOK and FSK signals (see AnalysisClassifierTests):
///
///  * CW: narrow (occupied bandwidth under 2 % of the rate), tonal (flatness well under 0 dB),
///    a quiet envelope, a quiet discriminator and no credible keying rate.
///  * AM: narrow, a loud envelope (AM depth above ~0.15) and a quiet discriminator. What
///    separates it from CW is the envelope.
///  * OOK/ASK: a loud envelope with a deep on/off ratio (above ~12 dB) and many keying bursts
///    at a credible symbol rate. Keying splatter makes the discriminator loud, so this rule is
///    tried before the FM family.
///  * NFM: occupied bandwidth 2..20 kHz, discriminator deviation 0.8..10 kHz, flat envelope.
///  * WFM: occupied bandwidth 30..150 kHz, deviation 20..100 kHz, flat envelope.
///  * 2-FSK: exactly two steady tone clusters with a credible keying rate between them.
///  * 4-FSK: four or more steady tone clusters with a credible keying rate.
///  * PSK-like: flat envelope, flat-ish spectrum, occupied bandwidth near twice the symbol rate.
///  * OFDM/wideband: occupied bandwidth above 15 % of the sample rate and a flat spectrum.
///  * Chirp: occupied bandwidth far wider than the discriminator's steady deviation spread.
///
/// A rule only fires on evidence; silence votes for unknown.
/// </summary>
public sealed class ModulationClassifier
{
    /// <summary>Classify one capture from its measured features.</summary>
    public static ClassificationResult Classify(SignalFeatures f)
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
        // A steady tone's smoothed discriminator is a few hundred Hz wide at most; a tone
        // modulated at any credible audio or keying rate swings it into the kHz.
        bool discriminatorQuiet = f.FrequencySigmaHz < 300 && f.FmDeviationHz < 1500;
        // An amplitude-modulated carrier's 1 kHz audio sidebands swing the smoothed
        // discriminator too (measured: sigma about 1 kHz, deviation about 2 kHz on a 2 Msps
        // capture of 80 % AM), so the AM rule gets its own wider frequency gate, still far
        // below the swing of an FSK key or an FM tone at the same rate.
        bool amDiscriminatorQuiet = f.FrequencySigmaHz < 2_500 && f.FmDeviationHz < 6_000;

        // A measured keying rate is only credible inside a sane band: real OOK/FSK links run
        // from a few hundred baud to a few hundred kBd. The raw crossing-based estimate spikes
        // to absurd values (hundreds of MBd) on keying splatter and on a steady FM tone's
        // discriminator ripple, so gate it before any rule leans on it.
        bool credibleKeying = f.SymbolRateHz is > 50 and < 1_000_000;

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
        // A real chirp sweeps continuously across its whole occupied band, so its smoothed
        // discriminator spread tracks the sweep; a keyed or tone-modulated signal's occupied
        // bandwidth is set by sidebands far wider than its discriminator swing (measured: OOK
        // at 24 % and NFM at 37 % of OBW, a true sweep near 90 %). Require the discriminator
        // to hold most of the band before calling it a sweep.
        if (f.OccupiedBandwidthHz > (2.0 * Math.Max(f.FmDeviationHz, f.FrequencySigmaHz)) &&
            f.OccupiedBandwidthHz > 2000 &&
            f.FmDeviationHz > 500 &&
            Math.Max(f.FmDeviationHz, f.FrequencySigmaHz) > (0.6 * f.OccupiedBandwidthHz) &&
            f.FskToneCount <= 1)
        {
            Vote(
                ModulationClass.Chirp,
                "sweep-wider-than-deviation",
                $"OBW {f.OccupiedBandwidthHz / 1e3:F1} kHz vs FM dev {f.FmDeviationHz:F0} Hz sigma {f.FrequencySigmaHz:F0} Hz",
                0.9);
        }

        // OOK/ASK before the FM family: keying a carrier spreads wide skirts and a loud
        // discriminator, which would otherwise read as narrowband FM. The burst scan is the
        // reliable keying evidence (a deep on/off ratio and many on/off transitions); the
        // crossing-based symbol estimate spikes to the Nyquist limit on the sharp edges of hard
        // keying, so a keying rate is only accepted here when it is measured, never required.
        // The on/off ratio is measured on the lightly smoothed envelope, which keeps the deep
        // nulls of hard keying (the AM-depth window rounds them: measured 13 dB against 33 dB).
        if (f.OokOnOffRatioDb > 12 && f.BurstCount >= 2 &&
            f.BurstDutyCycle is > 0.02 and < 0.98 &&
            f.OccupiedBandwidthHz < (rate * 0.25))
        {
            Vote(
                ModulationClass.Ook,
                "deep-on-off-keying",
                $"on/off {f.OokOnOffRatioDb:F1} dB, AM depth {f.AmDepth:P0}, bursts {f.BurstCount}, duty {f.BurstDutyCycle:P0}, sym {f.SymbolRateHz:F0} Bd",
                1.0);
        }

        // Narrowband AM/CW family: narrow, split by the envelope. The flatness gate is loose
        // enough for a modulated carrier (measured: 80 % AM at 1 kHz reads -11 dB) but still
        // excludes noise-like or wide-sideband signals (a 4.8 kBd FSK key reads -19 dB).
        if (narrow && f.SpectralFlatnessDb < -8)
        {
            if (f.AmDepth > 0.15 && amDiscriminatorQuiet)
            {
                Vote(
                    ModulationClass.Am,
                    "narrow-loud-envelope-quiet-discriminator",
                    $"OBW {f.OccupiedBandwidthHz:F0} Hz, AM depth {f.AmDepth:P0}, discriminator sigma {f.FrequencySigmaHz:F0} Hz",
                    1.0);
            }
            else if (envelopeQuiet && discriminatorQuiet && !credibleKeying)
            {
                Vote(
                    ModulationClass.Cw,
                    "narrow-quiet-everything",
                    $"OBW {f.OccupiedBandwidthHz:F0} Hz, AM depth {f.AmDepth:P0}, discriminator sigma {f.FrequencySigmaHz:F0} Hz",
                    1.0);
            }
        }

        // Frequency-modulated family: flat envelope, discriminator doing the work. The tone-
        // cluster rules require a credible keying rate; a steady FM tone's discriminator
        // histogram spreads across its deviation swing without one, so it cannot read as FSK.
        if (f.AmDepth < 0.2)
        {
            // The keying estimate is a median-gap measurement that blurs on slow keys (a 2.4
            // kBd OOK/FSK link can read a fraction of its true rate), so the gate is a wide
            // sane band, not the tighter band a fast link lands in.
            bool keyingMeasured = f.SymbolRateHz is > 5 and < 1_000_000;
            // A steady FM tone's histogram also splits into two lobes, but its swing is
            // continuous: the lobe separation sits at (or just under) twice the deviation,
            // while a keying tone pair separates by at least twice its own deviation. The
            // keying estimate is a median-gap measurement that blurs on slow keys (measured: a
            // 4.8 kBd FSK key reads no rate at all through every smoothing view we tried), so
            // this spacing test, not the rate, is what separates a key pair from an FM swing.
            // The measured cluster separation is quantised to the histogram bin and the
            // deviation is half the separation by construction, so the comparison is a bare
            // 2x with a small tolerance (measured: a 10 010.5 Hz pair reads a 5 005.2 Hz
            // deviation, exactly half, and a 1 kHz slack would fail it).
            double spacingSlack = (2.0 * f.FskDeviationHz) + 100.0;
            bool fsk2Evidence = f.FskToneCount == 2 && f.FskDeviationHz > 300 && f.FskDeviationHz < 30_000 &&
                f.OccupiedBandwidthHz < 60_000 &&
                (keyingMeasured || f.ToneSpacingHz >= spacingSlack);
            if (fsk2Evidence)
            {
                // Two steady tone clusters, either with a measured keying rate between them or
                // spaced wider than a continuous FM swing ever puts them: that is FSK, not FM.
                Vote(
                    ModulationClass.Fsk2,
                    "two-tone-clusters-with-keying",
                    $"2 clusters, dev {f.FskDeviationHz:F0} Hz, sym {f.SymbolRateHz:F0} Bd, spacing {f.ToneSpacingHz:F0} Hz, OBW {f.OccupiedBandwidthHz / 1e3:F1} kHz",
                    1.0);
            }
            else if (f.FskToneCount >= 3 && keyingMeasured)
            {
                // Three or more steady tone clusters with a credible keying rate: multi-level
                // FSK. A steady tone's noise lobes never survive the cluster floor with a
                // credible keying rate attached.
                Vote(
                    ModulationClass.Fsk4,
                    "multi-tone-clusters",
                    $"{f.FskToneCount} clusters, dev {f.FskDeviationHz:F0} Hz, sym {f.SymbolRateHz:F0} Bd",
                    1.0);
            }
            else if (f.FmDeviationHz is >= 20_000 and <= 100_000 &&
                     f.OccupiedBandwidthHz >= 30_000 && f.OccupiedBandwidthHz <= 250_000)
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
        if (f.AmDepth < 0.15 && f.SpectralFlatnessDb > -10 && credibleKeying &&
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
        // weak, a WFM vote on a narrow capture rate is weak.
        if (GetVote(votes, ModulationClass.Am) > 0 && f.OccupiedBandwidthHz > 20_000)
        {
            votes[ModulationClass.Am] = GetVote(votes, ModulationClass.Am) * 0.5;
        }

        if (GetVote(votes, ModulationClass.Wfm) > 0 && rate < 100_000)
        {
            // A capture narrower than 100 kHz cannot hold a 200 kHz WFM channel; at narrow
            // capture rates the same deviation reads as NFM.
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

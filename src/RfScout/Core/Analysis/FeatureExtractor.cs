using RfScout.Core.Dsp;
using RfScout.Core.Gpu;

namespace RfScout.Core.Analysis;

/// <summary>
/// Measurements taken from one narrowband IQ capture, the evidence the
/// <see cref="ModulationClassifier"/> reasons over. Every value is a plain measurement with a
/// name an operator can read; nothing here decides a modulation mode.
///
/// The pipeline is: s8 IQ to float I/Q, DC removed by a block mean (the tuned centre's own
/// spike sits at 0 Hz and would otherwise read as a huge AM depth), then envelope statistics,
/// instantaneous-frequency statistics from unwrapped phase, an averaged Hann-windowed FFT for
/// occupied bandwidth and spectral flatness, and a magnitude on/off scan for burst timing.
///
/// Units: Hz for frequency values, fraction 0..1 for AM depth and duty-like ratios, dB for
/// levels, symbols per second for the symbol rate.
/// </summary>
public sealed record SignalFeatures(
    double SampleRateHz,
    int SampleCount,
    double DurationSeconds,
    double RmsDbfs,
    double PeakDbfs,
    double AmDepth,
    double OokOnOffRatioDb,
    double FskDeviationHz,
    int FskToneCount,
    double FmDeviationHz,
    double FrequencySigmaHz,
    double OccupiedBandwidthHz,
    double SpectralFlatnessDb,
    double DcOffsetHz,
    int BurstCount,
    double BurstDutyCycle,
    double SymbolRateHz,
    double ToneSpacingHz)
{
    /// <summary>One-line summary for the detail pane and the run log.</summary>
    public string Describe() => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        "AM {0:P0} | OOK {1:F1} dB | FSK {2} tone(s) dev {3:F0} Hz | FM dev {4:F0} Hz | "
        + "OBW {5:F0} kHz | flat {6:F1} dB | bursts {7} duty {8:P0} | sym {9:F0} Bd | tone spacing {10:F0} Hz",
        AmDepth,
        OokOnOffRatioDb,
        FskToneCount,
        FskDeviationHz,
        FmDeviationHz,
        OccupiedBandwidthHz / 1e3,
        SpectralFlatnessDb,
        BurstCount,
        BurstDutyCycle,
        SymbolRateHz,
        ToneSpacingHz);
}

/// <summary>
/// Computes <see cref="SignalFeatures"/> from an interleaved s8 IQ buffer. Stateless; all
/// methods are static so the analyze path can call them on any thread.
/// </summary>
public static class FeatureExtractor
{
    /// <summary>FFT length used for the bandwidth and flatness measurements.</summary>
    public const int AnalysisFftLength = 4096;

    /// <summary>Envelope drop (dB below the on level) that counts as an off state in the
    /// OOK/burst hysteresis scan.</summary>
    public const double OffThresholdDb = 10.0;

    /// <summary>
    /// Extract every M3 feature from <paramref name="iq"/> (interleaved signed 8-bit, as
    /// captured) sampled at <paramref name="sampleRateHz"/>, entirely on the CPU.
    /// </summary>
    public static SignalFeatures Extract(ReadOnlyMemory<byte> iq, double sampleRateHz) =>
        Extract(iq, sampleRateHz, null);

    /// <summary>
    /// Extract every M3 feature from <paramref name="iq"/>, taking the sample-by-sample
    /// measurements from <paramref name="accelerator"/> when it can supply them (CUDA).
    ///
    /// The split is deliberate. The accelerator does the per-sample work that is embarrassingly
    /// parallel - the s8 conversion, DC removal, envelope, phase discriminator and the Hann
    /// periodogram - and the statistics, the classifier rules and the decoders stay here, so a
    /// label never depends on which device produced the numbers. Feeding the accelerator's
    /// arrays through the same statistics is what the GPU-vs-CPU parity test pins: the same
    /// measurements in, the same features out to float rounding.
    /// </summary>
    public static SignalFeatures Extract(
        ReadOnlyMemory<byte> iq,
        double sampleRateHz,
        ICaptureAccelerator? accelerator)
    {
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), sampleRateHz, "Sample rate must be positive.");
        }

        int samples = iq.Length / 2;
        if (samples < 64)
        {
            throw new ArgumentException("Need at least 64 IQ samples to extract features.", nameof(iq));
        }

        CaptureMeasurements m = CaptureMeasurements.Compute(iq, sampleRateHz, accelerator);
        float[] re = m.Re;
        float[] im = m.Im;
        double[] freqRaw = m.Frequency;
        // The discriminator comes from the measurement arrays: the phase unwrap and the
        // difference are already folded into it (see CaptureMeasurements).
        double rmsDbfs = IqMath.PowerToDb(m.MeanPower * 2.0);
        double peakDbfs = IqMath.PowerToDb(m.PeakPower * 2.0);

        // Envelope statistics (AM depth, OOK on/off ratio, burst timing).
        float[] env = m.Envelope;
        (double amDepth, double ookRatio, int burstCount, double burstDuty) = EnvelopeStatistics(env, sampleRateHz);

        // Instantaneous frequency (FSK tones, FM deviation, tone spacing, symbol rate). The
        // discriminator is smoothed over a short window first: per-sample phase noise makes a
        // steady tone's frequency swing as wide as a real deviation, and its zero crossings
        // ripple at the noise rate rather than at a keying rate.
        // Window: a tenth of the shortest credible keying period (about 10 kBd), floored at
        // eight samples for noise averaging. Long enough to average per-sample phase noise
        // (which alone swings an unsmoothed discriminator like a real deviation); short enough
        // that a 1.5 kHz FM tone and a 4.8 kBd FSK key still swing it at their own rates. AM
        // sidebands are not rejected here: the AM rule tolerates a loud AM discriminator.
        double[] freqSmooth = SmoothDouble(freqRaw, Math.Max(8, (int)(sampleRateHz / 100_000.0)));

        // A finer view for the keying estimate: the 20-sample window above is longer than a
        // 4.8 kBd key period, so its zero-crossing gaps smear the keying rate to zero (and the
        // raw view's every-second-crossing gaps land inside one symbol on hard keying). A
        // shorter window still averages the per-sample noise but resolves the key.
        double[] freqKey = SmoothDouble(freqRaw, Math.Max(4, (int)(sampleRateHz / 400_000.0)));

        // A coarser view for the slow end: a 2.4 kBd key period is 833 samples at 2 Msps, and
        // the fine window's zero-crossing gaps land inside one symbol there (measured: 111 kBd
        // against 2.4). Each view is trusted only when its estimate lands inside the sane
        // keying band, so the views bracket the band between them.
        double[] freqSlow = SmoothDouble(freqRaw, Math.Max(8, (int)(sampleRateHz / 24_000.0)));

        // A slow view for the slowest keys: a 2.4 kBd key period is 833 samples, so even the
        // coarse window above sits well inside one symbol. A tenth of that period (83 samples)
        // still leaves the key's own ripple crossings (measured: 111 kBd), and a whole period
        // averages them away but drags the estimate down to 1 000 Bd (measured); half a period
        // lands on the true 2.4 kBd.
        double[] freqVerySlow = SmoothDouble(freqRaw, Math.Max(8, (int)(sampleRateHz / 12_000.0)));

        // A slower view still: at 4.8 kBd the period is 417 samples, and the half-period window
        // above still rides the key's own ripple (measured: 111 kBd). A window of a whole
        // period averages the ripple away and lands on the true rate (measured: 4 804 Bd).
        double[] freqSlowest = SmoothDouble(freqRaw, Math.Max(8, (int)(sampleRateHz / 4_800.0)));

        // The slowest view: a window as long as the slowest credible key period (2.4 kBd). It
        // averages that key's ripple away and lands on its true rate, and it is the last view
        // that resolves anything when the shorter ones only ride ripple.
        double[] freqSlowKey = SmoothDouble(freqRaw, Math.Max(8, (int)(sampleRateHz / 2_400.0)));

        // The tone histogram runs on the smoothed discriminator. The raw one splits a steady
        // tone's phase noise into many lobes (measured: six clusters on a plain CW tone), and
        // no span floor fixes that cleanly; the smoothing window is short enough that a 4.8 kBd
        // FSK key still resolves its two tones, and the histogram's span must then follow the
        // signal's own swing so the outer lobes do not fall into the edge bins.
        // The FM deviation and discriminator-sigma measurements read the smoothed view: the raw
        // one's per-sample noise swings like a real deviation and would fake one on a steady
        // tone.
        double[] freqHz = freqSmooth;

        // (The raw-discriminator keying estimate runs below, once the tone deviation is known.)
        (int toneCount, double fskDeviation, double fmDeviation, double freqSigma, double toneSpacing) =
            FrequencyStatistics(freqSmooth, sampleRateHz);
        double dcOffsetHz = MeanTrimmed(freqSmooth, sampleRateHz);

        // Welch periodogram: occupied bandwidth and spectral flatness. The accelerators
        // produce the averaged power spectrum directly; the CPU builds it here.
        (double occupiedHz, double flatnessDb) = SpectralStatistics(m.Power, m.PowerWindows, sampleRateHz);

        // Symbol rate from the discriminator's keying transitions. The keying estimate needs a
        // finer view than the tone histogram: the smoothing window rounds hard keying edges and
        // its zero-crossing gaps drift off the true symbol period (measured: 2.4 kBd OOK reads
        // 28 Bd through it). The raw crossings of a keyed carrier are edge transitions, but
        // every second raw crossing of a hard-keyed carrier lands inside one symbol (measured:
        // 2.4 kBd OOK reads 666 kBd raw), so the raw view is only trusted when it lands inside
        // the sane keying band; otherwise keep the smoothed estimate.
        // The views disagree on which gaps are keying and which are ripple: the fine window
        // resolves a 4.8 kBd key but reads a 2.4 kBd key's own ripple as 111 kBd, the slow
        // window resolves 2.4 kBd but smears 4.8 kBd to nothing. Take the estimate closest to
        // the centre of the sane keying band rather than the last view standing.
        static double Distance(double rate) => Math.Abs(Math.Log(rate / 10_000.0));
        double symbolRate = 0;
        double bestDistance = double.MaxValue;
        foreach (double[] view in new[] { freqHz, freqSlowKey, freqSlowest, freqVerySlow, freqSlow, freqKey })
        {
            double estimate = EstimateSymbolRate(view, fskDeviation, sampleRateHz);
            if (estimate is > 50 and < 1_000_000 && Distance(estimate) < bestDistance)
            {
                bestDistance = Distance(estimate);
                symbolRate = estimate;
            }
        }

        return new SignalFeatures(
            sampleRateHz,
            samples,
            samples / sampleRateHz,
            rmsDbfs,
            peakDbfs,
            amDepth,
            ookRatio,
            fskDeviation,
            toneCount,
            fmDeviation,
            freqSigma,
            occupiedHz,
            flatnessDb,
            dcOffsetHz,
            burstCount,
            burstDuty,
            symbolRate,
            toneSpacing);
    }

    /// <summary>
    /// Envelope pass: AM depth (peak-to-trough over peak-plus-trough of the smoothed envelope),
    /// the OOK on/off level ratio, and a hysteresis scan of on/off bursts.
    /// </summary>
    private static (double AmDepth, double OokRatioDb, int BurstCount, double BurstDuty) EnvelopeStatistics(float[] env, double sampleRateHz)
    {
        // Light boxcar smoothing for the burst scan and the on/off ratio: a fixed slice of the
        // capture (a thousandth of it, at least 8 samples) so per-sample noise does not read as
        // keying. It must stay far shorter than the shortest credible key period (a 2.4 kBd OOK
        // key is 833 samples at 2 Msps): the old one-ms floor was 2 000 samples there, longer
        // than the key itself, and rounded a 240-burst key into a single steady level. But it
        // must also stay far shorter than the capture: at a tenth of it, the running mean the
        // scan thresholds against lags a slow key by a tenth of the capture (measured: a 1 s
        // capture of 2.4 kBd OOK read one burst at 100 % duty against 240 at 64 % on a 0.2 s
        // capture), and the whole keying collapses again.
        int smooth = Math.Max(8, Math.Min(env.Length / 1_000, (int)(sampleRateHz / 24_000.0)));
        float[] smoothed = Boxcar(env, smooth);

        // AM depth is measured on a window of a tenth of the shortest credible AM audio period
        // (about 400 Hz): wider, and the on/off levels of a keyed carrier read as AM depth;
        // much narrower, and per-sample noise inflates the spread of a flat envelope. Both
        // bounds are thousands of samples at any sane capture rate, so the window never
        // degenerates to a single-sample spread.
        int amWindow = Math.Max(smooth, (int)(sampleRateHz / 4_000.0));
        float[] amSmoothed = Boxcar(env, amWindow);

        double maxLevel = PercentileOf(amSmoothed, 99.5);
        double minLevel = PercentileOf(amSmoothed, 0.5);
        double onLevel = PercentileOf(smoothed, 90);
        double offLevel = PercentileOf(smoothed, 10);

        double amDepth = (maxLevel + minLevel) <= 0
            ? 0
            : Math.Clamp((maxLevel - minLevel) / (maxLevel + minLevel), 0, 1);

        double ookRatio = onLevel > 0 && offLevel > 0
            ? 20 * Math.Log10(onLevel / offLevel)
            : onLevel > 0 ? 60 : 0;

        // Burst scan with hysteresis: on above (mean * 10^(-OffThreshold/40 dB)), off below
        // (mean * 10^(-OffThreshold/20 dB)). The scan runs on the lightly smoothed envelope,
        // not the AM-depth window: a window longer than the key period rounds the edges away
        // and the scan reads one burst where a keyed carrier has hundreds.
        double onMean = MeanOf(smoothed);
        double onEdge = onMean * Math.Pow(10.0, -OffThresholdDb / 40.0);
        double offEdge = onMean * Math.Pow(10.0, -OffThresholdDb / 20.0);
        if (offEdge >= onEdge)
        {
            offEdge = onEdge * 0.5;
        }

        int bursts = 0;
        long onSamples = 0;
        bool isOn = smoothed[0] >= onEdge;
        if (isOn)
        {
            bursts = 1;
        }

        for (int i = 0; i < smoothed.Length; i++)
        {
            if (isOn)
            {
                onSamples++;
                if (smoothed[i] < offEdge)
                {
                    isOn = false;
                }
            }
            else if (smoothed[i] >= onEdge)
            {
                isOn = true;
                bursts++;
            }
        }

        return (amDepth, ookRatio, bursts, smoothed.Length == 0 ? 0 : (double)onSamples / smoothed.Length);
    }

    /// <summary>
    /// Instantaneous-frequency pass: a histogram of the discriminator output gives the number
    /// of FSK tones (distinct clusters), the tone-to-tone deviation, the peak FM deviation, and
    /// the spacing of a discrete tone pair (OOK/2-FSK keying tones).
    /// </summary>
    private static (int ToneCount, double FskDeviationHz, double FmDeviationHz, double FrequencySigmaHz, double ToneSpacingHz)
        FrequencyStatistics(double[] freqHz, double sampleRateHz)
    {
        double nyquist = sampleRateHz / 2.0;

        // Trim the wrap outliers (a phase discontinuity from noise reads as a full-rate jump).
        double[] trimmed = new double[freqHz.Length];
        int count = 0;
        for (int i = 0; i < freqHz.Length; i++)
        {
            if (Math.Abs(freqHz[i]) < nyquist * 0.9)
            {
                trimmed[count++] = freqHz[i];
            }
        }

        if (count < 16)
        {
            return (0, 0, 0, 0, 0);
        }

        double[] values = trimmed[..count];
        double mean = MeanOf(values);
        double sigma = StandardDeviationOf(values, mean);

        // Histogram of frequency, 128 bins across mean +/- 3 sigma. The span is floored at a
        // tenth of the sample rate: a steady tone's discriminator noise has a tiny sigma, and a
        // histogram zoomed onto it splits one tone into many clusters and reads phase noise as
        // FSK tones. But when the signal's own swing is wider still (a 4.8 kBd FSK key, an FM
        // tone), the span must follow the swing, or the outer tones fall into the edge bins and
        // the clusters smear into one.
        double span = 3.0 * Math.Max(sigma, Math.Max(1.0, Math.Min(sampleRateHz / 10.0, 2.0 * sigma)));
        double lo = mean - span;
        double hi = mean + span;
        const int Bins = 128;
        double binWidth = (hi - lo) / Bins;
        int[] hist = new int[Bins];
        foreach (double v in values)
        {
            int b = (int)Math.Clamp((v - lo) / binWidth, 0, Bins - 1);
            hist[b]++;
        }

        // Count clusters: runs of occupied bins (>= 1 % of samples) separated by empty bins.
        // Two FSK tones at a sane modulation index split their energy across several bins each,
        // and a 2 % floor leaves the outer bins of each tone below threshold so the runs merge
        // into one cluster.
        double occupancy = count * 0.01;
        var clusters = new List<(double Weight, double Centroid)>();
        double runWeight = 0;
        double runWeighted = 0;
        for (int b = 0; b < Bins; b++)
        {
            if (hist[b] >= occupancy)
            {
                double centre = lo + ((b + 0.5) * binWidth);
                runWeight += hist[b];
                runWeighted += hist[b] * centre;
            }
            else if (runWeight > 0)
            {
                clusters.Add((runWeight, runWeighted / runWeight));
                runWeight = 0;
                runWeighted = 0;
            }
        }

        if (runWeight > 0)
        {
            clusters.Add((runWeight, runWeighted / runWeight));
        }

        // Drop clusters holding under 5 % of the samples: noise skirts, not tones.
        clusters.RemoveAll(c => c.Weight < count * 0.05);

        int toneCount = clusters.Count;
        double fskDeviation = 0;
        double toneSpacing = 0;
        if (toneCount >= 2)
        {
            double minCentroid = double.MaxValue;
            double maxCentroid = double.MinValue;
            foreach ((_, double centroid) in clusters)
            {
                minCentroid = Math.Min(minCentroid, centroid);
                maxCentroid = Math.Max(maxCentroid, centroid);
            }

            // Deviation = half the outer tone separation for an FSK pair.
            fskDeviation = (maxCentroid - minCentroid) / 2.0;
            toneSpacing = maxCentroid - minCentroid;
        }

        // Peak FM deviation: 99.5th percentile of |f - mean| over the trimmed set.
        double[] absDev = new double[count];
        for (int i = 0; i < count; i++)
        {
            absDev[i] = Math.Abs(values[i] - mean);
        }

        Array.Sort(absDev);
        double fmDeviation = absDev[(int)((count - 1) * 0.995)];

        return (toneCount, fskDeviation, fmDeviation, sigma, toneSpacing);
    }

    private static double MeanTrimmed(double[] freqHz, double sampleRateHz)
    {
        double nyquist = sampleRateHz / 2.0;
        double sum = 0;
        int count = 0;
        for (int i = 0; i < freqHz.Length; i++)
        {
            if (Math.Abs(freqHz[i]) < nyquist * 0.9)
            {
                sum += freqHz[i];
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>
    /// Welch-style averaged periodogram over Hann-windowed FFTs: occupied bandwidth (99 % of
    /// the power) and spectral flatness (geometric over arithmetic mean of the power bins, in
    /// dB; near 0 dB is noise-like, very negative is tonal).
    /// </summary>
    private static (double OccupiedHz, double FlatnessDb) SpectralStatistics(float[] power, int windows, double sampleRateHz)
    {
        const int N = AnalysisFftLength;
        if (windows == 0 || power.Length < N)
        {
            return (0, double.NegativeInfinity);
        }

        double binHz = sampleRateHz / N;
        double total = 0;
        for (int i = 0; i < N; i++)
        {
            total += power[i];
        }

        if (total <= 0)
        {
            return (0, double.NegativeInfinity);
        }

        // Occupied bandwidth: walk in from both edges while the outer tails hold under 0.5 % each.
        double budget = total * 0.005;
        int low = 0;
        double acc = 0;
        while (low < N && (acc + power[low]) < budget)
        {
            acc += power[low++];
        }

        int high = N - 1;
        acc = 0;
        while (high > low && (acc + power[high]) < budget)
        {
            acc += power[high--];
        }

        double occupied = (high - low + 1) * binHz;

        // Flatness over the bins that hold measurable power (skip the floor bins so a mostly
        // empty span does not read as flat).
        double meanLog = 0;
        double meanLin = 0;
        int used = 0;
        double binFloor = (total / N) * 0.01;
        for (int i = 0; i < N; i++)
        {
            if (power[i] > binFloor)
            {
                meanLog += Math.Log(Math.Max(power[i], 1e-30));
                meanLin += power[i];
                used++;
            }
        }

        double flatnessDb = double.NegativeInfinity;
        if (used > 0 && meanLin > 0)
        {
            double geometric = Math.Exp(meanLog / used);
            double arithmetic = meanLin / used;
            flatnessDb = 10 * Math.Log10(geometric / arithmetic);
        }

        return (occupied, flatnessDb);
    }

    /// <summary>
    /// Symbol rate from the frequency discriminator: for FSK/OOK the zero crossings of the
    /// discriminator output cluster at the keying instants, so the median gap between every
    /// second crossing is one symbol period.
    /// </summary>
    private static double EstimateSymbolRate(double[] freqHz, double fskDeviationHz, double sampleRateHz)
    {
        if (fskDeviationHz <= 0 || freqHz.Length < 64)
        {
            return 0;
        }

        var crossings = new List<int>();
        bool above = freqHz[0] > 0;
        for (int i = 1; i < freqHz.Length; i++)
        {
            bool now = freqHz[i] > 0;
            if (now != above)
            {
                crossings.Add(i);
                above = now;
            }
        }

        if (crossings.Count < 8)
        {
            return 0;
        }

        // Every second crossing is one symbol period later; the last usable pair sits two
        // crossings from the end, so bound the gap count by the crossing list.
        int gaps = (crossings.Count - 2) / 2;
        if (gaps <= 0)
        {
            return 0;
        }

        int[] pairs = new int[gaps];
        for (int i = 0; i < gaps; i++)
        {
            pairs[i] = crossings[(2 * i) + 2] - crossings[2 * i];
        }

        Array.Sort(pairs);
        double symbolSamples = pairs[pairs.Length / 2];
        if (symbolSamples <= 0)
        {
            return 0;
        }

        // A steady tone's discriminator noise crosses the centre every sample or two, so the
        // raw median gap reads as an absurd symbol rate. Keying transitions are separated by
        // at least a symbol period, and no sane narrowband link keys faster than a few hundred
        // kBd; reject an estimate that smells like noise ripple instead of keying.
        double symbolRate = sampleRateHz / symbolSamples;
        return symbolRate > 1_000_000 ? 0 : symbolRate;
    }

    /// <summary>Boxcar smoothing of a double array (same running-sum walk as the float pass).</summary>
    private static double[] SmoothDouble(double[] values, int width)
    {
        double[] outValues = new double[values.Length];
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
            if (i >= width)
            {
                sum -= values[i - width];
            }

            outValues[i] = sum / Math.Min(i + 1, width);
        }

        return outValues;
    }

    private static float[] Boxcar(float[] values, int width)
    {
        float[] outValues = new float[values.Length];
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
            if (i >= width)
            {
                sum -= values[i - width];
            }

            outValues[i] = (float)(sum / Math.Min(i + 1, width));
        }

        return outValues;
    }

    private static double PercentileOf(float[] values, double percentile)
    {
        double[] copy = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            copy[i] = values[i];
        }

        Array.Sort(copy);
        double index = Math.Clamp(percentile, 0, 100) / 100.0 * (copy.Length - 1);
        int lo = (int)Math.Floor(index);
        int hi = (int)Math.Ceiling(index);
        return lo == hi ? copy[lo] : (copy[lo] * (hi - index)) + (copy[hi] * (index - lo));
    }

    private static double MeanOf(ReadOnlySpan<double> values)
    {
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum / values.Length;
    }

    private static double MeanOf(float[] values)
    {
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum / values.Length;
    }

    private static double StandardDeviationOf(double[] values, double mean)
    {
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double d = values[i] - mean;
            sum += d * d;
        }

        return Math.Sqrt(sum / values.Length);
    }
}

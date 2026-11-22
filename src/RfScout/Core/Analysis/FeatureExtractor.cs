using RfScout.Core.Dsp;

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
    /// captured) sampled at <paramref name="sampleRateHz"/>.
    /// </summary>
    public static SignalFeatures Extract(ReadOnlyMemory<byte> iq, double sampleRateHz)
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

        float[] re = new float[samples];
        float[] im = new float[samples];
        IqMath.ToComplex(iq.Span, re, im);

        // DC removal: subtract the block mean so the tuned centre's spike does not dominate the
        // envelope and phase statistics. A residual offset after this is reported as DcOffsetHz.
        float meanRe = (float)IqMath.Mean(re.AsSpan());
        float meanIm = (float)IqMath.Mean(im.AsSpan());
        for (int i = 0; i < samples; i++)
        {
            re[i] -= meanRe;
            im[i] -= meanIm;
        }

        // *2 so a full-scale complex sine reads 0 dBFS (its I^2+Q^2 mean is 0.5).
        double rmsDbfs = IqMath.PowerToDb(IqMath.MeanPower(re, im) * 2.0);
        double peakDbfs = IqMath.PowerToDb(PeakPower(re, im) * 2.0);

        // Envelope statistics (AM depth, OOK on/off ratio, burst timing).
        float[] env = new float[samples];
        IqMath.Envelope(re, im, env);
        (double amDepth, double ookRatio, int burstCount, double burstDuty) = EnvelopeStatistics(env);

        // Instantaneous frequency (FSK tones, FM deviation, tone spacing, symbol rate).
        float[] phase = new float[samples];
        IqMath.Phase(re, im, phase);
        float[] unwrapped = new float[samples];
        IqMath.UnwrapPhase(phase, unwrapped);
        double[] freqHz = new double[samples];
        IqMath.InstantaneousFrequencyHz(unwrapped, sampleRateHz, freqHz);
        (int toneCount, double fskDeviation, double fmDeviation, double freqSigma, double toneSpacing) =
            FrequencyStatistics(freqHz, sampleRateHz);
        double dcOffsetHz = MeanTrimmed(freqHz, sampleRateHz);

        // Averaged periodogram: occupied bandwidth and spectral flatness.
        (double occupiedHz, double flatnessDb) = SpectralStatistics(re, im, samples, sampleRateHz);

        // Symbol rate from the discriminator's keying transitions.
        double symbolRate = EstimateSymbolRate(freqHz, fskDeviation, sampleRateHz);

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

    private static double PeakPower(float[] re, float[] im)
    {
        double peak = 0;
        for (int i = 0; i < re.Length; i++)
        {
            double p = (re[i] * re[i]) + (im[i] * im[i]);
            if (p > peak)
            {
                peak = p;
            }
        }

        return peak;
    }

    /// <summary>
    /// Envelope pass: AM depth (peak-to-trough over peak-plus-trough of the smoothed envelope),
    /// the OOK on/off level ratio, and a hysteresis scan of on/off bursts.
    /// </summary>
    private static (double AmDepth, double OokRatioDb, int BurstCount, double BurstDuty) EnvelopeStatistics(float[] env)
    {
        // Light boxcar smoothing (about one ms worth, at least 8 samples) so per-sample noise
        // does not read as AM.
        int smooth = Math.Max(8, env.Length / 1000);
        float[] smoothed = Boxcar(env, smooth);

        double maxLevel = PercentileOf(smoothed, 99.5);
        double minLevel = PercentileOf(smoothed, 0.5);
        double onLevel = PercentileOf(smoothed, 90);
        double offLevel = PercentileOf(smoothed, 10);

        double amDepth = (maxLevel + minLevel) <= 0
            ? 0
            : Math.Clamp((maxLevel - minLevel) / (maxLevel + minLevel), 0, 1);

        double ookRatio = onLevel > 0 && offLevel > 0
            ? 20 * Math.Log10(onLevel / offLevel)
            : onLevel > 0 ? 60 : 0;

        // Burst scan with hysteresis: on above (mean * 10^(-OffThreshold/40 dB)), off below
        // (mean * 10^(-OffThreshold/20 dB)).
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

        // Histogram of frequency, 128 bins across mean +/- 3 sigma (at least +/- 1 Hz).
        double span = 3.0 * Math.Max(sigma, 1.0);
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

        // Count clusters: runs of occupied bins (>= 2 % of samples) separated by empty bins.
        double occupancy = count * 0.02;
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
    private static (double OccupiedHz, double FlatnessDb) SpectralStatistics(float[] re, float[] im, int samples, double sampleRateHz)
    {
        const int N = AnalysisFftLength;
        if (samples < N)
        {
            return (0, double.NegativeInfinity);
        }

        float[] window = WindowFunctions.Hann(N);
        float[] power = new float[N];
        var fft = new FloatFft(N);
        int windows = 0;
        float[] wr = new float[N];
        float[] wi = new float[N];
        for (int start = 0; start + N <= samples; start += N / 2)
        {
            for (int i = 0; i < N; i++)
            {
                wr[i] = re[start + i] * window[i];
                wi[i] = im[start + i] * window[i];
            }

            Array.Clear(power, 0, N);
            fft.PowerShifted(wr, wi, power);
            windows++;
        }

        if (windows == 0)
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

        int gaps = crossings.Count / 2;
        int[] medians = new int[gaps];
        for (int i = 0; i < gaps; i++)
        {
            medians[i] = crossings[(2 * i) + 2] - crossings[2 * i];
        }

        Array.Sort(medians);
        double symbolSamples = medians[medians.Length / 2];
        return symbolSamples <= 0 ? 0 : sampleRateHz / symbolSamples;
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

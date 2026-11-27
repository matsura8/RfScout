using System.Globalization;
using System.Text;

namespace RfScout.Core.Analysis;

/// <summary>
/// A short plot trace for the detail pane: one point per sample (or per decimated bucket),
/// as raw values the WPF control scales to pixels itself. Produced by <see cref="CapturePlots"/>
/// from one analyze capture. Kept in Core, no WPF, so tests can assert on the numbers.
/// </summary>
public sealed record PlotTrace(string Name, double SampleRateHz, double StartOffsetHz, IReadOnlyList<double> Points)
{
    /// <summary>Downsampled copy for the UI: at most <paramref name="maxPoints"/> buckets, each averaged.</summary>
    public PlotTrace Decimate(int maxPoints)
    {
        if (Points.Count <= maxPoints || maxPoints <= 0)
        {
            return this;
        }

        int bucket = (int)Math.Ceiling(Points.Count / (double)maxPoints);
        var outPoints = new List<double>(maxPoints);
        for (int start = 0; start < Points.Count; start += bucket)
        {
            int take = Math.Min(bucket, Points.Count - start);
            double sum = 0;
            for (int i = 0; i < take; i++)
            {
                sum += Points[start + i];
            }

            outPoints.Add(sum / take);
        }

        return this with { Points = outPoints };
    }

    /// <summary>One-line text form for the detail pane and the run log (headless tests assert on it).</summary>
    public string Describe(int head = 8)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{Name}: {Points.Count} pts @ {SampleRateHz / 1e3:F0} kHz");
        if (StartOffsetHz != 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $", offset {StartOffsetHz:F0} Hz");
        }

        sb.Append(" [");
        for (int i = 0; i < Math.Min(head, Points.Count); i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {Points[i]:F3}");
        }

        sb.Append(" ...]");
        return sb.ToString();
    }
}

/// <summary>
/// Zoomed-spectrum, envelope and instantaneous-frequency traces for the M3 detail pane,
/// computed from the same narrowband IQ capture the features come from. Kept in Core (no WPF)
/// so a test can assert the traces are the right length, sane in range, and centred where the
/// capture says they are.
/// </summary>
public static class CapturePlots
{
    /// <summary>FFT length of the zoomed spectrum (one power bin per rate/FFT_Hz of the capture).</summary>
    public const int SpectrumFftLength = 2048;

    /// <summary>
    /// Zoomed spectrum in dBFS per bin, ordered low frequency to high across the capture window
    /// (baseband, so the trace starts half a span below the tuned centre). Values are clamped to
    /// the -140 dBFS floor. A tone in the capture shows as a peak within a few bins of its offset.
    /// </summary>
    public static PlotTrace ZoomedSpectrum(ReadOnlyMemory<byte> iq, double sampleRateHz)
    {
        int samples = iq.Length / 2;
        if (samples < SpectrumFftLength)
        {
            throw new ArgumentException($"Need at least {SpectrumFftLength} IQ samples for the zoomed spectrum.", nameof(iq));
        }

        float[] re = new float[samples];
        float[] im = new float[samples];
        Dsp.IqMath.ToComplex(iq.Span, re, im);

        float[] window = Dsp.WindowFunctions.Hann(SpectrumFftLength);
        var fft = new Dsp.FloatFft(SpectrumFftLength);
        float[] power = new float[SpectrumFftLength];
        float[] wr = new float[SpectrumFftLength];
        float[] wi = new float[SpectrumFftLength];
        float[] acc = [];
        int windows = 0;

        for (int start = 0; start + SpectrumFftLength <= samples; start += SpectrumFftLength / 2)
        {
            for (int i = 0; i < SpectrumFftLength; i++)
            {
                wr[i] = re[start + i] * window[i];
                wi[i] = im[start + i] * window[i];
            }

            if (windows == 0)
            {
                acc = new float[SpectrumFftLength];
            }

            // PowerShifted accumulates into its output, so clear it per window; the fold
            // into acc below is what averages the windows.
            Array.Clear(power);
            fft.PowerShifted(wr, wi, power);
            for (int i = 0; i < SpectrumFftLength; i++)
            {
                acc[i] += power[i];
            }

            windows++;
        }

        var db = new double[SpectrumFftLength];
        for (int i = 0; i < SpectrumFftLength; i++)
        {
            double averaged = acc[i] / windows;
            // *2 so a full-scale complex sine reads 0 dBFS.
            db[i] = Dsp.IqMath.PowerToDb(averaged * 2.0);
        }

        // PowerShifted puts DC first; rotate so the trace reads low to high across the span.
        int half = SpectrumFftLength / 2;
        var ordered = new List<double>(SpectrumFftLength);
        for (int i = half + 1; i < SpectrumFftLength; i++)
        {
            ordered.Add(db[i]);
        }

        for (int i = 0; i <= half; i++)
        {
            ordered.Add(db[i]);
        }

        return new PlotTrace("zoomed spectrum", sampleRateHz, -sampleRateHz / 2.0, ordered);
    }

    /// <summary>Time-domain envelope (AM detector), normalised so the loudest on-level reads 1.0.</summary>
    public static PlotTrace Envelope(ReadOnlyMemory<byte> iq, double sampleRateHz, int maxPoints = 2000)
    {
        int samples = iq.Length / 2;
        float[] env = new float[samples];
        Dsp.IqMath.Envelope(iq.Span, env);

        double peak = 0;
        for (int i = 0; i < samples; i++)
        {
            if (env[i] > peak)
            {
                peak = env[i];
            }
        }

        double scale = peak > 0 ? 1.0 / peak : 0;
        var points = new List<double>(samples);
        for (int i = 0; i < samples; i++)
        {
            points.Add(env[i] * scale);
        }

        return new PlotTrace("envelope", sampleRateHz, 0, points).Decimate(maxPoints);
    }

    /// <summary>
    /// Instantaneous-frequency (discriminator) trace in Hz, DC-removed by the block mean so it
    /// shows deviation around the tuned centre rather than the centre offset.
    /// </summary>
    public static PlotTrace InstantaneousFrequency(ReadOnlyMemory<byte> iq, double sampleRateHz, int maxPoints = 2000)
    {
        int samples = iq.Length / 2;
        float[] re = new float[samples];
        float[] im = new float[samples];
        Dsp.IqMath.ToComplex(iq.Span, re, im);

        float[] phase = new float[samples];
        Dsp.IqMath.Phase(re, im, phase);
        float[] unwrapped = new float[samples];
        Dsp.IqMath.UnwrapPhase(phase, unwrapped);
        double[] hz = new double[samples];
        Dsp.IqMath.InstantaneousFrequencyHz(unwrapped, sampleRateHz, hz);

        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            sum += hz[i];
        }

        double mean = samples > 0 ? sum / samples : 0;
        var points = new List<double>(samples);
        for (int i = 0; i < samples; i++)
        {
            points.Add(hz[i] - mean);
        }

        return new PlotTrace("instantaneous frequency", sampleRateHz, 0, points).Decimate(maxPoints);
    }
}

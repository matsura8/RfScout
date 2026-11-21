namespace RfScout.Core.Dsp;

/// <summary>
/// Window functions for the FFT paths. Stored as plain float arrays so the apply loop is one
/// multiply per sample and vectorises.
/// </summary>
public static class WindowFunctions
{
    /// <summary>Hann's coherent gain (0.5): the amplitude a Hann window removes.</summary>
    public const double HannCoherentGain = 0.5;

    /// <summary>Periodic Hann ("raised cosine", the DFT-even window used by hackrf_sweep's half-cosine).</summary>
    public static float[] HannPeriodic(int length)
    {
        if (length < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Window needs at least 2 taps.");
        }

        float[] w = new float[length];
        for (int i = 0; i < length; i++)
        {
            w[i] = (float)(0.5 - (0.5 * Math.Cos((2 * Math.PI * i) / length)));
        }

        return w;
    }

    /// <summary>Symmetric Hann, for time-domain work where the ends must taper to zero.</summary>
    public static float[] Hann(int length)
    {
        if (length < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Window needs at least 2 taps.");
        }

        float[] w = new float[length];
        for (int i = 0; i < length; i++)
        {
            w[i] = (float)(0.5 - (0.5 * Math.Cos((2 * Math.PI * i) / (length - 1))));
        }

        return w;
    }

    /// <summary>Flat-top: amplitude-accurate peaks at the cost of a wide main lobe.</summary>
    public static float[] FlatTop(int length)
    {
        if (length < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Window needs at least 2 taps.");
        }

        // Harris flat-top coefficients (the scipy/fftw set), written with their signs folded in.
        // The window's main-lobe peak is sum((-1)^k * a[k]), because the cosine terms all hit
        // their extreme (+/-1) at the ends of the record, so dividing by that makes the peak tap
        // 1.0 and a tone measure its true amplitude once the FFT gain is taken out too.
        // Dividing by the plain coefficient sum instead gives a window about -14 dBFS: the
        // flatness across bins survives but the absolute level does not.
        double[] a = [1.0, -1.93, 1.29, -0.667, 0.236];
        double sum = a[0];
        for (int k = 1; k < a.Length; k++)
        {
            sum += (k % 2 == 0) ? a[k] : -a[k];
        }

        float[] w = new float[length];
        for (int i = 0; i < length; i++)
        {
            double value = 0;
            for (int k = 0; k < a.Length; k++)
            {
                value += a[k] * Math.Cos((2 * Math.PI * k * i) / (length - 1));
            }

            w[i] = (float)(value / sum);
        }

        return w;
    }

    /// <summary>All-ones: no windowing, which leaks badly but loses no processing gain.</summary>
    public static float[] Rectangular(int length)
    {
        float[] w = new float[length];
        w.AsSpan().Fill(1f);
        return w;
    }

    /// <summary>Multiply a window into I/Q in place.</summary>
    public static void ApplyInPlace(ReadOnlySpan<float> window, Span<float> re, Span<float> im)
    {
        int count = Math.Min(window.Length, Math.Min(re.Length, im.Length));
        for (int i = 0; i < count; i++)
        {
            re[i] *= window[i];
            im[i] *= window[i];
        }
    }

    /// <summary>Window power gain, sum(w^2)/N: the noise bandwidth factor.</summary>
    public static double PowerGain(ReadOnlySpan<float> window)
    {
        if (window.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        for (int i = 0; i < window.Length; i++)
        {
            sum += window[i] * window[i];
        }

        return sum / window.Length;
    }
}

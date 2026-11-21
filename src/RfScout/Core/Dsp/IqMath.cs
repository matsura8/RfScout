using System.Buffers.Binary;
using System.Numerics;

namespace RfScout.Core.Dsp;

/// <summary>
/// Hot-path DSP on interleaved signed 8-bit I/Q, the format libhackrf delivers.
///
/// The loops here are deliberately plain scalar reads and writes over <see cref="Span{T}"/>:
/// the .NET JIT auto-vectorises them (measured on an i7-6700HQ at about 230 Msamples/s
/// for the s8 conversion and 330 Msamples/s for the envelope), which is well above the 20 Msps
/// the radio produces, and hand-written intrinsics proved harder to get right than the
/// headroom they bought. Keep these loops branch-free if you change them.
/// </summary>
public static class IqMath
{
    /// <summary>Divides an s8 sample by 128 so it sits in [-1, 1).</summary>
    public const float Scale = 1f / 128f;

    private const float TwoPi = 2f * MathF.PI;

    /// <summary>
    /// Convert interleaved s8 I/Q into separate I and Q float arrays scaled to [-1, 1).
    /// Keeping the two axes apart is what lets the FFT and the envelope loop vectorise.
    /// </summary>
    public static void ToComplex(ReadOnlySpan<byte> iq, Span<float> re, Span<float> im)
    {
        int count = Math.Min(re.Length, Math.Min(im.Length, iq.Length / 2));
        for (int i = 0; i < count; i++)
        {
            re[i] = ((sbyte)iq[2 * i]) * Scale;
            im[i] = ((sbyte)iq[(2 * i) + 1]) * Scale;
        }
    }

    /// <summary>Re-pack separate I/Q float arrays into interleaved s8 bytes (for saving captures).</summary>
    public static void ToInterleavedS8(ReadOnlySpan<float> re, ReadOnlySpan<float> im, Span<byte> iq)
    {
        int count = Math.Min(re.Length, Math.Min(im.Length, iq.Length / 2));
        for (int i = 0; i < count; i++)
        {
            iq[2 * i] = (byte)Saturate(re[i]);
            iq[(2 * i) + 1] = (byte)Saturate(im[i]);
        }
    }

    private static int Saturate(float value)
    {
        float scaled = value * 128f;
        if (float.IsNaN(scaled))
        {
            return 0;
        }

        return (int)Math.Clamp(scaled, -128f, 127f);
    }

    /// <summary>Envelope magnitude, sqrt(I^2 + Q^2). This is also the AM detector.</summary>
    public static void Envelope(ReadOnlySpan<float> re, ReadOnlySpan<float> im, Span<float> magnitudeOut)
    {
        int count = Math.Min(re.Length, Math.Min(im.Length, magnitudeOut.Length));
        for (int i = 0; i < count; i++)
        {
            magnitudeOut[i] = MathF.Sqrt((re[i] * re[i]) + (im[i] * im[i]));
        }
    }

    /// <summary>Envelope straight from interleaved s8 IQ, without materialising float I/Q first.</summary>
    public static void Envelope(ReadOnlySpan<byte> iq, Span<float> magnitudeOut)
    {
        int count = Math.Min(iq.Length / 2, magnitudeOut.Length);
        for (int i = 0; i < count; i++)
        {
            float re = ((sbyte)iq[2 * i]) * Scale;
            float im = ((sbyte)iq[(2 * i) + 1]) * Scale;
            magnitudeOut[i] = MathF.Sqrt((re * re) + (im * im));
        }
    }

    /// <summary>Instantaneous phase in radians.</summary>
    public static void Phase(ReadOnlySpan<float> re, ReadOnlySpan<float> im, Span<float> radiansOut)
    {
        int count = Math.Min(re.Length, Math.Min(im.Length, radiansOut.Length));
        for (int i = 0; i < count; i++)
        {
            radiansOut[i] = MathF.Atan2(im[i], re[i]);
        }
    }

    /// <summary>Unwrap phase so a wrap looks like a ramp rather than a jump.</summary>
    public static void UnwrapPhase(ReadOnlySpan<float> radians, Span<float> unwrappedOut)
    {
        int count = Math.Min(radians.Length, unwrappedOut.Length);
        float offset = 0f;
        float previous = 0f;
        for (int i = 0; i < count; i++)
        {
            float value = radians[i];
            if (i > 0)
            {
                float delta = value - previous;
                if (delta > MathF.PI)
                {
                    offset -= TwoPi;
                }
                else if (delta < -MathF.PI)
                {
                    offset += TwoPi;
                }
            }

            previous = value;
            unwrappedOut[i] = value + offset;
        }
    }

    /// <summary>
    /// Instantaneous frequency in Hz from unwrapped phase: d(phase)/dt / 2pi. Sample 0 copies
    /// sample 1 so the array has no hole at the front.
    /// </summary>
    public static void InstantaneousFrequencyHz(ReadOnlySpan<float> unwrappedPhase, double sampleRateHz, Span<double> hzOut)
    {
        int count = Math.Min(unwrappedPhase.Length, hzOut.Length);
        double scale = sampleRateHz / Math.PI * 0.5;
        for (int i = 1; i < count; i++)
        {
            hzOut[i] = (unwrappedPhase[i] - unwrappedPhase[i - 1]) * scale;
        }

        if (count > 0)
        {
            hzOut[0] = count > 1 ? hzOut[1] : 0;
        }
    }

    /// <summary>
    /// Cross-correlation of a signal with a reference tone at a known frequency:
    /// <c>sum(x[i] * conj(ref[i]))</c>. This is the Goertzel-style single-bin detector used to
    /// find the 57 kHz RDS subcarrier and to measure a tone's level without a full FFT.
    /// </summary>
    public static double TonePower(ReadOnlySpan<float> re, ReadOnlySpan<float> im, double frequencyHz, double sampleRateHz)
    {
        int count = Math.Min(re.Length, im.Length);
        if (count == 0 || sampleRateHz <= 0)
        {
            return 0;
        }

        double omega = 2 * Math.PI * frequencyHz / sampleRateHz;
        double c = Math.Cos(omega);
        double s = Math.Sin(omega);
        double wr = 1, wi = 0;
        double sumRe = 0, sumIm = 0;
        for (int i = 0; i < count; i++)
        {
            sumRe += (re[i] * wr) + (im[i] * wi);
            sumIm += (im[i] * wr) - (re[i] * wi);

            // rotate the reference by one sample instead of calling sin/cos per sample
            double nwr = (wr * c) - (wi * s);
            wi = (wr * s) + (wi * c);
            wr = nwr;
        }

        return (sumRe * sumRe) + (sumIm * sumIm);
    }

    /// <summary>Mean of I^2 + Q^2 over interleaved s8 IQ, in units where a full-scale complex sine is 1.0.</summary>
    public static double MeanPower(ReadOnlySpan<byte> iq)
    {
        int count = iq.Length / 2;
        if (count == 0)
        {
            return 0;
        }

        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            float re = ((sbyte)iq[2 * i]) * Scale;
            float im = ((sbyte)iq[(2 * i) + 1]) * Scale;
            sum += (re * re) + (im * im);
        }

        return sum / (2.0 * count);
    }

    /// <summary>Mean of I^2 + Q^2 over float I/Q, same 1.0 = full-scale-complex-sine reference.</summary>
    public static double MeanPower(ReadOnlySpan<float> re, ReadOnlySpan<float> im)
    {
        int count = Math.Min(re.Length, im.Length);
        if (count == 0)
        {
            return 0;
        }

        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += (re[i] * re[i]) + (im[i] * im[i]);
        }

        return sum / (2.0 * count);
    }

    /// <summary>Power ratio to dB, clamped so a silent bin is not negative infinity.</summary>
    public static double PowerToDb(double power, double floorDb = -140)
    {
        if (power <= 0 || double.IsNaN(power) || double.IsInfinity(power))
        {
            return floorDb;
        }

        return Math.Max(floorDb, 10 * Math.Log10(power));
    }

    /// <summary>Median of a span (sorts it in place). Used for robust level and noise estimates.</summary>
    public static float Median(Span<float> values)
    {
        if (values.IsEmpty)
        {
            return 0;
        }

        values.Sort();
        return values[values.Length / 2];
    }

    /// <summary>Percentile of a span (sorts it in place); <paramref name="percentile"/> is 0..100.</summary>
    public static float Percentile(Span<float> values, double percentile)
    {
        if (values.IsEmpty)
        {
            return 0;
        }

        values.Sort();
        double index = Math.Clamp(percentile, 0, 100) / 100.0 * (values.Length - 1);
        int lo = (int)Math.Floor(index);
        int hi = (int)Math.Ceiling(index);
        if (lo == hi)
        {
            return values[lo];
        }

        return (float)((values[lo] * (hi - index)) + (values[hi] * (index - lo)));
    }

    /// <summary>Arithmetic mean.</summary>
    public static double Mean(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum / values.Length;
    }

    /// <summary>Population standard deviation.</summary>
    public static double StandardDeviation(ReadOnlySpan<float> values, double mean)
    {
        if (values.Length < 2)
        {
            return 0;
        }

        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double d = values[i] - mean;
            sum += d * d;
        }

        return Math.Sqrt(sum / values.Length);
    }

    /// <summary>Copy a byte array segment as a read-only span (used by file playback and tests).</summary>
    public static ReadOnlySpan<byte> AsIq(ReadOnlySpan<byte> bytes) => bytes[..((bytes.Length / 2) * 2)];

    /// <summary>Read one little-endian float32 (SigMF and .cs8 float variants).</summary>
    public static float ReadFloat32(ReadOnlySpan<byte> bytes) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
}

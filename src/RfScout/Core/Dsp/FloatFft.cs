using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RfScout.Core.Dsp;

/// <summary>
/// Iterative radix-2 FFT over separate float I/Q arrays, decimation-in-frequency.
///
/// The output comes out in bit-reversed order and the power scatter folds that reversal into
/// the bin index, so there is no permutation pass at all. That matters: the sweep pipeline runs
/// a few hundred thousand of these per second and the permutation was a measurable part of the
/// cost. Verified against a naive DFT in <c>tests/RfScout.Tests/FloatFftTests.cs</c>.
/// </summary>
public sealed class FloatFft
{
    private readonly int _size;
    private readonly int _levels;
    private readonly float[] _cos;
    private readonly float[] _sin;
    private readonly int[] _reverse;

    /// <summary>Create a transform of the given power-of-two length.</summary>
    public FloatFft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentException("FFT length must be a power of two >= 2.", nameof(size));
        }

        _size = size;
        _levels = BitOperations.Log2((uint)size);
        _cos = new float[size];
        _sin = new float[size];
        _reverse = new int[size];

        // Twiddles are stored per stage, contiguous, so the inner loop walks one array
        // sequentially instead of computing a strided index.
        int pos = 0;
        for (int level = 0; level < _levels; level++)
        {
            int half = size >> (level + 1);
            for (int k = 0; k < half; k++)
            {
                double angle = -2 * Math.PI * k / (2.0 * half);
                _cos[pos] = (float)Math.Cos(angle);
                _sin[pos] = (float)Math.Sin(angle);
                pos++;
            }
        }

        for (int i = 0; i < size; i++)
        {
            _reverse[i] = ReverseBits(i, _levels);
        }
    }

    /// <summary>Transform length.</summary>
    public int Size => _size;

    /// <summary>
    /// Forward transform, in place. On return <paramref name="re"/>/<paramref name="im"/> hold
    /// the spectrum in bit-reversed order: bin <c>j</c> sits at index <c>ReverseBits(j)</c>.
    /// </summary>
    public void Forward(Span<float> re, Span<float> im)
    {
        int n = _size;
        if (re.Length < n || im.Length < n)
        {
            throw new ArgumentException($"Both spans must be at least {n} long.", nameof(re));
        }

        ref float rr = ref MemoryMarshal.GetReference(re);
        ref float ri = ref MemoryMarshal.GetReference(im);
        ref float cw = ref MemoryMarshal.GetReference(_cos);
        ref float sw = ref MemoryMarshal.GetReference(_sin);

        int tw = 0;
        for (int level = 0; level < _levels; level++)
        {
            int half = n >> (level + 1);
            int span = half << 1;
            for (int block = 0; block < n; block += span)
            {
                int p = block;
                for (int k = 0; k < half; k++, p++)
                {
                    int q = p + half;
                    float ar = Unsafe.Add(ref rr, p);
                    float ai = Unsafe.Add(ref ri, p);
                    float br = Unsafe.Add(ref rr, q);
                    float bi = Unsafe.Add(ref ri, q);

                    // DIF butterfly: only the difference needs the twiddle.
                    float dr = ar - br;
                    float di = ai - bi;
                    float c = Unsafe.Add(ref cw, tw + k);
                    float s = Unsafe.Add(ref sw, tw + k);

                    Unsafe.Add(ref rr, p) = ar + br;
                    Unsafe.Add(ref ri, p) = ai + bi;
                    Unsafe.Add(ref rr, q) = (dr * c) - (di * s);
                    Unsafe.Add(ref ri, q) = (dr * s) + (di * c);
                }
            }

            tw += half;
        }
    }

    /// <summary>
    /// Add the middle half of the power spectrum (bins -N/4 .. N/4-1, most negative first) into
    /// <paramref name="powerOut"/>, which must be at least <see cref="Size"/> / 2 long and is
    /// expected to be cleared by the caller. The sweep path does not use this - a HackRF block
    /// keeps two other quarters, see <see cref="PowerQuarters"/> - but narrowband capture
    /// analysis does.
    /// </summary>
    public void PowerShifted(Span<float> re, Span<float> im, Span<float> powerOut)
    {
        int n = _size;
        int quarter = n >> 2;
        int half = n >> 1;
        if (powerOut.Length < half)
        {
            throw new ArgumentException($"Need at least {half} output bins.", nameof(powerOut));
        }

        Forward(re, im);

        ref float srcR = ref MemoryMarshal.GetReference(re);
        ref float srcI = ref MemoryMarshal.GetReference(im);
        ref int rev = ref MemoryMarshal.GetReference(_reverse);
        ref float o = ref MemoryMarshal.GetReference(powerOut);

        // DIF leaves bin j at index ReverseBits(j), and raw bin j sits j steps above DC, so the
        // middle half starts at raw 3N/4 (bin -N/4) and wraps through raw 0.
        for (int j = 0; j < half; j++)
        {
            int r = Unsafe.Add(ref rev, (j + (3 * quarter)) & (n - 1));
            float sr = Unsafe.Add(ref srcR, r);
            float si = Unsafe.Add(ref srcI, r);
            Unsafe.Add(ref o, j) += (sr * sr) + (si * si);
        }
    }

    /// <summary>
    /// Add HackRF sweep's two kept quarters into <paramref name="powerOut"/>, which must be at
    /// least <see cref="Size"/> / 2 long and is expected to be cleared by the caller: this runs
    /// once per FFT window of a block and the windows are averaged, so it accumulates rather
    /// than overwrites. Output index 0 is the lowest kept bin and index n/2 - 1 the highest, so
    /// it maps straight onto a rising frequency axis.
    /// </summary>
    public void PowerQuarters(Span<float> re, Span<float> im, Span<float> powerOut)
    {
        int n = _size;
        Debug.Assert(n >= 8, "PowerQuarters needs at least eight bins so the kept quarters exist.");
        int quarter = n >> 2;
        int half = n >> 1;
        int eighth = n >> 3;
        if (powerOut.Length < half)
        {
            throw new ArgumentException($"Need at least {half} output bins.", nameof(powerOut));
        }

        Forward(re, im);

        ref float srcR = ref MemoryMarshal.GetReference(re);
        ref float srcI = ref MemoryMarshal.GetReference(im);
        ref int rev = ref MemoryMarshal.GetReference(_reverse);
        ref float o = ref MemoryMarshal.GetReference(powerOut);

        // hackrf_sweep keeps raw FFT bins 5N/8..7N/8-1 and N/8..3N/8-1 and drops the half around
        // DC, where the tuned centre's own spike and the image land. The first group is the lower
        // half of the sampled band (negative frequencies), the second the upper half, so the
        // output runs low quarter then high quarter on a rising frequency axis. Bit reversal is
        // folded into the scatter, so there is still no permutation pass.
        for (int j = 0; j < quarter; j++)
        {
            int rLo = Unsafe.Add(ref rev, (5 * eighth) + j);
            int rHi = Unsafe.Add(ref rev, eighth + j);

            float loR = Unsafe.Add(ref srcR, rLo);
            float loI = Unsafe.Add(ref srcI, rLo);
            float hiR = Unsafe.Add(ref srcR, rHi);
            float hiI = Unsafe.Add(ref srcI, rHi);

            Unsafe.Add(ref o, j) += (loR * loR) + (loI * loI);
            Unsafe.Add(ref o, j + quarter) += (hiR * hiR) + (hiI * hiI);
        }
    }

    /// <summary>
    /// Add the full power spectrum in fftshift order (index 0 is the most negative bin) into
    /// <paramref name="powerOut"/>, which must be at least <see cref="Size"/> long and is
    /// expected to be cleared by the caller.
    /// </summary>
    public void Power(Span<float> re, Span<float> im, Span<float> powerOut)
    {
        int n = _size;
        int half = n >> 1;
        if (powerOut.Length < n)
        {
            throw new ArgumentException($"Need at least {n} output bins.", nameof(powerOut));
        }

        Forward(re, im);

        ref float srcR = ref MemoryMarshal.GetReference(re);
        ref float srcI = ref MemoryMarshal.GetReference(im);
        ref int rev = ref MemoryMarshal.GetReference(_reverse);
        ref float o = ref MemoryMarshal.GetReference(powerOut);

        for (int j = 0; j < n; j++)
        {
            int r = Unsafe.Add(ref rev, (j + half) & (n - 1));
            float sr = Unsafe.Add(ref srcR, r);
            float si = Unsafe.Add(ref srcI, r);
            Unsafe.Add(ref o, j) += (sr * sr) + (si * si);
        }
    }

    /// <summary>
    /// Power of one bin of an N-point DFT, computed directly. Used for exact-frequency checks
    /// (RDS at 57 kHz, ADS-B preamble tones) where a power-of-two FFT would land the frequency
    /// between bins.
    /// </summary>
    public static double SingleBinPower(ReadOnlySpan<float> re, ReadOnlySpan<float> im, double cyclesPerTransform)
    {
        int count = Math.Min(re.Length, im.Length);
        if (count == 0)
        {
            return 0;
        }

        double omega = 2 * Math.PI * cyclesPerTransform / count;
        double c = Math.Cos(omega);
        double s = Math.Sin(omega);
        double wr = 1, wi = 0;
        double sumRe = 0, sumIm = 0;
        for (int i = 0; i < count; i++)
        {
            sumRe += (re[i] * wr) + (im[i] * wi);
            sumIm += (im[i] * wr) - (re[i] * wi);
            double nwr = (wr * c) - (wi * s);
            wi = (wr * s) + (wi * c);
            wr = nwr;
        }

        return (sumRe * sumRe) + (sumIm * sumIm);
    }

    /// <summary>Reverse the low <paramref name="levels"/> bits of <paramref name="value"/>.</summary>
    internal static int ReverseBits(int value, int levels)
    {
        uint x = (uint)value;
        x = ((x >> 1) & 0x5555_5555u) | ((x & 0x5555_5555u) << 1);
        x = ((x >> 2) & 0x3333_3333u) | ((x & 0x3333_3333u) << 2);
        x = ((x >> 4) & 0x0F0F_0F0Fu) | ((x & 0x0F0F_0F0Fu) << 4);
        x = ((x >> 8) & 0x00FF_00FFu) | ((x & 0x00FF_00FFu) << 8);
        x = (x >> 16) | (x << 16);
        return (int)(x >> (32 - levels));
    }
}

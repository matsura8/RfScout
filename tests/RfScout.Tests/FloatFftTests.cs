using System.Numerics;
using RfScout.Core.Dsp;

namespace RfScout.Tests;

/// <summary>
/// The FFT is the one thing the whole spectrum display depends on, so it is checked against a
/// textbook O(n^2) DFT rather than against itself: a wrong twiddle or a missing bit-reversal
/// step is invisible to a self-consistent test.
/// </summary>
public sealed class FloatFftTests
{
    private const double RelativeTolerance = 1e-3;

    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(1024)]
    public void ForwardMatchesNaiveDft(int size)
    {
        var (re, im) = DeterministicSignal(size, seed: 31);
        Complex[] expected = NaiveDft(re, im);

        float[] actualRe = (float[])re.Clone();
        float[] actualIm = (float[])im.Clone();
        new FloatFft(size).Forward(actualRe, actualIm);

        // Forward leaves bin j at index ReverseBits(j): the scatter methods rely on that, so the
        // comparison has to undo it the same way the production code does.
        int levels = BitOperations.Log2((uint)size);
        double worst = 0;
        for (int j = 0; j < size; j++)
        {
            int stored = FloatFft.ReverseBits(j, levels);
            var got = new Complex(actualRe[stored], actualIm[stored]);
            double scale = Math.Max(1e-6, expected[j].Magnitude);
            worst = Math.Max(worst, (got - expected[j]).Magnitude / scale);
        }

        Assert.True(worst < RelativeTolerance, $"n={size}: worst relative error {worst:E3}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(0b0011_0101)]
    [InlineData(0b1100_1010_1010_1111)]
    public void ReverseBitsIsAnInvolution(int value)
    {
        for (int levels = 1; levels <= 16; levels++)
        {
            int masked = value & ((1 << levels) - 1);
            int once = FloatFft.ReverseBits(masked, levels);
            Assert.Equal(masked, FloatFft.ReverseBits(once, levels));
        }
    }

    [Fact]
    public void ForwardOfATonePutsAllPowerInItsOwnBin()
    {
        const int size = 64;
        const int bin = 5;
        var re = new float[size];
        var im = new float[size];
        for (int i = 0; i < size; i++)
        {
            double phase = 2 * Math.PI * bin * i / size;
            re[i] = (float)Math.Cos(phase);
            im[i] = (float)Math.Sin(phase);
        }

        float[] power = new float[size];
        new FloatFft(size).Power(re, im, power);

        int peak = IndexOfMax(power);
        Assert.Equal((size / 2) + bin, peak);

        // A pure tone at an exact bin has all its power in that bin, so the next bin over is
        // empty rather than merely smaller.
        Assert.True(power[peak] > 100 * power[(peak + 1) % size]);
    }

    [Fact]
    public void PowerQuartersKeepsExactlyTheTwoSweptQuarters()
    {
        const int size = 64;
        var (re, im) = DeterministicSignal(size, seed: 7);
        float[] full = new float[size];
        new FloatFft(size).Power((float[])re.Clone(), (float[])im.Clone(), full);

        float[] quarters = new float[size / 2];
        new FloatFft(size).PowerQuarters(re, im, quarters);

        // full[] is fftshift order: index 0 is signed bin -n/2. The kept raw bins 5n/8..7n/8-1
        // are signed bins -3n/8 .. -n/8-1 (the low RF quarter) and n/8..3n/8-1 are signed bins
        // +n/8 .. +3n/8-1 (the high RF quarter), so in full[] they sit at n/8+j and 5n/8+j.
        int quarter = size / 4;
        int eighth = size / 8;
        for (int j = 0; j < quarter; j++)
        {
            AssertClose(full[eighth + j], quarters[j]);
            AssertClose(full[(5 * eighth) + j], quarters[j + quarter]);
        }
    }

    [Fact]
    public void PowerShiftedStartsAtTheMostNegativeBin()
    {
        const int size = 64;
        var (re, im) = DeterministicSignal(size, seed: 11);
        float[] full = new float[size];
        new FloatFft(size).Power((float[])re.Clone(), (float[])im.Clone(), full);

        float[] middle = new float[size / 2];
        new FloatFft(size).PowerShifted(re, im, middle);

        for (int j = 0; j < size / 2; j++)
        {
            AssertClose(full[(size / 4) + j], middle[j]);
        }
    }

    [Fact]
    public void AccumulatingMethodsAddInsteadOfOverwriting()
    {
        // The sweep processor clears once and then calls PowerQuarters once per window of a
        // block, so overwriting instead of adding would silently drop every window but the last.
        const int size = 16;
        var (re, im) = DeterministicSignal(size, seed: 5);
        var fft = new FloatFft(size);

        float[] once = new float[size / 2];
        fft.PowerQuarters((float[])re.Clone(), (float[])im.Clone(), once);

        float[] twice = new float[size / 2];
        fft.PowerQuarters((float[])re.Clone(), (float[])im.Clone(), twice);
        fft.PowerQuarters((float[])re.Clone(), (float[])im.Clone(), twice);

        for (int i = 0; i < twice.Length; i++)
        {
            AssertClose(2 * once[i], twice[i]);
        }
    }

    [Fact]
    public void SingleBinPowerMatchesTheDftForANonPowerOfTwoLength()
    {
        const int size = 100;
        const int bin = 7;
        var re = new float[size];
        var im = new float[size];
        for (int i = 0; i < size; i++)
        {
            double phase = 2 * Math.PI * bin * i / size;
            re[i] = (float)Math.Cos(phase);
            im[i] = (float)Math.Sin(phase);
        }

        double exact = NaiveDft(re, im)[bin].Magnitude;
        double viaBin = Math.Sqrt(FloatFft.SingleBinPower(re, im, bin));
        Assert.True(Math.Abs(exact - viaBin) / exact < RelativeTolerance);

        // Half a bin off, the Dirichlet kernel gives 1/sin(pi*0.5/N) where the exact bin gives
        // N, i.e. 2/pi of it. Checking that ratio rather than "much less" catches a wrong sign
        // or a wrong step in the twiddle recurrence, which a loose bound would not.
        double halfBinRatio = Math.Sqrt(FloatFft.SingleBinPower(re, im, bin + 0.5)) / exact;
        Assert.True(
            Math.Abs(halfBinRatio - (2 / Math.PI)) < 0.01,
            $"half-bin offset measured {halfBinRatio:F4} of the exact bin, expected {2 / Math.PI:F4}");

        // A whole bin off lands on a kernel zero, so it must read as nothing.
        double nextBin = Math.Sqrt(FloatFft.SingleBinPower(re, im, bin + 1));
        Assert.True(nextBin < 1e-6 * exact, $"off-bin read {nextBin:E2} against {exact:E2}");
    }

    [Fact]
    public void RejectsNonPowerOfTwoLengths() => Assert.Throws<ArgumentException>(() => new FloatFft(100));

    private static (float[] Re, float[] Im) DeterministicSignal(int size, int seed)
    {
        var random = new Random(seed);
        var re = new float[size];
        var im = new float[size];
        for (int i = 0; i < size; i++)
        {
            re[i] = (float)(random.NextDouble() * 2 - 1);
            im[i] = (float)(random.NextDouble() * 2 - 1);
        }

        return (re, im);
    }

    private static Complex[] NaiveDft(float[] re, float[] im)
    {
        int size = re.Length;
        var output = new Complex[size];
        for (int k = 0; k < size; k++)
        {
            Complex sum = Complex.Zero;
            for (int n = 0; n < size; n++)
            {
                double angle = -2 * Math.PI * k * n / size;
                var twiddle = new Complex(Math.Cos(angle), Math.Sin(angle));
                sum += new Complex(re[n], im[n]) * twiddle;
            }

            output[k] = sum;
        }

        return output;
    }

    /// <summary>Compare with a relative tolerance, which is what power values need.</summary>
    private static void AssertClose(double expected, double actual, double relativeTolerance = 1e-4)
    {
        double scale = Math.Max(1.0, Math.Abs(expected));
        Assert.True(
            Math.Abs(expected - actual) <= (relativeTolerance * scale),
            $"expected {expected:G9}, got {actual:G9}");
    }

    private static int IndexOfMax(float[] values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best])
            {
                best = i;
            }
        }

        return best;
    }
}

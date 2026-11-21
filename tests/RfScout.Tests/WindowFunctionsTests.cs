using RfScout.Core.Dsp;

namespace RfScout.Tests;

/// <summary>
/// Window shape decides whether a peak on the display reads its true level, so the periodic
/// Hann used by the sweep path is pinned against the numbers it is derived from: coherent gain
/// 0.5, power gain 0.375, and a tone that measures 0 dBFS at full scale.
/// </summary>
public sealed class WindowFunctionsTests
{
    [Fact]
    public void HannPeriodicIsRaisedCosineWithZeroAtSampleZero()
    {
        float[] w = WindowFunctions.HannPeriodic(16);
        Assert.Equal(0f, w[0], 9);
        Assert.Equal(1f, w[8], 6);                       // peaks at n/2, not at the last sample
        Assert.True(w[15] > 0f, "periodic Hann must not taper back to zero at the end");

        for (int i = 0; i < 16; i++)
        {
            double expected = 0.5 - (0.5 * Math.Cos((2 * Math.PI * i) / 16));
            Assert.Equal(expected, w[i], 6);
        }
    }

    [Fact]
    public void HannPeriodicMeanIsTheCoherentGain()
    {
        float[] w = WindowFunctions.HannPeriodic(1024);
        Assert.Equal(WindowFunctions.HannCoherentGain, IqMath.Mean(w), 4);
    }

    [Fact]
    public void HannPeriodicPowerGainIsThreeEighths()
    {
        float[] w = WindowFunctions.HannPeriodic(1024);
        Assert.Equal(0.375, WindowFunctions.PowerGain(w), 4);
    }

    [Fact]
    public void SymmetricHannTapersToZeroAtBothEnds()
    {
        float[] w = WindowFunctions.Hann(17);
        Assert.Equal(0f, w[0], 9);
        Assert.Equal(0f, w[16], 9);
        Assert.Equal(1f, w[8], 6);
        for (int i = 0; i < 17; i++)
        {
            Assert.Equal(w[i], w[16 - i], 9);           // symmetric about the middle sample
        }
    }

    [Fact]
    public void PeriodicAndSymmetricHannDifferOnlyAtTheEnd()
    {
        float[] periodic = WindowFunctions.HannPeriodic(64);
        float[] symmetric = WindowFunctions.Hann(64);
        Assert.Equal(periodic[0], symmetric[0], 9);      // both start at zero
        Assert.True(periodic[63] > symmetric[63]);       // periodic keeps a tail, symmetric does not
    }

    [Fact]
    public void FlatTopMeasuresATonesAmplitudeExactly()
    {
        // The flat-top's whole purpose: a tone reads its true amplitude anywhere in the bin,
        // which is why the peak of a non-bin-centred tone must match a bin-centred one.
        const int size = 256;
        foreach (double cycles in new double[] { 20, 20.5 })
        {
            var re = new float[size];
            var im = new float[size];
            for (int i = 0; i < size; i++)
            {
                double phase = 2 * Math.PI * cycles * i / size;
                re[i] = (float)Math.Cos(phase);
                im[i] = (float)Math.Sin(phase);
            }

            float[] w = WindowFunctions.FlatTop(size);

            // The main-lobe peak tap is normalised to 1. For symmetric windows that peak sits at
            // the centre sample, which is index (N-1)/2 and only lands on a sample when N is odd,
            // so compare the largest tap rather than a fixed index.
            Assert.Equal(1.0, w.Max(), 3);

            WindowFunctions.ApplyInPlace(w, re, im);
            float[] power = new float[size];
            new FloatFft(size).Power(re, im, power);

            int peak = 0;
            for (int i = 1; i < size; i++)
            {
                if (power[i] > power[peak])
                {
                    peak = i;
                }
            }

            // Amplitude = sqrt(power) / (N * coherent gain / 2). The /2 is the FFT's own gain on
            // a complex tone, and the coherent gain is the window's mean, which for a flat top is
            // well below Hann's 0.5 - that is the price of the flatness.
            double amplitude = Math.Sqrt(power[peak]) / (size * IqMath.Mean(w));
            Assert.True(
                Math.Abs(amplitude - 1.0) < 0.02,
                $"{cycles} cycles/transform measured amplitude {amplitude:F4}");
        }
    }

    [Fact]
    public void FlatTopPaysForItsFlatnessInCoherentGain()
    {
        // Worth recording as a number: the flat top's main lobe is honest about amplitude but its
        // coherent gain is a fifth of Hann's, so the same tone reads about 12 dB lower before the
        // normalisation is applied. Anything that "fixes" the coefficient set will trip this.
        float[] flat = WindowFunctions.FlatTop(256);
        float[] hann = WindowFunctions.HannPeriodic(256);

        Assert.Equal(0.19, IqMath.Mean(flat), 2);
        Assert.Equal(0.148, WindowFunctions.PowerGain(flat), 2);
        Assert.True(IqMath.Mean(flat) < WindowFunctions.HannCoherentGain / 2);
        Assert.True(WindowFunctions.PowerGain(flat) < WindowFunctions.PowerGain(hann));
    }

    [Fact]
    public void RectangularIsAllOnesAndChangesNothing()
    {
        float[] w = WindowFunctions.Rectangular(8);
        Assert.All(w, v => Assert.Equal(1f, v));
        Assert.Equal(1.0, WindowFunctions.PowerGain(w), 9);

        float[] re = [1f, 2f, 3f, 4f];
        float[] im = [-1f, -2f, -3f, -4f];
        float[] reCopy = [1f, 2f, 3f, 4f];
        float[] imCopy = [-1f, -2f, -3f, -4f];
        WindowFunctions.ApplyInPlace(w, re, im);
        Assert.Equal(reCopy, re);
        Assert.Equal(imCopy, im);
    }

    [Fact]
    public void ApplyInPlaceScalesBothAxesByTheSameTaps()
    {
        float[] w = [0.5f, 1f, 0.5f];
        float[] re = [2f, 2f, 2f];
        float[] im = [4f, 4f, 4f];
        WindowFunctions.ApplyInPlace(w, re, im);
        Assert.Equal([1f, 2f, 1f], re);
        Assert.Equal([2f, 4f, 2f], im);
    }

    [Fact]
    public void ApplyInPlaceStopsAtTheShortestInput()
    {
        float[] w = [0.5f, 0.5f, 0.5f];
        float[] re = [2f, 2f];
        float[] im = [2f, 2f, 2f];
        WindowFunctions.ApplyInPlace(w, re, im);
        Assert.Equal([1f, 1f], re);
        Assert.Equal([1f, 1f, 2f], im);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void WindowsRejectLengthsBelowTwo(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowFunctions.HannPeriodic(length));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowFunctions.Hann(length));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowFunctions.FlatTop(length));
    }

    [Fact]
    public void PowerGainOfAnEmptyWindowIsZero() =>
        Assert.Equal(0.0, WindowFunctions.PowerGain([]), 9);

    [Fact]
    public void EveryWindowIsFiniteAndWithinItsRange()
    {
        foreach (float[] w in new[] { WindowFunctions.HannPeriodic(64), WindowFunctions.Hann(64), WindowFunctions.FlatTop(64), WindowFunctions.Rectangular(64) })
        {
            Assert.All(w, v => Assert.True(!float.IsNaN(v) && !float.IsInfinity(v)));
            double gain = WindowFunctions.PowerGain(w);
            Assert.True(gain is > 0 and <= 1.1, $"power gain {gain}");
        }
    }
}

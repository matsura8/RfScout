using RfScout.Core.Dsp;

namespace RfScout.Tests;

/// <summary>
/// The s8 I/Q helpers are the interface between the radio and everything else, so their scaling
/// is pinned exactly: a factor of two here would shift the whole dBFS calibration and every
/// decoder threshold with it.
/// </summary>
public sealed class IqMathTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void ToComplexScalesS8ToMinusOneThroughOneMinusLsb()
    {
        // -128 is the only s8 value that maps to exactly -1.0; +127 stops one LSB short of +1.
        // Written as bytes on purpose: 0x80 is s8 -128 and 0xFF is s8 -1.
        byte[] iq = [127, 0x80, 0x00, 0x01, 0xFF, 0x40];
        var re = new float[3];
        var im = new float[3];
        IqMath.ToComplex(iq, re, im);

        Assert.Equal(127 / 128.0, re[0], 9);
        Assert.Equal(-1.0, im[0], 9);
        Assert.Equal(0.0, re[1], 9);
        Assert.Equal(IqMath.Scale, im[1], 9);
        Assert.Equal(-IqMath.Scale, re[2], 9);
        Assert.Equal(0.5, im[2], 9);
    }

    [Fact]
    public void ToComplexStopsAtTheShortestBuffer()
    {
        byte[] iq = new byte[20];
        var re = new float[7];
        var im = new float[4];
        IqMath.ToComplex(iq, re, im);
        Assert.All(re[4..], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void InterleavedRoundTripPreservesS8Values()
    {
        byte[] original = new byte[256];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (byte)((i * 37) - 128);
        }

        var re = new float[128];
        var im = new float[128];
        IqMath.ToComplex(original, re, im);

        byte[] back = new byte[256];
        IqMath.ToInterleavedS8(re, im, back);
        Assert.Equal(original, back);
    }

    [Theory]
    [InlineData(1.0f, 127)]           // clamps to the top of the s8 range
    [InlineData(-1.0f, -128)]
    [InlineData(2.0f, 127)]           // out of range still clamps rather than wrapping
    [InlineData(-2.0f, -128)]
    [InlineData(0.0f, 0)]
    public void ToInterleavedS8SaturatesInsteadOfWrapping(float value, sbyte expected)
    {
        byte[] iq = new byte[2];
        IqMath.ToInterleavedS8([value], [value], iq);
        Assert.Equal(expected, (sbyte)iq[0]);
        Assert.Equal(expected, (sbyte)iq[1]);
    }

    [Fact]
    public void ToInterleavedS8TurnsNaNIntoSilence()
    {
        byte[] iq = new byte[2];
        IqMath.ToInterleavedS8([float.NaN], [float.NaN], iq);
        Assert.Equal(0, iq[0]);
    }

    [Fact]
    public void EnvelopeIsTheModulusOfTheVector()
    {
        // 3-4-5 on purpose: exact in binary floating point, so no tolerance argument needed.
        float[] mag = new float[1];
        IqMath.Envelope([3f], [4f], mag);
        Assert.Equal(5f, mag[0], 9);

        float[] neg = new float[1];
        IqMath.Envelope([0f], [-1f], neg);
        Assert.Equal(1f, neg[0], 9);
    }

    [Fact]
    public void EnvelopeFromBytesMatchesTheFloatPath()
    {
        byte[] iq = new byte[64];
        for (int i = 0; i < iq.Length; i++)
        {
            iq[i] = (byte)((i * 53) - 128);
        }

        var re = new float[32];
        var im = new float[32];
        IqMath.ToComplex(iq, re, im);

        float[] fromFloat = new float[32];
        float[] fromBytes = new float[32];
        IqMath.Envelope(re, im, fromFloat);
        IqMath.Envelope(iq, fromBytes);
        Assert.Equal(fromFloat.Length, fromBytes.Length);
        for (int i = 0; i < fromBytes.Length; i++)
        {
            Assert.Equal(fromFloat[i], fromBytes[i], 9);
        }
    }

    [Fact]
    public void PhaseIsTheAngleOfTheVector()
    {
        float[] phase = new float[4];
        IqMath.Phase([1f, 0f, -1f, 0f], [0f, 1f, 0f, -1f], phase);

        // Compared against float32 pi: MathF.Atan2 returns a float, so nine decimals against a
        // double constant is one decimal too many to be meaningful.
        Assert.Equal(0f, phase[0], 9);
        Assert.Equal((float)(Math.PI / 2), phase[1], 6);
        Assert.Equal((float)Math.PI, phase[2], 6);
        Assert.Equal((float)(-Math.PI / 2), phase[3], 6);
    }

    [Fact]
    public void UnwrapPhaseTurnsWrapsIntoARamp()
    {
        // A rising ramp that crosses +pi twice: the raw values jump, the unwrapped ones must not.
        // Generated from the true ramp, then wrapped - picking numbers by hand is how you end up
        // testing a ramp that genuinely does turn back down.
        float[] wrapped = new float[40];
        for (int i = 0; i < wrapped.Length; i++)
        {
            double raw = 2.9 + (0.15 * i);
            wrapped[i] = (float)(((raw + Math.PI) % (2 * Math.PI)) - Math.PI);
        }
        float[] unwrapped = new float[wrapped.Length];
        IqMath.UnwrapPhase(wrapped, unwrapped);

        Assert.Equal(wrapped[0], unwrapped[0], 6);
        for (int i = 1; i < unwrapped.Length; i++)
        {
            Assert.True(unwrapped[i] > unwrapped[i - 1], $"sample {i} went backwards after unwrap");
            Assert.True(unwrapped[i] - unwrapped[i - 1] < MathF.PI, $"sample {i} still jumps");
        }
    }

    [Fact]
    public void InstantaneousFrequencyOfAToneIsTheToneFrequency()
    {
        const int count = 256;
        const double sampleRate = 1_000_000;
        const double toneHz = 10_000;
        var phase = new float[count];
        for (int i = 0; i < count; i++)
        {
            phase[i] = (float)(2 * Math.PI * toneHz * i / sampleRate % (2 * Math.PI));
        }

        float[] unwrapped = new float[count];
        IqMath.UnwrapPhase(phase, unwrapped);
        double[] hz = new double[count];
        IqMath.InstantaneousFrequencyHz(unwrapped, sampleRate, hz);

        // Sample 0 copies sample 1 so the array has no hole at the front.
        Assert.Equal(hz[1], hz[0], 9);
        for (int i = 1; i < count; i++)
        {
            Assert.True(Math.Abs(hz[i] - toneHz) < 1.0, $"sample {i} read {hz[i]:F3} Hz");
        }
    }

    [Fact]
    public void InstantaneousFrequencyOfSilenceIsZero()
    {
        double[] hz = new double[1];
        IqMath.InstantaneousFrequencyHz([0f], 48_000, hz);
        Assert.Equal(0.0, hz[0], 9);
    }

    [Fact]
    public void TonePowerPeaksAtTheTrueToneFrequency()
    {
        const int count = 1000;
        const double sampleRate = 1_000_000;
        const double toneHz = 57_000;      // the RDS subcarrier this is meant to find
        var re = new float[count];
        var im = new float[count];
        for (int i = 0; i < count; i++)
        {
            double ph = 2 * Math.PI * toneHz * i / sampleRate;
            re[i] = (float)Math.Cos(ph);
            im[i] = (float)Math.Sin(ph);
        }

        double onTone = IqMath.TonePower(re, im, toneHz, sampleRate);

        // A unit complex tone and a unit reference correlate to N, so the power is N^2. Checked
        // relatively: the float32 sin/cos inputs carry ~1e-7 each, which is a few units here.
        Assert.True(Math.Abs(onTone - (count * (double)count)) / (count * (double)count) < 1e-5,
            $"on-tone power {onTone:E4}, expected {count * (double)count:E4}");

        // The resolution here is sampleRate/count = 1 kHz, so five bins away the Dirichlet
        // kernel is at a zero and the answer is nothing, not merely small.
        double offTone = IqMath.TonePower(re, im, toneHz + 5_000, sampleRate);
        Assert.True(offTone < 1e-6 * onTone, $"on {onTone:E2} vs off {offTone:E2}");

        // Half a bin away the kernel says 2/pi of the amplitude, i.e. 4/pi^2 of the power.
        double halfBin = IqMath.TonePower(re, im, toneHz + 500, sampleRate) / onTone;
        Assert.True(Math.Abs(halfBin - (4 / (Math.PI * Math.PI))) < 0.01, $"half-bin read {halfBin:F4}");

        Assert.Equal(0.0, IqMath.TonePower([], [], toneHz, sampleRate), 9);
        Assert.Equal(0.0, IqMath.TonePower(re, im, toneHz, 0), 9);
    }

    [Fact]
    public void MeanPowerOfAComplexToneIsOneSixthOfFullScale()
    {
        // A unit-amplitude complex sine has I^2+Q^2 = 1 per sample, but the reference here is a
        // full-scale complex sine in both axes, i.e. 2, so a unit tone reads 0.5.
        const int count = 512;
        var re = new float[count];
        var im = new float[count];
        for (int i = 0; i < count; i++)
        {
            double ph = 2 * Math.PI * 7 * i / count;
            re[i] = (float)Math.Cos(ph);
            im[i] = (float)Math.Sin(ph);
        }

        Assert.Equal(0.5, IqMath.MeanPower(re, im), 3);

        // The byte path sees the same tone quantised to 127 levels, which shaves a little over
        // one percent off the power. Pinning the size of that loss is the point: a factor-of-two
        // slip in Scale would otherwise be invisible next to a loose tolerance.
        byte[] iq = new byte[2 * count];
        IqMath.ToInterleavedS8(re, im, iq);
        double quantised = IqMath.MeanPower(iq);
        Assert.True(Math.Abs(quantised - 0.5) < 0.01, $"s8 mean power {quantised:F5}");
    }

    [Fact]
    public void MeanPowerOfEmptyInputIsZero()
    {
        Assert.Equal(0.0, IqMath.MeanPower([]), 9);
        Assert.Equal(0.0, IqMath.MeanPower([], []), 9);
    }

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(10.0, 10.0)]
    [InlineData(0.1, -10.0)]
    [InlineData(0.5, -3.0102999)]
    [InlineData(0.0, -140.0)]
    [InlineData(-1.0, -140.0)]
    public void PowerToDbIsClampedNotInfinite(double power, double expectedDb) =>
        Assert.Equal(expectedDb, IqMath.PowerToDb(power), 4);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void PowerToDbSurvivesNonFiniteValues(double power) =>
        Assert.Equal(-140.0, IqMath.PowerToDb(power), 4);

    [Fact]
    public void PowerToDbHonoursACustomFloor() =>
        Assert.Equal(-60.0, IqMath.PowerToDb(0, -60), 9);

    [Fact]
    public void MedianAndPercentileSortWithoutLyingAboutTheInput()
    {
        float[] values = [5f, 1f, 9f, 3f, 7f];
        Assert.Equal(5f, IqMath.Median(values));

        float[] again = [5f, 1f, 9f, 3f, 7f];
        Assert.Equal(1f, IqMath.Percentile(again, 0), 6);
        Assert.Equal(9f, IqMath.Percentile(again, 100), 6);
        Assert.Equal(5f, IqMath.Percentile(again, 50), 6);
        Assert.Equal(3f, IqMath.Percentile(again, 25), 6);   // halfway between 1 and 5
        Assert.Equal(0f, IqMath.Median([]));
        Assert.Equal(0f, IqMath.Percentile([], 50));
    }

    [Fact]
    public void MeanAndStandardDeviationMatchHandValues()
    {
        float[] values = [2f, 4f, 4f, 4f, 5f, 5f, 7f, 9f];
        double mean = IqMath.Mean(values);
        Assert.Equal(5.0, mean, 9);
        Assert.Equal(2.0, IqMath.StandardDeviation(values, mean), 9);   // population sigma

        Assert.Equal(0.0, IqMath.Mean([]), 9);
        Assert.Equal(0.0, IqMath.StandardDeviation([1f], 1), 9);
    }

    [Fact]
    public void AsIqDropsATrailingOddByte()
    {
        byte[] bytes = new byte[11];
        Assert.Equal(10, IqMath.AsIq(bytes).Length);
        Assert.Equal(0, IqMath.AsIq([]).Length);
    }

    [Fact]
    public void ReadFloat32IsLittleEndian()
    {
        byte[] bytes = [0x00, 0x00, 0x80, 0x3f];      // 1.0f little-endian
        Assert.Equal(1f, IqMath.ReadFloat32(bytes), 9);
    }

    [Fact]
    public void ScaleIsOneOverOneHundredAndTwentyEight() =>
        Assert.Equal(1.0 / 128.0, IqMath.Scale, 12);
}

using RfScout.Core.Dsp;
using RfScout.Core.Fakes;
using RfScout.Core.Spectrum;

namespace RfScout.Core;

/// <summary>
/// Hardware-free checks behind <c>RfScout.exe --self-test</c>, the smoke run that
/// the test suite and CI run. Everything here must pass with no
/// HackRF attached, no drivers, no files and no network: it checks the maths and the framing,
/// not the radio.
/// </summary>
public static class SelfTest
{
    /// <summary>Run every check. False means the build is broken, whatever the reason.</summary>
    public static bool Run()
    {
        bool ok = true;
        ok &= FftPutsToneInItsOwnBin();
        ok &= SweepBlockFrames();
        ok &= BinWidthPlanningIsSane();
        return ok;
    }

    /// <summary>
    /// A complex tone five cycles per transform must peak at bin +5 of a 64-point transform.
    /// <see cref="FloatFft.Power"/> returns fftshift order, so bin +5 is index 32 + 5.
    /// </summary>
    internal static bool FftPutsToneInItsOwnBin()
    {
        const int n = 64;
        const int bin = 5;
        float[] re = new float[n];
        float[] im = new float[n];
        for (int i = 0; i < n; i++)
        {
            double phase = 2 * Math.PI * bin * i / n;
            re[i] = (float)Math.Cos(phase);
            im[i] = (float)Math.Sin(phase);
        }

        var fft = new FloatFft(n);
        float[] power = new float[n];
        fft.Power(re, im, power);

        int peak = 0;
        for (int i = 1; i < n; i++)
        {
            if (power[i] > power[peak])
            {
                peak = i;
            }
        }

        return peak == (n / 2) + bin;
    }

    /// <summary>One synthetic 16384-byte block with a 100 MHz header must frame as one block.</summary>
    internal static bool SweepBlockFrames()
    {
        const long headerHz = 100_000_000;
        byte[] block = SyntheticSignal.SweepBlockBytes(headerHz, SweepMath.SamplesPerBlock, cyclesPerSample: 0.05);
        if (block.Length != SweepMath.BytesPerBlock)
        {
            return false;
        }

        var parser = new SweepBlockParser();
        var framed = new List<SweepBlock>();
        parser.Feed(block, framed.Add);

        return framed.Count == 1
            && framed[0].HeaderFrequencyHz == headerHz
            && framed[0].SampleCount == SweepMath.SamplesPerBlock
            && parser.HeaderErrors == 0;
    }

    /// <summary>
    /// Bin-width planning must land on a power-of-two FFT: 100 kHz asks for at least 200 bins,
    /// so 256, and the realised width must never be coarser than what was asked for.
    /// </summary>
    internal static bool BinWidthPlanningIsSane()
    {
        int fft = SweepMath.FftLengthForBinWidth(100_000);
        if (fft != 256)
        {
            return false;
        }

        double realised = SweepMath.BinWidthHz(fft);
        return realised <= 100_000 && realised > 50_000;
    }
}

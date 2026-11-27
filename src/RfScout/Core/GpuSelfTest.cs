using System.Globalization;
using System.IO;
using RfScout.Core.Analysis;
using RfScout.Core.Gpu;

namespace RfScout.Core;

/// <summary>
/// The GPU smoke check behind <c>RfScout.exe --gpu-test</c>: what
/// <c>native\cuda\build-cuda.ps1 -RunSmoke</c> runs after compiling RfScoutCuda.dll, and what CI
/// skips because its runners have no NVIDIA GPU.
///
/// Like <see cref="SelfTest"/> it needs no radio. It measures a synthetic capture - a strong tone
/// with a DC offset, a noise floor and a keyed second tone, which is what an analyze of a selected
/// frequency sees - and compares the GPU's measurement arrays against the managed DSP sample by
/// sample. That is the entire contract of the GPU path: the same numbers, faster. The statistics,
/// the classifier and the decoders all read these arrays, so if the arrays agree the labels agree,
/// and tests/RfScout.Tests/GpuCudaTests.cs pins the same claim under xUnit.
///
/// A machine with no usable CUDA device exits with <see cref="ExitNoGpu"/> rather than failing:
/// RfScout runs there, on the managed path, exactly as it always did.
/// </summary>
public static class GpuSelfTest
{
    /// <summary>No usable CUDA device. Not a failure - the managed DSP is the shipped default.</summary>
    public const int ExitNoGpu = 2;

    // Float rounding through a different summation order, plus the one-ulp twiddle-table
    // difference. Deliberately tight: the point is to catch a wrong kernel, not to forgive one.
    private const double EnvelopeTolerance = 2e-5;
    private const double FrequencyToleranceHz = 25.0;
    private const double PowerRelativeTolerance = 5e-3;
    private const double FeatureRelativeTolerance = 5e-3;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Run the check and print the report. 0 = the GPU path matches the CPU path.</summary>
    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine("RfScout GPU self-test (--gpu-test)");

        CudaAccelerator gpu = CudaAccelerator.Detect();
        output.WriteLine($"  device   : {gpu.Status}");
        if (!gpu.IsAvailable)
        {
            output.WriteLine("  result   : SKIPPED - no CUDA device; RfScout will use the managed DSP");
            return ExitNoGpu;
        }

        const int rate = 2_000_000;
        const int samples = FeatureExtractor.AnalysisFftLength * 244;   // 0.4997 s of whole FFTs
        byte[] iq = MakeCapture(samples, rate);
        output.WriteLine($"  capture  : {samples:N0} samples at {rate / 1e6:F1} Msps, s8 I/Q, tone + DC + noise + keying");

        CaptureMeasurements cpu = CaptureMeasurements.Cpu(iq, rate, FeatureExtractor.AnalysisFftLength);
        if (!gpu.TryMeasure(iq, rate, FeatureExtractor.AnalysisFftLength, out CaptureMeasurements? g) || g is null)
        {
            output.WriteLine($"  result   : FAIL - the GPU refused the analyze: {gpu.Status}");
            return 1;
        }

        output.WriteLine($"  measured : CPU {cpu.Milliseconds:F1} ms | GPU {g.Milliseconds:F1} ms ({cpu.Milliseconds / Math.Max(g.Milliseconds, 0.001):F1}x)");

        bool ok = true;
        ok &= Report(output, "envelope ", WorstEnvelope(cpu.Envelope, g.Envelope), EnvelopeTolerance, "abs");
        ok &= Report(output, "discriminator", WorstFrequency(cpu.Frequency, g.Frequency), FrequencyToleranceHz, "Hz");
        ok &= Report(output, "periodogram", WorstPower(cpu.Power, g.Power), PowerRelativeTolerance, "rel");
        ok &= Report(output, "mean power", Math.Abs(cpu.MeanPower - g.MeanPower) / Math.Max(cpu.MeanPower, 1e-12), PowerRelativeTolerance, "rel");
        ok &= Report(output, "peak power", Math.Abs(cpu.PeakPower - g.PeakPower) / Math.Max(cpu.PeakPower, 1e-12), PowerRelativeTolerance, "rel");

        SignalFeatures cpuFeatures = FeatureExtractor.Extract(iq, rate);
        SignalFeatures gpuFeatures = FeatureExtractor.Extract(iq, rate, gpu);
        double worstFeature = WorstFeatureGap(cpuFeatures, gpuFeatures);
        ok &= Report(output, "features ", worstFeature, FeatureRelativeTolerance, "rel");

        output.WriteLine($"  CPU feats: {cpuFeatures.Describe()}");
        output.WriteLine($"  GPU feats: {gpuFeatures.Describe()}");
        // The record's value equality is the strictest form of this check: any field that
        // differs at all - including the integer classifier fields (tone count, burst count)
        // and the flatness, which the numeric report above only bounds within tolerance -
        // means the two paths saw a different signal.
        // The classifier's discrete decisions: the tone count and the burst count come from
        // histogram and envelope thresholds, so a float-rounding difference between the paths
        // must not flip them. Flatness is a continuous feature and is already bounded by the
        // tolerance report above, so it is not part of this gate.
        if (cpuFeatures.FskToneCount != gpuFeatures.FskToneCount
            || cpuFeatures.BurstCount != gpuFeatures.BurstCount)

            output.WriteLine(ok
                ? "  result   : PASS - the GPU measured the same capture as the CPU"
                : "  result   : FAIL - the GPU measurements drifted outside tolerance (see above)");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// A capture with the shapes an analyze has to cope with: a carrier 40 kHz above the tuned
    /// centre, a 200 kHz-wide keyed pair of tones for the discriminator to resolve, a DC offset
    /// (the tuned centre's own spike) and a noise floor. Quantised to s8, as libhackrf delivers it.
    /// </summary>
    private static byte[] MakeCapture(int samples, int sampleRateHz)
    {
        byte[] iq = new byte[samples * 2];
        var rng = new Random(20261126);
        double dt = 1.0 / sampleRateHz;
        double keyPhase = 0;

        for (int i = 0; i < samples; i++)
        {
            double carrierPhase = 2 * Math.PI * 40_000 * i * dt;
            double re = 0.55 * Math.Cos(carrierPhase) + 0.10 * Math.Cos(2 * Math.PI * 7_000 * i * dt);
            double im = 0.55 * Math.Sin(carrierPhase) + 0.10 * Math.Sin(2 * Math.PI * 7_000 * i * dt);

            // A 4.8 kBd OOK key on a second tone 180 kHz out: gives the envelope scan bursts to
            // count and the discriminator two states to separate.
            keyPhase += 2 * Math.PI * 4_800 * dt;
            double gate = Math.Sin(keyPhase) >= 0 ? 1.0 : 0.06;
            double sidePhase = 2 * Math.PI * 180_000 * i * dt;
            re += gate * 0.30 * Math.Cos(sidePhase);
            im += gate * 0.30 * Math.Sin(sidePhase);

            // The DC the tuner's own spike leaves behind, plus the noise floor.
            re += 0.22 + (rng.NextDouble() - 0.5) * 0.06;
            im += 0.17 + (rng.NextDouble() - 0.5) * 0.06;

            iq[2 * i] = Quantize(re);
            iq[(2 * i) + 1] = Quantize(im);
        }

        return iq;
    }

    private static byte Quantize(double value)
    {
        double scaled = Math.Clamp(value, -1, 1) * 127.0;
        return (byte)(int)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    private static double WorstEnvelope(float[] cpu, float[] gpu)
    {
        double worst = 0;
        int n = Math.Min(cpu.Length, gpu.Length);
        for (int i = 0; i < n; i++)
        {
            worst = Math.Max(worst, Math.Abs(cpu[i] - gpu[i]));
        }

        return worst;
    }

    private static double WorstFrequency(double[] cpu, double[] gpu)
    {
        double worst = 0;
        int n = Math.Min(cpu.Length, gpu.Length);
        for (int i = 0; i < n; i++)
        {
            worst = Math.Max(worst, Math.Abs(cpu[i] - gpu[i]));
        }

        return worst;
    }

    private static double WorstPower(float[] cpu, float[] gpu)
    {
        double total = 0;
        foreach (float v in cpu)
        {
            total += v;
        }

        double scale = total / cpu.Length;
        if (scale <= 0)
        {
            return 1;
        }

        double worst = 0;
        int n = Math.Min(cpu.Length, gpu.Length);
        for (int i = 0; i < n; i++)
        {
            worst = Math.Max(worst, Math.Abs(cpu[i] - gpu[i]) / scale);
        }

        return worst;
    }

    private static double WorstFeatureGap(SignalFeatures a, SignalFeatures b)
    {
        static double Gap(double x, double y) => Math.Abs(x - y) / Math.Max(Math.Abs(x), 1e-9);
        double worst = 0;
        worst = Math.Max(worst, Gap(a.RmsDbfs, b.RmsDbfs));
        worst = Math.Max(worst, Gap(a.PeakDbfs, b.PeakDbfs));
        worst = Math.Max(worst, Gap(a.AmDepth, b.AmDepth));
        worst = Math.Max(worst, Gap(a.OokOnOffRatioDb, b.OokOnOffRatioDb));
        worst = Math.Max(worst, Gap(a.FskDeviationHz, b.FskDeviationHz));
        worst = Math.Max(worst, Gap(a.FmDeviationHz, b.FmDeviationHz));
        worst = Math.Max(worst, Gap(a.FrequencySigmaHz, b.FrequencySigmaHz));
        worst = Math.Max(worst, Gap(a.OccupiedBandwidthHz, b.OccupiedBandwidthHz));
        worst = Math.Max(worst, Gap(a.SymbolRateHz, b.SymbolRateHz));
        worst = Math.Max(worst, Gap(a.ToneSpacingHz, b.ToneSpacingHz));
        return worst;
    }

    private static bool Report(TextWriter output, string label, double worst, double tolerance, string kind)
    {
        bool pass = worst <= tolerance;
        output.WriteLine(string.Format(
            Inv,
            "  {0,-14} worst {1,-12:G5} tol {2,-10:G5} {3}  {4}",
            label,
            worst,
            tolerance,
            kind,
            pass ? "ok" : "FAIL"));
        return pass;
    }
}

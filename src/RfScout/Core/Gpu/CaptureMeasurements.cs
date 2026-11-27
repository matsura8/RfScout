using System.Diagnostics;
using RfScout.Core.Analysis;
using RfScout.Core.Dsp;

namespace RfScout.Core.Gpu;

/// <summary>
/// The sample-by-sample measurements one narrowband capture yields, the input to every
/// statistic in <see cref="FeatureExtractor"/> and therefore to the classifier and the decoders.
///
/// Holding them in one type is what lets the analyze path run on a GPU: the accelerator fills the
/// same arrays the managed code would have, the statistics then run unchanged, and a label never
/// depends on which device did the arithmetic. <see cref="Accelerator"/> records which one did,
/// so the detail pane can say so.
/// </summary>
public sealed class CaptureMeasurements
{
    /// <summary>DC-removed real part, scaled so a full-scale s8 reads 1.0.</summary>
    public required float[] Re { get; init; }

    /// <summary>DC-removed imaginary part, same scaling.</summary>
    public required float[] Im { get; init; }

    /// <summary>Envelope |x|, the AM detector and the burst scan's input.</summary>
    public required float[] Envelope { get; init; }

    /// <summary>
    /// Instantaneous frequency in Hz. The phase unwrap and the sample-to-sample difference are
    /// folded into a cross-product discriminator, so this is the same wrapped phase step the
    /// managed atan2 + UnwrapPhase + difference path produces, without the two extra passes.
    /// </summary>
    public required double[] Frequency { get; init; }

    /// <summary>
    /// The Hann-windowed periodograms accumulated bin by bin (fftshift order), as a Welch
    /// average: divide by <see cref="PowerWindows"/> for the mean spectrum.
    /// </summary>
    public required float[] Power { get; init; }

    /// <summary>How many FFT windows went into <see cref="Power"/>; 0 means "too short to measure".</summary>
    public required int PowerWindows { get; init; }

    /// <summary>Mean of I^2 + Q^2 over the DC-removed capture (a full-scale complex sine reads 0.5).</summary>
    public required double MeanPower { get; init; }

    /// <summary>Largest I^2 + Q^2 in the capture.</summary>
    public required double PeakPower { get; init; }

    /// <summary>Null when the CPU measured them; otherwise the device that did.</summary>
    public string? Accelerator { get; init; }

    /// <summary>How long the measurement took, for the status line and the benchmark.</summary>
    public double Milliseconds { get; init; }

    /// <summary>
    /// The measurements for one capture: from <paramref name="accelerator"/> when it offers them,
    /// otherwise from the managed DSP. Both paths must agree; the caller does not have to care
    /// which one ran.
    /// </summary>
    public static CaptureMeasurements Compute(
        ReadOnlyMemory<byte> iq,
        double sampleRateHz,
        ICaptureAccelerator? accelerator)
    {
        int samples = iq.Length / 2;
        int fft = FeatureExtractor.AnalysisFftLength;

        // The GPU kernel's periodogram grid is exact only when the capture is a whole number of
        // FFTs long. Every analyze plan satisfies that (2..20 Msps for 0.5..2 s), but a playback
        // or a test capture need not, so the accelerator is skipped rather than approximated.
        if (accelerator is not null && samples >= fft && (samples % fft) == 0)
        {
            if (accelerator.TryMeasure(iq, sampleRateHz, fft, out CaptureMeasurements? gpu) && gpu is not null)
            {
                return gpu;
            }
        }

        return Cpu(iq.Span, sampleRateHz, fft);
    }

    /// <summary>
    /// The managed reference: s8 to complex, block-mean DC removal, envelope, cross-product
    /// discriminator and the Hann periodogram. Kept branch-light on purpose - the JIT
    /// vectorises these loops (see IqMath) - and unchanged in behaviour by the GPU work.
    /// </summary>
    public static CaptureMeasurements Cpu(ReadOnlySpan<byte> iq, double sampleRateHz, int fftLength)
    {
        Stopwatch clock = Stopwatch.StartNew();

        int samples = iq.Length / 2;
        float[] re = new float[samples];
        float[] im = new float[samples];
        IqMath.ToComplex(iq, re, im);

        float meanRe = (float)IqMath.Mean(re.AsSpan());
        float meanIm = (float)IqMath.Mean(im.AsSpan());
        for (int i = 0; i < samples; i++)
        {
            re[i] -= meanRe;
            im[i] -= meanIm;
        }

        double meanPower = 0;
        double peakPower = 0;
        for (int i = 0; i < samples; i++)
        {
            double p = (re[i] * re[i]) + (im[i] * im[i]);
            meanPower += p;
            if (p > peakPower)
            {
                peakPower = p;
            }
        }

        meanPower /= samples;

        float[] envelope = new float[samples];
        IqMath.Envelope(re, im, envelope);

        // Cross-product discriminator: the phase step between consecutive samples, wrapped by
        // atan2 of the cross and dot products. Identical to atan2 + UnwrapPhase + difference
        // (unwrapping only removes the very jumps atan2 already reported wrapped), and it needs
        // no phase array and no carry, which is why the GPU can run it in parallel.
        double[] freq = new double[samples];
        const double hzPerRad = Math.PI * 2.0;
        double scale = sampleRateHz / hzPerRad;
        for (int i = 1; i < samples; i++)
        {
            double cross = (im[i] * re[i - 1]) - (re[i] * im[i - 1]);
            double dot = (re[i] * re[i - 1]) + (im[i] * im[i - 1]);
            freq[i] = Math.Atan2(cross, dot) * scale;
        }

        if (samples > 0)
        {
            freq[0] = samples > 1 ? freq[1] : 0;
        }

        float[] power = new float[fftLength];
        int windows = 0;
        if (samples >= fftLength)
        {
            float[] window = WindowFunctions.Hann(fftLength);
            float[] wr = new float[fftLength];
            float[] wi = new float[fftLength];
            var fft = new FloatFft(fftLength);
            // FloatFft.PowerShifted accumulates into its output (the sweep path relies on
            // that), so power starts cleared and every Welch window adds into it - the same
            // accumulate-every-window contract the GPU kernel uses. The mean is taken once
            // after the loop by dividing by the window count, matching the device's
            // k_scale_power (1/windows).
            Array.Clear(power);
            for (int start = 0; start + fftLength <= samples; start += fftLength / 2)
            {
                for (int i = 0; i < fftLength; i++)
                {
                    wr[i] = re[start + i] * window[i];
                    wi[i] = im[start + i] * window[i];
                }

                fft.PowerShifted(wr, wi, power);
                windows++;
            }

            for (int i = 0; i < fftLength; i++)
            {
                power[i] /= windows;
            }
        }

        clock.Stop();
        return new CaptureMeasurements
        {
            Re = re,
            Im = im,
            Envelope = envelope,
            Frequency = freq,
            Power = power,
            PowerWindows = windows,
            MeanPower = meanPower,
            PeakPower = peakPower,
            Accelerator = null,
            Milliseconds = clock.Elapsed.TotalMilliseconds,
        };
    }
}

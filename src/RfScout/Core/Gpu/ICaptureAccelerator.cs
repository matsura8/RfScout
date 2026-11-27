namespace RfScout.Core.Gpu;

/// <summary>
/// The per-sample half of a narrowband measurement: the part of an analyze that is the same
/// arithmetic on every one of a million samples, and therefore the part worth putting on a GPU.
///
/// Implementations must produce exactly what <see cref="CaptureMeasurements.Cpu"/> produces, to
/// within float rounding, because the statistics, the modulation classifier and the decoders all
/// read these arrays. A label must never depend on which device measured the signal, so the
/// parity test (GpuCudaTests) compares this against the managed reference sample by sample.
///
/// An accelerator that is absent, unsupported or busy returns false and leaves the caller to the
/// CPU path; it must never throw over arbitrary RF data.
/// </summary>
public interface ICaptureAccelerator
{
    /// <summary>What the accelerator is, for the detail pane and the log (e.g. "CUDA: GeForce GTX 970M").</summary>
    string Name { get; }

    /// <summary>
    /// Measure <paramref name="iq"/> (interleaved s8, as libhackrf delivers it) at
    /// <paramref name="sampleRateHz"/> into a <see cref="CaptureMeasurements"/>.
    /// False means "not available", for any reason; the caller then uses the CPU.
    /// </summary>
    bool TryMeasure(ReadOnlyMemory<byte> iq, double sampleRateHz, int fftLength, out CaptureMeasurements? result);
}

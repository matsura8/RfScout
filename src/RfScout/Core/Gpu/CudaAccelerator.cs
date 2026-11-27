using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RfScout.Core.Gpu;

/// <summary>
/// The CUDA accelerator: RfScout's optional NVIDIA-GPU path for the per-sample half of an
/// analyze on a selected frequency.
///
/// <see cref="Detect"/> loads RfScoutCuda.dll and asks the driver what it finds. Everything about
/// that is allowed to fail - no NVIDIA GPU, no CUDA-capable device, a GPU older than Maxwell, a
/// build without the DLL, a driver mid-update - and every failure becomes one sentence in
/// <see cref="Status"/> plus "use the CPU". The managed DSP is not the fallback of last resort
/// here, it is the reference implementation the GPU output is checked against, so losing the GPU
/// costs speed and nothing else.
///
/// The DLL keeps one scratch arena and one stream, so calls are serialised behind a lock; the
/// analyze path is single-flight anyway (see AnalyzeController), and Locate holds the radio while
/// it runs, so the lock is belt-and-braces rather than the thing that makes it work.
/// </summary>
public sealed unsafe class CudaAccelerator : ICaptureAccelerator, IDisposable
{
    private readonly Lock _gate = new();
    private bool _available;
    private bool _disposed;
    private string _status;

    private CudaAccelerator(bool available, string status, string deviceName, string capability, CudaNative.RsGpuInfo info, int deviceCount)
    {
        _available = available;
        _status = status;
        DeviceName = deviceName;
        ComputeCapability = capability;
        DeviceCount = deviceCount;
        TotalMemoryBytes = info.TotalBytes;
        DriverVersion = info.DrvVersion;
        MultiprocessorCount = info.MultiprocessorCount;
    }

    /// <summary>Human-readable device name, "" when there is no usable GPU.</summary>
    public string DeviceName { get; }

    /// <summary>"5.2" style compute capability, "" when there is no usable GPU.</summary>
    public string ComputeCapability { get; }

    /// <summary>CUDA devices the driver reported, usable or not.</summary>
    public int DeviceCount { get; }

    /// <summary>VRAM in bytes.</summary>
    public long TotalMemoryBytes { get; }

    /// <summary>Driver CUDA version (e.g. 12090 for CUDA 12.9).</summary>
    public int DriverVersion { get; }

    /// <summary>Streaming multiprocessors on the chosen device.</summary>
    public int MultiprocessorCount { get; }

    /// <inheritdoc />
    public string Name => _available ? $"CUDA: {DeviceName}" : "CUDA";

    /// <summary>True when an analyze can be sent to the GPU.</summary>
    public bool IsAvailable => _available && !_disposed;

    /// <summary>One sentence on what was found: the device, or why the CPU will be used.</summary>
    public string Status => _status;

    /// <summary>
    /// Probe for a usable GPU. Never throws: a missing DLL, a missing driver and an unsupported
    /// device all come back as an unavailable accelerator with an explanation.
    /// </summary>
    public static CudaAccelerator Detect()
    {
        CudaNative.RsGpuInfo info = default;
        info.StructSize = sizeof(CudaNative.RsGpuInfo);

        int status;
        try
        {
            status = CudaNative.Probe(ref info);
        }
        catch (DllNotFoundException)
        {
            return new CudaAccelerator(false, "CUDA: RfScoutCuda.dll not found - built without the CUDA path", "", "", info, 0);
        }
        catch (EntryPointNotFoundException ex)
        {
            return new CudaAccelerator(false, $"CUDA: RfScoutCuda.dll is out of date ({ex.Message})", "", "", info, 0);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or DllNotFoundException)
        {
            return new CudaAccelerator(false, $"CUDA: RfScoutCuda.dll could not be loaded ({ex.Message})", "", "", info, 0);
        }

        if (status == CudaNative.Ok)
        {
            string device = info.ReadName();
            string capability = $"{info.Major}.{info.Minor}";
            return new CudaAccelerator(
                true,
                $"CUDA: {device} (sm_{info.Major}{info.Minor}, {info.MultiprocessorCount} SM, "
                + $"{info.TotalBytes / (1024 * 1024)} MB, driver {info.DrvVersion})",
                device,
                capability,
                info,
                info.DeviceCount);
        }

        string why = ReadNativeError();
        string label = status switch
        {
            CudaNative.ErrNoDevice => "no CUDA device",
            CudaNative.ErrUnsupported => "no CUDA-capable GPU",
            CudaNative.ErrBadArg => "RfScoutCuda.dll does not match this build",
            CudaNative.ErrMemory => "the GPU ran out of memory",
            _ => "the CUDA driver reported an error",
        };

        return new CudaAccelerator(false, $"CUDA: {label}{(string.IsNullOrWhiteSpace(why) ? string.Empty : $" - {why}")}", "", "", info, info.DeviceCount);
    }

    /// <summary>The last message the DLL recorded, for the log and the status line.</summary>
    public static string ReadNativeError()
    {
        try
        {
            const int capacity = 512;
            nint buffer = Marshal.StringToHGlobalAnsi(string.Empty);
            try
            {
                CudaNative.GetLastError(buffer, capacity);
                return (Marshal.PtrToStringAnsi(buffer) ?? string.Empty).Trim();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _ = ex;
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public bool TryMeasure(ReadOnlyMemory<byte> iq, double sampleRateHz, int fftLength, out CaptureMeasurements? result)
    {
        result = null;
        if (!IsAvailable)
        {
            return false;
        }

        int samples = iq.Length / 2;
        if (samples < 64 || fftLength < 2 || (samples % fftLength) != 0)
        {
            return false;
        }

        float[] re = new float[samples];
        float[] im = new float[samples];
        float[] env = new float[samples];
        float[] freq = new float[samples];
        float[] power = new float[fftLength];
        CudaNative.RsStats stats = default;
        Stopwatch clock = Stopwatch.StartNew();
        int status;

        lock (_gate)
        {
            if (!_available || _disposed)
            {
                return false;
            }

            try
            {
                using MemoryHandle pinnedIq = iq.Pin();
                using MemoryHandle pinnedRe = re.AsMemory().Pin();
                using MemoryHandle pinnedIm = im.AsMemory().Pin();
                using MemoryHandle pinnedEnv = env.AsMemory().Pin();
                using MemoryHandle pinnedFreq = freq.AsMemory().Pin();
                using MemoryHandle pinnedPower = power.AsMemory().Pin();

                status = CudaNative.ExtractFeatures(
                    (nint)pinnedIq.Pointer,
                    samples,
                    sampleRateHz,
                    fftLength,
                    (nint)pinnedRe.Pointer,
                    (nint)pinnedIm.Pointer,
                    (nint)pinnedEnv.Pointer,
                    (nint)pinnedFreq.Pointer,
                    (nint)pinnedPower.Pointer,
                    ref stats);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException or InvalidOperationException or AccessViolationException)
            {
                // A driver update or a removed GPU mid-session must not take the sweep down.
                _available = false;
                _status = $"CUDA: lost the device ({ex.GetType().Name}) - using the CPU";
                return false;
            }
        }

        clock.Stop();

        if (status != CudaNative.Ok)
        {
            _available = false;
            _status = $"CUDA: analyze failed ({NativeErrorOrCode(status)}) - using the CPU";
            return false;
        }

        double[] frequency = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            frequency[i] = freq[i];
        }

        result = new CaptureMeasurements
        {
            Re = re,
            Im = im,
            Envelope = env,
            Frequency = frequency,
            Power = power,
            PowerWindows = (int)(stats.SampleCount / (fftLength / 2)) - 1,
            MeanPower = stats.MeanPower,
            PeakPower = stats.PeakPower,
            Accelerator = Name,
            Milliseconds = clock.Elapsed.TotalMilliseconds,
        };
        return true;
    }

    private static string NativeErrorOrCode(int status)
    {
        string why = ReadNativeError();
        return string.IsNullOrWhiteSpace(why) ? $"status {status}" : why;
    }

    /// <summary>Free the GPU scratch arena. RfScout calls this on exit; it is safe to call twice.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _available = false;
            if (_status.StartsWith("CUDA:", StringComparison.Ordinal) && !Status.Contains("using the CPU", StringComparison.Ordinal))
            {
                _status = "CUDA: released";
            }
        }

        try
        {
            CudaNative.Destroy();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // Nothing to release.
            _ = ex;
        }
    }
}

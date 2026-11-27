using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RfScout.Core.Gpu;

/// <summary>
/// The flat C surface of RfScoutCuda.dll, built from native\cuda\rfscout_cuda.cu by
/// native\cuda\build-cuda.ps1.
///
/// Like the libhackrf binding, marshalling is source-generated ([LibraryImport], no
/// DllImport), the search path is pinned to safe directories, and everything unsafe stays
/// inside this one class. The struct layouts mirror the C header exactly: 64-bit fields
/// first, then 32-bit, then the fixed character buffer, natural packing on both sides.
///
/// The DLL is optional. Its absence is the normal case on a machine with no NVIDIA GPU, and
/// <see cref="CudaAccelerator"/> turns a failed load into "use the CPU".
/// </summary>
internal static unsafe partial class CudaNative
{
    /// <summary>Solved from the app directory (the csproj copies native\x64\*.dll there).</summary>
    internal const string LibraryName = "RfScoutCuda";

    // Status codes from rfscout_cuda.cu.
    public const int Ok = 0;
    public const int ErrNoDevice = -1;
    public const int ErrUnsupported = -2;
    public const int ErrBadArg = -3;
    public const int ErrCuda = -4;
    public const int ErrMemory = -5;

    /// <summary>Character width of the device-name field in <see cref="RsGpuInfo"/>.</summary>
    public const int NameLength = 128;

    /// <summary>One CUDA device, as the DLL reports it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RsGpuInfo
    {
        public long TotalBytes;
        public long FreeBytes;
        public long MaxThreadsPerBlock;
        public long MaxSharedPerBlock;
        public int StructSize;
        public int DeviceCount;
        public int Device;
        public int Major;
        public int Minor;
        public int MultiprocessorCount;
        public int RtVersion;
        public int DrvVersion;
        public int WarpSize;
        public int L2CacheBytes;
        public int ConcurrentKernels;
        public int Reserved;
        public fixed byte Name[NameLength];

        /// <summary>The device name the DLL filled into the fixed buffer.</summary>
        public readonly string ReadName()
        {
            fixed (byte* first = Name)
            {
                int length = 0;
                while (length < NameLength && first[length] != 0)
                {
                    length++;
                }

                return new string((sbyte*)first, 0, length, System.Text.Encoding.UTF8);
            }
        }
    }

    /// <summary>What one GPU analyze measured.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RsStats
    {
        public long SampleCount;
        public long PowerCount;
        public double MeanRe;
        public double MeanIm;
        public double MeanPower;
        public double PeakPower;
        public double SampleRateHz;
        public double Reserved0;
    }

    [LibraryImport(LibraryName, EntryPoint = "rs_cuda_probe")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int Probe(ref RsGpuInfo info);

    [LibraryImport(LibraryName, EntryPoint = "rs_cuda_last_error")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial void GetLastError(IntPtr buffer, int bufferLength);

    [LibraryImport(LibraryName, EntryPoint = "rs_cuda_last_status")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int LastStatus();

    [LibraryImport(LibraryName, EntryPoint = "rs_cuda_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial void Destroy();

    /// <summary>
    /// One analyze on the GPU. Every host buffer is pinned by the caller for the duration of
    /// the call, which is what lets the DLL DMA straight in and out; the pointers are handed
    /// over as raw addresses because there is nothing to marshal.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "rs_cuda_extract_features")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial int ExtractFeatures(
        nint iq,
        long sampleCount,
        double sampleRateHz,
        int fftSize,
        nint re,
        nint im,
        nint env,
        nint freq,
        nint power,
        ref RsStats stats);
}

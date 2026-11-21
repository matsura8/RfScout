using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RfScout.Services.HackRf;

/// <summary>
/// The RX-only slice of libhackrf 0.9.2 (hackrf v2026.01.3), built in <c>native\build-libhackrf.ps1</c>
/// and collected into <c>native\x64\</c>. The declarations follow <c>native\x64\hackrf.h</c>
/// exactly; <c>[LibraryImport]</c> source-generates the marshalling, so there is no runtime
/// reflection and no <c>DllImport</c>.
///
/// <para>
/// <strong>Receive only, by construction.</strong> RfScout never transmits, so this class names
/// no TX entry point: no <c>hackrf_start_tx</c>, <c>hackrf_set_txvga_gain</c>,
/// <c>hackrf_set_tx_underrun_limit</c>, <c>hackrf_set_hw_sync_mode</c> or anything else that
/// could put energy on the antenna. <c>NativeMethodsTests</c> fails if a TX entry point is ever
/// added here, so the rule is enforced by the build rather than by review.
/// </para>
///
/// <para>
/// libhackrf is built with <c>ADDCALL</c>, which is <c>__cdecl</c> on MSVC, so every import
/// declares the Cdecl calling convention. Strings that the library owns are read as UTF-8
/// pointers and copied immediately; the version string is read into a caller-owned byte buffer
/// because the library writes into it.
/// </para>
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>Resolved from the application directory, where the csproj copies native\x64.</summary>
    public const string LibraryName = "hackrf";

    /// <summary>Bytes in one sweep block: the 10-byte header plus 8187 interleaved s8 samples.</summary>
    public const int BytesPerBlock = 16_384;

    /// <summary>Most start/stop MHz pairs one sweep may request.</summary>
    public const int MaxSweepRanges = 10;

    /// <summary>libhackrf's <c>HACKRF_SUCCESS</c>.</summary>
    public const int Success = 0;

    /// <summary>Version-string buffer size used by hackrf_info.c.</summary>
    public const int VersionStringLength = 255;

    public const int ErrorInvalidParam = -2;
    public const int ErrorNotFound = -5;
    public const int ErrorBusy = -6;
    public const int ErrorUsbApiVersion = -21;
    public const int ErrorLibusb = -1000;
    public const int ErrorNotLastDevice = -1002;

    /// <summary><c>enum sweep_style</c>. Interleaved is what hackrf_sweep uses and what the
    /// quarter-keeping in <see cref="Core.Spectrum.SweepMath"/> assumes, because the discarded
    /// half of each block's FFT is where the tuned centre's own spike lands.</summary>
    public const int SweepStyleLinear = 0;

    public const int SweepStyleInterleaved = 1;

    /// <summary>Board id 2, reported by <c>hackrf_board_id_read</c> for a HackRF One.</summary>
    public const byte BoardIdHackRfOne = 2;

    /// <summary>Initialise libhackrf (loads libusb). Must run before any other call.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_init();

    /// <summary>Shut libhackrf down. Safe to call more than once; nothing else may be called after it.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_exit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_exit();

    /// <summary>libhackrf version, e.g. "0.9.2". Owned by the library, so copy it before using.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_library_version", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial string? hackrf_library_version();

    /// <summary>libhackrf release string, e.g. "git-1cfe7df". Owned by the library.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_library_release", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial string? hackrf_library_release();

    /// <summary>Enumerate connected HackRFs. Free the result with <see cref="hackrf_device_list_free"/>.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_device_list")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial IntPtr hackrf_device_list();

    /// <summary>Open one entry of a device list without touching any other HackRF.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_device_list_open")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_device_list_open(IntPtr list, int index, out IntPtr device);

    /// <summary>Depth of the transfer queue libhackrf keeps in flight (4 in 0.9.x).</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_get_transfer_queue_depth")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_get_transfer_queue_depth();

    /// <summary>
    /// Size in bytes of one RX callback buffer (262144 in 0.9.x, i.e. 16 sweep blocks). The ring
    /// buffer is sized from this so a slow DSP thread costs whole sweeps, not partial blocks.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_get_transfer_buffer_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_get_transfer_buffer_size();

    /// <summary>True when that device shares its USB bus, which is where dropped blocks come from.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_device_list_bus_sharing")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_device_list_bus_sharing(IntPtr list, int index);

    /// <summary>Release a device list.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_device_list_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial void hackrf_device_list_free(IntPtr list);

    /// <summary>Open the first HackRF found.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_open")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_open(out IntPtr device);

    /// <summary>Open a HackRF by serial number; null serial means the first one.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_open_by_serial", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_open_by_serial(string? desiredSerialNumber, out IntPtr device);

    /// <summary>Close a device. Stops any stream first, as libhackrf does.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_close(IntPtr device);

    /// <summary>
    /// RX callback: the library hands over a buffer of interleaved s8 I/Q and wants it copied and
    /// returned at once. Returning non-zero stops the stream, which is how a device-unplug error
    /// surfaces.
    /// </summary>
    public delegate int SampleBlockCallback(IntPtr transfer);

    /// <summary>Start narrowband streaming. The callback is invoked on a libhackrf thread.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_start_rx")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static unsafe partial int hackrf_start_rx(
        IntPtr device,
        delegate* unmanaged[Cdecl]<IntPtr, int> callback,
        IntPtr context);

    /// <summary>Start sweep-mode streaming (needs firmware API 1.02+, after <see cref="hackrf_init_sweep"/>).</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_start_rx_sweep")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static unsafe partial int hackrf_start_rx_sweep(
        IntPtr device,
        delegate* unmanaged[Cdecl]<IntPtr, int> callback,
        IntPtr context);

    /// <summary>Stop streaming. Safe to call when already stopped.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_stop_rx")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_stop_rx(IntPtr device);

    /// <summary>Returns <c>HACKRF_TRUE</c> (1) while a stream is running.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_is_streaming")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_is_streaming(IntPtr device);

    /// <summary>
    /// Program the firmware sweep list. <paramref name="frequencyList"/> is a packed array of
    /// little-endian uint16 MHz pairs (start, stop) per range, so a caller with N ranges passes
    /// 2N entries and <paramref name="numRanges"/> = N.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_init_sweep")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static unsafe partial int hackrf_init_sweep(
        IntPtr device,
        ushort* frequencyList,
        int numRanges,
        uint numBytes,
        uint stepWidthHz,
        uint offsetHz,
        int sweepStyle);

    /// <summary>Set the tuned centre frequency.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_set_freq")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_set_freq(IntPtr device, ulong freqHz);

    /// <summary>Set the sample rate; libhackrf also picks a baseband filter no wider than 0.75 of the rate.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_set_sample_rate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_set_sample_rate(IntPtr device, double freqHz);

    /// <summary>Set the sample rate as Xtal/divider, for exact rates.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_set_sample_rate_manual")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_set_sample_rate_manual(IntPtr device, uint freqHz, uint divider);

    /// <summary>Set the MAX2837 baseband filter width; only certain widths are valid.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_set_baseband_filter_bandwidth")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_set_baseband_filter_bandwidth(IntPtr device, uint bandwidthHz);

    /// <summary>Nearest valid filter width strictly below the request.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_compute_baseband_filter_bw_round_down_lt")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial uint hackrf_compute_baseband_filter_bw_round_down_lt(uint bandwidthHz);

    /// <summary>LNA gain: 0-40 dB in 8 dB steps.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_set_lna_gain")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_set_lna_gain(IntPtr device, uint valueDb);

    /// <summary>Baseband (VGA) gain: 0-62 dB in 2 dB steps.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_set_vga_gain")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_set_vga_gain(IntPtr device, uint valueDb);

    /// <summary>The 14 dB RF amplifier, 0 or 1.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_set_amp_enable")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_set_amp_enable(IntPtr device, byte value);

    /// <summary>Antenna bias-tee power, 0 or 1.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_set_antenna_enable")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int hackrf_set_antenna_enable(IntPtr device, byte value);

    /// <summary>Read the board id (2 is a HackRF One).</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_board_id_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static unsafe partial int hackrf_board_id_read(IntPtr device, byte* value);

    /// <summary>Board id as text, owned by the library.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_board_id_name", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial string? hackrf_board_id_name(int boardId);

    /// <summary>
    /// Read the firmware version into a caller-owned buffer. The library writes at most
    /// <paramref name="length"/> bytes and does not guarantee termination, so the caller must
    /// bound the copy itself.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_version_string_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static unsafe partial int hackrf_version_string_read(IntPtr device, byte* version, byte length);

    /// <summary>Read the firmware USB API version (0x0108 means 1.08).</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_usb_api_version_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static unsafe partial int hackrf_usb_api_version_read(IntPtr device, ushort* version);

    /// <summary>libhackrf's own text for an error code, owned by the library.</summary>
    [LibraryImport(LibraryName, EntryPoint = "hackrf_error_name", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial string? hackrf_error_name(int errorCode);

    /// <summary>
    /// Mirror of <c>struct hackrf_device_list</c> from hackrf.h, whose fields are documented
    /// read-only. There is no accessor for the serial numbers, so the array has to be read from
    /// the struct; keep the field order in step with the header (v2026.01.3, line 1100).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct HackrfDeviceList
    {
        /// <summary><c>char** serial_numbers</c>; each entry may be NULL.</summary>
        public readonly IntPtr SerialNumbers;

        /// <summary><c>enum hackrf_usb_board_id* usb_board_ids</c>, the USB product ids.</summary>
        public readonly IntPtr UsbBoardIds;

        /// <summary><c>int* usb_device_index</c>, internal to libhackrf.</summary>
        public readonly IntPtr UsbDeviceIndex;

        /// <summary>Number of devices; the length of the three arrays above.</summary>
        public readonly int DeviceCount;

        /// <summary><c>libusb_device** usb_devices</c>, internal to libhackrf.</summary>
        public readonly IntPtr UsbDevices;

        /// <summary>Number of USB devices libhackrf saw on the bus.</summary>
        public readonly int UsbDeviceCount;

        /// <summary>
        /// Serial of entry <paramref name="index"/> as libhackrf printed it, or the empty string
        /// when the entry has none. The pointer belongs to the list, so the text is copied here
        /// and stays valid after <see cref="hackrf_device_list_free"/>.
        /// </summary>
        public string SerialNumber(int index)
        {
            if ((uint)index >= (uint)DeviceCount || SerialNumbers == IntPtr.Zero)
            {
                return string.Empty;
            }

            IntPtr entry = Marshal.ReadIntPtr(SerialNumbers, index * IntPtr.Size);
            return entry == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(entry) ?? string.Empty);
        }

        /// <summary>USB product id of entry <paramref name="index"/>, or 0xFFFF when unknown.</summary>
        public int UsbBoardId(int index)
        {
            if ((uint)index >= (uint)DeviceCount || UsbBoardIds == IntPtr.Zero)
            {
                return 0xFFFF;
            }

            // enum is int-sized in the C ABI.
            return Marshal.ReadInt32(UsbBoardIds, index * sizeof(int));
        }
    }

    /// <summary>Read a device list header into its managed mirror.</summary>
    internal static HackrfDeviceList ReadDeviceList(IntPtr list) =>
        list == IntPtr.Zero ? default : Marshal.PtrToStructure<HackrfDeviceList>(list);

    /// <summary>
    /// Turn a libhackrf return code into an exception carrying libhackrf's own wording, so a
    /// failure in the status bar reads like the tools' output ("HACKRF_ERROR_NOT_FOUND") instead
    /// of a bare number.
    /// </summary>
    internal static void ThrowForError(int result, string action)
    {
        if (result == Success)
        {
            return;
        }

        string name = hackrf_error_name(result) ?? "unknown error";
        throw new HackRfException(action, result, name);
    }
}

/// <summary>A libhackrf call failed. Carries the code and libhackrf's own name for it.</summary>
public sealed class HackRfException : Exception
{
    public HackRfException(string action, int errorCode, string errorName)
        : base($"{action} failed: {errorName} ({errorCode})")
    {
        Action = action;
        ErrorCode = errorCode;
        ErrorName = errorName;
    }

    /// <summary>The libhackrf function that failed.</summary>
    public string Action { get; }

    /// <summary>The raw <c>hackrf_error</c> value.</summary>
    public int ErrorCode { get; }

    /// <summary>libhackrf's name for it, e.g. <c>HACKRF_ERROR_NOT_FOUND</c>.</summary>
    public string ErrorName { get; }

    /// <summary>True when the failure means the radio is simply not plugged in.</summary>
    public bool DeviceMissing =>
        ErrorCode is NativeMethods.ErrorNotFound or NativeMethods.ErrorBusy;
}

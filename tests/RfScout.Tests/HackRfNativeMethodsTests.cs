using System.Reflection;
using System.Runtime.InteropServices;
using RfScout.Services.HackRf;

namespace RfScout.Tests;

/// <summary>
/// The native wrapper is the only place RfScout touches the radio, so its shape is checked here
/// without a radio attached: the receive-only rule, the exact set of entry points, and the two
/// struct mirrors that the RX callback reads.
/// </summary>
public sealed class HackRfNativeMethodsTests
{
    private static Type NativeMethodsType { get; } =
        typeof(HackRfException).Assembly.GetType("RfScout.Services.HackRf.NativeMethods")!;

    /// <summary>
    /// The methods the wrapper itself declares. <c>[LibraryImport]</c> source-generates a stub per
    /// import whose name is compiler-generated (<c>&lt;hackrf_init&gt;g____PInvoke|43_0</c>), and
    /// those stubs are not part of the surface being pinned here - only the hand-written
    /// declarations are.
    /// </summary>
    private static List<MethodInfo> DeclaredMethods() =>
        NativeMethodsType
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => !m.Name.StartsWith('<'))
            .ToList();

    /// <summary>
    /// Rule 1 of the brief: RfScout never transmits. Rather than trusting review, this fails if
    /// the wrapper names any TX entry point at all - the exact functions the brief forbids, plus
    /// anything else whose name mentions TX, which is what a future edit would add.
    /// </summary>
    [Fact]
    public void WrapperExposesNoTransmitEntryPoint()
    {
        string[] forbidden =
        [
            "hackrf_start_tx",
            "hackrf_stop_tx",
            "hackrf_set_txvga_gain",
            "hackrf_set_tx_underrun_limit",
            "hackrf_set_hw_sync_mode",
            "hackrf_enable_tx_flush",
            "hackrf_disable_tx_flush",
        ];

        var names = DeclaredMethods().Select(m => m.Name).ToList();

        foreach (string name in forbidden)
        {
            Assert.False(names.Contains(name), $"{name} is a transmit entry point and must not be bound");
        }

        // And nothing at all that looks like a TX entry point, which is how a future edit would
        // add one by accident.
        Assert.DoesNotContain(names, n => n.StartsWith("hackrf_", StringComparison.Ordinal) && n.Contains("tx", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The bound surface, pinned. Adding a function is fine when the milestone needs it, but it
    /// should be a deliberate edit that comes back here, because every name below is a way for
    /// the app to reach the radio.
    /// </summary>
    [Fact]
    public void WrapperBindsExactlyTheExpectedRxSurface()
    {
        string[] expected =
        [
            "hackrf_close",
            "hackrf_board_id_read",
            "hackrf_board_id_name",
            "hackrf_compute_baseband_filter_bw_round_down_lt",
            "hackrf_device_list",
            "hackrf_device_list_bus_sharing",
            "hackrf_device_list_free",
            "hackrf_device_list_open",
            "hackrf_error_name",
            "hackrf_exit",
            "hackrf_get_transfer_buffer_size",
            "hackrf_get_transfer_queue_depth",
            "hackrf_init",
            "hackrf_init_sweep",
            "hackrf_is_streaming",
            "hackrf_library_release",
            "hackrf_library_version",
            "hackrf_open",
            "hackrf_open_by_serial",
            "hackrf_set_amp_enable",
            "hackrf_set_antenna_enable",
            "hackrf_set_baseband_filter_bandwidth",
            "hackrf_set_freq",
            "hackrf_set_lna_gain",
            "hackrf_set_sample_rate",
            "hackrf_set_sample_rate_manual",
            "hackrf_set_vga_gain",
            "hackrf_start_rx",
            "hackrf_start_rx_sweep",
            "hackrf_stop_rx",
            "hackrf_usb_api_version_read",
            "hackrf_version_string_read",
            "ThrowForError",
            "ReadDeviceList",
        ];

        var actual = DeclaredMethods()
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), actual);
    }

    /// <summary>
    /// libhackrf is built with ADDCALL, which is __cdecl on MSVC. A stdcall mismatch on x64 is
    /// harmless, but the declarations must say what the library actually is, and the search-path
    /// attribute is the one that keeps the loader from probing odd directories for hackrf.dll.
    /// </summary>
    [Fact]
    public void EveryImportDeclaresTheSafeSearchPath()
    {
        var names = DeclaredMethods().Where(m => SearchPaths(m) is not null).Select(m => m.Name).ToList();

        // hackrf_init and friends must all be listed; a missing attribute means the loader uses
        // the default search order, which on Windows includes the current directory.
        foreach (string required in new[] { "hackrf_init", "hackrf_open", "hackrf_start_rx", "hackrf_start_rx_sweep", "hackrf_set_freq" })
        {
            Assert.Contains(required, names);
        }

        // Every hackrf_* import, not just the ones named above: one missing attribute is enough
        // for the loader to pick up a stray hackrf.dll from the current directory.
        var all = DeclaredMethods()
            .Where(m => m.Name.StartsWith("hackrf_", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(all);
        Assert.All(all, m => Assert.Equal(DllImportSearchPath.SafeDirectories, SearchPaths(m)));

        // And the calling convention libhackrf was built with, on the two functions that take the
        // RX callback, where a convention mismatch would corrupt the stack.
        foreach (string name in CallbackImportNames)
        {
            MethodInfo import = DeclaredMethods().Single(m => m.Name == name);

            Assert.Contains(
                "System.Runtime.InteropServices.UnmanagedCallConvAttribute",
                DeclaredAttributes(import));
        }
    }

    private static readonly string[] CallbackImportNames = ["hackrf_start_rx", "hackrf_start_rx_sweep"];

    /// <summary>The search-path flags a method declares, or null when it declares none.</summary>
    private static DllImportSearchPath? SearchPaths(MethodInfo method)
    {
        foreach (CustomAttributeData data in method.GetCustomAttributesData())
        {
            if (data.AttributeType.FullName == "System.Runtime.InteropServices.DefaultDllImportSearchPathsAttribute")
            {
                return (DllImportSearchPath)Convert.ToInt32(data.ConstructorArguments[0].Value, null);
            }
        }

        return null;
    }

    private static List<string> DeclaredAttributes(MethodInfo method) =>
        method.GetCustomAttributesData().Select(a => a.AttributeType.FullName!).ToList();

    /// <summary>
    /// The RX callback reads the transfer struct through a pointer, so a field added or reordered
    /// in hackrf.h without updating the mirror reads valid_length out of what used to be
    /// buffer_length. The offsets are the contract; assert them.
    /// </summary>
    [Fact]
    public void TransferStructMirrorMatchesHackrfHeader()
    {
        Type header = typeof(RfScout.Services.HackRf.HackRfDevice)
            .GetNestedType("HackrfTransferHeader", BindingFlags.NonPublic | BindingFlags.Public)!;

        // hackrf_transfer on x64: device* 0, buffer* 8, buffer_length 16, valid_length 20,
        // rx_ctx 24, tx_ctx 32 - 40 bytes. The two ints pack into one 8-byte slot, which is why
        // rx_ctx sits at 24 and not 32.
        Assert.Equal(0, OffsetOf(header, "Device"));
        Assert.Equal(IntPtr.Size, OffsetOf(header, "Buffer"));
        Assert.Equal(2 * IntPtr.Size, OffsetOf(header, "BufferLength"));
        Assert.Equal((2 * IntPtr.Size) + 4, OffsetOf(header, "ValidLength"));
        Assert.Equal(3 * IntPtr.Size, OffsetOf(header, "RxContext"));
        Assert.Equal(4 * IntPtr.Size, OffsetOf(header, "TxContext"));
        Assert.Equal(5 * IntPtr.Size, Marshal.SizeOf(header));
    }

    /// <summary>
    /// Same for the device-list mirror, which is read to get the serial number without opening the
    /// radio. Field order comes from hackrf.h v2026.01.3.
    /// </summary>
    [Fact]
    public void DeviceListMirrorMatchesHackrfHeader()
    {
        Type list = NativeMethodsType.GetNestedType("HackrfDeviceList", BindingFlags.NonPublic | BindingFlags.Public)!;

        // struct hackrf_device_list on x64: serial_numbers 0, usb_board_ids 8, usb_device_index 16,
        // devicecount 24, usb_devices 32 (the compiler pads to keep the pointer aligned),
        // usb_devicecount 40 - 48 bytes. Reading DeviceCount from the wrong offset would return a
        // pointer's low word and the app would then index a serial array that is not there.
        Assert.Equal(0, OffsetOf(list, "SerialNumbers"));
        Assert.Equal(IntPtr.Size, OffsetOf(list, "UsbBoardIds"));
        Assert.Equal(2 * IntPtr.Size, OffsetOf(list, "UsbDeviceIndex"));
        Assert.Equal(3 * IntPtr.Size, OffsetOf(list, "DeviceCount"));
        Assert.Equal(4 * IntPtr.Size, OffsetOf(list, "UsbDevices"));
        Assert.Equal(5 * IntPtr.Size, OffsetOf(list, "UsbDeviceCount"));
        Assert.Equal(6 * IntPtr.Size, Marshal.SizeOf(list));
    }

    [Fact]
    public void DeviceListMirrorToleratesMissingArraysAndShortCounts()
    {
        Type listType = NativeMethodsType.GetNestedType("HackrfDeviceList", BindingFlags.NonPublic | BindingFlags.Public)!;
        object empty = Activator.CreateInstance(listType)!;

        string serial = (string)listType.GetMethod("SerialNumber")!.Invoke(empty, [0])!;
        Assert.Equal(string.Empty, serial);
        Assert.Equal(0xFFFF, (int)listType.GetMethod("UsbBoardId")!.Invoke(empty, [0])!);

        // Out of range on a default-constructed (DeviceCount 0) list must not throw.
        Assert.Equal(string.Empty, (string)listType.GetMethod("SerialNumber")!.Invoke(empty, [7])!);
    }

    [Fact]
    public void ReadDeviceListTreatsAZeroPointerAsEmpty()
    {
        var read = NativeMethodsType.GetMethod("ReadDeviceList", BindingFlags.NonPublic | BindingFlags.Static)!;
        object result = read.Invoke(null, [IntPtr.Zero])!;
        int count = (int)result.GetType().GetField("DeviceCount")!.GetValue(result)!;
        Assert.Equal(0, count);
    }

    [Fact]
    public void ConstantsMatchTheHeader()
    {
        // These constants are the numbers the firmware ABI is defined by, so they are read by
        // name through reflection: renaming one is a deliberate edit, changing one is a bug this
        // catches.
        // The constants are public on the wrapper, so ask for both visibilities: a lookup that
        // only asks for NonPublic returns null and the test fails for the wrong reason.
        static object Constant(string name) =>
            NativeMethodsType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .GetRawConstantValue()!;

        Assert.Equal(16_384, Convert.ToInt32(Constant("BytesPerBlock"), null));
        Assert.Equal(10, Convert.ToInt32(Constant("MaxSweepRanges"), null));
        Assert.Equal(0, Convert.ToInt32(Constant("Success"), null));
        Assert.Equal(0, Convert.ToInt32(Constant("SweepStyleLinear"), null));
        Assert.Equal(1, Convert.ToInt32(Constant("SweepStyleInterleaved"), null));
        Assert.Equal(2, Convert.ToInt32(Constant("BoardIdHackRfOne"), null));
        Assert.Equal(-5, Convert.ToInt32(Constant("ErrorNotFound"), null));
        Assert.Equal(-6, Convert.ToInt32(Constant("ErrorBusy"), null));
        Assert.Equal("hackrf", Constant("LibraryName"));
    }

    [Theory]
    [InlineData(-5, true)]   // HACKRF_ERROR_NOT_FOUND: the radio is not plugged in
    [InlineData(-6, true)]   // HACKRF_ERROR_BUSY: something else has it open
    [InlineData(-2, false)]  // invalid parameter: a bug in our calling code
    [InlineData(-1000, false)]
    [InlineData(-21, false)] // firmware too old for the call
    public void MissingDeviceIsRecognisedSeparatelyFromACodingError(int code, bool expectMissing)
    {
        var ex = new HackRfException("hackrf_open", code, "HACKRF_ERROR_TEST");
        Assert.Equal(expectMissing, ex.DeviceMissing);

        // The status bar shows ex.Message verbatim, so it has to name the call, libhackrf's own
        // error name and the raw code. A bare number is what makes a HackRF failure undiagnosable.
        Assert.Equal($"hackrf_open failed: HACKRF_ERROR_TEST ({code})", ex.Message);
    }

    [Fact]
    public void ErrorCarriesTheActionThatFailed()
    {
        var ex = new HackRfException("hackrf_init_sweep", -2, "HACKRF_ERROR_INVALID_PARAM");
        Assert.Equal("hackrf_init_sweep", ex.Action);
        Assert.Equal(-2, ex.ErrorCode);
        Assert.Equal("HACKRF_ERROR_INVALID_PARAM", ex.ErrorName);
    }

    /// <summary>Field offset of a nested struct mirror, as the RX callback sees it.</summary>
    private static long OffsetOf(Type structType, string field) => Marshal.OffsetOf(structType, field).ToInt64();

}

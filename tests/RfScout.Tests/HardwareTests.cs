using RfScout.Core;
using RfScout.Services;

namespace RfScout.Tests;

/// <summary>
/// Tests that need real USB devices. The Hardware trait keeps them out of the normal test run
/// and CI. Run them with HARDWARE_TESTS=1 and HARDWARE_USB_DEVICES=hackrf=1d50:6089, then
/// dotnet test --filter-trait Category=Hardware.
/// </summary>
[Trait("Category", "Hardware")]
public class HardwareTests
{
    [Fact]
    public void DeclaredDevicesAreEnumerated()
    {
        if (Environment.GetEnvironmentVariable("HARDWARE_TESTS") != "1")
        {
            Assert.Skip("set HARDWARE_TESTS=1 to run hardware tests");
        }

        var wanted = Usb.ParseDeviceList(Environment.GetEnvironmentVariable("HARDWARE_USB_DEVICES"));
        Assert.NotEmpty(wanted);
        var present = UsbEnumerator.ListDevices();
        foreach (var (name, id) in wanted)
        {
            Assert.True(present.Contains(id), $"{name} ({id}) not found");
        }
    }

    // Add device-specific checks here, e.g. open the device and read its firmware version.
}

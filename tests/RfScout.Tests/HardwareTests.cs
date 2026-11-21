using RfScout.Core;
using RfScout.Services;

namespace RfScout.Tests;

/// <summary>
/// Tests that need real USB devices. The Hardware trait keeps them out of `dev test` and CI;
/// `dev test -Hardware` checks the devices declared in devflow.json and sets
/// DEVFLOW_HARDWARE / DEVFLOW_USB_DEVICES before running them.
/// </summary>
[Trait("Category", "Hardware")]
public class HardwareTests
{
    [Fact]
    public void DeclaredDevicesAreEnumerated()
    {
        if (Environment.GetEnvironmentVariable("DEVFLOW_HARDWARE") != "1")
        {
            Assert.Skip("run with: dev test -Hardware");
        }

        var wanted = Usb.ParseDeviceList(Environment.GetEnvironmentVariable("DEVFLOW_USB_DEVICES"));
        Assert.NotEmpty(wanted);
        var present = UsbEnumerator.ListDevices();
        foreach (var (name, id) in wanted)
        {
            Assert.True(present.Contains(id), $"{name} ({id}) not found");
        }
    }

    // Add device-specific checks here, e.g. open the device and read its firmware version.
}

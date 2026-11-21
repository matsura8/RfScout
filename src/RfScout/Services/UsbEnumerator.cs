using System.Management;
using RfScout.Core;

namespace RfScout.Services;

/// <summary>
/// Lists present USB devices through WMI (Win32_PnPEntity). Covered by the Hardware tests.
/// To talk to a device, use WinUSB via P/Invoke or a library such as LibUsbDotNet / HidSharp
/// (see C:\workspace\devflow\docs\USB.md).
/// </summary>
public static class UsbEnumerator
{
    public static IReadOnlyList<UsbDeviceId> ListDevices()
    {
        using var searcher = new ManagementObjectSearcher(@"SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\VID%'");
        using var results = searcher.Get();
        return results.Cast<ManagementBaseObject>()
            .Select(o => Usb.ParseHardwareId(o["DeviceID"] as string))
            .OfType<UsbDeviceId>()
            .Distinct()
            .ToList();
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace RfScout.Core;

/// <summary>A USB vendor/product ID pair.</summary>
public readonly record struct UsbDeviceId(ushort Vid, ushort Pid)
{
    public override string ToString() => $"{Vid:x4}:{Pid:x4}";
}

/// <summary>USB ID parsing. Pure and unit tested; enumeration lives in <c>Services/UsbEnumerator</c>.</summary>
public static partial class Usb
{
    /// <summary>Parses "1d50:6089".</summary>
    public static UsbDeviceId? ParseVidPid(string? text)
    {
        if (text is not { Length: 9 } || text[4] != ':') return null;
        return ushort.TryParse(text.AsSpan(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid) &&
               ushort.TryParse(text.AsSpan(5, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid)
            ? new UsbDeviceId(vid, pid)
            : null;
    }

    /// <summary>Finds VID_xxxx&amp;PID_yyyy (any case) in a PnP device ID or device path.</summary>
    public static UsbDeviceId? ParseHardwareId(string? deviceId)
    {
        if (deviceId is null) return null;
        var m = HardwareIdRegex().Match(deviceId);
        return m.Success
            ? new UsbDeviceId(ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                              ushort.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>Parses the HARDWARE_USB_DEVICES format: "name=vid:pid;name2=vid:pid". Bad items are skipped.</summary>
    public static IReadOnlyList<(string Name, UsbDeviceId Id)> ParseDeviceList(string? text) =>
        (text ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .Where(parts => parts.Length == 2 && ParseVidPid(parts[1]) is not null)
            .Select(parts => (parts[0], ParseVidPid(parts[1])!.Value))
            .ToList();

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HardwareIdRegex();
}

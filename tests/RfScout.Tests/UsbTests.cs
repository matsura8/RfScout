using RfScout.Core;

namespace RfScout.Tests;

public class UsbTests
{
    [Theory]
    [InlineData("1d50:6089", 0x1d50, 0x6089)]
    [InlineData("1D50:6089", 0x1d50, 0x6089)]
    public void ParsesVidPid(string text, int vid, int pid) =>
        Assert.Equal(new UsbDeviceId((ushort)vid, (ushort)pid), Usb.ParseVidPid(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1d50-6089")]
    [InlineData("zzzz:6089")]
    public void RejectsBadVidPid(string? text) => Assert.Null(Usb.ParseVidPid(text));

    [Fact]
    public void ParsesPnpDeviceId()
    {
        Assert.Equal(new UsbDeviceId(0x1d50, 0x6089), Usb.ParseHardwareId(@"USB\VID_1D50&PID_6089\0000000000000000088869DC382C711B"));
        Assert.Equal(new UsbDeviceId(0x046d, 0xc52b), Usb.ParseHardwareId(@"\\?\usb#vid_046d&pid_c52b&mi_00#7&35dd60c9"));
        Assert.Null(Usb.ParseHardwareId(@"PCI\VEN_8086&DEV_1234"));
    }

    [Fact]
    public void ParsesDeviceList()
    {
        var list = Usb.ParseDeviceList("hackrf=1d50:6089;bad;dongle=0403:6001");
        Assert.Equal(2, list.Count);
        Assert.Equal("hackrf", list[0].Name);
        Assert.Equal(0x6001, list[1].Id.Pid);
    }
}

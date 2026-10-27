using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>
/// ADB 输出解析测试（不依赖真实 adb.exe）。
/// </summary>
public class AdbServiceTests
{
    [Fact]
    public void ParseDevices_ReturnsDevicesAndModel()
    {
        var output = string.Join("\n",
            "List of devices attached",
            "emulator-5554          device product:sdk_gphone64_x86_64 model:Pixel_7 device:emulator5554 transport_id:1",
            "0123456789abcdef       offline",
            "");

        var devices = AdbService.ParseDevices(output);

        Assert.Equal(2, devices.Count);
        Assert.Equal("emulator-5554", devices[0].Serial);
        Assert.True(devices[0].IsAvailable);
        Assert.Equal("Pixel_7", devices[0].Model);
        Assert.Equal("0123456789abcdef", devices[1].Serial);
        Assert.False(devices[1].IsAvailable);
    }

    [Fact]
    public void ParseDevices_EmptyOutput_ReturnsEmptyList()
    {
        var devices = AdbService.ParseDevices("List of devices attached\r\n\r\n");
        Assert.Empty(devices);
    }

    /// <summary>设备显示文本应包含品牌前缀与型号；无品牌时不显示前缀。</summary>
    [Fact]
    public void AdbDevice_Display_IncludesBrandPrefix()
    {
        var withBrand = new AdbDevice("emulator-5558", "device", "V2324HA")
        {
            Brand = "MuMu",
        };
        Assert.Equal("MuMu · emulator-5558 (V2324HA)", withBrand.Display);

        var withoutBrand = new AdbDevice("emulator-5558", "device", "");
        Assert.Equal("emulator-5558 (device)", withoutBrand.Display);
    }

    /// <summary>应从 getevent 能力输出中选择支持 Protocol B 的触摸屏并读取坐标范围。</summary>
    [Fact]
    public void ParseMultiTouchDevice_ReturnsProtocolBTouchscreen()
    {
        const string output = """
            add device 1: /dev/input/event5
              name: "Joystick"
              events:
                ABS (0003): ABS_X : value 0, min -1, max 1
            add device 2: /dev/input/event4
              name: "Xiaomi Input"
              events:
                ABS (0003): ABS_MT_SLOT           : value 0, min 0, max 31
                            ABS_MT_POSITION_X     : value 0, min 0, max 1080
                            ABS_MT_POSITION_Y     : value 0, min 0, max 1920
                            ABS_MT_TRACKING_ID    : value 0, min 0, max 65535
            """;

        var device = AdbService.ParseMultiTouchDevice(output);

        Assert.NotNull(device);
        Assert.Equal("/dev/input/event4", device!.Path);
        Assert.Equal(0, device.MinX);
        Assert.Equal(1080, device.MaxX);
        Assert.Equal(0, device.MinY);
        Assert.Equal(1920, device.MaxY);
    }

}

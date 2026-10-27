using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>
/// 模拟器类型自动识别测试：从 ADB / 模拟器路径识别 MuMu、雷电、BlueStacks。
/// </summary>
public class EmulatorTypesTests
{
    [Theory]
    [InlineData(@"H:\MuMu\nx_main\adb.exe", "mumu")]
    [InlineData(@"C:\Program Files\Netease\MuMu Player 12\shell\adb.exe", "mumu")]
    [InlineData(@"D:\LDPlayer\LDPlayer9\adb.exe", "ld")]
    [InlineData(@"C:\LDPlayer\LDPlayer9\dnplayer.exe", "ld")]
    [InlineData(@"C:\Program Files\BlueStacks_nxt\HD-Adb.exe", "bs")]
    [InlineData(@"D:\Portable\HD-Adb.exe", "bs")]
    [InlineData(@"C:\Program Files\BlueStacks_nxt\HD-Player.exe", "bs")]
    public void DetectFromAdbPath_KnownEmulator_ReturnsType(string path, string expected)
    {
        Assert.Equal(expected, EmulatorTypes.DetectFromAdbPath(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("C:\\tools\\adb.exe")]
    [InlineData("adb")]
    public void DetectFromAdbPath_Unknown_ReturnsNull(string? path)
    {
        Assert.Null(EmulatorTypes.DetectFromAdbPath(path));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe", "mumu")]
    [InlineData(@"D:\LDPlayer\LDPlayer9\ldconsole.exe", "ld")]
    [InlineData(@"C:\Program Files\BlueStacks_nxt\HD-Player.exe", "bs")]
    public void DetectFromEmulatorPath_KnownEmulator_ReturnsType(string path, string expected)
    {
        Assert.Equal(expected, EmulatorTypes.DetectFromEmulatorPath(path));
    }

    [Fact]
    public void IsMuMu_CaseInsensitive()
    {
        Assert.True(EmulatorTypes.IsMuMu("mumu"));
        Assert.True(EmulatorTypes.IsMuMu("MuMu"));
        Assert.True(EmulatorTypes.IsMuMu("MUMU"));
        Assert.False(EmulatorTypes.IsMuMu("ld"));
        Assert.False(EmulatorTypes.IsMuMu(null));
    }

    /// <summary>默认启动参数来自档案表：雷电自动补全，MuMu/自定义等按档案返回。</summary>
    [Theory]
    [InlineData("mumu", "control launch --vmindex 0")]
    [InlineData("ld", "launch --index 0")]
    [InlineData("bs", "")]
    [InlineData("custom", "")]
    [InlineData(null, "")]
    [InlineData("unknown", "")]
    public void GetDefaultArgs_ReturnsProfileDefault(string? type, string expected)
    {
        Assert.Equal(expected, EmulatorTypes.GetDefaultArgs(type));
    }

    /// <summary>品牌短名：用于设备列表前缀；自定义 / 未知类型不显示前缀。</summary>
    [Theory]
    [InlineData("mumu", "MuMu")]
    [InlineData("ld", "雷电")]
    [InlineData("bs", "BlueStacks")]
    [InlineData("custom", null)]
    [InlineData(null, null)]
    public void GetBrandLabel_ReturnsShortBrand(string? type, string? expected)
    {
        Assert.Equal(expected, EmulatorTypes.GetBrandLabel(type));
    }

    /// <summary>已知厂商命名可通过，任意可执行文件仍被拒绝。</summary>
    [Theory]
    [InlineData("adb", true)]
    [InlineData("adb.exe", true)]
    [InlineData(@"C:\Nox\bin\nox_adb.exe", true)]
    [InlineData(@"C:\Emulator\adb_server.exe", true)]
    [InlineData("NOX_ADB.EXE", true)]
    [InlineData("nox_adb.exe.bak", false)]
    [InlineData(@"C:\Program Files\BlueStacks_nxt\HD-Adb.exe", true)]
    [InlineData(@"C:\Emulator\random.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupportedAdbPath_AllowsOnlyKnownNames(string? path, bool expected)
    {
        Assert.Equal(expected, EmulatorTypes.IsSupportedAdbPath(path));
    }

    [Fact]
    public void BlueStacksProfile_DeclaresPairedExecutables()
    {
        var profile = EmulatorTypes.GetProfile("bs");

        Assert.Contains("HD-Adb.exe", profile.AdbExecutableNames);
        Assert.Contains("HD-Player.exe", profile.LauncherExecutableNames);
    }
}

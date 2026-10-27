using System.IO;
using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>
/// MuMu 安装位置探测测试。
/// </summary>
public class EmulatorLocatorTests
{
    /// <summary>通用 ADB 配合显式播放器路径时，优先使用该安装内的管理器。</summary>
    [Theory]
    [InlineData("shell")]
    [InlineData("nx_main")]
    public void ResolveMuMuManager_FindsManagerNearConfiguredPlayer(string layout)
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, layout);
        Directory.CreateDirectory(directory);
        var manager = Path.Combine(directory, "MuMuManager.exe");
        File.WriteAllBytes(manager, []);
        Assert.Equal(manager, EmulatorLocator.ResolveMuMuManager(
            Path.Combine(temp.Path, "external", "adb.exe"), Path.Combine(directory, "MuMuPlayer.exe")));
    }

    /// <summary>ADB 位于安装根或其它子目录时，两代目录布局都可配对，不依赖盘符。</summary>
    [Theory]
    [InlineData("shell")]
    [InlineData("nx_main")]
    public void FindLauncher_FindsManagerInBothLayouts(string layout)
    {
        using var temp = new TempDirectory();
        var directory = Path.Combine(temp.Path, layout);
        Directory.CreateDirectory(directory);
        var manager = Path.Combine(directory, "MuMuManager.exe");
        File.WriteAllBytes(manager, []);
        Assert.Equal(manager, AdbDiscovery.FindLauncher(Path.Combine(temp.Path, "adb.exe"), EmulatorTypes.MuMu));
    }

    /// <summary>自定义安装路径优先采用显式管理器，未配置时使用 ADB 同目录的管理器。</summary>
    [Fact]
    public void ResolveMuMuManager_PrefersConfiguredThenAdbSibling()
    {
        using var temp = new TempDirectory();
        var configured = Path.Combine(temp.Path, "configured", "MuMuManager.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(configured)!);
        File.WriteAllBytes(configured, Array.Empty<byte>());
        var sibling = Path.Combine(temp.Path, "MuMuManager.exe");
        File.WriteAllBytes(sibling, Array.Empty<byte>());
        var adb = Path.Combine(temp.Path, "adb.exe");

        Assert.Equal(configured, EmulatorLocator.ResolveMuMuManager(adb, configured));
        Assert.Equal(sibling, EmulatorLocator.ResolveMuMuManager(adb, string.Empty));
    }

    [Fact]
    public void FindMuMuManagerUnderRoot_FindsManagerInNxMain()
    {
        using var temp = new TempDirectory();
        var nxMain = Path.Combine(temp.Path, "nx_main");
        Directory.CreateDirectory(nxMain);
        var managerPath = Path.Combine(nxMain, "MuMuManager.exe");
        File.WriteAllBytes(managerPath, Array.Empty<byte>());

        var found = EmulatorLocator.FindMuMuManagerUnderRoot(temp.Path);

        Assert.Equal(managerPath, found);
    }

    [Fact]
    public void FindMuMuManagerUnderRoot_Missing_ReturnsNull()
    {
        using var temp = new TempDirectory();
        Assert.Null(EmulatorLocator.FindMuMuManagerUnderRoot(temp.Path));
    }

    [Fact]
    public void FindBlueStacksUnderRoot_FindsPairedExecutables()
    {
        using var temp = new TempDirectory();
        var install = Path.Combine(temp.Path, "BlueStacks_nxt");
        Directory.CreateDirectory(install);
        var adbPath = Path.Combine(install, "HD-Adb.exe");
        var launcherPath = Path.Combine(install, "HD-Player.exe");
        File.WriteAllBytes(adbPath, Array.Empty<byte>());
        File.WriteAllBytes(launcherPath, Array.Empty<byte>());

        var found = EmulatorLocator.FindBlueStacksUnderRoot(temp.Path);

        Assert.NotNull(found);
        Assert.Equal(install, found!.RootPath);
        Assert.Equal(adbPath, found.AdbPath);
        Assert.Equal(launcherPath, found.LauncherPath);
    }

    [Fact]
    public void FindBlueStacksUnderRoot_IncompleteInstallation_ReturnsNull()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "HD-Adb.exe"), Array.Empty<byte>());

        Assert.Null(EmulatorLocator.FindBlueStacksUnderRoot(temp.Path));
    }

    /// <summary>测试用临时目录，自动清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }
    }
}

using System.IO;
using GGEA.App.Config;
using GGEA.App.Device;

namespace GGEA.Tests;

/// <summary>使用隔离目录验证检索、排序、首次选择与持久化，不启动真实 ADB。</summary>
public sealed class AdbDiscoveryTests : IDisposable
{
    /// <summary>目录扫描与手动选择共用厂商文件名集合，避免允许选择却永远搜不到。</summary>
    [Theory]
    [InlineData("nox_adb.exe")]
    [InlineData("adb_server.exe")]
    public void VendorClient_IsDiscovered(string name)
    {
        var path = CreateAdb("Vendor", name);
        Assert.Equal(path, Assert.Single(AdbDiscovery.FindCandidates([_root], new Dictionary<string, DateTime>())).Path);
        Assert.True(EmulatorTypes.IsSupportedAdbPath(path));
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ggea-adb-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建模拟安装，不依赖开发电脑上已有的模拟器。</summary>
    private string CreateAdb(string folder, string name = "adb.exe")
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, name));
        File.WriteAllBytes(path, []);
        return path;
    }

    /// <summary>最近运行优先；重叠根目录不重复，非白名单文件不入选。</summary>
    [Fact]
    public void Discovery_SortsByRunTimeAndDeduplicates()
    {
        var old = CreateAdb("MuMu");
        var recent = CreateAdb("BlueStacks", "HD-Adb.exe");
        CreateAdb("Other", "tool.exe");
        var runs = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            [old] = DateTime.UtcNow.AddDays(-1), [recent] = DateTime.UtcNow
        };
        var found = AdbDiscovery.FindCandidates([_root, Path.GetDirectoryName(recent)!], runs);
        Assert.Equal(new[] { recent, old }, found.Select(c => c.Path));
        var config = new AppConfig();
        AdbDiscovery.Apply(config, found);
        Assert.Equal(recent, config.AdbPath);
        Assert.True(config.AdbDiscoveryCompleted);
    }

    /// <summary>单候选自动选择，无执行记录时不伪造时间。</summary>
    [Fact]
    public void SingleCandidate_IsSelectedWithoutHistory()
    {
        var path = CreateAdb("MuMu/nx_main");
        var found = AdbDiscovery.FindCandidates([_root], new Dictionary<string, DateTime>());
        Assert.Null(Assert.Single(found).LastRunUtc);
        var config = new AppConfig();
        AdbDiscovery.Apply(config, found);
        Assert.Equal(path, config.AdbPath);
    }

    /// <summary>没有候选时仍能完成首次检索，保留手动修复入口。</summary>
    [Fact]
    public void MissingDirectory_ReturnsEmpty()
    {
        var found = AdbDiscovery.FindCandidates([_root], new Dictionary<string, DateTime>());
        Assert.Empty(found);
        var config = new AppConfig();
        AdbDiscovery.Apply(config, found);
        Assert.Equal("adb", config.AdbPath);
        Assert.Empty(config.AdbPaths);
    }

    /// <summary>升级不覆盖用户已经保存的选择；手动历史可跨重启恢复。</summary>
    [Fact]
    public void ExistingSelectionAndHistory_SurviveSaveReload()
    {
        var manual = CreateAdb("Manual");
        var other = CreateAdb("MuMu");
        var config = new AppConfig { AdbPath = manual, AdbPaths = [manual, manual.ToUpperInvariant()] };
        AdbDiscovery.Apply(config, [new(other, DateTime.UtcNow)]);
        var service = new ConfigService(new TestLog());
        var file = Path.Combine(_root, "config.json");
        service.Save(config, file);
        var restored = service.Load(file);
        Assert.Equal(manual, restored.AdbPath);
        Assert.Equal(2, restored.AdbPaths.Count);
        Assert.Contains(other, restored.AdbPaths);
        Assert.True(restored.AdbDiscoveryCompleted);
    }

    /// <summary>删除已经失效的旧选择后可由检索结果恢复。</summary>
    [Fact]
    public void StaleSelection_IsReplaced()
    {
        var valid = CreateAdb("MuMu");
        var config = new AppConfig { AdbPath = Path.Combine(_root, "missing", "adb.exe"), AdbPaths = null! };
        AdbDiscovery.Apply(config, [new(valid, null)]);
        Assert.Equal(valid, config.AdbPath);
        Assert.Contains(valid, config.AdbPaths);
    }

    /// <summary>仅清理本测试创建的独立临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

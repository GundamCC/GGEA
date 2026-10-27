using System.IO;
using System.Text.Json;
using GGEA.App.Device;
using GGEA.App.Logging;

namespace GGEA.Tests;

/// <summary>独立环境日志的真实采集、隐私、限频、轮转和故障隔离测试。</summary>
public sealed class EnvironmentDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ggea-env-" + Guid.NewGuid().ToString("N"));

    /// <summary>真实 Windows 采集必须得到内存和权限数据，而不是伪成功。</summary>
    [Fact]
    public async Task HostSnapshot_WritesFields()
    {
        var status = await new EnvironmentDiagnostics(_directory).CaptureAsync("startup", "adb");
        Assert.Contains("environmentId=", status);
        using var json = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(_directory))));
        var root = json.RootElement;
        Assert.True(root.GetProperty("systemMemory").GetProperty("totalPhysicalBytes").GetUInt64() > 0);
        Assert.Contains(root.GetProperty("elevated").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.True(root.GetProperty("logDirectoryWritable").GetBoolean());
        Assert.False(root.TryGetProperty("environmentVariables", out _));
        Assert.False(root.TryGetProperty("commandLine", out _));
    }

    /// <summary>同设备重复报错复用快照，不同设备必须重新采集。</summary>
    [Fact]
    public async Task Throttle_DoesNotMixDevices()
    {
        var diagnostics = new EnvironmentDiagnostics(_directory, () => new());
        await diagnostics.CaptureAsync("screenshot-failure", "adb", serial: "first");
        Assert.Contains("复用", await diagnostics.CaptureAsync("screenshot-failure", "adb", serial: "first"));
        await diagnostics.CaptureAsync("screenshot-failure", "adb", serial: "second");
        Assert.Equal(2, File.ReadAllLines(Assert.Single(Directory.GetFiles(_directory))).Length);
    }

    /// <summary>采集失败和写入失败都不能掩盖原截图错误或暴露内部异常内容。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failure_PreservesOriginalError(bool collectThrows)
    {
        Directory.CreateDirectory(_directory);
        var path = _directory;
        if (!collectThrows) { path = Path.Combine(path, "blocked"); File.WriteAllText(path, "file"); }
        var diagnostics = new EnvironmentDiagnostics(path, () => collectThrows ? throw new IOException("private-text") : new());
        var adb = new AdbService("missing-ggea-test-adb.exe", new TestLog(), diagnostics: diagnostics);
        var error = await Assert.ThrowsAsync<AdbException>(() => adb.ScreenshotAsync("device"));
        Assert.Contains("environment-unavailable=", error.Message);
        Assert.Contains("无法启动 ADB", error.Message);
        Assert.DoesNotContain("private-text", error.Message);
    }

    /// <summary>超过容量限制时保留旧日志并写入新的有效 JSON。</summary>
    [Fact]
    public async Task LargeFile_Rotates()
    {
        var diagnostics = new EnvironmentDiagnostics(_directory, () => new());
        await diagnostics.CaptureAsync("startup", "adb");
        var file = Assert.Single(Directory.GetFiles(_directory));
        File.WriteAllText(file, new string('x', 1024 * 1024));
        await diagnostics.CaptureAsync("startup", "adb");
        Assert.True(File.Exists(file + ".previous"));
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal("startup", json.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>实际截图失败会生成可按编号对应的独立日志，而非仅测试采集器本身。</summary>
    [Fact]
    public async Task ScreenshotFailure_LinksSnapshot()
    {
        var diagnostics = new EnvironmentDiagnostics(_directory, () => new());
        var adb = new AdbService("missing-ggea-test-adb.exe", new TestLog(), diagnostics: diagnostics);
        var error = await Assert.ThrowsAsync<AdbException>(() => adb.ScreenshotAsync("emulator-5558"));
        using var json = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(_directory))));
        Assert.Contains(json.RootElement.GetProperty("id").GetString()!, error.Message);
        Assert.Equal("emulator-5558", json.RootElement.GetProperty("serial").GetString());
        Assert.Equal("screenshot-failure", json.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>并发调用仍逐条写入完整 JSON，避免交错或重复编号。</summary>
    [Fact]
    public async Task ConcurrentSnapshots_RemainReadable()
    {
        var diagnostics = new EnvironmentDiagnostics(_directory, () => new());
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => diagnostics.CaptureAsync("startup", "adb")));
        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_directory)));
        Assert.Equal(12, lines.Length);
        var ids = lines.Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.GetProperty("id").GetString(); });
        Assert.Equal(12, ids.Distinct().Count());
    }

    /// <summary>用户名不会随标准用户目录写入快照。</summary>
    [Fact]
    public void UserPath_IsRedacted()
        => Assert.Equal(@"C:\Users\<USER>\tools\adb.exe", EnvironmentDiagnostics.RedactPath(@"C:\Users\example-person\tools\adb.exe"));

    /// <summary>仅清理当前测试拥有的临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

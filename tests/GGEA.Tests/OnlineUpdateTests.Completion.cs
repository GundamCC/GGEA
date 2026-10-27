using System.IO;
using GGEA.App.Config;
using GGEA.App.Updates;

namespace GGEA.Tests;

public sealed partial class OnlineUpdateTests
{
    /// <summary>旧配置默认自动安装；保存更新选项只修改该字段，保留其他设置和未知扩展。</summary>
    [Fact]
    public void AutoInstallPreference_DefaultsOn_AndPreservesOtherSettings()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(path, """{"adbPath":"custom-adb","extension":{"value":42}}""");
        var service = new ConfigService(new TestLog());
        Assert.True(service.Load(path).AutoInstallUpdates);
        service.SaveAutoInstallUpdates(false, path);
        Assert.False(service.Load(path).AutoInstallUpdates);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(42, json.RootElement.GetProperty("extension").GetProperty("value").GetInt32());
        Assert.Equal("custom-adb", service.Load(path).AdbPath);
        service.SaveAutoInstallUpdates(true, path);
        Assert.True(service.Load(path).AutoInstallUpdates);
        File.WriteAllText(path, "{broken");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => service.SaveAutoInstallUpdates(false, path));
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    /// <summary>下载仍在进行时不得安装；校验完成后按用户选项决定是否自动交接。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completion_WaitsForVerifiedDownload_ThenHonorsPreference(bool autoInstall)
    {
        var verified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new UpdateDownloadSession();
        var installs = 0;
        var run = session.RunAsync(_ => verified.Task, (package, _) =>
        {
            Assert.Equal("verified-package", package);
            installs++;
            return Task.CompletedTask;
        }, autoInstall, default);
        Assert.Equal(0, installs);
        Assert.Null(session.PreparedPackage);
        verified.SetResult("verified-package");
        await run;
        Assert.Equal(autoInstall ? 1 : 0, installs);
        Assert.Equal("verified-package", session.PreparedPackage);
    }

    /// <summary>连接或校验失败、下载返回瞬间取消，都不能触发安装。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_FailureOrCancellationNeverInstalls(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var session = new UpdateDownloadSession();
        var installs = 0;
        var run = session.RunAsync(_ =>
        {
            if (!cancel) throw new InvalidDataException("checksum mismatch");
            cancellation.Cancel();
            return Task.FromResult("verified-package");
        }, (_, _) => { installs++; return Task.CompletedTask; }, true, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        else await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Equal(0, installs);
    }

    /// <summary>更新器启动失败仍保留已经验证的包，界面可手动重试，不需要重新下载。</summary>
    [Fact]
    public async Task Completion_HandoffFailureRetainsVerifiedPackage()
    {
        var session = new UpdateDownloadSession();
        await Assert.ThrowsAsync<IOException>(() => session.RunAsync(_ => Task.FromResult("verified-package"),
            (_, _) => throw new IOException("runner unavailable"), true, default));
        Assert.Equal("verified-package", session.PreparedPackage);
    }
}

using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>验证正常取帧与只读观察不隐式扫描错误弹窗，异常和取消不会遗留后台工作。</summary>
public sealed class ScreenshotObservationTests
{
    /// <summary>隔离诊断素材，结束时只清理本用例创建的文件。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Directory.CreateTempSubdirectory("ggea-observation-").FullName;
        public void Dispose() => System.IO.Directory.Delete(Path, true);
    }

    /// <summary>损坏的诊断素材不应在正常观察中被读取；只有异常入口才负责网络与更新检测。</summary>
    [Fact]
    public async Task Observation_DoesNotRunFailureDiagnostics()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "broken.png");
        System.IO.File.WriteAllBytes(path, [1, 2, 3]);
        var adb = new FakeAdbService(TestImageFactory.CreateStageMenuPng());
        var log = new TestLog();
        var matcher = new TemplateMatcher();
        using var automation = new AutomationService(adb, matcher, log,
            connectionErrorDetector: new ConnectionErrorDetector(adb, matcher, log, path,
                dateUpdate: new DateUpdateDialogRecognizer(path, path)));
        var result = await automation.CaptureAndObserveAsync("serial", (frame, _) => Task.FromResult(frame.Width), default);
        Assert.Equal(result.Screen.Width, result.Observation);
        Assert.Equal(1, adb.ScreenshotCount);
        Assert.Empty(log.Entries);
        Assert.Empty(adb.Taps);
    }

    /// <summary>观察失败不伪装为截图失败，也不能重复获取新帧再执行有缺陷的观察回调。</summary>
    [Fact]
    public async Task ObservationFailure_IsNotRetriedAsCaptureFailure()
    {
        var adb = new FakeAdbService(TestImageFactory.CreateStageMenuPng());
        using var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        await Assert.ThrowsAsync<InvalidOperationException>(() => automation.CaptureAndObserveAsync<int>("serial",
            (_, _) => throw new InvalidOperationException("模拟识别资源损坏"), default));
        Assert.Equal(1, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>取消后等待观察回调收束，不返回结果或遗留后台识别。</summary>
    [Fact]
    public async Task Cancellation_JoinsBothObservers()
    {
        var adb = new FakeAdbService(TestImageFactory.CreateStageMenuPng());
        using var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        var pending = automation.CaptureAndObserveAsync("serial", async (_, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return 42; }
            finally { finished = true; }
        }, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(finished);
    }
}

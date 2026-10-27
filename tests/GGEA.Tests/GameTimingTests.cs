using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>验证真实五秒轮询能容纳慢加载，且用户取消不必等待完整间隔。</summary>
public sealed class GameTimingTests
{
    /// <summary>已有候选时使用短间隔复核三帧；不再被默认五秒检测间隔拖到十秒。</summary>
    [Fact]
    public async Task CandidatePolling_ConfirmsThreeFreshFramesWithoutLongSleep()
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        using var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        var gate = new StableStageEntry();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await automation.WaitForImageAsync("test", "candidate", new WaitOptions { AllowGlobalContinuation = false },
            deadline.Token, (_, _) => Task.FromResult(gate.Observe(new(new Roi(100, 100, 50, 50), .99), "entry")),
            () => gate.HasCandidate ? GameTiming.ConfirmationIntervalMs : GameTiming.RecognitionIntervalMs);
        Assert.NotNull(result);
        Assert.Equal(3, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>画布移动撤销此前稳定证据；重新连续三帧停稳后才允许返回。</summary>
    [Fact]
    public void CanvasMotion_ResetsOnMovement()
    {
        var gate = new SceneMotionGate(new Roi(0, 0, 64, 64));
        var first = ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(64, 64, 20), 64, 64));
        var moved = ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(64, 64, 100), 64, 64));
        Assert.False(gate.Observe(first));
        Assert.False(gate.Observe(first));
        Assert.False(gate.Observe(moved));
        Assert.False(gate.Observe(moved));
        Assert.True(gate.Observe(moved));
    }

    /// <summary>目标在第三轮才就绪时，默认二十秒预算仍能返回当前帧。</summary>
    [Fact]
    public async Task DefaultPolling_AllowsTargetAfterTenSeconds()
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = await automation.WaitForImageAsync("test", "slow-target", new WaitOptions { AllowGlobalContinuation = false },
            matchTarget: (_, _) => Task.FromResult<TemplateMatchResult?>(adb.ScreenshotCount >= 3
                ? new(new Roi(100, 100, 50, 50), .99) : null));
        Assert.NotNull(result);
        Assert.Equal(3, adb.ScreenshotCount);
        Assert.True(elapsed.ElapsedMilliseconds >= 10000);
        Assert.Empty(adb.Taps);
    }

    /// <summary>在首次识别完成后取消，五秒延时应立即传播取消且不得继续截图或点击。</summary>
    [Fact]
    public async Task DefaultPolling_CancellationInterruptsDelay()
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        using var cancellation = new CancellationTokenSource();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = automation.WaitForImageAsync("test", "slow-target", new WaitOptions { AllowGlobalContinuation = false },
            cancellation.Token, (_, _) => { observed.SetResult(); return Task.FromResult<TemplateMatchResult?>(null); });
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }
}

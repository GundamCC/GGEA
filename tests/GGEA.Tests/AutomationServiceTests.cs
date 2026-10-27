using System.IO;
using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 自动化原语测试：用假 ADB 服务验证识别后点击与超时行为。
/// </summary>
public class AutomationServiceTests
{
    /// <summary>继续动作耗尽原预算后，仍须观察新帧中的目标出现或连续消失，且不会误点旧目标。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalContinuation_RestoresObservationBudget(bool disappearance)
    {
        using var temp = new TempDirectory();
        var tapPath = Path.Combine(temp.Path, "tap.png");
        var targetPath = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(tapPath, CreateTapToNextPng(280, 70, 100, 180, 240));
        File.WriteAllBytes(targetPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(50, 50, 250), 50, 50));
        var overlay = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTapToNext(overlay, 1920, 280, 70, 830, 995, 100, 180, 240);
        var recovered = TestImageFactory.CreateBgra(1920, 1080, 20);
        if (!disappearance) TestImageFactory.PasteBlock(recovered, 1920, 50, 50, 100, 300, 250);
        var adb = new FakeAdbService(ImageCodec.EncodePng(overlay, 1920, 1080));
        adb.EnqueueScreenshots(ImageCodec.EncodePng(recovered, 1920, 1080));
        var service = new AutomationService(adb, new TemplateMatcher(), new TestLog(), [tapPath], 0, textOcr: TapToNextOcrSamples.SharedOcr);
        var wait = new WaitOptions { TimeoutMs = 700, PollIntervalMs = 5, Roi = new Roi(100, 300, 50, 50) };
        if (disappearance) Assert.True(await service.WaitImageDisappearAsync("serial", targetPath, wait));
        else Assert.NotNull(await service.WaitForImageAsync("serial", targetPath, wait));
        Assert.Equal(new[] { (970, 1030) }, adb.Taps);
    }

    /// <summary>全局略过仅接受右上角模板；只读入口和旧 TAP 专用入口均不能顺带点击略过。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GlobalSkip_RespectsRoiAndReadOnlyEntries(bool topRight)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "skip.png");
        File.WriteAllBytes(path, CreateTapToNextPng(280, 70, 100, 180, 240));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTapToNext(pixels, 1920, 280, 70, topRight ? 1640 : 100, 20, 100, 180, 240);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var service = new AutomationService(adb, new TemplateMatcher(), new TestLog(), skipTemplatePath: path, textOcr: TapToNextOcrSamples.SharedOcr);
        await service.ScreenshotAsync("serial");
        await service.FindImageAsync("serial", path, new WaitOptions { Roi = new Roi(1640, 0, 280, 140) });
        Assert.False(await service.HandleTapToNextAsync("serial"));
        Assert.Empty(adb.Taps);
        Assert.Equal(topRight ? GlobalScreenResult.SkipActionSent : GlobalScreenResult.None,
            await service.HandleGlobalContinuationAsync("serial"));
        Assert.Equal(topRight ? new[] { (1780, 55) } : [], adb.Taps);
    }

    /// <summary>略过消失不能代替 TAP TO NEXT 成功；即使底部继续曾出现，也必须排除后续略过页。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalSkip_DoesNotCompleteTapToNextWait(bool tapFirst)
    {
        using var temp = new TempDirectory();
        var skipPath = Path.Combine(temp.Path, "skip.png");
        var tapPath = Path.Combine(temp.Path, "tap.png");
        var targetPath = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(skipPath, CreateTapToNextPng(280, 70, 100, 180, 240));
        File.Copy(skipPath, tapPath);
        File.WriteAllBytes(targetPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(30, 30, 250), 30, 30));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        var blank = ImageCodec.EncodePng(pixels, 1920, 1080);
        PasteTapToNext(pixels, 1920, 280, 70, 1640, 20, 100, 180, 240);
        var skipOnly = ImageCodec.EncodePng(pixels, 1920, 1080);
        if (tapFirst) PasteTapToNext(pixels, 1920, 280, 70, 830, 995, 100, 180, 240);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        adb.EnqueueScreenshots(tapFirst ? skipOnly : blank, blank);
        var service = new AutomationService(adb, new TemplateMatcher(), new TestLog(), [tapPath], 0,
            skipTemplatePath: skipPath, textOcr: TapToNextOcrSamples.SharedOcr);
        var result = await service.WaitForImageOrTapToNextAsync("serial", targetPath,
            new WaitOptions { TimeoutMs = 1800, PollIntervalMs = 5, Roi = new Roi(100, 200, 50, 50) });
        Assert.Null(result.Match);
        Assert.False(result.TapToNextHandled);
        Assert.Equal(tapFirst ? new[] { (970, 1030) } : [(1780, 55)], adb.Taps);
    }

    /// <summary>略过与业务目标同帧时先略过；下一张截图中目标换位后只能点击新坐标。</summary>
    [Fact]
    public async Task GlobalSkip_ClickImageDiscardsOldTargetCoordinates()
    {
        using var temp = new TempDirectory();
        var skipPath = Path.Combine(temp.Path, "skip.png");
        var targetPath = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(skipPath, CreateTapToNextPng(280, 70, 100, 180, 240));
        File.WriteAllBytes(targetPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(50, 50, 250), 50, 50));
        var before = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTapToNext(before, 1920, 280, 70, 1640, 20, 100, 180, 240);
        TestImageFactory.PasteBlock(before, 1920, 50, 50, 100, 300, 250);
        var after = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(after, 1920, 50, 50, 300, 300, 250);
        var adb = new FakeAdbService(ImageCodec.EncodePng(before, 1920, 1080));
        adb.EnqueueScreenshots(ImageCodec.EncodePng(after, 1920, 1080));
        var service = new AutomationService(adb, new TemplateMatcher(), new TestLog(), tapToNextCheckDelayMs: 0,
            skipTemplatePath: skipPath, textOcr: TapToNextOcrSamples.SharedOcr);
        Assert.True(await service.ClickImageAsync("serial", targetPath,
            new WaitOptions { TimeoutMs = 3000, PollIntervalMs = 5, Roi = new Roi(50, 250, 400, 150) }));
        Assert.Equal(new[] { (1780, 55), (325, 325) }, adb.Taps);
    }

    /// <summary>冷却按设备隔离；冷却中的遮罩必须阻塞旧帧，无遮罩则立即放行业务识别。</summary>
    [Fact]
    public async Task GlobalScreens_CooldownBlocksOverlayButNotOtherDeviceOrClearFrame()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "tap_to_next.png");
        File.WriteAllBytes(path, CreateTapToNextPng(280, 70, 100, 180, 240));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        var blank = ImageCodec.EncodePng(pixels, 1920, 1080);
        PasteTapToNext(pixels, 1920, 280, 70, 830, 995, 100, 180, 240);
        var png = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(png);
        var time = new ManualMonotonicTime();
        var handler = new GlobalScreenHandler(adb, new TestLog(), [path], null, time, textOcr: TapToNextOcrSamples.SharedOcr);
        Assert.Equal(GlobalScreenResult.ActionSent, await handler.HandleContinuationAsync("one", png, default));
        Assert.Equal(GlobalScreenResult.Blocked, await handler.HandleContinuationAsync("one", png, default));
        Assert.Equal(GlobalScreenResult.None, await handler.HandleContinuationAsync("one", blank, default));
        Assert.Single(adb.Taps);
        Assert.Equal(GlobalScreenResult.ActionSent, await handler.HandleContinuationAsync("two", png, default));
        time.Milliseconds = 4999;
        Assert.Equal(GlobalScreenResult.Blocked, await handler.HandleContinuationAsync("one", png, default));
        time.Milliseconds = 5000;
        Assert.Equal(GlobalScreenResult.ActionSent, await handler.HandleContinuationAsync("one", png, default));
        Assert.Equal(3, adb.Taps.Count);
    }

    /// <summary>仅推进冷却时间；识别与动作仍经过真实生产代码。</summary>
    private sealed class ManualMonotonicTime : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    /// <summary>安全观察不能偷偷关闭弹窗；即使配置零延迟，也只有显式恢复入口允许点击。</summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task GlobalScreens_ReadOnlyCaptureAndRecognitionNeverTap()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "tap_to_next.png");
        File.WriteAllBytes(path, CreateTapToNextPng(280, 70, 100, 180, 240));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTapToNext(pixels, 1920, 280, 70, 830, 995, 100, 180, 240);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var service = new AutomationService(adb, new TemplateMatcher(), new TestLog(), [path], 0, textOcr: TapToNextOcrSamples.SharedOcr);

        var png = await service.ScreenshotAsync("serial", retryOnFailure: false);
        Assert.True(await service.GlobalScreens.IsTapToNextVisibleAsync(png));
        Assert.NotNull(await service.FindImageAsync("serial", path,
            new WaitOptions { Roi = new Roi(830, 995, 280, 70) }));
        Assert.Empty(adb.Taps);
        Assert.True(await service.HandleTapToNextAsync("serial"));
        Assert.Single(adb.Taps);
    }

    /// <summary>正常观察不扫描错误；明确异常后诊断先于恢复继续动作，不能点击已确认的网络弹窗。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalScreens_AbnormalCheckPrecedesRecoveryContinuation(bool skip)
    {
        using var temp = new TempDirectory();
        var overlay = Path.Combine(temp.Path, "tap_to_next.png");
        var error = Path.Combine(temp.Path, "error.png");
        File.WriteAllBytes(overlay, CreateTapToNextPng(280, 70, 100, 180, 240));
        File.WriteAllBytes(error, CreateTapToNextPng(200, 70, 240, 80, 160));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTapToNext(pixels, 1920, 280, 70, skip ? 1640 : 830, skip ? 20 : 995, 100, 180, 240);
        PasteTapToNext(pixels, 1920, 200, 70, 860, 205, 240, 80, 160);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var log = new TestLog();
        var matcher = new TemplateMatcher();
        var service = new AutomationService(adb, matcher, log, [overlay], 0,
            new ConnectionErrorDetector(adb, matcher, log, error), skipTemplatePath: skip ? overlay : null, textOcr: TapToNextOcrSamples.SharedOcr);
        var screen = await service.CaptureScreenAsync("serial");
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("检测到网络错误弹窗"));
        await Assert.ThrowsAsync<ConnectionErrorException>(() => service.GlobalScreens.CheckAbnormalScreenAsync(screen));
        Assert.Empty(adb.Taps);
        Assert.Equal(1, adb.ScreenshotCount);

        // 主界面单帧观察也不重复诊断；导航失败才进入共享异常入口。
        var home = new MainScreenDetector(adb, matcher, log, globalScreens: service.GlobalScreens);
        await home.DetectButtonAsync("serial");
        Assert.Single(log.Entries, e => e.Message.Contains("检测到网络错误弹窗"));
        Assert.Empty(adb.Taps);
    }

    /// <summary>终止检查必须先于全局继续点击，并覆盖点击后的新截图，不能误报等待成功。</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task WaitForImageOrTapToNextAsync_StopGuardPrecedesProgress(int stopAtFrame, bool skip)
    {
        using var temp = new TempDirectory();
        var overlayPath = Path.Combine(temp.Path, "tap_to_next.png");
        var targetPath = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(overlayPath, CreateTapToNextPng(280, 70, 100, 180, 240));
        File.WriteAllBytes(targetPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 200), 100, 100));
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        var blank = ImageCodec.EncodePng(screen, 1920, 1080);
        PasteTapToNext(screen, 1920, 280, 70, skip ? 1640 : 830, skip ? 20 : 995, 100, 180, 240);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        adb.EnqueueScreenshots(blank);
        var log = new TestLog();
        var service = new AutomationService(adb, new TemplateMatcher(), log, [overlayPath], 0, skipTemplatePath: skip ? overlayPath : null, textOcr: TapToNextOcrSamples.SharedOcr);
        var checks = 0;
        var result = await service.WaitForImageOrTapToNextAsync("serial", targetPath,
            new WaitOptions { TimeoutMs = 3000, PollIntervalMs = 1 },
            stopWhen: (_, _) => Task.FromResult(++checks == stopAtFrame));
        Assert.Null(result.Match);
        Assert.False(result.TapToNextHandled);
        Assert.Equal(stopAtFrame, adb.ScreenshotCount);
        Assert.Equal(stopAtFrame - 1, adb.Taps.Count);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("识别成功") || e.Message.Contains("等待图片超时"));
    }

    /// <summary>传输失败、空图和损坏 PNG 均须有界停止，不能触发后续动作或成功日志。</summary>
    [Theory]
    [InlineData("adb")]
    [InlineData("empty")]
    [InlineData("corrupt")]
    public async Task CaptureFailure_StopsWithoutControlActions(string failure)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(path, ImageCodec.EncodePng(TestImageFactory.CreateBgra(10, 10, 200), 10, 10));
        byte[] png = failure == "corrupt" ? [137, 80, 78, 71, 13, 10, 26, 10] : [];
        var adb = new FakeAdbService(png);
        if (failure == "adb") adb.ScreenshotFailure = _ => new GGEA.App.Device.AdbException("测试截图失败");
        var log = new TestLog();
        var service = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
        var options = new WaitOptions { TimeoutMs = 3000, PollIntervalMs = 1 };

        await Assert.ThrowsAsync<ScreenshotCaptureException>(async () =>
        {
            if (await service.WaitImageDisappearAsync("serial", path, options))
                await service.TapAsync("serial", 1, 1);
        });
        Assert.Equal(3, adb.ScreenshotCount);
        await Assert.ThrowsAsync<ScreenshotCaptureException>(() => service.ClickImageAsync("serial", path, options));
        await Assert.ThrowsAsync<ScreenshotCaptureException>(() => service.ClickIfExistsAsync("serial", path, options));
        Assert.Empty(adb.Taps);
        Assert.Empty(adb.Swipes);
        Assert.Contains(log.Entries, e => e.Level == "ERROR" && e.Message.Contains("连续截图捕获失败"));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("图片已消失") || e.Message.Contains("目标不存在"));
    }

    /// <summary>单次安全取证失败只警告并继续抛出异常，停止范围由购买或招募任务决定。</summary>
    [Fact]
    public async Task SingleCaptureFailure_WarnsAndStillThrows()
    {
        var adb = new FakeAdbService([]);
        var log = new TestLog();
        var service = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);

        await Assert.ThrowsAsync<ScreenshotCaptureException>(() =>
            service.ScreenshotAsync("serial", retryOnFailure: false));

        Assert.Equal(1, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
        Assert.Contains(log.Entries, entry => entry.Level == "WARN" && entry.Message.Contains("无法判断页面状态"));
        Assert.DoesNotContain(log.Entries, entry => entry.Level == "ERROR");
    }

    /// <summary>短暂捕获失败可恢复；消失仍需两张新有效帧，目标重新出现会重置确认。</summary>
    [Fact]
    public async Task WaitImageDisappearAsync_ConfirmsConsecutiveValidFrames()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "target.png");
        var present = ImageCodec.EncodePng(TestImageFactory.CreateBgra(10, 10, 200), 10, 10);
        var absent = ImageCodec.EncodePng(TestImageFactory.CreateBgra(10, 10, 0), 10, 10);
        File.WriteAllBytes(path, present);
        var adb = new FakeAdbService(absent) { ScreenshotFailure = n => n == 1 ? new GGEA.App.Device.AdbException("暂时失败") : null };
        adb.EnqueueScreenshots(present, absent, absent);
        var log = new TestLog();
        var service = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
        Assert.True(await service.WaitImageDisappearAsync("serial", path,
            new WaitOptions { TimeoutMs = 3000, PollIntervalMs = 1 }));
        Assert.Equal(5, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
        Assert.Contains(log.Entries, e => e.Message.Contains("图片已消失"));
    }

    /// <summary>发送 TAP TO NEXT 后目标仍存在或截图失败，均不得报告目标消失。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitImageDisappearAsync_AfterOverlayClick_RequiresFreshEvidence(bool captureFails)
    {
        using var temp = new TempDirectory();
        var overlayPath = Path.Combine(temp.Path, "tap_to_next.png");
        var path = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(overlayPath, CreateTapToNextPng(280, 70, 100, 180, 240));
        File.WriteAllBytes(path, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 200), 100, 100));
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 300, 600, 200);
        var targetOnly = ImageCodec.EncodePng(screen, 1920, 1080);
        PasteTapToNext(screen, 1920, 280, 70, 830, 995, 100, 180, 240);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        adb.EnqueueScreenshots(targetOnly);
        if (captureFails) adb.ScreenshotFailure = n => n > 1 ? new GGEA.App.Device.AdbException("后续截图失败") : null;
        var log = new TestLog();
        var service = new AutomationService(adb, new TemplateMatcher(), log, [overlayPath], 0, textOcr: TapToNextOcrSamples.SharedOcr);
        var options = new WaitOptions { TimeoutMs = 2200, PollIntervalMs = 10 };
        if (captureFails)
            await Assert.ThrowsAsync<ScreenshotCaptureException>(() => service.WaitImageDisappearAsync("serial", path, options));
        else
            Assert.False(await service.WaitImageDisappearAsync("serial", path, options));
        Assert.True(adb.ScreenshotCount >= 2);
        Assert.Single(adb.Taps);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("图片已消失"));
    }

    /// <summary>等待继续遮罩的入口须以点击后的截图确认消失，截图异常直接失败。</summary>
    [Theory]
    [InlineData("present")]
    [InlineData("absent")]
    [InlineData("failure")]
    public async Task WaitForImageOrTapToNextAsync_ConfirmsOverlayDisappearance(string state)
    {
        using var temp = new TempDirectory();
        var overlayPath = Path.Combine(temp.Path, "tap_to_next.png");
        var path = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(overlayPath, CreateTapToNextPng(280, 70, 100, 180, 240));
        File.WriteAllBytes(path, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 200), 100, 100));
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        var blank = ImageCodec.EncodePng(screen, 1920, 1080);
        PasteTapToNext(screen, 1920, 280, 70, 830, 995, 100, 180, 240);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        if (state == "absent") adb.EnqueueScreenshots(blank);
        if (state == "failure") adb.ScreenshotFailure = n => n > 1 ? new GGEA.App.Device.AdbException("截图失败") : null;
        var service = new AutomationService(adb, new TemplateMatcher(), new TestLog(), [overlayPath], 0, textOcr: TapToNextOcrSamples.SharedOcr);
        var options = new WaitOptions { TimeoutMs = 1800, PollIntervalMs = 10 };
        if (state == "failure")
            await Assert.ThrowsAsync<ScreenshotCaptureException>(() => service.WaitForImageOrTapToNextAsync("serial", path, options));
        else
        {
            var result = await service.WaitForImageOrTapToNextAsync("serial", path, options);
            Assert.Null(result.Match);
            Assert.Equal(state == "absent", result.TapToNextHandled);
        }
        Assert.True(adb.ScreenshotCount >= 2);
    }

    /// <summary>取消会直接中止捕获重试，不吞掉取消或继续发送控制命令。</summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task CaptureRetry_CancellationStopsImmediately()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(path, ImageCodec.EncodePng(TestImageFactory.CreateBgra(10, 10, 200), 10, 10));
        using var cts = new CancellationTokenSource();
        var adb = new FakeAdbService([])
        {
            ScreenshotFailure = _ =>
            {
                cts.Cancel();
                return new GGEA.App.Device.AdbException("取消时捕获失败");
            }
        };
        var service = new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WaitImageDisappearAsync("serial", path, ct: cts.Token));
        Assert.Equal(1, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>恢复入口只处理识别成功的继续遮罩，空白错误页面不得产生点击。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleTapToNextAsync_ClicksOnlyRecognizedOverlay(bool present)
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        if (present) PasteTapToNext(screen, 1920, 280, 70, 830, 995, 100, 180, 240);
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "tap_to_next.png");
        File.WriteAllBytes(path, CreateTapToNextPng(280, 70, 100, 180, 240));
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var service = new AutomationService(adb, new TemplateMatcher(), new TestLog(), [path], textOcr: TapToNextOcrSamples.SharedOcr);
        Assert.Equal(present, await service.HandleTapToNextAsync("serial"));
        Assert.Equal(present ? new[] { (970, 1030) } : [], adb.Taps);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ClickImageAsync_Found_TapsAtCenter()
    {
        // 屏幕 200x160，目标在 (80,50) 大小 30x20，中心应为 (95,60)
        var screen = TestImageFactory.CreateBgra(200, 160, 20);
        TestImageFactory.PasteBlock(screen, 200, 30, 20, 80, 50, 200);
        var template = TestImageFactory.CreateBgra(30, 20, 200);

        using var temp = new TempDirectory();
        var templatePath = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(templatePath, ImageCodec.EncodePng(template, 30, 20));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 200, 160));
        var service = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);

        var clicked = await service.ClickImageAsync(
            "serial",
            templatePath,
            new WaitOptions { TimeoutMs = 2000, PollIntervalMs = 10, Threshold = 0.9 });

        Assert.True(clicked);
        Assert.Single(fakeAdb.Taps);
        Assert.Equal((95, 60), fakeAdb.Taps[0]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WaitForImageAsync_NotFound_TimesOutAndReturnsNull()
    {
        // 屏幕与模板完全不同，应超时返回 null
        var screen = TestImageFactory.CreateBgra(100, 100, 10);
        var template = TestImageFactory.CreateBgra(20, 20, 200);

        using var temp = new TempDirectory();
        var templatePath = Path.Combine(temp.Path, "absent.png");
        File.WriteAllBytes(templatePath, ImageCodec.EncodePng(template, 20, 20));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 100, 100));
        var service = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);

        var result = await service.WaitForImageAsync(
            "serial",
            templatePath,
            new WaitOptions { TimeoutMs = 200, PollIntervalMs = 20, Threshold = 0.9 });

        Assert.Null(result);
    }

    [Fact]
    public async Task WaitForImageAsync_GlobalTapToNextPresent_ClicksAndContinues()
    {
        // 第一帧只有 TAP TO NEXT（280x70，灰度 150）位于底部中间 (830,995)；
        // 第二帧 TAP TO NEXT 消失、目标出现（100x100，灰度 200）位于 (300,600)
        var tapToNextScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTapToNext(tapToNextScreen, 1920, 280, 70, 830, 995, 100, 180, 240);
        var targetScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(targetScreen, 1920, 100, 100, 300, 600, 200);

        using var temp = new TempDirectory();
        var tapToNextPath = Path.Combine(temp.Path, "tap_to_next.png");
        var targetPath = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(tapToNextPath, CreateTapToNextPng(280, 70, 100, 180, 240));
        File.WriteAllBytes(targetPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 200), 100, 100));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(tapToNextScreen, 1920, 1080));
        fakeAdb.EnqueueScreenshots(ImageCodec.EncodePng(targetScreen, 1920, 1080));
        var service = new AutomationService(
            fakeAdb,
            new TemplateMatcher(),
            new TestLog(),
            new[] { tapToNextPath },
            tapToNextCheckDelayMs: 0, textOcr: TapToNextOcrSamples.SharedOcr);

        var result = await service.WaitForImageAsync(
            "serial",
            targetPath,
            new WaitOptions { TimeoutMs = 3000, PollIntervalMs = 10, Threshold = 0.9 });

        Assert.NotNull(result);
        Assert.Equal(350, result!.CenterX);
        Assert.Equal(650, result.CenterY);
        // 只点击了 TAP TO NEXT，等待目标本身不产生点击
        Assert.Equal(new[] { (970, 1030) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task ClickImageAsync_GlobalTapToNextPresent_DoesNotDoubleTapSameFrame()
    {
        // 第一帧同时包含 TAP TO NEXT 与目标：全局先点击 TAP TO NEXT，
        // 本轮不再匹配目标，避免同帧连续点击两次；
        // 第二帧只有目标：正常匹配并点击目标中心
        var bothScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(bothScreen, 1920, 100, 100, 300, 600, 200); // 目标
        PasteTapToNext(bothScreen, 1920, 280, 70, 830, 995, 100, 180, 240); // TAP TO NEXT
        var targetOnlyScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(targetOnlyScreen, 1920, 100, 100, 300, 600, 200);

        using var temp = new TempDirectory();
        var tapToNextPath = Path.Combine(temp.Path, "tap_to_next.png");
        var targetPath = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(tapToNextPath, CreateTapToNextPng(280, 70, 100, 180, 240));
        File.WriteAllBytes(targetPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 200), 100, 100));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(bothScreen, 1920, 1080));
        fakeAdb.EnqueueScreenshots(ImageCodec.EncodePng(targetOnlyScreen, 1920, 1080));
        var service = new AutomationService(
            fakeAdb,
            new TemplateMatcher(),
            new TestLog(),
            new[] { tapToNextPath },
            tapToNextCheckDelayMs: 0, textOcr: TapToNextOcrSamples.SharedOcr);

        var clicked = await service.ClickImageAsync(
            "serial",
            targetPath,
            new WaitOptions { TimeoutMs = 3000, PollIntervalMs = 10, Threshold = 0.9 });

        Assert.True(clicked);
        // 顺序：先点击 TAP TO NEXT，再点击目标中心
        Assert.Equal(new[] { (970, 1030), (350, 650) }, fakeAdb.Taps);
    }

    /// <summary>使用真实 TAP TO NEXT 字形生成状态机页面。</summary>
    private static byte[] CreateTapToNextPng(int width, int height, byte v1, byte v2, byte v3)
    {
        return TapToNextOcrSamples.TextPng(width, height);
    }

    /// <summary>在屏幕中粘贴真实 TAP TO NEXT 字形。</summary>
    private static void PasteTapToNext(
        byte[] screen,
        int screenWidth,
        int width,
        int height,
        int x,
        int y,
        byte v1,
        byte v2,
        byte v3)
    {
        TapToNextOcrSamples.Paste(screen, screenWidth, width, height, x, y);
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

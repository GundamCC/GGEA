using System.IO;
using System.IO.Compression;
using System.Text.Json;
using GGEA.App.Config;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>独立略过服务测试。</summary>
public class StageSkipServiceTests
{
    /// <summary>缺少结果识别时，不能以二十秒睡眠伪造成功；必须在消费前停止。</summary>
    [Fact]
    public async Task SkipCurrentAsync_LongWaitWithoutResultEvidence_StopsBeforeConsumption()
    {
        var adb = new FakeAdbService([]);
        var service = CreateService(adb, new TestLog(), new StageSkipOptions());
        var result = await service.SkipCurrentAsync("serial", new UiLayout());
        Assert.False(result.Success);
        Assert.Contains("未配置略过结果", result.Message);
        Assert.Empty(adb.Taps);
        Assert.Equal(0, adb.ScreenshotCount);
    }

    /// <summary>回放用户提供的 Narrative 结果页，按真实模板、布局和阈值关闭 OK 并确认返回。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipCurrentAsync_NarrativeResultFrame_RecognizesTitleAndClosesOk(bool delayed)
    {
        var resultFrame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "skip-result-narrative-mumu5556.png"));
        var resultPath = ConfigService.ResolvePath("resources/templates/stage/skip/skip_result.png");
        var countPath = ConfigService.ResolvePath("resources/templates/stage/skip/count_limit.png");
        var layout = new UiLayoutService(new TestLog()).Load(ConfigService.ResolvePath("resources/ui_layout/stage.json"));
        var count = ImageCodec.DecodePng(File.ReadAllBytes(countPath));
        var returned = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var row = 0; row < count.Height; row++)
            Buffer.BlockCopy(count.Bgra, row * count.Width * 4, returned, ((680 + row) * 1920 + 1590) * 4, count.Width * 4);
        var returnedFrame = ImageCodec.EncodePng(returned, 1920, 1080);
        // 次数上限是暗色素材，纯黑占位会被 SSD 误命中；用亮过渡帧保持“仍可略过”的前提。
        var initial = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 200), 1920, 1080);
        var adb = new FakeAdbService(initial);
        long? executedAt = null;
        adb.ScreenshotProvider = () =>
        {
            if (adb.Taps.Count < 2) return initial;
            executedAt ??= Environment.TickCount64;
            // 真实日志的旧等待窗口在约 11.5 秒结束；回放 11 秒加载，证明不能仍采用 10 秒预算。
            if (adb.Taps.Count == 2) return delayed && Environment.TickCount64 - executedAt.Value < 11000 ? initial : resultFrame;
            return returnedFrame;
        };
        var service = CreateService(adb, new TestLog(), new StageSkipOptions
        {
            LateResultRecoveryTimeoutMs = 0,
            SkipResultTemplatePath = resultPath, SkipCountLimitTemplatePath = countPath,
            SkipResultThreshold = .85, SkipCountLimitThreshold = .85,
            SkipDialogWaitMs = 0, SettleAfterOkMs = 0,
        });

        var result = await service.SkipCurrentAsync("serial", layout);

        var screen = ImageCodec.DecodePng(resultFrame);
        var title = ImageCodec.DecodePng(File.ReadAllBytes(resultPath));
        var observed = new TemplateMatcher().MatchCandidate(screen.Bgra, screen.Width, screen.Height,
            title.Bgra, title.Width, title.Height, .85, layout.Find("skipResultTitle")!.Roi, CancellationToken.None);
        Assert.True(result.Success, $"{result.Message}; 标题原图置信度 {observed?.Confidence:P1}");
        Assert.Equal(new[] { (1674, 710), (1150, 850), (1676, 988) }, adb.Taps);
    }

    /// <summary>正常预算耗尽后接管迟到结果；未知画面和取消均不得点击 OK 或重复执行。</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SkipCurrentAsync_LateResult_RecoversOrStopsSafely(bool arrives, bool cancel)
    {
        var frame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "skip-result-narrative-mumu5556.png"));
        var unknown = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 200), 1920, 1080);
        var adb = new FakeAdbService(unknown);
        using var cancellation = new CancellationTokenSource();
        adb.ScreenshotProvider = () =>
        {
            if (adb.ScreenshotCount >= 2 && cancel) cancellation.Cancel();
            return adb.ScreenshotCount >= 2 && arrives ? frame : unknown;
        };
        var service = CreateService(adb, new TestLog(), new StageSkipOptions
        {
            SkipResultTemplatePath = ConfigService.ResolvePath("resources/templates/stage/skip/skip_result.png"),
            SkipResultTimeoutMs = 1, LateResultRecoveryTimeoutMs = 2000,
            SkipDialogWaitMs = 0, SettleAfterOkMs = 0,
        });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SkipCurrentAsync("serial", CreateLayout(), cancellation.Token));
        else
            Assert.Equal(arrives, (await service.SkipCurrentAsync("serial", CreateLayout())).Success);
        Assert.Equal(arrives ? 3 : 2, adb.Taps.Count);
        Assert.Equal((1674, 710), adb.Taps[0]);
        Assert.Equal((1150, 850), adb.Taps[1]);
        if (arrives) Assert.Equal((1676, 988), adb.Taps[2]);
    }

    /// <summary>结果恰好出现在原 AP 检查那张截图时，不能反复丢弃；两目标必须共享观察帧。</summary>
    [Fact]
    public async Task SkipCurrentAsync_ResultOnAlternatingFrame_IsNotConsumedByApCheck()
    {
        using var temp = new TempDirectory();
        var titlePath = Path.Combine(temp.Path, "skip_result.png");
        var recoveryPath = Path.Combine(temp.Path, "recovery.png");
        File.WriteAllBytes(titlePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(330, 80, 200), 330, 80));
        File.WriteAllBytes(recoveryPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(110, 70, 150), 110, 70));
        var result = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(result, 1920, 330, 80, 820, 30, 200);
        var resultFrame = ImageCodec.EncodePng(result, 1920, 1080);
        var unknown = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 255), 1920, 1080);
        var adb = new FakeAdbService(unknown);
        adb.ScreenshotProvider = () => adb.ScreenshotCount % 2 == 0 ? resultFrame : unknown;
        var service = CreateService(adb, new TestLog(), new StageSkipOptions
        {
            LateResultRecoveryTimeoutMs = 0,
            ApRecoveryPromptTemplatePath = ConfigService.ResolvePath("resources/templates/stage/skip/ap_recovery_title.png"),
            SkipResultTemplatePath = titlePath, ApRecoveryCloseTemplatePath = recoveryPath,
            // 两帧回放必须容纳生产轮询间隔，不能在第二帧读取之前超时。
            SkipResultTimeoutMs = 2 * GameTiming.RecognitionIntervalMs, SkipDialogWaitMs = 0, SettleAfterOkMs = 0,
        });

        Assert.True((await service.SkipCurrentAsync("serial", CreateLayout())).Success);
        Assert.Equal(new[] { (1674, 710), (1150, 850), (1676, 988) }, adb.Taps);
        Assert.Equal(2, adb.ScreenshotCount);
    }

    /// <summary>结果加载预算延长后，返回页仍必须在自己的短预算内确认，不能继续等待整个加载窗口。</summary>
    [Fact]
    public async Task ConfirmPostSkipAsync_UsesIndependentReturnBudget()
    {
        using var temp = new TempDirectory();
        var titlePath = Path.Combine(temp.Path, "return.png");
        File.WriteAllBytes(titlePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(330, 80, 200), 330, 80));
        var unknown = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 255), 1920, 1080);
        var service = CreateService(new FakeAdbService(unknown), new TestLog(), new StageSkipOptions
        {
            LateResultRecoveryTimeoutMs = 0,
            SkipResultTimeoutMs = 20000, PostSkipTimeoutMs = 100, PostSkipPageTemplatePath = titlePath,
        });
        using var guard = new CancellationTokenSource(2000);

        Assert.False(await service.ConfirmPostSkipAsync("serial", CreateLayout(), guard.Token));
    }

    /// <summary>结果超时保存当前阶段最后帧及标题候选，不补截图、不点击 OK，也不只记录主界面模板。</summary>
    [Fact]
    public async Task SkipCurrentAsync_ResultTimeout_SavesStageEvidenceWithoutOk()
    {
        using var temp = new TempDirectory();
        var titlePath = ConfigService.ResolvePath("resources/templates/stage/skip/skip_result.png");
        var unknown = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 200), 1920, 1080);
        var adb = new FakeAdbService(unknown);
        var log = new TestLog();
        var service = new StageSkipService(new AutomationService(adb, new TemplateMatcher(), log), log,
            new StageSkipOptions { LateResultRecoveryTimeoutMs = 0, SkipResultTemplatePath = titlePath, SkipResultTimeoutMs = 100, SkipDialogWaitMs = 0 },
            new RecognitionEvidence(temp.Path, log));

        Assert.False((await service.SkipCurrentAsync("serial", CreateLayout())).Success);
        Assert.Equal(new[] { (1674, 710), (1150, 850) }, adb.Taps);
        Assert.Equal(1, adb.ScreenshotCount);
        using var zip = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(temp.Path, "recognition-*.zip")));
        using var metadataStream = zip.GetEntry("diagnostic.json")!.Open();
        using var metadata = JsonDocument.Parse(metadataStream);
        Assert.Equal("skip-result-timeout", metadata.RootElement.GetProperty("kind").GetString());
        var candidate = Assert.Single(metadata.RootElement.GetProperty("candidates").EnumerateArray());
        Assert.Equal("skip_result.png", candidate.GetProperty("Name").GetString());
        Assert.False(candidate.GetProperty("accepted").GetBoolean());
        using var frameStream = zip.GetEntry("frame.png")!.Open();
        using var frameBytes = new MemoryStream();
        frameStream.CopyTo(frameBytes);
        Assert.Equal(ImageCodec.DecodePng(unknown).Bgra, ImageCodec.DecodePng(frameBytes.ToArray()).Bgra);
    }

    [Fact]
    public async Task SkipCurrentAsync_WithoutTemplates_TapsFullFlow()
    {
        var adb = new FakeAdbService(CreateBlankPng());
        var service = CreateService(adb, new TestLog(), new StageSkipOptions
        {
            LateResultRecoveryTimeoutMs = 0,
            SkipDialogWaitMs = 0,
            SettleAfterOkMs = 0,
            SkipResultTimeoutMs = 0,
        });

        var result = await service.SkipCurrentAsync("serial", CreateLayout());

        Assert.True(result.Success, result.Message);
        Assert.Contains((1674, 710), adb.Taps);
        Assert.Contains((1150, 850), adb.Taps);
        Assert.Contains((1676, 988), adb.Taps);
    }

    [Fact]
    public async Task SkipCurrentAsync_WithTemplates_RecognizesResultAndConfirmsLimit()
    {
        using var temp = new TempDirectory();
        var skipResultPath = Path.Combine(temp.Path, "skip_result.png");
        var countLimitPath = Path.Combine(temp.Path, "count_limit.png");
        File.WriteAllBytes(
            skipResultPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(330, 80, 200), 330, 80));
        File.WriteAllBytes(
            countLimitPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 60, 180), 160, 60));

        var baseScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        var resultScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(resultScreen, 1920, 330, 80, 820, 30, 200);
        var afterOkScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(afterOkScreen, 1920, 160, 60, 1590, 680, 180);

        var adb = new FakeAdbService(ImageCodec.EncodePng(baseScreen, 1920, 1080));
        adb.EnqueueScreenshots(
            ImageCodec.EncodePng(resultScreen, 1920, 1080),
            ImageCodec.EncodePng(afterOkScreen, 1920, 1080));
        var service = CreateService(adb, new TestLog(), new StageSkipOptions
        {
            LateResultRecoveryTimeoutMs = 0,
            SkipDialogWaitMs = 0,
            SettleAfterOkMs = 0,
            SkipResultTimeoutMs = 2000,
            SkipResultThreshold = 0.9,
            SkipCountLimitThreshold = 0.9,
            SkipResultTemplatePath = skipResultPath,
            SkipCountLimitTemplatePath = countLimitPath,
        });

        var result = await service.SkipCurrentAsync("serial", CreateLayout());

        Assert.True(result.Success, result.Message);
        Assert.Contains((1674, 710), adb.Taps);
        Assert.Contains((1150, 850), adb.Taps);
        Assert.Contains((1676, 988), adb.Taps);
    }

    [Fact]
    public async Task IsSkipEnabledAsync_CountLimitVisible_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var countLimitPath = Path.Combine(temp.Path, "count_limit.png");
        File.WriteAllBytes(
            countLimitPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 60, 180), 160, 60));
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 160, 60, 1590, 680, 180);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var service = CreateService(adb, new TestLog(), new StageSkipOptions
        {
            LateResultRecoveryTimeoutMs = 0,
            SkipCountLimitTemplatePath = countLimitPath,
            SkipCountLimitThreshold = 0.9,
        });

        var enabled = await service.IsSkipEnabledAsync("serial", CreateLayout());

        Assert.False(enabled);
    }

    [Fact]
    public async Task SkipCurrentAsync_CountLimitVisible_ReturnsFailWithoutTaps()
    {
        using var temp = new TempDirectory();
        var countLimitPath = Path.Combine(temp.Path, "count_limit.png");
        File.WriteAllBytes(
            countLimitPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 60, 180), 160, 60));
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 160, 60, 1590, 680, 180);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var service = CreateService(adb, new TestLog(), new StageSkipOptions
        {
            LateResultRecoveryTimeoutMs = 0,
            SkipCountLimitTemplatePath = countLimitPath,
            SkipCountLimitThreshold = 0.9,
        });

        var result = await service.SkipCurrentAsync("serial", CreateLayout());

        Assert.False(result.Success);
        Assert.True(result.IsUnavailable);
        Assert.Empty(adb.Taps);
    }

    /// <summary>通用关闭按钮必须与 AP 文字同帧出现，且位于底部；跨帧或单一证据不能停止 AP 任务。</summary>
    [Theory]
    [InlineData(930, true, true, false, true)]
    [InlineData(300, true, true, false, false)]
    [InlineData(930, false, true, false, false)]
    [InlineData(930, true, false, false, false)]
    [InlineData(930, true, true, true, false)]
    public async Task CompleteExecutedSkip_ApRecovery_RequiresSameFrameEvidence(
        int y, bool showPrompt, bool showClose, bool splitFrames, bool expected)
    {
        var path = ConfigService.ResolvePath("resources/templates/common/close.png");
        var promptPath = ConfigService.ResolvePath("resources/templates/stage/skip/ap_recovery_title.png");
        var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        var promptOnly = TestImageFactory.CreateBgra(1920, 1080, 20);
        if (showClose) PasteTemplate(pixels, path, 300, y);
        if (showPrompt) PasteTemplate(splitFrames ? promptOnly : pixels, promptPath, 800, 200);
        var closeFrame = ImageCodec.EncodePng(pixels, 1920, 1080);
        var promptFrame = ImageCodec.EncodePng(promptOnly, 1920, 1080);
        var adb = new FakeAdbService(closeFrame);
        if (splitFrames) adb.ScreenshotProvider = () => adb.ScreenshotCount % 2 == 0 ? promptFrame : closeFrame;
        var notices = new List<string>();
        var service = CreateService(adb, new TestLog(), new StageSkipOptions { LateResultRecoveryTimeoutMs = 0,
            ApRecoveryCloseTemplatePath = path, ApRecoveryPromptTemplatePath = promptPath,
            SkipResultTimeoutMs = splitFrames ? 2 * GameTiming.RecognitionIntervalMs : 1000,
            SkipResultThreshold = .9, ApRecoverySettleMs = 0, SkipDialogWaitMs = 0
        }, apStopped: notices.Add);
        var result = await service.CompleteExecutedSkipAsync("serial", CreateLayout());
        Assert.False(result.Success);
        Assert.Equal(expected, result.IsApInsufficient);
        Assert.Equal(expected ? 1 : 0, notices.Count);
        if (expected) Assert.Equal(new[] { (300 + image.Width / 2, y + image.Height / 2), (770, 850) }, adb.Taps);
        else Assert.Empty(adb.Taps);
        if (splitFrames) Assert.True(adb.ScreenshotCount >= 2, "必须观察到分离的两种证据帧");
    }

    /// <summary>强化培育不配置 AP 检测；即使过渡画面出现 AP 模板组合也只能等待真实结果。</summary>
    [Fact]
    public async Task CompleteExecutedSkip_ApDetectionDisabled_WaitsForResultWithoutStopping()
    {
        using var temp = new TempDirectory();
        var titlePath = Path.Combine(temp.Path, "result.png");
        File.WriteAllBytes(titlePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(330, 80, 200), 330, 80));
        var transition = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTemplate(transition, ConfigService.ResolvePath("resources/templates/common/close.png"), 300, 930);
        PasteTemplate(transition, ConfigService.ResolvePath("resources/templates/stage/skip/ap_recovery_title.png"), 800, 200);
        var resultPixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(resultPixels, 1920, 330, 80, 820, 30, 200);
        var transitionFrame = ImageCodec.EncodePng(transition, 1920, 1080);
        var resultFrame = ImageCodec.EncodePng(resultPixels, 1920, 1080);
        var adb = new FakeAdbService(transitionFrame);
        adb.ScreenshotProvider = () => adb.ScreenshotCount == 1 ? transitionFrame : resultFrame;
        var notices = new List<string>();
        var service = CreateService(adb, new TestLog(), new StageSkipOptions { LateResultRecoveryTimeoutMs = 0,
            SkipResultTemplatePath = titlePath, SkipResultTimeoutMs = 2 * GameTiming.RecognitionIntervalMs, SettleAfterOkMs = 0,
        }, apStopped: notices.Add);
        var result = await service.CompleteExecutedSkipAsync("serial", CreateLayout());
        Assert.True(result.Success, result.Message);
        Assert.False(result.IsApInsufficient);
        Assert.Empty(notices);
        Assert.Equal(new[] { (1676, 988) }, adb.Taps);
    }

    /// <summary>回放雷电真实回复 AP 选择页：没有“AP不足”询问文字，也必须关闭并通知停止。</summary>
    [Fact]
    public async Task CompleteExecutedSkip_RealApRecoveryPage_ClosesAndStops()
    {
        var frame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sortie", "ap-recovery-ldplayer.png"));
        var adb = new FakeAdbService(frame);
        var notices = new List<string>();
        var service = CreateService(adb, new TestLog(), new StageSkipOptions { LateResultRecoveryTimeoutMs = 0,
            SkipResultTemplatePath = ConfigService.ResolvePath("resources/templates/stage/skip/skip_result.png"),
            ApRecoveryPromptTemplatePath = ConfigService.ResolvePath("resources/templates/stage/skip/ap_recovery_title.png"),
            ApRecoveryCloseTemplatePath = ConfigService.ResolvePath("resources/templates/common/close.png"),
            SkipResultTimeoutMs = 2000, ApRecoverySettleMs = 0, SkipDialogWaitMs = 0,
        }, apStopped: notices.Add);
        var result = await service.CompleteExecutedSkipAsync("serial", CreateLayout());
        Assert.False(result.Success);
        Assert.True(result.IsApInsufficient, result.Message);
        Assert.Single(notices);
        Assert.Equal(new[] { (960, 994), (770, 850) }, adb.Taps);
    }

    /// <summary>MuMu 日文 AP 回复实机帧复用原停止流程，关闭与取消以外不得点击回复方式。</summary>
    [Fact]
    public async Task CompleteExecutedSkip_MuMuJapaneseApRecovery_ClosesAndStops()
    {
        using var temp = new TempDirectory();
        var title = Path.Combine(temp.Path, "ap_recovery_title.png");
        var close = Path.Combine(temp.Path, "close.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "resources/ja/templates/stage/skip/ap_recovery_title.png"), title);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "resources/ja/templates/common/close.png"), close);
        var adb = new FakeAdbService(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Sortie/ap-recovery-mumu-ja.png")));
        var notices = new List<string>();
        var service = CreateService(adb, new TestLog(), new StageSkipOptions { LateResultRecoveryTimeoutMs = 0,
            SkipResultTemplatePath = ConfigService.ResolvePath("resources/templates/stage/skip/skip_result.png"),
            ApRecoveryPromptTemplatePath = title, ApRecoveryCloseTemplatePath = close,
            SkipResultTimeoutMs = 2000, ApRecoverySettleMs = 0, SkipDialogWaitMs = 0,
        }, apStopped: notices.Add);
        var result = await service.CompleteExecutedSkipAsync("serial", CreateLayout());
        Assert.False(result.Success);
        Assert.True(result.IsApInsufficient, result.Message);
        Assert.Single(notices);
        Assert.Equal(new[] { (960, 995), (770, 850) }, adb.Taps);
    }

    /// <summary>在合成回放帧中放置真实模板，保持生产识别的尺寸与像素。</summary>
    private static void PasteTemplate(byte[] pixels, string path, int x, int y)
    {
        var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
        for (var row = 0; row < image.Height; row++)
            Buffer.BlockCopy(image.Bgra, row * image.Width * 4, pixels, ((y + row) * 1920 + x) * 4, image.Width * 4);
    }

    [Fact]
    public async Task SkipCurrentAsync_ApRecovery_ClosesAndReturnsUnavailable()
    {
        using var temp = new TempDirectory();
        var recoveryPath = Path.Combine(temp.Path, "ap_recovery_close.png");
        File.WriteAllBytes(
            recoveryPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(110, 70, 150), 110, 70));

        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 110, 70, 910, 960, 150);
        var promptPath = ConfigService.ResolvePath("resources/templates/stage/skip/ap_recovery_title.png");
        PasteTemplate(screen, promptPath, 800, 200);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var notices = new List<string>();
        var service = CreateService(adb, new TestLog(), new StageSkipOptions
        {
            LateResultRecoveryTimeoutMs = 0,
            SkipDialogWaitMs = 0,
            SettleAfterOkMs = 0,
            SkipResultTimeoutMs = 2000,
            SkipResultThreshold = 0.9,
            ApRecoveryPromptTemplatePath = promptPath,
            ApRecoveryCloseTemplatePath = recoveryPath,
        }, apStopped: notices.Add);

        var result = await service.SkipCurrentAsync("serial", CreateLayout());

        Assert.False(result.Success);
        Assert.True(result.IsUnavailable);
        Assert.True(result.IsApInsufficient);
        Assert.Contains("AP 不足", Assert.Single(notices));
        Assert.Contains((1674, 710), adb.Taps);
        Assert.Contains((1150, 850), adb.Taps);
        Assert.Contains((965, 995), adb.Taps);
        Assert.Contains((770, 850), adb.Taps);
        Assert.DoesNotContain((1676, 988), adb.Taps);
    }

    /// <summary>真实继续文字回放：强敌可关闭获得单位展示，普通略过不启用；继续后仍必须出现结果页。</summary>
    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 3)]
    [InlineData(true, true, 6)]
    [InlineData(true, true, 7)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    public async Task SkipCurrentAsync_UnitAcquisition_RequiresNextThenResult(bool enabled, bool showResult, int showcaseCount)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        TapToNextOcrSamples.Paste(pixels, 1920, 400, 70, 760, 930);
        var showcase = ImageCodec.EncodePng(pixels, 1920, 1080);
        var resultFrame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "skip-result-narrative-mumu5556.png"));
        var blank = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 200), 1920, 1080);
        var adb = new FakeAdbService(showcase);
        // 六台回放在每次奖励出现前插入动画空帧，总时长超过旧的两段预算。
        var animationShownForTapCount = -1;
        adb.ScreenshotProvider = () => {
            if (showcaseCount == 6 && animationShownForTapCount != adb.Taps.Count)
            {
                animationShownForTapCount = adb.Taps.Count;
                return blank;
            }
            return adb.Taps.Count < 2 + showcaseCount ? showcase : showResult ? resultFrame : blank;
        };
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
        var service = new StageSkipService(automation, log, new StageSkipOptions { LateResultRecoveryTimeoutMs = 0,
            ContinueUnitAcquisition = enabled,
            SkipResultTemplatePath = ConfigService.ResolvePath("resources/templates/stage/skip/skip_result.png"),
            SkipResultTimeoutMs = 2500, SkipDialogWaitMs = 0, SettleAfterOkMs = 0 });
        var result = await service.SkipCurrentAsync("serial", CreateLayout());
        var expectedSuccess = enabled && showResult && showcaseCount <= 6;
        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(enabled ? Math.Min(showcaseCount, 6) + (expectedSuccess ? 3 : 2) : 2, adb.Taps.Count);
        if (enabled)
            foreach (var tap in adb.Taps.Skip(2).Take(showcaseCount)) Assert.InRange(tap.Item2, 930, 1000);
        Assert.Equal(expectedSuccess, adb.Taps.Contains((1676, 988)));
    }

    private static StageSkipService CreateService(
        FakeAdbService adb,
        TestLog log,
        StageSkipOptions? options = null, Action<string>? apStopped = null)
        => new(new AutomationService(adb, new TemplateMatcher(), log), log, options, apStopSignal: apStopped is null ? null : new ApStopSignal(apStopped, log));

    private static byte[] CreateBlankPng()
        => ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080);

    private static UiLayout CreateLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "stageSkip", X = 1594, Y = 678, Width = 160, Height = 64 },
            new() { Name = "skipDialogCancel", X = 710, Y = 820, Width = 120, Height = 60 },
            new() { Name = "skipDialogExecute", X = 1090, Y = 820, Width = 120, Height = 60 },
            new() { Name = "skipResultOk", X = 1640, Y = 960, Width = 72, Height = 56 },
            new() { Name = "skipResultTitle", X = 820, Y = 30, Width = 330, Height = 80 },
            new() { Name = "skipCountLimit", X = 1590, Y = 680, Width = 160, Height = 60 },
            new() { Name = "apRecoveryClose", X = 910, Y = 960, Width = 110, Height = 70 },
        },
    };

    /// <summary>测试用临时目录，自动清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-skip-tests-" + Guid.NewGuid().ToString("N"));

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

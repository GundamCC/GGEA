using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>用页面序列验证 Logo 导航的点击授权、禁点区域、重新定位和有限恢复。</summary>
public sealed class WorkLogoNavigatorTests
{
    /// <summary>真实作品下载弹窗遮挡背景时只下载一次，等待期间不恢复主页、不点击背景。</summary>
    [Fact]
    public async Task WorkDownload_WaitsForTargetWithoutRepeatingDownload()
    {
        var frame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/ja-work-download.png"));
        var prompt = Confirmed with { ScreenshotPng = frame };
        var test = new Scenario(prompt, prompt, Canvas(), Confirmed)
            { EnableDownload = true, AllowDownload = true, MaxAttempts = 2 };
        Assert.True((await test.Run()).Result.Success);
        Assert.Equal(0, test.HomeCalls);
        Assert.Equal(2, test.Adb.Taps.Count);
        Assert.InRange(test.Adb.Taps[0].X, 1100, 1200);
        Assert.InRange(test.Adb.Taps[0].Y, 800, 900);
        Assert.Equal((Select.CenterX, Select.CenterY), test.Adb.Taps[1]);
        Assert.Empty(test.Adb.Swipes);
    }

    /// <summary>拒绝下载时停止目标；允许下载但未进入目标时保持有界等待与用户取消。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WorkDownload_RespectsPermissionTimeoutAndCancellation(bool allowed, bool cancel)
    {
        var frame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/ja-work-download.png"));
        using var cts = new CancellationTokenSource();
        var test = new Scenario(Confirmed with { ScreenshotPng = frame })
        {
            EnableDownload = true, AllowDownload = allowed, ObservationCostMs = 310000,
            BeforeObservation = count => { if (cancel && count == 2) cts.Cancel(); }
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Run(cts.Token));
        else
        {
            var result = await test.Run();
            Assert.False(result.Result.Success);
            Assert.Contains(allowed ? "十分钟" : "未允许下载", result.Result.Message);
        }
        Assert.Single(test.Adb.Taps);
        Assert.Equal(0, test.HomeCalls);
        Assert.Empty(test.Adb.Swipes);
    }

    /// <summary>恢复主界面后关卡标题仍未出现时，立即停止，不再点击主要关卡入口。</summary>
    [Fact]
    public async Task Recovery_MenuTitleMissing_StopsBeforeMainStage()
    {
        var test = new Scenario(new WorkLogoObservation(false, null, false, false, []));
        test.Adb.ScreenshotProvider = () => ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080);

        var result = await test.Run();

        Assert.Equal(StageNavigationFailure.StageMenuNotRecognized, result.Result.Failure);
        Assert.Contains("请检查主界面是否卡顿", result.Result.Message);
        Assert.Equal(1, test.HomeCalls);
        Assert.Equal(new[] { (1000, 977) }, test.Adb.Taps);
        Assert.Empty(test.Adb.Swipes);
    }

    /// <summary>连续弹层累计超过原预算仍逐张处理；背景 Logo 或选择按钮不能绕过继续提示。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedUnlockPrompts_ContinueUntilLogoAppears(bool backgroundVisible)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        // 全局继续已改为 OCR，复用真实文字样本，不能再把旧模板拼图当作命中保证。
        TapToNextOcrSamples.Paste(pixels, 1920, 320, 80, 800, 950);
        var png = ImageCodec.EncodePng(pixels, 1920, 1080);
        var prompt = new WorkLogoObservation(false, null, false, false,
            backgroundVisible ? [Match(2)] : []) { ScreenshotPng = png };
        var finalPrompt = backgroundVisible ? Confirmed with { ScreenshotPng = png } : prompt;
        var test = new Scenario(prompt, prompt, finalPrompt, Canvas(Match()), Canvas(Match()), Confirmed)
            { ObservationWaitMs = 15000, WaitContinuationCooldown = true, MaxAttempts = 3,
                NavigationTimeoutMs = 8000, ObservationCostMs = 5000 };
        Assert.True((await test.Run()).Result.Success);
        Assert.Equal(0, test.HomeCalls);
        Assert.Equal(5, test.Adb.Taps.Count);
        Assert.All(test.Adb.Taps.Take(3), tap => Assert.InRange(tap.Y, 920, 1079));
        Assert.Equal(new[] { (1000, 500), (Select.CenterX, Select.CenterY) }, test.Adb.Taps.Skip(3));
        Assert.Empty(test.Adb.Swipes);
    }

    /// <summary>冷却中的重复提示和动画空帧均不能授权点击背景；用户取消立即终止连续观察。</summary>
    [Fact]
    public async Task ContinuationCooldownAndGap_DoNotClickBackgroundBeforeCancellation()
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        TapToNextOcrSamples.Paste(pixels, 1920, 320, 80, 800, 950);
        var prompt = Confirmed with { ScreenshotPng = ImageCodec.EncodePng(pixels, 1920, 1080) };
        using var cancel = new CancellationTokenSource();
        var test = new Scenario(prompt, prompt, Confirmed, prompt)
            { BeforeObservation = count => { if (count == 4) cancel.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Run(cancel.Token));
        Assert.Single(test.Adb.Taps);
        Assert.Empty(test.Adb.Swipes);
        Assert.Equal(0, test.HomeCalls);
    }

    /// <summary>两者皆无时保存判断所用原始帧，不重新截取其他页面冒充证据。</summary>
    [Fact]
    public async Task MissingLogoAndContinuation_SavesObservedFrame()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-logo-" + Guid.NewGuid());
        var png = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 37), 1920, 1080);
        var test = new Scenario(new WorkLogoObservation(false, null, false, false, []) { ScreenshotPng = png })
            { Evidence = new RecognitionEvidence(directory, new TestLog()), HomeSuccess = false };
        try
        {
            Assert.False((await test.Run()).Result.Success);
            using var zip = System.IO.Compression.ZipFile.OpenRead(Assert.Single(System.IO.Directory.GetFiles(directory, "*.zip")));
            using var input = zip.GetEntry("frame.png")!.Open();
            using var output = new System.IO.MemoryStream();
            input.CopyTo(output);
            Assert.Equal(png, output.ToArray());
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }

    /// <summary>入口加载的空白帧不是错误页面，不能立刻返回主界面打断随后到达的作品地图。</summary>
    [Fact]
    public async Task EntryLoading_DoesNotReturnHomeBeforeCanvasArrives()
    {
        var unknown = new WorkLogoObservation(false, null, false, false, []);
        var test = new Scenario(unknown, unknown, Canvas(Match()), Canvas(Match()), Confirmed)
            { HomeSuccess = false, ObservationWaitMs = 15000 };
        Assert.True((await test.Run()).Result.Success);
        Assert.Equal(0, test.HomeCalls);
        Assert.Equal(new[] { (1000, 500), (Select.CenterX, Select.CenterY) }, test.Adb.Taps);
    }

    /// <summary>地图标题先出现、作品稍后加载时，不能按三张空帧就重建页面。</summary>
    [Fact]
    public async Task CanvasLoading_WaitsForLogoInsteadOfCountingThreeFrames()
    {
        var test = new Scenario(Canvas(), Canvas(), Canvas(), Canvas(), Canvas(Match()), Canvas(Match()), Confirmed)
            { HomeSuccess = false, ObservationWaitMs = 15000 };
        Assert.True((await test.Run()).Result.Success);
        Assert.Equal(0, test.HomeCalls);
    }

    /// <summary>识图处理一帧耗时超过窗口时，仍需重新采集；旧帧不能证明加载一直没有结束。</summary>
    [Fact]
    public async Task SlowFirstObservation_RecapturesBeforeRecovery()
    {
        var test = new Scenario(new WorkLogoObservation(false, null, false, false, []),
            Canvas(Match()), Canvas(Match()), Confirmed)
            { HomeSuccess = false, ObservationWaitMs = 15000, ObservationCostMs = 16000 };
        Assert.True((await test.Run()).Result.Success);
        Assert.Equal(0, test.HomeCalls);
    }

    /// <summary>持续空白不能随每帧续期；恢复后给新的页面独立窗口，最终按恢复上限停止。</summary>
    [Fact]
    public async Task LoadingNeverCompletes_StopsWithSignalsAndBoundedRecoveries()
    {
        var test = new Scenario(new WorkLogoObservation(false, null, false, false, []))
            { ObservationWaitMs = 15000, ObservationCostMs = 8000 };
        var result = await test.Run();
        Assert.False(result.Result.Success);
        Assert.Contains("重试已达上限", result.Result.Message);
        Assert.Contains("可见作品数=0", result.Result.Message);
        Assert.Equal(1, test.HomeCalls);
        Assert.Equal(6, test.Observations);
        Assert.Equal(2, test.Adb.Taps.Count);
    }

    private static readonly HardStageEntry Target = new() { Id = 1, Work = "目标", Screenshot = "logo.webp" };
    private static readonly HardStageEntry Other = new() { Id = 2, Work = "锚点", Screenshot = "other.webp" };
    private static readonly TemplateMatchResult Select = new(new Roi(1500, 840, 300, 100), 0.95);
    private static WorkLogoObservation Confirmed => new(true, Select, false, true, []);
    private static WorkLogoMatch Match(int id = 1, int x = 1000, int y = 500, double score = 0.95)
        => new(id, score, new Roi(x - 50, y - 30, 100, 60));
    private static WorkLogoObservation Canvas(params WorkLogoMatch[] matches) => new(false, null, false, true, matches);

    /// <summary>同帧两项成立才点击选择，且使用按钮实际位置。</summary>
    [Fact]
    public async Task BothSignals_SelectsImmediately()
    {
        var test = new Scenario(Confirmed);
        Assert.True((await test.Run()).Result.Success);
        Assert.Equal(new[] { (Select.CenterX, Select.CenterY) }, test.Adb.Taps);
    }

    /// <summary>单独标题或按钮都不能借用上一帧的识别结果。</summary>
    [Fact]
    public async Task SignalsOnDifferentFrames_NeverSelects()
    {
        var test = new Scenario(new WorkLogoObservation(true, null, false, true, []), new(false, Select, false, true, []));
        Assert.False((await test.Run()).Result.Success);
        Assert.DoesNotContain((Select.CenterX, Select.CenterY), test.Adb.Taps);
    }

    /// <summary>标题缺失和标题存在但按钮缺失两条分支都重新查找当前目标 Logo。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOverviewSignal_ReacquiresTarget(bool titleVisible)
    {
        var state = new WorkLogoObservation(titleVisible, null, false, true, [Match()]);
        var test = new Scenario(state, state, Confirmed);
        Assert.True((await test.Run()).Result.Success);
        Assert.Equal(new[] { (1000, 500), (Select.CenterX, Select.CenterY) }, test.Adb.Taps);
    }

    /// <summary>拖动尚未停稳时不使用上一次的点击坐标。</summary>
    [Fact]
    public async Task MovingLogo_WaitsForStableCoordinates()
    {
        var test = new Scenario(Canvas(Match(x: 900)), Canvas(Match(x: 1100)), Canvas(Match(x: 1100)), Confirmed);
        Assert.True((await test.Run()).Result.Success);
        Assert.Equal((1100, 500), test.Adb.Taps[0]);
        Assert.DoesNotContain((900, 500), test.Adb.Taps);
    }

    /// <summary>作品点击被游戏吞掉后，重新获取两帧坐标再尝试。</summary>
    [Fact]
    public async Task DroppedTap_RechecksBeforeRetry()
    {
        var test = new Scenario(Canvas(Match()), Canvas(Match()), Canvas(Match()), Canvas(Match()), Confirmed);
        Assert.True((await test.Run()).Result.Success);
        Assert.Equal(2, test.Adb.Taps.Count(t => t == (1000, 500)));
    }

    /// <summary>目标在挑战或历史按钮区域时先拖动，不点击该区域。</summary>
    [Theory]
    [InlineData(172, 886)]
    [InlineData(558, 888)]
    public async Task ForbiddenArea_MovesTargetBeforeClick(int x, int y)
    {
        var test = new Scenario(Canvas(Match(x: x, y: y)), Canvas(Match(x: 1100, y: 850)),
            Canvas(Match(x: 1100, y: 850)), Confirmed);
        Assert.True((await test.Run()).Result.Success);
        Assert.True(test.Adb.Swipes.Single().X2 > test.Adb.Swipes.Single().X1);
        Assert.DoesNotContain((x, y), test.Adb.Taps);
        Assert.Equal((1100, 850), test.Adb.Taps[0]);
    }

    /// <summary>禁点矩形和底部导航栏边界不能产生一像素越界误点。</summary>
    [Theory]
    [InlineData(719, 830, false)]
    [InlineData(720, 830, true)]
    [InlineData(719, 829, true)]
    [InlineData(1000, 975, false)]
    [InlineData(10, 500, false)]
    public void ClickBoundary_IsExplicit(int x, int y, bool safe)
        => Assert.Equal(safe, WorkLogoNavigator.IsSafeClick(x, y));

    /// <summary>没有目标时使用最高置信度作品作为锚点，拖动后必须重新识别。</summary>
    [Fact]
    public async Task MissingTarget_UsesHighestConfidenceAnchor()
    {
        var test = new Scenario(Canvas(Match(2, x: 500, score: 0.97), Match(3, x: 1500, score: 0.85)),
            Canvas(Match()), Canvas(Match()), Confirmed);
        Assert.True((await test.Run()).Result.Success);
        // 目标画布 X=1000、最高分锚点 X=3000，因此手指应向右滑动。
        Assert.True(test.Adb.Swipes.Single().X2 > test.Adb.Swipes.Single().X1);
        Assert.DoesNotContain((500, 500), test.Adb.Taps);
    }

    /// <summary>复现 00 预计 X=476、配置 Y=770 被入口提示遮住；移动后必须重新识别两帧才点击。</summary>
    [Fact]
    public async Task CoveredPrediction_MovesBeforeReacquiringTarget()
    {
        var test = new Scenario(Canvas(Match(2, x: 1427, y: 528)),
            Canvas(Match(x: 1100, y: 830)), Canvas(Match(x: 1100, y: 830)), Confirmed)
            { TargetCanvasX = 4702, TargetCanvasY = 770, OtherCanvasX = 5653 };
        Assert.True((await test.Run()).Result.Success);
        var swipe = Assert.Single(test.Adb.Swipes);
        Assert.True(swipe.X2 > swipe.X1);
        Assert.Equal(new[] { (1100, 830), (Select.CenterX, Select.CenterY) }, test.Adb.Taps);
        Assert.Equal(0, test.HomeCalls);
    }

    /// <summary>遮挡避让手势无效时仍有限恢复，预测位置不能直接触发作品或选择点击。</summary>
    [Fact]
    public async Task CoveredPrediction_DroppedSwipeStopsWithoutTargetClick()
    {
        var test = new Scenario(Canvas(Match(2, x: 1427, y: 528)))
            { TargetCanvasX = 4702, TargetCanvasY = 770, OtherCanvasX = 5653 };
        Assert.Contains("重试已达上限", (await test.Run()).Result.Message);
        Assert.Equal(2, test.Adb.Swipes.Count);
        Assert.Equal(1, test.HomeCalls);
        Assert.Equal(new[] { (1000, 977), (430, 770) }, test.Adb.Taps);
    }

    /// <summary>拖动失效后不反复发送同一手势，每次恢复最多尝试一次，最后按恢复上限停止。</summary>
    [Fact]
    public async Task DroppedSwipes_StopAtBoundedAttempts()
    {
        var test = new Scenario(Canvas(Match(2, x: 500)));
        Assert.Contains("重试已达上限", (await test.Run()).Result.Message);
        Assert.Equal(2, test.Adb.Swipes.Count);
        Assert.Equal(1, test.HomeCalls);
        Assert.DoesNotContain((Select.CenterX, Select.CenterY), test.Adb.Taps);
    }

    /// <summary>复现初代预计 X=171→777：进入画面后等待目标出现，不再强求拖到 1100。</summary>
    [Fact]
    public async Task LeftBoundary_TargetAppearsAfterWaiting_DoesNotKeepSwiping()
    {
        var test = new Scenario(Canvas(Match(2, x: 802)), Canvas(Match(2, x: 1408)),
            Canvas(Match(2, x: 1408)), Canvas(Match(x: 777)), Canvas(Match(x: 777)), Confirmed)
            { ObservationWaitMs = 15000, TargetCanvasX = 769, OtherCanvasX = 1400 };
        Assert.True((await test.Run()).Result.Success);
        Assert.Single(test.Adb.Swipes);
        Assert.Equal(0, test.HomeCalls);
        Assert.Equal(new[] { (777, 500), (Select.CenterX, Select.CenterY) }, test.Adb.Taps);
    }

    /// <summary>目标在画面内持续漏识别时保留原始帧并有限恢复，不能盲点预测坐标。</summary>
    [Fact]
    public async Task VisiblePrediction_MissingTarget_SavesEvidenceAndStops()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-target-" + Guid.NewGuid());
        var png = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 37), 1920, 1080);
        var test = new Scenario(Canvas(Match(2, x: 1408)) with { ScreenshotPng = png })
            { Evidence = new RecognitionEvidence(directory, new TestLog()), ObservationWaitMs = 15000,
                ObservationCostMs = 8000, TargetCanvasX = 769, OtherCanvasX = 1400 };
        try
        {
            Assert.Contains("重试已达上限", (await test.Run()).Result.Message);
            Assert.Empty(test.Adb.Swipes);
            Assert.Equal(1, test.HomeCalls);
            Assert.Equal(6, test.Observations);
            Assert.DoesNotContain((777, 500), test.Adb.Taps);
            Assert.DoesNotContain((Select.CenterX, Select.CenterY), test.Adb.Taps);
            foreach (var file in System.IO.Directory.GetFiles(directory, "*.zip"))
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(file);
                using var input = zip.GetEntry("frame.png")!.Open();
                using var output = new System.IO.MemoryStream();
                input.CopyTo(output);
                Assert.Equal(png, output.ToArray());
            }
            Assert.NotEmpty(System.IO.Directory.GetFiles(directory, "*.zip"));
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }

    /// <summary>没有 Logo 且没有主要关卡标识时，走主界面、关卡、主要关卡恢复路径。</summary>
    [Fact]
    public async Task WrongPage_ReentersViaHome()
    {
        var test = new Scenario(new WorkLogoObservation(false, null, false, false, []), Canvas(Match()), Canvas(Match()), Confirmed);
        Assert.True((await test.Run()).Result.Success);
        Assert.Equal(1, test.HomeCalls);
        Assert.Equal((1000, 977), test.Adb.Taps[0]);
        Assert.Equal((430, 770), test.Adb.Taps[1]);
    }

    /// <summary>返回主界面失败时不继续点击关卡入口。</summary>
    [Fact]
    public async Task HomeRecoveryFailed_StopsWithoutEntryClicks()
    {
        var test = new Scenario(new WorkLogoObservation(false, null, false, false, [])) { HomeSuccess = false };
        Assert.False((await test.Run()).Result.Success);
        Assert.Empty(test.Adb.Taps);
    }

    /// <summary>主要关卡标识存在但始终没有 Logo 时，也不会无限等待。</summary>
    [Fact]
    public async Task MainStageWithoutLogos_EventuallyStops()
    {
        var test = new Scenario(Canvas());
        Assert.False((await test.Run()).Result.Success);
        Assert.Equal(1, test.HomeCalls);
        Assert.DoesNotContain((Select.CenterX, Select.CenterY), test.Adb.Taps);
    }

    /// <summary>进入错误作品详情后，先返回画布，不能直接按另一作品的选择按钮。</summary>
    [Fact]
    public async Task WrongWorkOverview_ReturnsThenFindsTarget()
    {
        var test = new Scenario(new WorkLogoObservation(false, Select, false, true, [Match(2)]), Canvas(Match()), Canvas(Match()), Confirmed);
        Assert.True((await test.Run()).Result.Success);
        Assert.Equal((130, 52), test.Adb.Taps[0]);
        Assert.Equal((1000, 500), test.Adb.Taps[1]);
    }

    /// <summary>暗态选择仅在目标标题同时成立时报告未解锁。</summary>
    [Fact]
    public async Task LockedTarget_DoesNotClickSelect()
    {
        var test = new Scenario(new WorkLogoObservation(true, null, true, true, []));
        Assert.Equal(StageNavigationFailure.WorkStageLocked, (await test.Run()).Result.Failure);
        Assert.Empty(test.Adb.Taps);
    }

    /// <summary>用户取消原样向上传递，不转为可恢复的普通失败。</summary>
    [Fact]
    public async Task Cancellation_StopsImmediately()
    {
        var test = new Scenario(Confirmed);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Run(cancel.Token));
        Assert.Empty(test.Adb.Taps);
    }

    /// <summary>缺少 Logo 导航依赖时，WebP 配置不能绕过二次验证。</summary>
    [Fact]
    public async Task WebpWithoutNavigator_FailsClosed()
    {
        var adb = new FakeAdbService([]);
        var log = new TestLog();
        var service = new StageService(new AutomationService(adb, new TemplateMatcher(), log), new FakeStageWorkRecognizer(), log);
        var result = await service.EnterWorkPageAsync("test", new(), new(),
            new() { Positions = [new() { Id = 1 }] }, [Target], 1);
        Assert.False(result.Success);
        Assert.Empty(adb.Taps);
    }

    /// <summary>集中构造无真实延时的确定性导航环境，记录全部副作用。</summary>
    private sealed class Scenario : IWorkLogoVision
    {
        private readonly Queue<WorkLogoObservation> _states;
        private WorkLogoObservation _last;
        // 页面状态仍由假视觉服务控制；自动化截图入口现在要求真正可解码的 PNG。
        public FakeAdbService Adb { get; } = new(TestImageFactory.CreateStageMenuPng());
        public int HomeCalls { get; private set; }
        public bool HomeSuccess { get; init; } = true;
        public int ObservationWaitMs { get; init; }
        public int ObservationCostMs { get; init; }
        public int Observations { get; private set; }
        /// <summary>在指定观察帧触发取消，避免用机器耗时决定测试行为。</summary>
        public Action<int>? BeforeObservation { get; init; }
        public int MaxAttempts { get; init; } = 12;
        public int NavigationTimeoutMs { get; init; } = 180000;
        public int TargetCanvasX { get; init; } = 1000;
        public int TargetCanvasY { get; init; } = 500;
        public int OtherCanvasX { get; init; } = 3000;
        public bool WaitContinuationCooldown { get; init; }
        public RecognitionEvidence? Evidence { get; init; }
        public bool EnableDownload { get; init; }
        public bool AllowDownload { get; init; }
        private readonly ObservationClock _clock = new();

        public Scenario(params WorkLogoObservation[] states)
        {
            _states = new(states);
            _last = states[^1];
        }

        public async Task<WorkLogoObservation> ObserveAsync(string serial, HardStageEntry target,
            IReadOnlyList<HardStageEntry> works, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Observations++;
            BeforeObservation?.Invoke(Observations);
            ct.ThrowIfCancellationRequested();
            _clock.Milliseconds += ObservationCostMs;
            if (_states.Count > 0) _last = _states.Dequeue();
            // 连续提示用例遵守真实五秒点击冷却；普通导航用例仍以可控时钟快速验证。
            if (WaitContinuationCooldown && Observations > 1 && _last.ScreenshotPng is not null)
                await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
            return _last;
        }

        public async Task<WorkLogoNavigationResult> Run(CancellationToken ct = default)
        {
            var log = new TestLog();
            using var automation = new AutomationService(Adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
            var navigator = new WorkLogoNavigator(automation, this,
                (_, _) => { HomeCalls++; return Task.FromResult(HomeSuccess); }, log,
                new() { PageSettleMs = 0, SwipeSettleMs = 0, WorkSelectPollIntervalMs = 0,
                    WorkSelectWaitTimeoutMs = 0, LogoNavigationMaxAttempts = MaxAttempts, LogoRecoveryMaxCount = 1,
                    LogoNavigationTimeoutMs = NavigationTimeoutMs,
                    LogoObservationWaitTimeoutMs = ObservationWaitMs }, _clock, Evidence,
                new StageMenuEntryService(automation, log, timeoutMs: 100, pollIntervalMs: 10),
                EnableDownload ? new DownloadDialogService(automation, new TemplateMatcher(), log,
                    Path.Combine(AppContext.BaseDirectory, "resources/ja/templates/dialog/download.png"),
                    Path.Combine(AppContext.BaseDirectory, "resources/ja/templates/dialog/cancel.png"), .85) : null,
                AllowDownload);
            return await navigator.OpenAsync("test",
                new() { Anchors = [new() { Name = "navStage", X = 1000, Y = 977 }] },
                new() { Anchors = [new() { Name = "mainStage", X = 430, Y = 770 }, new() { Name = "stageBack", X = 130, Y = 52 }] },
                new() { Positions = [new() { Id = 1, CanvasX = TargetCanvasX, CanvasY = TargetCanvasY }, new() { Id = 2, CanvasX = OtherCanvasX }, new() { Id = 3, CanvasX = 0 }] },
                [Target, Other], Target, ct);
        }
    }

    /// <summary>只推进观察时钟，不用真实睡眠测试超时边界。</summary>
    private sealed class ObservationClock : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}

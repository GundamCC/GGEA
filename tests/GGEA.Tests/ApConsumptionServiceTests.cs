using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>AP 消耗略过流程测试。</summary>
public class ApConsumptionServiceTests
{
    /// <summary>关卡菜单未确认时 AP 队列保留当前位置，向用户报告卡顿且不继续消费。</summary>
    [Fact]
    public async Task MenuTitleMissing_InterruptsWithHomeLagReminder()
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var log = new TestLog();
        var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var title = new FakeStageTitleRecognizer();
        var stage = new StageService(automation, new FakeStageWorkRecognizer(), log, CreateNavigationOptions(), title,
            menuEntry: new StageMenuEntryService(automation, log, timeoutMs: 100, pollIntervalMs: 10));
        var service = new ApConsumptionService(stage, new StageSkipService(automation, log), log);
        var chain = new ApConsumptionTaskChain("serial",
            [new() { WorkId = 7, Work = "F91", HardCount = 3, Hard1 = true }]);

        var result = await service.ExecuteAsync("serial", chain, CreateMainLayout(), CreateStageLayout(),
            CreatePositions((7, 1000, 400)), CreateWorks((7, "F91", 5, 3, "f91.png")));

        Assert.False(result.Success);
        Assert.True(result.IsInterrupted);
        Assert.Contains("请检查主界面是否卡顿", result.Message);
        Assert.Equal(result.Message, chain.InterruptionReason);
        Assert.Equal(0, chain.CurrentIndex);
        Assert.Equal(new[] { (1000, 950) }, adb.Taps);
        Assert.Equal(0, title.CallCount);
    }

    /// <summary>导航中断写入普通日志和最终摘要，保留当前作品供恢复且不继续消耗 AP。</summary>
    [Fact]
    public async Task NavigationInterrupted_PreservesReasonInSummaryAndLog()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var stage = CreateStageService(adb, new FakeStageWorkRecognizer(), new FakeStageTitleRecognizer(), log);
        var service = new ApConsumptionService(stage,
            new StageSkipService(new AutomationService(adb, new TemplateMatcher(), log), log), log);
        var chain = new ApConsumptionTaskChain("serial",
            [new() { WorkId = 7, Work = "F91", HardCount = 3, Hard1 = true }]);
        // 缺少导航依赖的 WebP 路径稳定返回中断，覆盖与重试耗尽相同的服务分支。
        var result = await service.ExecuteAsync("serial", chain, CreateMainLayout(), CreateStageLayout(),
            CreatePositions((7, 1000, 400)), CreateWorks((7, "F91", 5, 3, "f91.webp")));
        Assert.True(result.IsInterrupted);
        Assert.Equal(result.Message, chain.InterruptionReason);
        Assert.Contains($"已中断：{result.Message}", chain.GetExecutionSummary());
        Assert.Contains(log.Entries, entry => entry.Level == "WARN" && entry.Message.Contains(result.Message));
        Assert.Equal(0, chain.CurrentIndex);
        Assert.Empty(adb.Taps);
    }

    /// <summary>F91 当前 HARD3 时，应按倒序略过 HARD3、HARD1。</summary>
    [Fact]
    public async Task ExecuteAsync_SkipsHard3ThenHard1()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var workRecognizer = new FakeStageWorkRecognizer();
        workRecognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(
            CreateTitle(3),
            CreateTitle(3),
            CreateTitle(3),
            CreateTitle(1),
            CreateTitle(1));

        var stageService = CreateStageService(adb, workRecognizer, titleRecognizer, log);
        var skipService = new StageSkipService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log,
            new StageSkipOptions
            {
                SkipDialogWaitMs = 0,
                SettleAfterOkMs = 0,
                SkipResultTimeoutMs = 0,
            });
        var service = new ApConsumptionService(stageService, skipService, log);

        var plan = new[]
        {
            new ApConsumptionWorkItem
            {
                WorkId = 7,
                Work = "F91",
                HardCount = 3,
                Hard1 = true,
                Hard3 = true,
            },
        };

        var result = await service.ExecuteAsync(
            "serial",
            plan,
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((7, 1000, 400)),
            CreateWorks((7, "F91", 5, 3, "f91.png")));

        Assert.True(result.Success, result.Message);
        // HARD3 在槽位5，HARD1 在槽位3
        Assert.Contains((790, 730), adb.Taps);
        Assert.Contains((415, 780), adb.Taps);
        // 每次略过都包含：略过、执行、结果 OK
        Assert.Equal(2, adb.Taps.Count(tap => tap == (1674, 710)));
        Assert.Equal(2, adb.Taps.Count(tap => tap == (1150, 850)));
        Assert.Equal(2, adb.Taps.Count(tap => tap == (1676, 988)));
        Assert.Equal(5, titleRecognizer.CallCount);
    }

    /// <summary>当前 HARD1 时，只执行 HARD1，计划中的 HARD2/HARD3 不应执行。</summary>
    [Fact]
    public async Task ExecuteAsync_FiltersUnlockedTargets()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var workRecognizer = new FakeStageWorkRecognizer();
        workRecognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(CreateTitle(1), CreateTitle(1), CreateTitle(1));

        var stageService = CreateStageService(adb, workRecognizer, titleRecognizer, log);
        var skipService = new StageSkipService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log,
            new StageSkipOptions { SkipDialogWaitMs = 0, SettleAfterOkMs = 0 });
        var service = new ApConsumptionService(stageService, skipService, log);

        var plan = new[]
        {
            new ApConsumptionWorkItem
            {
                WorkId = 7,
                Work = "F91",
                HardCount = 3,
                Hard1 = true,
                Hard2 = true,
                Hard3 = true,
            },
        };

        var result = await service.ExecuteAsync(
            "serial",
            plan,
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((7, 1000, 400)),
            CreateWorks((7, "F91", 5, 3, "f91.png")));

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, adb.Taps.Count(tap => tap == (1674, 710)));
        Assert.Contains((415, 780), adb.Taps);
        Assert.Equal(3, titleRecognizer.CallCount);
    }

    /// <summary>选关重试仍失败时返回主要关卡画布、记录失败，并继续任务链中的下一作品。</summary>
    [Fact]
    public async Task ExecuteAsync_StageSelectionFails_ReturnsCanvasAndContinuesNextWork()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var workRecognizer = new FakeStageWorkRecognizer();
        workRecognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(
            CreateTitle(3),
            CreateTitle(3),
            CreateTitle(3),
            CreateTitle(1),
            CreateTitle(1));
        var nodeRecognizer = new FakeStageNodeRecognizer();
        nodeRecognizer.EnqueueResults(new StageNodeRecognition(
            790,
            730,
            WorkStageKind.Hard,
            3,
            WorkStageNodeState.Completed,
            new WorkStagePosition(WorkStageKind.Hard, 3, 8),
            "当前选中 HARD 3，状态已通过"));

        var stageService = CreateStageService(
            adb,
            workRecognizer,
            titleRecognizer,
            log,
            nodeRecognizer: nodeRecognizer);
        var skipService = new StageSkipService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log,
            new StageSkipOptions
            {
                SkipDialogWaitMs = 0,
                SettleAfterOkMs = 0,
                SkipResultTimeoutMs = 0,
            });
        var service = new ApConsumptionService(stageService, skipService, log);
        var chain = new ApConsumptionTaskChain(
            "serial",
            new[]
            {
                new ApConsumptionWorkItem { WorkId = 1, Work = "Failed", HardCount = 3, Hard1 = true },
                new ApConsumptionWorkItem { WorkId = 2, Work = "Next", HardCount = 1, Hard1 = true },
            });
        var positions = CreatePositions((1, 1000, 400), (2, 2500, 400));
        var works = CreateWorks(
            (1, "Failed", 5, 3, "failed.png"),
            (2, "Next", 3, 1, "next.png"));

        var result = await service.ExecuteAsync(
            "serial",
            chain,
            CreateMainLayout(),
            CreateStageLayout(),
            positions,
            works);

        Assert.False(result.Success);
        Assert.False(result.IsInterrupted);
        Assert.True(chain.IsCompleted);
        Assert.Equal(1, chain.SuccessCount);
        Assert.Equal(1, chain.FailureCount);
        Assert.Equal(2, adb.Taps.Count(tap => tap == (415, 780)));
        Assert.Contains((130, 50), adb.Taps);
        Assert.Equal(1, adb.Taps.Count(tap => tap == (1674, 710)));
        Assert.Equal(1, log.Entries.Count(entry => entry.Message.Contains("AP消耗 1/2")));
        Assert.Equal(1, log.Entries.Count(entry => entry.Message.Contains("AP消耗 2/2")));
        Assert.Contains("失败作品：Failed", result.Message);
    }

    /// <summary>略过不可用（次数上限）时，应继续处理列表中的下一个 HARD 任务而不是中止。</summary>
    [Fact]
    public async Task ExecuteAsync_SkipUnavailable_ContinuesNextTarget()
    {
        using var temp = new TempDirectory();
        var countLimitPath = Path.Combine(temp.Path, "count_limit.png");
        File.WriteAllBytes(
            countLimitPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 60, 180), 160, 60));

        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 160, 60, 1590, 680, 180);
        TestImageFactory.PasteStageMenuTitle(screen, 1920);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var log = new TestLog();
        var workRecognizer = new FakeStageWorkRecognizer();
        workRecognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(
            CreateTitle(3),
            CreateTitle(3),
            CreateTitle(1));

        var stageService = CreateStageService(adb, workRecognizer, titleRecognizer, log);
        var skipService = new StageSkipService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log,
            new StageSkipOptions
            {
                SkipCountLimitTemplatePath = countLimitPath,
                SkipCountLimitThreshold = 0.9,
            });
        var service = new ApConsumptionService(stageService, skipService, log);
        var plan = new[]
        {
            new ApConsumptionWorkItem
            {
                WorkId = 7,
                Work = "F91",
                HardCount = 3,
                Hard1 = true,
                Hard3 = true,
            },
        };

        var result = await service.ExecuteAsync(
            "serial",
            plan,
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((7, 1000, 400)),
            CreateWorks((7, "F91", 5, 3, "f91.png")));

        Assert.True(result.Success, result.Message);
        Assert.Contains((790, 730), adb.Taps);
        Assert.Contains((415, 780), adb.Taps);
        Assert.DoesNotContain((1674, 710), adb.Taps);
        Assert.Equal(3, titleRecognizer.CallCount);
    }

    /// <summary>未解锁作品命中暗态选择按钮后，应回到画布并继续处理队列中的下一部作品。</summary>
    [Fact]
    public async Task ExecuteAsync_WorkSelectClosed_ContinuesNextWork()
    {
        using var temp = new TempDirectory();
        var selectPath = Path.Combine(temp.Path, "select.png");
        var closedPath = Path.Combine(temp.Path, "select_close.png");
        File.WriteAllBytes(selectPath, CreateSolidPng(30, 20, 220));
        File.WriteAllBytes(closedPath, CreateSolidPng(30, 20, 180));

        var closedScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(closedScreen, 1920, 1200, 600, 360, 180, 80);
        TestImageFactory.PasteBlock(closedScreen, 1920, 30, 20, 1650, 880, 180);
        var enabledScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(enabledScreen, 1920, 1200, 600, 360, 180, 80);
        TestImageFactory.PasteBlock(enabledScreen, 1920, 30, 20, 1650, 880, 220);
        var blankPng = CreateStageMenuPng();
        var closedPng = ImageCodec.EncodePng(closedScreen, 1920, 1080);
        var adb = new FakeAdbService(blankPng);
        var enabledPng = ImageCodec.EncodePng(enabledScreen, 1920, 1080);
        // 每次作品点击前后均提供不同画面，验证详情页切换后才接受按钮状态。
        // 第二部作品先检查暗态模板、再检查可用模板，均使用可用按钮画面。
        adb.EnqueueScreenshots(blankPng, closedPng, closedPng, blankPng, enabledPng, enabledPng, enabledPng);

        var log = new TestLog();
        var workRecognizer = new FakeStageWorkRecognizer();
        workRecognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(CreateTitle(1), CreateTitle(1));
        var stageService = CreateStageService(
            adb,
            workRecognizer,
            titleRecognizer,
            log,
            CreateNavigationOptions(selectPath, closedPath));
        var skipService = new StageSkipService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log,
            new StageSkipOptions { SkipDialogWaitMs = 0, SettleAfterOkMs = 0, SkipResultTimeoutMs = 0 });
        var service = new ApConsumptionService(stageService, skipService, log);

        var result = await service.ExecuteAsync(
            "serial",
            new[]
            {
                new ApConsumptionWorkItem { WorkId = 1, Work = "Locked", HardCount = 1, Hard1 = true },
                new ApConsumptionWorkItem { WorkId = 2, Work = "Unlocked", HardCount = 1, Hard1 = true },
            },
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400), (2, 4000, 500)),
            CreateWorks((1, "Locked", 3, 1, "locked.png"), (2, "Unlocked", 3, 1, "unlocked.png")));

        Assert.True(result.Success, result.Message);
        Assert.Contains((130, 50), adb.Taps);
        Assert.Contains((1665, 890), adb.Taps);
        Assert.Equal(1, adb.Taps.Count(tap => tap == (1674, 710)));
    }

    /// <summary>当前作品停在未通过普通关时，应返回画布并继续后续作品，不能触发主界面恢复。</summary>
    [Fact]
    public async Task ExecuteAsync_UnconfirmedNormalStage_ReturnsCanvasAndContinuesNextWork()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var nodeRecognizer = new FakeStageNodeRecognizer();
        nodeRecognizer.EnqueueResults(
            new StageNodeRecognition(413, 838, WorkStageKind.Normal, 2, WorkStageNodeState.Incomplete,
                null, "当前选中 NORMAL 2，状态未通过"),
            new StageNodeRecognition(415, 780, WorkStageKind.Hard, 1, WorkStageNodeState.Completed,
                new WorkStagePosition(WorkStageKind.Hard, 1, 6), "当前选中 HARD 1，状态已通过"),
            new StageNodeRecognition(415, 780, WorkStageKind.Hard, 1, WorkStageNodeState.Completed,
                new WorkStagePosition(WorkStageKind.Hard, 1, 6), "当前选中 HARD 1，状态已通过"),
            new StageNodeRecognition(415, 780, WorkStageKind.Hard, 1, WorkStageNodeState.Completed,
                new WorkStagePosition(WorkStageKind.Hard, 1, 6), "当前选中 HARD 1，状态已通过"));
        var workRecognizer = new FakeStageWorkRecognizer();
        workRecognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var stageService = CreateStageService(
            adb,
            workRecognizer,
            new FakeStageTitleRecognizer(),
            log,
            CreateNavigationOptions(titleNodeFallbackWaitMs: 0),
            nodeRecognizer);
        var skipService = new StageSkipService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log,
            new StageSkipOptions { SkipDialogWaitMs = 0, SettleAfterOkMs = 0, SkipResultTimeoutMs = 0 });
        var service = new ApConsumptionService(stageService, skipService, log);

        var result = await service.ExecuteAsync(
            "serial",
            new[]
            {
                new ApConsumptionWorkItem { WorkId = 1, Work = "Normal", HardCount = 1, Hard1 = true },
                new ApConsumptionWorkItem { WorkId = 2, Work = "Next", HardCount = 1, Hard1 = true },
            },
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400), (2, 2500, 400)),
            CreateWorks((1, "Normal", 5, 1, "normal.png"), (2, "Next", 5, 1, "next.png")));

        Assert.True(result.Success, result.Message);
        Assert.Contains((130, 50), adb.Taps);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("当前关卡无法确认"));
    }

    /// <summary>只有 1 个 HARD 关卡时，应直接略过，不点击五槽位。</summary>
    [Fact]
    public async Task ExecuteAsync_SingleHardWork_SkipsWithoutSlotTap()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var workRecognizer = new FakeStageWorkRecognizer();
        workRecognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(CreateTitle(1), CreateTitle(1));

        var stageService = CreateStageService(adb, workRecognizer, titleRecognizer, log);
        var skipService = new StageSkipService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log,
            new StageSkipOptions
            {
                SkipDialogWaitMs = 0,
                SettleAfterOkMs = 0,
                SkipResultTimeoutMs = 0,
            });
        var service = new ApConsumptionService(stageService, skipService, log);
        var plan = new[]
        {
            new ApConsumptionWorkItem
            {
                WorkId = 7,
                Work = "Hathaway",
                HardCount = 1,
                Hard1 = true,
            },
        };

        var result = await service.ExecuteAsync(
            "serial",
            plan,
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((7, 1000, 400)),
            CreateWorks((7, "Hathaway", 3, 1, "hathaway.png")));

        Assert.True(result.Success, result.Message);
        Assert.Contains((1674, 710), adb.Taps);
        Assert.DoesNotContain((415, 780), adb.Taps);
    }

    /// <summary>AP 不足时应关闭回复AP弹窗、取消略过窗口并结束消耗AP任务。</summary>
    [Fact]
    public async Task ExecuteAsync_ApInsufficient_StopsAndReturnsFailure()
    {
        using var temp = new TempDirectory();
        var recoveryPath = Path.Combine(temp.Path, "ap_recovery_close.png");
        File.WriteAllBytes(
            recoveryPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(110, 70, 150), 110, 70));

        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 110, 70, 910, 960, 150);
        var promptPath = ConfigService.ResolvePath("resources/templates/stage/skip/ap_recovery_title.png");
        var prompt = ImageCodec.DecodePng(File.ReadAllBytes(promptPath));
        // 回放同帧 AP 文字证据，关闭按钮本身不再代表 AP 不足。
        for (var row = 0; row < prompt.Height; row++)
            Buffer.BlockCopy(prompt.Bgra, row * prompt.Width * 4, screen, ((200 + row) * 1920 + 800) * 4, prompt.Width * 4);
        TestImageFactory.PasteStageMenuTitle(screen, 1920);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var log = new TestLog();
        var workRecognizer = new FakeStageWorkRecognizer();
        workRecognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(CreateTitle(3), CreateTitle(3));

        var stageService = CreateStageService(adb, workRecognizer, titleRecognizer, log);
        var skipService = new StageSkipService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log,
            new StageSkipOptions
            {
                SkipDialogWaitMs = 0,
                ApRecoverySettleMs = 0,
                SkipResultTimeoutMs = 2000,
                SkipResultThreshold = 0.9,
                ApRecoveryPromptTemplatePath = promptPath,
                ApRecoveryCloseTemplatePath = recoveryPath,
            });
        var service = new ApConsumptionService(stageService, skipService, log);
        var plan = new[]
        {
            new ApConsumptionWorkItem
            {
                WorkId = 7,
                Work = "F91",
                HardCount = 3,
                Hard1 = true,
                Hard3 = true,
            },
        };

        var result = await service.ExecuteAsync(
            "serial",
            plan,
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((7, 1000, 400)),
            CreateWorks((7, "F91", 5, 3, "f91.png")));

        Assert.False(result.Success);
        Assert.Contains("AP不足", result.Message);
        Assert.True(result.IsInterrupted);
        Assert.Contains((790, 730), adb.Taps);
        Assert.Contains((770, 850), adb.Taps);
        Assert.DoesNotContain((415, 780), adb.Taps);
        Assert.Equal(2, titleRecognizer.CallCount);
    }

    private static StageService CreateStageService(
        FakeAdbService adb,
        FakeStageWorkRecognizer workRecognizer,
        FakeStageTitleRecognizer titleRecognizer,
        TestLog log,
        StageNavigationOptions? navigationOptions = null,
        IStageNodeRecognizer? nodeRecognizer = null)
    {
        var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var options = navigationOptions ?? CreateNavigationOptions();
        return new StageService(automation, workRecognizer, log, options, titleRecognizer, nodeRecognizer);
    }

    /// <summary>创建关闭真实等待时间的导航参数，可选开启右下角选择按钮识别。</summary>
    private static StageNavigationOptions CreateNavigationOptions(
        string workSelectTemplatePath = "",
        string workSelectClosedTemplatePath = "",
        int titleNodeFallbackWaitMs = 100)
        => new()
        {
            SwipeSettleMs = 0,
            PageSettleMs = 0,
            TitleWaitTimeoutMs = 100,
            TitlePollIntervalMs = 10,
            TitleNodeFallbackWaitMs = titleNodeFallbackWaitMs,
            ResetSearchMaxSwipes = 2,
            ResetBoundarySwipeStartX = 120,
            ResetBoundarySwipeEndX = 1800,
            ResetAnchorThreshold = 0.85,
            SwipeCanvasDelta = 1500,
            WorkSelectTemplatePath = workSelectTemplatePath,
            WorkSelectClosedTemplatePath = workSelectClosedTemplatePath,
            WorkSelectThreshold = 0.9,
            WorkSelectWaitTimeoutMs = 100,
            WorkSelectPollIntervalMs = 10,
        };

    private static StageTitleMatch CreateTitle(int hardIndex)
        => new(hardIndex, 0.95, 1450, 130, new Roi(1440, 120, 300, 40));

    /// <summary>提供已确认的关卡菜单初始画面，不跳过实际的入口识别。</summary>
    private static byte[] CreateStageMenuPng()
        => TestImageFactory.CreateStageMenuPng();

    /// <summary>创建单色模板，用于验证右下角选择按钮状态识别。</summary>
    private static byte[] CreateSolidPng(int width, int height, byte value)
        => ImageCodec.EncodePng(TestImageFactory.CreateBgra(width, height, value), width, height);

    private static UiLayout CreateMainLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "navStage", X = 900, Y = 900, Width = 200, Height = 100 },
        },
    };

    private static UiLayout CreateStageLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "mainStage", X = 100, Y = 700, Width = 600, Height = 100 },
            new() { Name = "workSelect", X = 1600, Y = 850, Width = 150, Height = 100 },
            new() { Name = "stageBack", X = 80, Y = 20, Width = 100, Height = 60 },
            new() { Name = "stageTitle", X = 1000, Y = 70, Width = 900, Height = 120 },
            new() { Name = "stageSkip", X = 1594, Y = 678, Width = 160, Height = 64 },
            new() { Name = "skipDialogCancel", X = 710, Y = 820, Width = 120, Height = 60 },
            new() { Name = "skipDialogExecute", X = 1090, Y = 820, Width = 120, Height = 60 },
            new() { Name = "skipResultOk", X = 1640, Y = 960, Width = 72, Height = 56 },
            new() { Name = "skipCountLimit", X = 1590, Y = 680, Width = 160, Height = 60 },
            new() { Name = "apRecoveryClose", X = 910, Y = 960, Width = 110, Height = 70 },
            new() { Name = "fiveHardStage1", X = 18, Y = 756, Width = 40, Height = 40 },
            new() { Name = "stageSlot1", X = 0, Y = 740, Width = 110, Height = 75 },
            new() { Name = "stageSlot2", X = 150, Y = 680, Width = 140, Height = 85 },
            new() { Name = "stageSlot3", X = 340, Y = 740, Width = 150, Height = 80 },
            new() { Name = "stageSlot4", X = 530, Y = 680, Width = 140, Height = 85 },
            new() { Name = "stageSlot5", X = 760, Y = 690, Width = 60, Height = 80 },
        },
    };

    private static StageWorkPositionsData CreatePositions(params (int Id, int X, int Y)[] values)
    {
        var data = new StageWorkPositionsData();
        foreach (var value in values)
        {
            data.Positions.Add(new StageWorkPosition
            {
                Id = value.Id,
                CanvasX = value.X,
                CanvasY = value.Y,
                Verified = true,
            });
        }

        return data;
    }

    private static IReadOnlyList<HardStageEntry> CreateWorks(
        params (int Id, string Name, int NormalCount, int HardCount, string Screenshot)[] values)
        => values.Select(value => new HardStageEntry
        {
            Id = value.Id,
            Work = value.Name,
            NormalCount = value.NormalCount,
            HardCount = value.HardCount,
            Screenshot = value.Screenshot,
        }).ToList();

    /// <summary>测试用临时目录，自动清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-ap-tests-" + Guid.NewGuid().ToString("N"));

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

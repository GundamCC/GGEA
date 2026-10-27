using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;
using System.IO;

namespace GGEA.Tests;

/// <summary>主要关卡左端绝对定位与居中作品相对切换测试。</summary>
public class StageServiceTests
{
    /// <summary>菜单标题未出现时必须清空旧上下文，不点击主要关卡或作品，也不滑动画布。</summary>
    [Fact]
    public async Task EnterWorkPageAsync_MenuTitleMissing_StopsBeforeMainStage()
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var log = new TestLog();
        var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var recognizer = new FakeStageWorkRecognizer();
        var service = new StageService(automation, recognizer, log, CreateOptions(),
            menuEntry: new StageMenuEntryService(automation, log, timeoutMs: 100, pollIntervalMs: 10));
        service.Context.SetWorkStageList("serial", StageMapMode.Main, workId: 1, hardIndex: 1);

        var result = await service.EnterWorkPageAsync("serial", CreateMainLayout(), CreateStageLayout(),
            CreatePositions((1, 1000, 400)), CreateWorks((1, "A", "work_a.png")), workId: 1);

        Assert.False(result.Success);
        Assert.Equal(StageNavigationFailure.StageMenuNotRecognized, result.Failure);
        Assert.Contains("请检查主界面是否卡顿", result.Message);
        Assert.Equal(new[] { (1000, 950) }, adb.Taps);
        Assert.Empty(adb.Swipes);
        Assert.Equal(0, recognizer.FindCallCount);
        Assert.False(service.Context.IsReliable);
    }

    /// <summary>首次进入必须先复位到左端，再从绝对原点移动到目标作品。</summary>
    [Fact]
    public async Task EnterWorkPageAsync_AlwaysResetsLeftThenUsesAbsolutePosition()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var recognizer = new FakeStageWorkRecognizer();
        recognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var service = CreateService(adb, recognizer, log);

        var result = await service.EnterWorkPageAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400), (2, 4000, 500)),
            CreateWorks((1, "A", "work_a.png"), (2, "B", "work_b.png")),
            workId: 2);

        Assert.True(result.Success, result.Message);
        Assert.Equal(5, adb.Swipes.Count);
        Assert.All(adb.Swipes.Take(2), swipe => Assert.True(swipe.X1 < swipe.X2));
        Assert.Equal((120, 540, 1800, 540, 600), adb.Swipes[2]);
        Assert.All(adb.Swipes.Skip(3), swipe => Assert.True(swipe.X1 > swipe.X2));
        Assert.Equal(3, recognizer.FindCallCount);
        Assert.Contains((1000, 500), adb.Taps);
        Assert.Equal(StagePageState.WorkStageList, service.Context.PageState);
        Assert.Equal(2, service.Context.CurrentWorkId);
    }

    /// <summary>详情页退出后应利用当前作品居中状态相对移动，不能再次执行左端复位。</summary>
    [Fact]
    public async Task SwitchWorkAsync_CenteredCurrentWork_UsesRelativeDistanceWithoutReset()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var recognizer = new FakeStageWorkRecognizer();
        var service = CreateService(adb, recognizer, log);
        service.Context.SetWorkStageList("serial", StageMapMode.Main, workId: 1);

        var result = await service.SwitchWorkAsync(
            "serial",
            CreateStageLayout(),
            CreatePositions((1, 1000, 400), (2, 4200, 500)),
            CreateWorks((1, "A", "work_a.png"), (2, "B", "work_b.png")),
            targetWorkId: 2);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, adb.Swipes.Count);
        Assert.All(adb.Swipes, swipe => Assert.True(swipe.X1 > swipe.X2));
        Assert.Equal(1, adb.ScreenshotCount); // 新增关卡入口的全局遮罩检查，作品定位仍无需截图复位。
        Assert.Equal(0, recognizer.FindCallCount);
        Assert.Equal(1, adb.Taps.Count(tap => tap == (130, 50)));
        Assert.Contains((1160, 500), adb.Taps);
        Assert.Equal(2, service.Context.CurrentWorkId);
        Assert.Equal(StagePageState.WorkStageList, service.Context.PageState);
    }

    /// <summary>进入作品后未识别到 HARD STAGE 标题时，应返回并退出当前作品。</summary>
    [Fact]
    public async Task EnterWorkPageAsync_TitleNotRecognized_ExitsWork()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var recognizer = new FakeStageWorkRecognizer();
        recognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults((StageTitleMatch?)null);
        var service = CreateService(adb, recognizer, log, titleRecognizer);

        var result = await service.EnterWorkPageAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400)),
            CreateWorks((1, "A", "work_a.png")),
            workId: 1);

        Assert.False(result.Success);
        Assert.Equal(StageNavigationFailure.TitleNotRecognized, result.Failure);
        Assert.True(titleRecognizer.CallCount >= 1);
        Assert.Contains((130, 50), adb.Taps);
        Assert.Equal(StagePageState.MainStageCanvas, service.Context.PageState);
    }

    /// <summary>进入作品后识别到 HARD STAGE 标题时，应记录当前 HARD 编号。</summary>
    [Fact]
    public async Task EnterWorkPageAsync_TitleRecognized_StoresHardIndex()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var recognizer = new FakeStageWorkRecognizer();
        recognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(new StageTitleMatch(
            3,
            0.95,
            1450,
            130,
            new Roi(1440, 120, 300, 40)));
        var service = CreateService(adb, recognizer, log, titleRecognizer);

        var result = await service.EnterWorkPageAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400)),
            CreateWorks((1, "A", "work_a.png")),
            workId: 1);

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, service.Context.CurrentHardIndex);
        Assert.Equal(StagePageState.WorkStageList, service.Context.PageState);
    }

    /// <summary>网络较慢时标题第一次未出现，应在轮询后识别成功。</summary>
    [Fact]
    public async Task EnterWorkPageAsync_TitleAppearsAfterRetry_Succeeds()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var recognizer = new FakeStageWorkRecognizer();
        recognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(null, CreateTitle(3));
        var service = CreateService(adb, recognizer, log, titleRecognizer);

        var result = await service.EnterWorkPageAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400)),
            CreateWorks((1, "A", "work_a.png")),
            workId: 1);

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, service.Context.CurrentHardIndex);
        Assert.Equal(2, titleRecognizer.CallCount);
    }

    /// <summary>标题未命中时，应使用左侧黄色光圈选中的 HARD 节点继续流程。</summary>
    [Theory]
    [InlineData(WorkStageNodeState.Completed)]
    [InlineData(WorkStageNodeState.Cleared)]
    public async Task EnterWorkPageAsync_TitleMissing_UsesSelectedLeftNode(WorkStageNodeState passedState)
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var workRecognizer = new FakeStageWorkRecognizer();
        workRecognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(null, null);
        var nodeRecognizer = new FakeStageNodeRecognizer();
        nodeRecognizer.EnqueueResults(new StageNodeRecognition(
            415,
            780,
            WorkStageKind.Hard,
            2,
            passedState,
            new WorkStagePosition(WorkStageKind.Hard, 2, 7),
            "当前选中 HARD 2，状态已通过"));
        var service = CreateService(
            adb,
            workRecognizer,
            new TestLog(),
            titleRecognizer,
            CreateOptions(titleNodeFallbackWaitMs: 20),
            nodeRecognizer);

        var result = await service.EnterWorkPageAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400)),
            CreateWorks((1, "A", "work_a.png")),
            workId: 1);

        Assert.True(result.Success, result.Message);
        Assert.True(titleRecognizer.CallCount >= 1);
        Assert.Equal(1, nodeRecognizer.CallCount);
        Assert.Equal(2, service.Context.CurrentHardIndex);
    }

    /// <summary>标题失败时，未通过节点绝不能作为可略过 HARD 的替代确认。</summary>
    [Fact]
    public async Task EnterWorkPageAsync_TitleMissing_IncompleteNodeStopsInsteadOfTreatingItAsHard()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var workRecognizer = new FakeStageWorkRecognizer();
        workRecognizer.EnqueueFindResults(
            null,
            null,
            new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(null, null);
        var nodeRecognizer = new FakeStageNodeRecognizer();
        nodeRecognizer.EnqueueResults(new StageNodeRecognition(
            415,
            780,
            WorkStageKind.Hard,
            2,
            WorkStageNodeState.Incomplete,
            new WorkStagePosition(WorkStageKind.Hard, 2, 7),
            "当前选中 HARD 2，状态未通过"));
        var service = CreateService(
            adb,
            workRecognizer,
            new TestLog(),
            titleRecognizer,
            CreateOptions(titleNodeFallbackWaitMs: 20),
            nodeRecognizer);

        var result = await service.EnterWorkPageAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400)),
            CreateWorks((1, "A", "work_a.png")),
            workId: 1);

        Assert.False(result.Success);
        Assert.Equal(StageNavigationFailure.TitleNotRecognized, result.Failure);
        Assert.Equal(StagePageState.MainStageCanvas, service.Context.PageState);
        Assert.Contains((130, 50), adb.Taps);
    }

    /// <summary>作品详情页的可用“选择”按钮必须先识别，再按识别中心点击。</summary>
    [Fact]
    public async Task EnterWorkPageAsync_WorkSelectEnabled_ClicksMatchedButton()
    {
        using var temp = new TempDirectory();
        var selectPath = Path.Combine(temp.Path, "select.png");
        File.WriteAllBytes(selectPath, CreateSolidPng(30, 20, 220));

        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 1200, 600, 360, 180, 80);
        // 放在旧 workSelect 窄框外、用户指定的右下角识别区内，验证不会回退到旧 ROI。
        TestImageFactory.PasteBlock(screen, 1920, 30, 20, 1500, 820, 220);
        var enabledPng = ImageCodec.EncodePng(screen, 1920, 1080);
        var adb = new FakeAdbService(CreateStageMenuPng());
        // 作品节点点击前仍是画布；点击后才进入详情页并显示选择按钮。
        adb.EnqueueScreenshots(TestImageFactory.CreateStageMenuPng(), enabledPng, enabledPng);
        var recognizer = new FakeStageWorkRecognizer();
        recognizer.EnqueueFindResults(null, null, new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var service = CreateService(
            adb,
            recognizer,
            new TestLog(),
            options: CreateOptions(workSelectTemplatePath: selectPath, pageSettleMs: 1));

        var result = await service.EnterWorkPageAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400)),
            CreateWorks((1, "A", "work_a.png")),
            workId: 1);

        Assert.True(result.Success, result.Message);
        Assert.Contains((1515, 830), adb.Taps);
        Assert.DoesNotContain((1675, 900), adb.Taps);
    }

    /// <summary>暗态“选择”按钮返回后，必须保留定位前的真实视口，而不是伪造作品居中状态。</summary>
    [Theory]
    [InlineData(2565, 1500, 1500)]
    [InlineData(1984, 1850, 725)]
    public async Task EnterWorkPageAsync_WorkSelectClosed_ReturnsLockedAndBackToCanvas(
        int targetX, int swipeDelta, int expectedOffset)
    {
        using var temp = new TempDirectory();
        var closedPath = Path.Combine(temp.Path, "select_close.png");
        File.WriteAllBytes(closedPath, CreateSolidPng(30, 20, 180));

        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 1200, 600, 360, 180, 80);
        TestImageFactory.PasteBlock(screen, 1920, 30, 20, 1650, 880, 180);
        var closedPng = ImageCodec.EncodePng(screen, 1920, 1080);
        var adb = new FakeAdbService(CreateStageMenuPng());
        adb.EnqueueScreenshots(TestImageFactory.CreateStageMenuPng(), closedPng, closedPng);
        var recognizer = new FakeStageWorkRecognizer();
        recognizer.EnqueueFindResults(null, null, new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var service = CreateService(
            adb,
            recognizer,
            new TestLog(),
            options: CreateOptions(workSelectClosedTemplatePath: closedPath, swipeCanvasDelta: swipeDelta));

        var result = await service.EnterWorkPageAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((3, targetX, 400)),
            CreateWorks((3, "A", "work_a.png")),
            workId: 3);

        Assert.False(result.Success);
        Assert.Equal(StageNavigationFailure.WorkStageLocked, result.Failure);
        Assert.DoesNotContain((1675, 900), adb.Taps);
        Assert.Contains((130, 50), adb.Taps);
        Assert.Equal(StagePageState.MainStageCanvas, service.Context.PageState);
        Assert.Equal(3, service.Context.CurrentWorkId);
        // 完整滑动及带补偿的半屏滑动，都必须保存与实际点击点一致的规划视口。
        Assert.Equal(expectedOffset, service.Context.ViewOffsetX);
        Assert.Contains((targetX - expectedOffset, 400), adb.Taps);
    }

    /// <summary>作品节点漏点且画面未变化时，右下角孤立暗态匹配不得触发返回操作。</summary>
    [Fact]
    public async Task EnterWorkPageAsync_NoOverviewTransition_DoesNotTreatClosedTemplateAsLocked()
    {
        using var temp = new TempDirectory();
        var closedPath = Path.Combine(temp.Path, "select_close.png");
        File.WriteAllBytes(closedPath, CreateSolidPng(30, 20, 180));

        var unchanged = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(unchanged, 1920, 30, 20, 1650, 880, 180);
        TestImageFactory.PasteStageMenuTitle(unchanged, 1920);
        var adb = new FakeAdbService(ImageCodec.EncodePng(unchanged, 1920, 1080));
        var recognizer = new FakeStageWorkRecognizer();
        recognizer.EnqueueFindResults(null, null, new StageWorkMatch("mobile_suit_gundam", 0.92, 734, 445, true));
        var service = CreateService(
            adb,
            recognizer,
            new TestLog(),
            options: CreateOptions(workSelectClosedTemplatePath: closedPath));

        var result = await service.EnterWorkPageAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400)),
            CreateWorks((1, "A", "work_a.png")),
            workId: 1);

        Assert.False(result.Success);
        Assert.Equal(StageNavigationFailure.InvalidContext, result.Failure);
        Assert.DoesNotContain((130, 50), adb.Taps);
        Assert.Equal(StagePageState.Unknown, service.Context.PageState);
    }

    /// <summary>只有 1 个 HARD 关卡时，选中流程不应使用五槽位点击。</summary>
    [Fact]
    public async Task SelectHardStageAsync_SingleHardWork_NoSlotTap()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var service = CreateService(adb, new FakeStageWorkRecognizer(), new TestLog());
        service.Context.SetWorkStageList("serial", StageMapMode.Main, workId: 7, hardIndex: 1);
        var works = new List<HardStageEntry>
        {
            new()
            {
                Id = 7,
                Work = "Hathaway",
                NormalCount = 3,
                HardCount = 1,
                Screenshot = "hathaway.png",
            },
        };

        var result = await service.SelectHardStageAsync(
            "serial",
            CreateStageLayout(),
            works,
            workId: 7,
            targetHardIndex: 1);

        Assert.True(result.Success, result.Message);
        Assert.Empty(adb.Taps);
        Assert.Equal(1, service.Context.CurrentHardIndex);
    }

    /// <summary>切换槽位后必须重新识别标题，防止点击未生效时继续略过错误关卡。</summary>
    [Fact]
    public async Task SelectHardStageAsync_ReconfirmsTitleAfterTap()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(CreateTitle(3));
        var service = CreateService(
            adb,
            new FakeStageWorkRecognizer(),
            new TestLog(),
            titleRecognizer);
        service.Context.SetWorkStageList("serial", StageMapMode.Main, workId: 7, hardIndex: 3);
        var works = new List<HardStageEntry>
        {
            new()
            {
                Id = 7,
                Work = "F91",
                NormalCount = 5,
                HardCount = 3,
                Screenshot = "f91.png",
            },
        };

        var result = await service.SelectHardStageAsync(
            "serial",
            CreateStageLayout(),
            works,
            workId: 7,
            targetHardIndex: 3);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, titleRecognizer.CallCount);
        Assert.Contains((790, 730), adb.Taps);
    }

    /// <summary>标题未变化时应识别左侧选中节点，并按识别结果重新点击目标 HARD。</summary>
    [Fact]
    public async Task SelectHardStageAsync_SameTitle_RecognizesLeftNodeAndRetries()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(CreateTitle(3), CreateTitle(1));
        var nodeRecognizer = new FakeStageNodeRecognizer();
        nodeRecognizer.EnqueueResults(new StageNodeRecognition(
            790,
            730,
            WorkStageKind.Hard,
            3,
            WorkStageNodeState.Completed,
            new WorkStagePosition(WorkStageKind.Hard, 3, 8),
            "当前选中 HARD 3，状态已通过"));
        var service = CreateService(
            adb,
            new FakeStageWorkRecognizer(),
            new TestLog(),
            titleRecognizer,
            nodeRecognizer: nodeRecognizer);
        service.Context.SetWorkStageList("serial", StageMapMode.Main, workId: 7, hardIndex: 3);

        var result = await service.SelectHardStageAsync(
            "serial",
            CreateStageLayout(),
            new[]
            {
                new HardStageEntry
                {
                    Id = 7,
                    Work = "F91",
                    NormalCount = 5,
                    HardCount = 3,
                    Screenshot = "f91.png",
                },
            },
            workId: 7,
            targetHardIndex: 1);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, adb.Taps.Count(tap => tap == (415, 780)));
        Assert.Equal(1, nodeRecognizer.CallCount);
        Assert.Equal(1, service.Context.CurrentHardIndex);
    }

    /// <summary>左侧节点定位后的第二次点击仍失败时，应保留作品关卡页上下文供调用方返回画布。</summary>
    [Fact]
    public async Task SelectHardStageAsync_RetryStillFails_KeepsReliableWorkContext()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(CreateTitle(3), CreateTitle(3));
        var nodeRecognizer = new FakeStageNodeRecognizer();
        nodeRecognizer.EnqueueResults(new StageNodeRecognition(
            790,
            730,
            WorkStageKind.Hard,
            3,
            WorkStageNodeState.Completed,
            new WorkStagePosition(WorkStageKind.Hard, 3, 8),
            "当前选中 HARD 3，状态已通过"));
        var service = CreateService(
            adb,
            new FakeStageWorkRecognizer(),
            new TestLog(),
            titleRecognizer,
            nodeRecognizer: nodeRecognizer);
        service.Context.SetWorkStageList("serial", StageMapMode.Main, workId: 7, hardIndex: 3);

        var result = await service.SelectHardStageAsync(
            "serial",
            CreateStageLayout(),
            new[]
            {
                new HardStageEntry
                {
                    Id = 7,
                    Work = "F91",
                    NormalCount = 5,
                    HardCount = 3,
                    Screenshot = "f91.png",
                },
            },
            workId: 7,
            targetHardIndex: 1);

        Assert.False(result.Success);
        Assert.Equal(2, adb.Taps.Count(tap => tap == (415, 780)));
        Assert.Equal(StagePageState.WorkStageList, service.Context.PageState);
        Assert.True(service.Context.IsReliable);
        Assert.Equal(3, service.Context.CurrentHardIndex);
    }

    /// <summary>五个 HARD 的作品切换到 HARD 1 时，应点击左边缘专用位置。</summary>
    [Fact]
    public async Task SelectHardStageAsync_FiveHardWork_Hard1UsesDedicatedAnchor()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var titleRecognizer = new FakeStageTitleRecognizer();
        titleRecognizer.EnqueueResults(CreateTitle(1));
        var service = CreateService(
            adb,
            new FakeStageWorkRecognizer(),
            new TestLog(),
            titleRecognizer);
        service.Context.SetWorkStageList("serial", StageMapMode.Main, workId: 7, hardIndex: 2);
        var works = new List<HardStageEntry>
        {
            new()
            {
                Id = 7,
                Work = "Mobile Suit Gundam 00",
                NormalCount = 24,
                HardCount = 5,
                Screenshot = "mobile_suit_gundam_00.png",
            },
        };

        var result = await service.SelectHardStageAsync(
            "serial",
            CreateStageLayout(),
            works,
            workId: 7,
            targetHardIndex: 1);

        Assert.True(result.Success, result.Message);
        Assert.Equal([(38, 776)], adb.Taps);
        Assert.Equal(1, service.Context.CurrentHardIndex);
    }

    [Fact]
    public async Task EnterWorkPageAsync_ChallengeMode_ReturnsUnsupportedWithoutInput()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var service = CreateService(adb, new FakeStageWorkRecognizer(), new TestLog());

        var result = await service.EnterWorkPageAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400)),
            CreateWorks((1, "A", "work_a.png")),
            workId: 1,
            mode: StageMapMode.Challenge);

        Assert.False(result.Success);
        Assert.Equal(StageNavigationFailure.UnsupportedMode, result.Failure);
        Assert.Empty(adb.Taps);
        Assert.Empty(adb.Swipes);
    }

    /// <summary>目标在当前作品左侧时，相对距离应转换为向右拖动画布。</summary>
    [Fact]
    public async Task SwitchWorkAsync_TargetToLeft_UsesReverseRelativeMovement()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var service = CreateService(adb, new FakeStageWorkRecognizer(), log);
        service.Context.SetWorkStageList("serial", StageMapMode.Main, workId: 2);

        var result = await service.SwitchWorkAsync(
            "serial",
            CreateStageLayout(),
            CreatePositions((1, 1000, 400), (2, 4000, 500)),
            CreateWorks((1, "A", "work_a.png"), (2, "B", "work_b.png")),
            targetWorkId: 1);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, adb.Swipes.Count);
        Assert.All(adb.Swipes, swipe => Assert.True(swipe.X1 < swipe.X2));
        Assert.Equal(1, adb.Taps.Count(tap => tap == (130, 50)));
        Assert.Contains((960, 400), adb.Taps);
    }

    /// <summary>整屏滑动跨过安全点击区时，服务必须执行固定的半屏 ADB 手势。</summary>
    [Fact]
    public async Task SwitchWorkAsync_SkippedSafeRange_UsesHalfFinalSwipe()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var log = new TestLog();
        var service = CreateService(
            adb,
            new FakeStageWorkRecognizer(),
            log,
            options: CreateOptions(swipeCanvasDelta: 1850));
        // 复现未解锁的 SEED DESTINY 返回后，画布仍停留在 2578px 的实际状态。
        service.Context.SetWorkCanvas("serial", StageMapMode.Main, workId: 15, viewOffsetX: 2578);

        var result = await service.SwitchWorkAsync(
            "serial",
            CreateStageLayout(),
            CreatePositions((15, 2896, 190), (16, 4562, 770)),
            CreateWorks((15, "SEED DESTINY", "seed_destiny.png"), (16, "00", "00.png")),
            targetWorkId: 16);

        Assert.True(result.Success, result.Message);
        Assert.Single(adb.Swipes);
        Assert.Equal((1500, 540, 960, 540, 300), adb.Swipes[0]);
        Assert.Contains((1259, 770), adb.Taps);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("执行末次半屏滑动"));
    }

    /// <summary>右缘作品节点即使显示在屏幕内，也必须先移入安全区，不能直接点击 (1888,525)。</summary>
    [Fact]
    public async Task SwitchWorkAsync_RightEdgeWork_RepositionsBeforeTapping()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var service = CreateService(
            adb,
            new FakeStageWorkRecognizer(),
            new TestLog(),
            options: CreateOptions(swipeCanvasDelta: 1770));
        // 当前作品退出后居中，目标作品在右缘 X=1888、Y=525。
        service.Context.SetWorkStageList("serial", StageMapMode.Main, workId: 6);

        var result = await service.SwitchWorkAsync(
            "serial",
            CreateStageLayout(),
            CreatePositions((6, 6460, 190), (7, 7388, 525)),
            CreateWorks((6, "00", "00.png"), (7, "F91", "f91.png")),
            targetWorkId: 7);

        Assert.True(result.Success, result.Message);
        Assert.Contains((1500, 540, 960, 540, 300), adb.Swipes);
        Assert.Contains((1203, 525), adb.Taps);
        Assert.DoesNotContain((1888, 525), adb.Taps);
    }

    /// <summary>超过最大滑动次数仍找不到初代高达锚点时，必须停止且不能点击作品。</summary>
    [Fact]
    public async Task EnterWorkPageAsync_AnchorNotFound_StopsBeforeWorkTap()
    {
        var adb = new FakeAdbService(CreateStageMenuPng());
        var recognizer = new FakeStageWorkRecognizer();
        recognizer.EnqueueFindResults(null, null, null);
        var service = CreateService(adb, recognizer, new TestLog());

        var result = await service.EnterWorkPageAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreatePositions((1, 1000, 400)),
            CreateWorks((1, "A", "work_a.png")),
            workId: 1);

        Assert.False(result.Success);
        Assert.Equal(StageNavigationFailure.InitialLocationFailed, result.Failure);
        Assert.Equal(3, recognizer.FindCallCount);
        Assert.Equal(2, adb.Swipes.Count);
        Assert.DoesNotContain((1000, 400), adb.Taps);
    }

    /// <summary>创建测试服务，并关闭真实等待时间。</summary>
    private static StageService CreateService(
        FakeAdbService adb,
        IStageWorkRecognizer recognizer,
        TestLog log,
        IStageTitleRecognizer? titleRecognizer = null,
        StageNavigationOptions? options = null,
        IStageNodeRecognizer? nodeRecognizer = null)
    {
        var automation = new AutomationService(adb, new TemplateMatcher(), log);
        options ??= CreateOptions();
        return new StageService(automation, recognizer, log, options, titleRecognizer, nodeRecognizer);
    }

    /// <summary>创建关闭真实等待时间的导航参数，可按测试需要加入按钮模板。</summary>
    private static StageNavigationOptions CreateOptions(
        string workSelectTemplatePath = "",
        string workSelectClosedTemplatePath = "",
        int pageSettleMs = 0,
        int titleNodeFallbackWaitMs = 100,
        int swipeCanvasDelta = 1500)
        => new()
        {
            SwipeSettleMs = 0,
            WorkTapSettleMs = 0,
            SelectTapExtraDelayMs = 0,
            PageSettleMs = pageSettleMs,
            TitleWaitTimeoutMs = 100,
            TitlePollIntervalMs = 10,
            TitleNodeFallbackWaitMs = titleNodeFallbackWaitMs,
            ResetSearchMaxSwipes = 2,
            ResetBoundarySwipeStartX = 120,
            ResetBoundarySwipeEndX = 1800,
            ResetAnchorThreshold = 0.85,
            SwipeCanvasDelta = swipeCanvasDelta,
            WorkSelectTemplatePath = workSelectTemplatePath,
            WorkSelectClosedTemplatePath = workSelectClosedTemplatePath,
            WorkSelectThreshold = 0.9,
            WorkSelectWaitTimeoutMs = 100,
            WorkSelectPollIntervalMs = 10,
        };

    /// <summary>初始入口画面包含真实标题，其他关卡信号仍由测试替身控制。</summary>
    private static byte[] CreateStageMenuPng()
        => TestImageFactory.CreateStageMenuPng();

    /// <summary>创建单色模板，用于验证右下角 ROI 内的模板识别结果。</summary>
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
            new() { Name = "skipDialogExecute", X = 1090, Y = 820, Width = 120, Height = 60 },
            new() { Name = "skipResultOk", X = 1640, Y = 960, Width = 72, Height = 56 },
            new() { Name = "skipResultTitle", X = 820, Y = 30, Width = 330, Height = 80 },
            new() { Name = "skipCountLimit", X = 1590, Y = 680, Width = 160, Height = 60 },
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
        params (int Id, string Name, string Screenshot)[] values)
        => values.Select(value => new HardStageEntry
        {
            Id = value.Id,
            Work = value.Name,
            Screenshot = value.Screenshot,
            HardCount = 5,
        }).ToList();

    private static StageTitleMatch CreateTitle(int hardIndex)
        => new(hardIndex, 0.95, 1450, 130, new Roi(1440, 120, 300, 40));

    /// <summary>测试用临时目录，自动清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-stage-tests-" + Guid.NewGuid().ToString("N"));

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

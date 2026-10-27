using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>Logo 导航与原 HARD 确认、连续作品上下文的集成边界。</summary>
public sealed class StageLogoIntegrationTests
{
    /// <summary>Logo 导航入口同样必须确认关卡标题，不能将卡住的主界面交给作品恢复流程。</summary>
    [Fact]
    public async Task MenuTitleMissing_DoesNotStartLogoNavigation()
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var log = new TestLog();
        var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var navigator = new Navigator();
        var service = new StageService(automation, new FakeStageWorkRecognizer(), log, Options(),
            logoNavigator: navigator,
            menuEntry: new StageMenuEntryService(automation, log, timeoutMs: 100, pollIntervalMs: 10));
        var (main, stage, positions, works) = Data();

        var result = await service.EnterWorkPageAsync("test", main, stage, positions, works, 1);

        Assert.Equal(StageNavigationFailure.StageMenuNotRecognized, result.Failure);
        Assert.Contains("请检查主界面是否卡顿", result.Message);
        Assert.Single(adb.Taps);
        Assert.Empty(navigator.Requests);
        Assert.False(service.Context.IsReliable);
    }

    /// <summary>即使从未解锁详情直接开始、没有画布偏移，队列也只访问每部作品一次并正常结束。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(321.0)]
    public async Task LockedWorks_AdvanceQueueWithoutRetrying(double? offset)
    {
        var adb = new FakeAdbService(TestImageFactory.CreateStageMenuPng());
        var log = new TestLog();
        var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var navigator = new Navigator
        {
            Response = new(StageNavigationResult.Fail(StageNavigationFailure.WorkStageLocked, "locked"), offset)
        };
        var stageService = new StageService(automation, new FakeStageWorkRecognizer(), log,
            Options(), logoNavigator: navigator);
        var service = new ApConsumptionService(stageService, new StageSkipService(automation, log), log);
        var (main, stage, positions, works) = Data();
        var result = await service.ExecuteAsync("test",
            works.Select(work => new ApConsumptionWorkItem
            {
                WorkId = work.Id, Work = $"Work {work.Id}", HardCount = 3, Hard1 = true
            }).ToArray(), main, stage, positions, works);

        Assert.True(result.Success, result.Message);
        Assert.Contains("未解锁已跳过（2）：Work 1、Work 2", result.Message);
        Assert.False(result.IsInterrupted);
        Assert.Equal(new[] { 1, 2 }, navigator.Requests);
        Assert.Equal(4, adb.Taps.Count); // 两次入口、每个未解锁详情各返回一次，无选择或略过点击。
        Assert.Empty(adb.Swipes);
        Assert.Equal(offset.HasValue, stageService.Context.IsReliable);
    }

    /// <summary>身份验证后才调用 HARD 识别，切换时不再按旧视口推算点击。</summary>
    [Fact]
    public async Task EnterAndSwitch_UseLogoNavigatorAndConfirmHard()
    {
        var adb = new FakeAdbService(TestImageFactory.CreateStageMenuPng());
        var log = new TestLog();
        var title = new FakeStageTitleRecognizer();
        title.EnqueueResults(new StageTitleMatch(1, .95, 1400, 100, new(1000, 60, 800, 100)),
            new StageTitleMatch(2, .95, 1400, 100, new(1000, 60, 800, 100)));
        var navigator = new Navigator();
        var service = new StageService(new AutomationService(adb, new TemplateMatcher(), log),
            new FakeStageWorkRecognizer(), log, Options(), title, logoNavigator: navigator);
        var (main, stage, positions, works) = Data();
        Assert.True((await service.EnterWorkPageAsync("test", main, stage, positions, works, 1)).Success);
        Assert.Equal(1, service.Context.CurrentHardIndex);
        Assert.True((await service.SwitchWorkAsync("test", stage, positions, works, 2)).Success);
        Assert.Equal(new[] { 1, 2 }, navigator.Requests);
        Assert.Equal(2, service.Context.CurrentWorkId);
        Assert.Equal(2, service.Context.CurrentHardIndex);
        Assert.Equal(3, adb.Taps.Count); // 两次入口点击和一次返回，作品点击由 Logo 导航拥有。
        Assert.Empty(adb.Swipes);
    }

    /// <summary>已确认未解锁的作品返回后，只保留识图测得的镜头偏移。</summary>
    [Theory]
    [InlineData(321.0, true)]
    [InlineData(null, false)]
    public async Task LockedWork_PreservesOnlyMeasuredOffset(double? offset, bool reliable)
    {
        var log = new TestLog(); var adb = new FakeAdbService(TestImageFactory.CreateStageMenuPng());
        var navigator = new Navigator { Response = new(StageNavigationResult.Fail(StageNavigationFailure.WorkStageLocked, "locked"), offset) };
        var service = new StageService(new AutomationService(adb, new TemplateMatcher(), log),
            new FakeStageWorkRecognizer(), log, Options(), logoNavigator: navigator);
        var (main, stage, positions, works) = Data();
        var result = await service.EnterWorkPageAsync("test", main, stage, positions, works, 1);
        Assert.Equal(StageNavigationFailure.WorkStageLocked, result.Failure);
        Assert.Equal(reliable, service.Context.IsReliable);
        if (reliable) Assert.Equal(offset, service.Context.ViewOffsetX);
    }

    /// <summary>导航恢复失败必须使上下文失效，不能继续消耗 AP。</summary>
    [Fact]
    public async Task NavigationFailure_InvalidatesContext()
    {
        var log = new TestLog(); var adb = new FakeAdbService(TestImageFactory.CreateStageMenuPng());
        var navigator = new Navigator { Response = new(StageNavigationResult.Fail(StageNavigationFailure.InvalidContext, "failed"), null) };
        var service = new StageService(new AutomationService(adb, new TemplateMatcher(), log),
            new FakeStageWorkRecognizer(), log, Options(), logoNavigator: navigator);
        var (main, stage, positions, works) = Data();
        Assert.False((await service.EnterWorkPageAsync("test", main, stage, positions, works, 1)).Success);
        Assert.False(service.Context.IsReliable);
    }

    /// <summary>测试中关闭动画等待。</summary>
    private static StageNavigationOptions Options() => new() { PageSettleMs = 0 };

    /// <summary>两个 WebP 作品和入口布局。</summary>
    private static (UiLayout Main, UiLayout Stage, StageWorkPositionsData Positions, HardStageEntry[] Works) Data()
        => (new() { Anchors = [new() { Name = "navStage" }] },
            new() { Anchors = [new() { Name = "mainStage" }, new() { Name = "stageBack" }, new() { Name = "stageTitle" }] },
            new() { Positions = [new() { Id = 1, CanvasX = 1000 }, new() { Id = 2, CanvasX = 3000 }] },
            [new() { Id = 1, Screenshot = "first.webp", HardCount = 3 }, new() { Id = 2, Screenshot = "second.webp", HardCount = 3 }]);

    /// <summary>返回预设的作品身份确认结果，记录服务边界调用。</summary>
    private sealed class Navigator : IWorkLogoNavigator
    {
        public List<int> Requests { get; } = [];
        public WorkLogoNavigationResult Response { get; init; } = new(StageNavigationResult.Ok("confirmed"), 100);
        public Task<WorkLogoNavigationResult> OpenAsync(string serial, UiLayout mainLayout, UiLayout stageLayout,
            StageWorkPositionsData positions, IReadOnlyList<HardStageEntry> works, HardStageEntry target, CancellationToken ct)
        {
            Requests.Add(target.Id);
            return Task.FromResult(Response);
        }
    }
}

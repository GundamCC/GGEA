using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>作品首次进入遮罩的实机回归；节点/标题必须在全局继续点击后的新帧上识别。</summary>
public sealed class StageContinuationTests
{
    /// <summary>真实系列初始部队提示在底部通用区域内读取完整文字，无须新增模板。</summary>
    [Fact]
    public void RealSeriesReward_BottomOcrRecognizesCompleteText()
    {
        var match = new OnnxTapToNextRecognizer(TapToNextOcrSamples.SharedOcr, new TestLog())
            .Recognize(ImageCodec.DecodePng(RewardFrame()));
        Assert.NotNull(match);
        Assert.True(match.Confidence >= .99);
        Assert.Equal((959, 957), (match.CenterX, match.CenterY));
    }

    /// <summary>普通推图入口关闭一个或连续两个奖励遮罩，再读取节点；关闭遮罩本身不能确认关卡。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ProgressEntry_ClosesRewardsBeforeReadingNode(int rewards)
    {
        using var scene = new Scene(rewards);
        var node = await scene.EnterProgress();
        Assert.Equal(WorkStageKind.Normal, node.Kind);
        Assert.Equal(1, scene.NodeCalls);
        Assert.Equal(rewards, scene.ContinueClicks);
        Assert.Contains(scene.Log.Entries, entry => entry.Message.Contains("全局检测到 TAP TO NEXT"));
    }

    /// <summary>通关后继续同一作品直接读原选关页，不能点击关卡菜单、作品 Logo 或返回主界面。</summary>
    [Fact]
    public async Task ProgressContinuation_ReadsCurrentPageWithoutNavigation()
    {
        using var scene = new Scene(0);
        await scene.EnterProgress();
        scene.Adb.Taps.Clear();

        var node = await scene.Stage.ReadCurrentWorkForProgressAsync("test", scene.Work, default);

        Assert.Equal(WorkStageKind.Normal, node.Kind);
        Assert.Equal(2, scene.NodeCalls);
        Assert.Equal(1, scene.WorkOpenCalls);
        Assert.Empty(scene.Adb.Taps);
        Assert.False(scene.Stage.Context.IsReliable);
    }

    /// <summary>原页无法读取编号时保留未知，不点击或重新导航；真实出击入口仍负责阻止未知页面上的操作。</summary>
    [Fact]
    public async Task ProgressContinuation_UnreadableNodeDoesNotStopOrNavigate()
    {
        using var scene = new Scene(0) { UnknownNode = true };

        var node = await scene.Stage.ReadCurrentWorkForProgressAsync("test", scene.Work, default);
        Assert.Equal(StageNodeRecognition.Unidentified, node);
        Assert.Equal(1, scene.NodeCalls);
        Assert.Equal(0, scene.WorkOpenCalls);
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>HARD 标题与标题失败后的节点回退同样经过全局继续入口，仍只接受已通过 HARD。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardEntry_ClosesRewardBeforeTitleOrNodeFallback(bool fallback)
    {
        using var scene = new Scene(1, fallback: fallback);
        var result = await scene.Stage.EnterWorkPageAsync("test", scene.Main, scene.Layout,
            scene.Positions, [scene.Work], scene.Work.Id);
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, scene.ContinueClicks);
        Assert.Equal(1, scene.Stage.Context.CurrentHardIndex);
        Assert.Equal(fallback ? 1 : 0, scene.NodeCalls);
        Assert.Equal(fallback ? 0 : 1, scene.TitleCalls);
    }

    /// <summary>作品入口及遮罩处理完成后，缺失编号只返回未知资料，不伪造可信节点或推图完成。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task UnknownNode_AfterEntryReturnsUnknownObservation(int rewards)
    {
        using var scene = new Scene(rewards) { UnknownNode = true };
        var node = await scene.EnterProgress();
        Assert.Equal(StageNodeRecognition.Unidentified, node);
        Assert.Null(node.Position);
        Assert.Equal(WorkStageNodeState.Unknown, node.State);
        Assert.Equal(1, scene.NodeCalls);
        Assert.Equal(rewards, scene.ContinueClicks);
        Assert.False(scene.Stage.Context.IsReliable);
    }

    /// <summary>点击未能关闭遮罩时只等待有限预算，不把遮罩下的节点交给出击流程。</summary>
    [Fact]
    public async Task PersistentReward_StopsWithoutReadingCoveredNode()
    {
        using var scene = new Scene(int.MaxValue, waitMs: 100);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scene.EnterProgress());
        Assert.Contains("关卡继续遮罩未在限定时间内关闭", error.Message);
        Assert.Equal(1, scene.ContinueClicks);
        Assert.Equal(0, scene.NodeCalls);
        Assert.False(scene.Stage.Context.IsReliable);
    }

    /// <summary>用户取消立即阻止继续；已确认异常后的网络诊断也不能点击弹窗上的继续文字。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkErrorOrCancellation_PreventsContinuation(bool cancel)
    {
        using var scene = new Scene(1, networkError: !cancel);
        using var cts = new CancellationTokenSource();
        if (cancel) scene.Adb.BeforeScreenshotAsync = (_, _) =>
        {
            if (scene.Opened) cts.Cancel();
            return Task.CompletedTask;
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.EnterProgress(cts.Token));
        else await Assert.ThrowsAsync<ConnectionErrorException>(() => scene.CheckAbnormalFrame());
        Assert.Equal(0, scene.ContinueClicks);
        Assert.Equal(0, scene.NodeCalls);
    }

    /// <summary>原样拼接实机底部裁剪；关卡业务数据不进入截图或外部服务。</summary>
    private static byte[] RewardFrame()
    {
        var bottom = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "series-first-units-tap-mumu5556.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Buffer.BlockCopy(bottom.Bgra, 0, pixels, 880 * 1920 * 4, bottom.Bgra.Length);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }

    /// <summary>截图由真实继续点击驱动，识别替身只负责验证调用发生在无遮罩的新帧之后。</summary>
    private sealed class Scene : IDisposable, IWorkLogoNavigator, IStageNodeRecognizer, IStageTitleRecognizer
    {
        private readonly string _errorPath = Path.Combine(Path.GetTempPath(), $"ggea-stage-error-{Guid.NewGuid():N}.png");
        private readonly AutomationService _automation;
        private bool _lastFrameCovered;
        private readonly bool _fallback;
        public FakeAdbService Adb { get; }
        public TestLog Log { get; } = new();
        public StageService Stage { get; }
        public bool Opened { get; private set; }
        public bool UnknownNode { get; init; }
        public int NodeCalls { get; private set; }
        public int WorkOpenCalls { get; private set; }
        public int TitleCalls { get; private set; }
        public int ContinueClicks => Adb.Taps.Count(tap => tap == (959, 957));
        public HardStageEntry Work { get; } = new() { Id = 1, NormalCount = 3, HardCount = 3, Screenshot = "work.webp" };
        public UiLayout Main { get; } = new() { Anchors = [new() { Name = "navStage" }] };
        public UiLayout Layout { get; } = new()
        {
            Anchors = [new() { Name = "mainStage" }, new() { Name = "stageTitle" }, new() { Name = "stageBack" }]
        };
        public StageWorkPositionsData Positions { get; } = new() { Positions = [new() { Id = 1, CanvasX = 1000 }] };

        /// <summary>模型借用公共测试会话，真实识别与生产阈值不替换；等待只缩短本测试的流程预算。</summary>
        public Scene(int rewards, bool fallback = false, int waitMs = 5000, bool networkError = false)
        {
            _fallback = fallback;
            var reward = RewardFrame();
            var menu = TestImageFactory.CreateStageMenuPng();
            var plain = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080);
            var error = TestImageFactory.CreateBgra(200, 70, 210);
            File.WriteAllBytes(_errorPath, ImageCodec.EncodePng(error, 200, 70));
            if (networkError)
            {
                var frame = ImageCodec.DecodePng(reward);
                TestImageFactory.PasteBlock(frame.Bgra, 1920, 200, 70, 860, 205, 210);
                reward = ImageCodec.EncodePng(frame.Bgra, 1920, 1080);
            }
            Adb = new FakeAdbService(menu);
            Adb.ScreenshotProvider = () =>
            {
                _lastFrameCovered = Opened && ContinueClicks < rewards;
                return !Opened ? menu : _lastFrameCovered ? reward : plain;
            };
            var matcher = new TemplateMatcher();
            _automation = new AutomationService(Adb, matcher, Log,
                connectionErrorDetector: new ConnectionErrorDetector(Adb, matcher, Log, _errorPath),
                textOcr: TapToNextOcrSamples.SharedOcr);
            Stage = new StageService(_automation, new FakeStageWorkRecognizer(), Log,
                new StageNavigationOptions { PageSettleMs = 0, TitleWaitTimeoutMs = waitMs,
                    TitleNodeFallbackWaitMs = fallback ? 0 : waitMs, TitlePollIntervalMs = 10 },
                titleRecognizer: this, nodeRecognizer: this, logoNavigator: this);
        }

        /// <summary>通过真实导航公开入口执行普通推图，不能绕开作品身份确认。</summary>
        public Task<StageNodeRecognition> EnterProgress(CancellationToken ct = default)
            => Stage.EnterWorkForProgressAsync("test", Main, Layout, Positions, [Work], Work, ct);

        /// <summary>模拟任务已报告异常后的诊断入口，不调用正常作品导航或继续点击。</summary>
        public async Task CheckAbnormalFrame()
        {
            Opened = true;
            var screen = await _automation.CaptureScreenAsync("test");
            await _automation.GlobalScreens.CheckAbnormalScreenAsync(screen);
        }

        /// <summary>替身只确认作品身份并激活奖励遮罩，不发送继续点击。</summary>
        public Task<WorkLogoNavigationResult> OpenAsync(string serial, UiLayout mainLayout, UiLayout stageLayout,
            StageWorkPositionsData positions, IReadOnlyList<HardStageEntry> works, HardStageEntry target, CancellationToken ct)
        {
            WorkOpenCalls++;
            Opened = true;
            return Task.FromResult(new WorkLogoNavigationResult(StageNavigationResult.Ok("confirmed"), 100));
        }

        /// <summary>遮罩未消失时调用即令测试失败；未知结果仍由任务层判断是否停止。</summary>
        public Task<StageNodeRecognition?> RecognizeSelectedAsync(string serial, HardStageEntry work, CancellationToken ct = default)
        {
            NodeCalls++;
            Assert.False(_lastFrameCovered);
            StageNodeRecognition? node = UnknownNode ? null : new(200, 750,
                _fallback ? WorkStageKind.Hard : WorkStageKind.Normal, 1,
                _fallback ? WorkStageNodeState.Completed : WorkStageNodeState.Incomplete,
                new WorkStagePosition(_fallback ? WorkStageKind.Hard : WorkStageKind.Normal, 1, 1), "confirmed");
            return Task.FromResult(node);
        }

        /// <summary>HARD 标题返回之前也必须已经观察到无遮罩新帧。</summary>
        public Task<StageTitleMatch?> RecognizeAsync(string serial, Roi titleRoi, double threshold = .85,
            CancellationToken ct = default)
        {
            TitleCalls++;
            Assert.False(_lastFrameCovered);
            return Task.FromResult<StageTitleMatch?>(new(1, .99, 1500, 100, new(1000, 70, 800, 100)));
        }

        /// <summary>测试拥有自动化会话和临时素材，借用的模型不由场景释放。</summary>
        public void Dispose() { _automation.Dispose(); File.Delete(_errorPath); }
    }
}

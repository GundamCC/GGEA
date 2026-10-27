using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>自动推图未自动选下一关的实机回归；NEXT 点击和点击后的选中确认共同授权出击。</summary>
public sealed class StageNextNodeTests
{
    private static readonly string Templates = Path.Combine(AppContext.BaseDirectory, "resources/common/templates/stage/node_states");
    private static readonly HardStageEntry Work = new() { Id = 20, Work = "Mobile Suit Victory Gundam", NormalCount = 20, HardCount = 3 };

    /// <summary>上一场已结算而编号未变化时，即使状态没有 COMPLETE，也必须两帧定位 NEXT 并复核后才允许下一关。</summary>
    [Theory]
    [InlineData(WorkStageNodeState.Cleared)]
    [InlineData(WorkStageNodeState.Incomplete)]
    public async Task AfterBattle_SameHard_AdvancesAndVerifies(WorkStageNodeState state)
    {
        using var scene = new Scene("after-battle", afterBattleState: state);
        var selected = await scene.Advance();
        Assert.Equal(new WorkStagePosition(WorkStageKind.Hard, 2, 22), selected.Position);
        Assert.Equal(WorkStageNodeState.Incomplete, selected.State);
        Assert.Equal(2, scene.NextCalls);
        Assert.Single(scene.Adb.Taps);
        Assert.Empty(scene.Adb.Swipes);
    }

    /// <summary>HARD 1 结算后游戏自动选中 HARD 2，真实回放必须直接接续第 22 关，不重导航或点击 NEXT。</summary>
    [Fact]
    public async Task CurrentPage_AfterHardOne_RealHardTwoContinuesWithoutNavigation()
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/StageNodes/hard2-selected-feedback.png")));
        // 附件原像素补黑边用于通过测试截图规格，不对生产截图做缩放或宽容处理。
        for (var row = 0; row < crop.Height; row++)
            Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, pixels, ((row + 400) * 1920) * 4, crop.Width * 4);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var log = new TestLog();
        using var recognizer = CreateRecognizer(adb);
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
        var stage = CreateStage(automation, log, recognizer);
        var battles = new List<WorkStagePosition>();
        var opens = 0;
        var runner = new AutoProgressRunner((_, _) =>
        {
            opens++;
            return Task.FromResult(new StageNodeRecognition(400, 880, WorkStageKind.Hard, 1,
                WorkStageNodeState.Incomplete, new(WorkStageKind.Hard, 1, 21), "测试首关"));
        }, (_, ct) => stage.ReadCurrentWorkForProgressAsync("test", Work, ct), (node, _) =>
        {
            battles.Add(node.Position!);
            return Task.FromResult(new AutoProgressBattleResult(battles.Count == 1, "已验证 HARD 2 出击入口"));
        });
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.Equal("已验证 HARD 2 出击入口", result.Message);
        Assert.Equal(new[] { new WorkStagePosition(WorkStageKind.Hard, 1, 21), new(WorkStageKind.Hard, 2, 22) }, battles);
        Assert.Equal(1, opens);
        Assert.Equal(1, result.CompletedStages);
        Assert.Empty(adb.Taps);
        Assert.Empty(adb.Swipes);
    }

    /// <summary>用户轨道原像素：选中 NORMAL 19 已通过，唯一 NEXT 指向尚未选中的 BOSS 20。</summary>
    [Fact]
    public void RealRail_CompletedNineteenHasNextBossTwenty()
    {
        var frame = RealFrame();
        using var recognizer = CreateRecognizer(new FakeAdbService([]));
        recognizer.ValidateForProgress();
        var selected = recognizer.RecognizeSelected(frame, Work);
        var next = recognizer.RecognizeNext(frame, Work);
        Assert.Equal(19, selected?.Number);
        Assert.Equal(WorkStageNodeState.Completed, selected?.State);
        Assert.Equal(new WorkStagePosition(WorkStageKind.Boss, 20, 20), next?.Position);
        Assert.InRange(next!.CenterX, 775, 795);
        Assert.InRange(next.CenterY, 650, 680);
    }

    /// <summary>无标记、多标记、已锁定、已通关及与作品配置不符的目标均不能授权选关。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("multiple")]
    [InlineData("locked")]
    [InlineData("complete")]
    [InlineData("wrong-work")]
    public void RealRail_UntrustedNextDoesNotAuthorize(string problem)
    {
        var frame = RealFrame();
        if (problem == "missing") TestImageFactory.PasteBlock(frame.Bgra, 1920, 150, 180, 710, 400, 0);
        if (problem == "multiple") Paste(frame, ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(Templates, "next_marker.png"))), 350, 456);
        if (problem is "locked" or "complete")
        {
            var template = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(Templates,
                problem == "locked" ? "locked_boss.png" : "complete_badge.png")));
            Paste(frame, template, 784 - template.Width / 2, 745);
        }
        using var recognizer = CreateRecognizer(new FakeAdbService([]));
        var work = problem == "wrong-work" ? new HardStageEntry { NormalCount = 19, HardCount = 3 } : Work;
        Assert.Null(recognizer.RecognizeNext(frame, work));
    }

    /// <summary>实际公开原页入口读取 NEXT，点击编号后用合成选中光圈的新帧复核，传给战斗的是 BOSS 20。</summary>
    [Fact]
    public async Task CurrentPage_RealNextIsClickedAndVerifiedBeforeBattle()
    {
        var before = RealFrame();
        var after = SelectedBossFrame();
        var adb = new FakeAdbService([]);
        adb.ScreenshotProvider = () => Encode(adb.Taps.Count == 0 ? before : after);
        var log = new TestLog();
        using var recognizer = CreateRecognizer(adb);
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
        var stage = CreateStage(automation, log, recognizer);
        StageNodeRecognition? battleNode = null;
        var runner = new AutoProgressRunner((_, ct) => stage.ReadCurrentWorkForProgressAsync("test", Work, ct),
            (_, _) => throw new InvalidOperationException("测试在首个战斗入口终止"),
            (node, _) => { battleNode = node; return Task.FromResult(new AutoProgressBattleResult(false, "已验证战斗入口")); });
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.Equal("已验证战斗入口", result.Message);
        Assert.Equal(new WorkStagePosition(WorkStageKind.Boss, 20, 20), battleNode?.Position);
        Assert.Equal(WorkStageNodeState.Incomplete, battleNode?.State);
        var tap = Assert.Single(adb.Taps);
        Assert.InRange(tap.X, 775, 795);
        Assert.InRange(tap.Y, 650, 680);
        Assert.Empty(adb.Swipes);
        Assert.False(stage.Context.IsReliable);
    }

    /// <summary>正常自动选中的未完成关卡和最终 HARD 已通过都原样返回，不执行 NEXT 识别或导航点击。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentPage_AutomaticSelectionOrFinalHardDoesNotClick(bool finalHard)
    {
        using var scene = new Scene(finalHard ? "final" : "incomplete");
        var selected = await scene.Read();
        Assert.Equal(scene.Initial, selected);
        Assert.Equal(0, scene.NextCalls);
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>缺失标记和两帧位置变化只有限观察，不能按旧编号加一或靠单帧点击。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("moving")]
    public async Task CurrentPage_MissingOrUnstableNextDoesNotClick(string problem)
    {
        using var scene = new Scene(problem);
        Assert.Equal(scene.Initial, await scene.Read());
        Assert.InRange(scene.NextCalls, 5, 10);
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>点击不生效或选中其他关卡时不返回可出击节点，不反复点击掩盖失败。</summary>
    [Theory]
    [InlineData("unchanged")]
    [InlineData("wrong-selection")]
    public async Task CurrentPage_UnconfirmedSelectionStopsAfterOneClick(string problem)
    {
        using var scene = new Scene(problem);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(scene.Read);
        Assert.Contains("点击 NEXT 后确认仍为", error.Message);
        Assert.Single(scene.Adb.Taps);
        Assert.Equal(6, scene.SelectedCalls);
    }

    /// <summary>第二帧候选读取时取消，必须在点击前中止，不将第一帧候选当作已授权。</summary>
    [Fact]
    public async Task CurrentPage_CancellationBetweenNextFramesPreventsClick()
    {
        using var scene = new Scene("cancel");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(scene.Read);
        Assert.Empty(scene.Adb.Taps);
        Assert.Equal(1, scene.NextCalls);
    }

    /// <summary>进入异常诊断时即使已有 NEXT 候选，网络弹窗仍停止恢复，不沿用候选点击。</summary>
    [Fact]
    public async Task CurrentPage_NetworkErrorBetweenNextFramesPreventsClick()
    {
        using var scene = new Scene("network");
        await Assert.ThrowsAsync<ConnectionErrorException>(scene.CheckAbnormalNextFrame);
        Assert.Empty(scene.Adb.Taps);
        Assert.Equal(1, scene.NextCalls);
    }

    /// <summary>编号缺失时不强制选 NEXT、不等待五轮；未知资料交给真实出击入口确认，而不是作为流程失败。</summary>
    [Fact]
    public async Task CurrentPage_NoSelectedHaloFollowsGameWithoutNextClick()
    {
        using var scene = new Scene("missing", missingSelection: true);
        Assert.Equal(StageNodeRecognition.Unidentified, await scene.Read());
        Assert.Equal(1, scene.SelectedCalls);
        Assert.Equal(0, scene.NextCalls);
        Assert.Empty(scene.Adb.Taps);
        Assert.Empty(scene.Adb.Swipes);
    }

    /// <summary>明确选着已通关节点才点 NEXT；点击后只是读不到编号，仍交给出击入口，不伪造目标编号。</summary>
    [Fact]
    public async Task CurrentPage_NextClickedButNumberMissingReturnsUnknown()
    {
        using var scene = new Scene("missing-after-click");
        Assert.Equal(StageNodeRecognition.Unidentified, await scene.Read());
        Assert.Single(scene.Adb.Taps);
        Assert.Null((await scene.Read()).Position);
    }

    /// <summary>真实截图只保留左侧 NEXT 和轨道区域，以原坐标补黑边满足生产截图规格。</summary>
    private static ScreenImage RealFrame()
    {
        var frame = ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 0), 1920, 1080));
        Paste(frame, ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/StageNodes/normal19-complete-next-boss20.png"))), 0, 400);
        return frame;
    }

    /// <summary>仅用于模拟点击后的选中状态：保留真实编号和台座，移除旧黄圈并在 BOSS 台座绘制黄圈；不是实机截图。</summary>
    private static ScreenImage SelectedBossFrame()
    {
        var frame = RealFrame();
        TestImageFactory.PasteBlock(frame.Bgra, 1920, 255, 95, 460, 850, 0);
        for (var step = 0; step < 720; step++)
        {
            var angle = step * Math.PI / 360;
            var x = 784 + (int)(110 * Math.Cos(angle));
            var y = 828 + (int)(24 * Math.Sin(angle));
            TestImageFactory.PasteColoredBlock(frame.Bgra, 1920, 3, 3, x, y, 245, 205, 35);
        }
        return frame;
    }

    /// <summary>共用离线模型，识别阈值及完整编号校验保持生产设置。</summary>
    private static StageNodeRecognizer CreateRecognizer(FakeAdbService adb) => new(adb, new TemplateMatcher(),
        new TestLog(), Templates, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
        new OnnxStageNumberRecognizer(TapToNextOcrSamples.SharedOcr));

    /// <summary>缩短测试等待，原页公开入口、全局安全检查和节点确认不替换。</summary>
    private static StageService CreateStage(AutomationService automation, TestLog log, IStageNodeRecognizer recognizer)
        => new(automation, new FakeStageWorkRecognizer(), log,
            new StageNavigationOptions { PageSettleMs = 0, TitlePollIntervalMs = 1 }, nodeRecognizer: recognizer);

    /// <summary>原尺寸复制，透明模板仅覆盖实心字形，不修改标记或编号比例。</summary>
    private static void Paste(ScreenImage target, ScreenImage source, int x, int y)
    {
        for (var sy = 0; sy < source.Height; sy++)
            for (var sx = 0; sx < source.Width; sx++)
                if (source.Bgra[(sy * source.Width + sx) * 4 + 3] >= 128)
                    Buffer.BlockCopy(source.Bgra, (sy * source.Width + sx) * 4,
                        target.Bgra, ((y + sy) * target.Width + x + sx) * 4, 4);
    }

    /// <summary>重新编码修改后的测试帧，避免 Bitmap 与 BGRA 副本不同步。</summary>
    private static byte[] Encode(ScreenImage frame) => ImageCodec.EncodePng(frame.Bgra, frame.Width, frame.Height);

    /// <summary>可控的节点观察替身验证点击所有权和停止条件；截图安全入口保持真实。</summary>
    private sealed class Scene : IDisposable, IStageNodeRecognizer, IStageNextNodeRecognizer
    {
        private readonly string _problem;
        private readonly bool _missingSelection;
        private readonly CancellationTokenSource _cts = new();
        private readonly AutomationService _automation;
        private readonly StageService _stage;
        private readonly string? _errorPath;
        public FakeAdbService Adb { get; } = new(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 0), 1920, 1080));
        public StageNodeRecognition Initial { get; }
        public int NextCalls { get; private set; }
        public int SelectedCalls { get; private set; }

        /// <summary>正常选中、完成终点、缺失/移动候选和点击无效分别模拟真实分支。</summary>
        public Scene(string problem, bool missingSelection = false, WorkStageNodeState? afterBattleState = null)
        {
            _problem = problem;
            _missingSelection = missingSelection;
            Initial = Node(problem == "final" ? WorkStageKind.Hard : WorkStageKind.Normal,
                problem == "final" ? 3 : 19, problem == "incomplete" ? WorkStageNodeState.Incomplete : WorkStageNodeState.Completed);
            if (afterBattleState is { } state) Initial = Node(WorkStageKind.Hard, 1, state);
            var log = new TestLog();
            var matcher = new TemplateMatcher();
            ConnectionErrorDetector? errorDetector = null;
            if (problem == "network")
            {
                _errorPath = Path.Combine(Path.GetTempPath(), $"ggea-next-error-{Guid.NewGuid():N}.png");
                File.WriteAllBytes(_errorPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(200, 70, 210), 200, 70));
                errorDetector = new(Adb, matcher, log, _errorPath);
                var plain = TestImageFactory.CreateBgra(1920, 1080, 0);
                var error = (byte[])plain.Clone();
                TestImageFactory.PasteBlock(error, 1920, 200, 70, 860, 205, 210);
                var plainPng = ImageCodec.EncodePng(plain, 1920, 1080);
                var errorPng = ImageCodec.EncodePng(error, 1920, 1080);
                Adb.ScreenshotProvider = () => NextCalls == 0 ? plainPng : errorPng;
            }
            _automation = new(Adb, matcher, log, connectionErrorDetector: errorDetector, textOcr: TapToNextOcrSamples.SharedOcr);
            _stage = CreateStage(_automation, log, this);
            Adb.BeforeScreenshotAsync = (_, _) =>
            {
                if (problem == "cancel" && NextCalls == 1) _cts.Cancel();
                return Task.CompletedTask;
            };
        }

        /// <summary>使用真实原页业务入口，不以直接调用候选识别代替流程验证。</summary>
        public Task<StageNodeRecognition> Read() => _stage.ReadCurrentWorkForProgressAsync("test", Work, _cts.Token);

        /// <summary>先保留候选，再模拟流程异常后的新帧诊断；诊断不能授权消费候选。</summary>
        public async Task CheckAbnormalNextFrame()
        {
            var first = await _automation.CaptureScreenAsync("test");
            Assert.NotNull(RecognizeNext(first, Work));
            var current = await _automation.CaptureScreenAsync("test");
            await _automation.GlobalScreens.CheckAbnormalScreenAsync(current);
        }

        /// <summary>模拟队列已确认本轮结算后的推进调用，仍执行真实导航与截图复核。</summary>
        public Task<StageNodeRecognition> Advance() => _stage.AdvanceAfterBattleAsync("test", Work, Initial, _cts.Token);

        /// <summary>点击前完成旧关，点击后按场景模拟没变化或错选；均不能满足目标确认。</summary>
        public Task<StageNodeRecognition?> RecognizeSelectedAsync(string serial, HardStageEntry work, CancellationToken ct = default)
        {
            SelectedCalls++;
            if (_problem == "after-battle" && Adb.Taps.Count > 0)
                return Task.FromResult<StageNodeRecognition?>(Node(WorkStageKind.Hard, 2, WorkStageNodeState.Incomplete));
            if (_missingSelection || (_problem == "missing-after-click" && Adb.Taps.Count > 0))
                return Task.FromResult<StageNodeRecognition?>(null);
            return Task.FromResult<StageNodeRecognition?>(Adb.Taps.Count > 0 && _problem == "wrong-selection"
                ? Node(WorkStageKind.Normal, 18, WorkStageNodeState.Incomplete) : Initial);
        }

        /// <summary>第一帧证据不能单独点击，移动模拟完整编号和标记同时偏离。</summary>
        public StageNextNodeRecognition? RecognizeNext(ScreenImage screen, HardStageEntry work, CancellationToken ct = default)
        {
            NextCalls++;
            if (_problem == "after-battle")
                return new(784, 664, new(WorkStageKind.Hard, 2, 22), new(738, 456, 91, 40), new(772, 639, 24, 50));
            if (_problem == "missing") return null;
            var shift = _problem == "moving" && NextCalls % 2 == 0 ? 20 : 0;
            return new(784 + shift, 664, new(WorkStageKind.Boss, 20, 20), new(738 + shift, 456, 91, 40), new(752 + shift, 639, 64, 50));
        }

        /// <summary>测试节点仍通过作品序列映射，最终 HARD 具有正确总序号。</summary>
        private static StageNodeRecognition Node(WorkStageKind kind, int number, WorkStageNodeState state)
        {
            WorkStageSequencePlanner.TryGetPosition(20, 0, 3, kind, number, out var position);
            return new(580, 890, kind, number, state, position, "confirmed");
        }

        /// <summary>释放场景所有资源，共用 OCR 会话由测试公共宿主管理。</summary>
        public void Dispose()
        {
            _automation.Dispose(); _cts.Dispose();
            if (_errorPath is not null) File.Delete(_errorPath);
        }
    }
}

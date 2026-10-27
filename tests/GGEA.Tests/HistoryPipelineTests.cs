using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>历史推图的真实页面回放及阶段边界：六个 BOSS 独立编号，双 100% 才结束作品。</summary>
public sealed class HistoryPipelineTests
{
    private static readonly string Templates = Path.Combine(AppContext.BaseDirectory, "resources/common/templates/stage");
    private static HardStageEntry Work => new() { Id = 7, Work = "X Astray", NormalCount = 7, HardCount = 3 };

    /// <summary>第 08 MS 小队浅色山体曾形成第二条白色候选行；必须读取真实 HARD 3 与 COMPLETE。</summary>
    [Fact]
    public void Actual08thHard3_BrightBackgroundKeepsNumberAndCompletion()
    {
        var screen = Frame(("history-08th-hard3-nodes.png", 0, 290));
        using var numbers = new OnnxStageNumberRecognizer(TapToNextOcrSamples.SharedOcr);
        using var nodes = new StageNodeRecognizer(new FakeAdbService([]), new TemplateMatcher(), new TestLog(),
            Path.Combine(Templates, "node_states"), numberRecognizer: numbers, layout: StageNodeLayout.History);
        var work = new HardStageEntry { NormalCount = 8, HardCount = 3 };
        var result = nodes.RecognizeSelected(screen, work);
        Assert.Equal(new WorkStagePosition(WorkStageKind.Hard, 3, 11), result?.Position);
        Assert.Equal(WorkStageNodeState.Completed, result?.State);
    }

    /// <summary>同节点两帧完成/锁定状态不能因编号缺失而丢失，也不能将单帧编号升级为最终关证明。</summary>
    [Theory]
    [InlineData(WorkStageNodeState.Completed)]
    [InlineData(WorkStageNodeState.Cleared)]
    [InlineData(WorkStageNodeState.Locked)]
    public void HistoryObservation_MissingNumberPreservesStopEvidence(WorkStageNodeState state)
    {
        var first = new StageNodeRecognition(790, 620, WorkStageKind.Hard, 3, state,
            new(WorkStageKind.Hard, 3, 11), "first", new(775, 430, 25, 50));
        var next = first with { Number = null, Position = null, NumberRegion = null };
        var result = HistoryStageNodeRecognizer.ConfirmObservation(first, next);
        Assert.Equal(state, result?.State);
        Assert.Null(result?.Position);
        Assert.Null(result?.Number);
        Assert.Null(HistoryStageNodeRecognizer.ConfirmObservation(first, next with { CenterX = 850 }));
        Assert.Null(HistoryStageNodeRecognizer.ConfirmObservation(first, next with { State = WorkStageNodeState.Incomplete }));
    }

    /// <summary>原页读取不能将无编号的已通过节点重新包装为 Unidentified 并授权出击。</summary>
    [Fact]
    public async Task HistoryReader_PassedWithoutNumber_NeverStartsBattle()
    {
        var passed = new StageNodeRecognition(790, 620, WorkStageKind.Unknown, null,
            WorkStageNodeState.Completed, null, "two frames passed");
        using var automation = new AutomationService(new FakeAdbService([]), new TemplateMatcher(), new TestLog());
        var reader = new StageProgressReader(automation, new FixedNode(passed), new TestLog(), new(),
            (_, _) => Task.CompletedTask);
        var observed = await reader.ReadCurrentWorkForProgressAsync("test", Work, default);
        Assert.True(observed.HasPassed);
        var runner = new AutoProgressRunner((_, _) => Task.FromResult(observed),
            (_, _) => Task.FromResult(observed), (_, _) => throw new InvalidOperationException("不得重复出击"));
        Assert.False((await runner.RunAsync([Work], _ => { }, default)).Completed);
    }

    /// <summary>隔离识别输入，验证真实读取器与队列间的状态传递。</summary>
    private sealed class FixedNode(StageNodeRecognition node) : IStageNodeRecognizer
    {
        public Task<StageNodeRecognition?> RecognizeSelectedAsync(string serial, HardStageEntry work,
            CancellationToken ct = default) => Task.FromResult<StageNodeRecognition?>(node);
    }

    /// <summary>历史轨道比主线上移约三百像素，仍使用相同字形与光圈阈值读取最终 HARD 3。</summary>
    [Fact]
    public void ActualHistoryNodes_RecognizeFinalHardUsingOwnLayout()
    {
        var screen = Frame(("history-progress-nodes.png", 0, 290));
        using var numbers = new OnnxStageNumberRecognizer(TapToNextOcrSamples.SharedOcr);
        using var nodes = new StageNodeRecognizer(new FakeAdbService([]), new TemplateMatcher(), new TestLog(),
            Path.Combine(Templates, "node_states"), numberRecognizer: numbers, layout: StageNodeLayout.History);
        var result = nodes.RecognizeSelected(screen, Work);
        Assert.NotNull(result);
        Assert.Equal(WorkStageKind.Hard, result.Kind);
        Assert.Equal(3, result.Number);
        Assert.Equal(WorkStageNodeState.Completed, result.State);
        Assert.Equal(10, result.Position?.OverallIndex);
    }

    /// <summary>同作品的三种页面不能混用固定按钮：节点页的小 Logo 与 BOSS 大 Logo 都须独立确认。</summary>
    [Theory]
    [InlineData(HistoryPageKind.Overview)]
    [InlineData(HistoryPageKind.StageNodes)]
    [InlineData(HistoryPageKind.Boss)]
    public void ActualPageKinds_KeepIdentityAndNavigationSeparate(HistoryPageKind page)
    {
        var catalog = HistoryStageCatalog.Load(Path.Combine(AppContext.BaseDirectory, "resources/common/history_stages.json"),
            Path.Combine(Templates, "history_stage"));
        using var vision = new HistoryStageVision(catalog, Templates);
        var screen = page switch
        {
            HistoryPageKind.Overview => Frame(("history-progress-complete.png", 1040, 220), ("history-overview-logo.png", 0, 700)),
            HistoryPageKind.StageNodes => Frame(("history-progress-nodes.png", 0, 290), ("history-node-entry.png", 1470, 754)),
            _ => Frame(("history-boss-logo.png", 0, 700), ("history-boss-top.png", 1000, 128))
        };
        Paste(screen.Bgra, 1920, ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(Templates,
            page == HistoryPageKind.Boss ? "history_boss_title.png" : "history_stage.png"))), 218, 20);
        var observation = vision.Inspect(screen, 2, default);
        Assert.Equal(7, observation.DetailWorkId);
        Assert.Equal(page, observation.Page);
        Assert.Equal(page == HistoryPageKind.Overview, observation.Progress is not null);
    }

    /// <summary>三种难度每种两张卡片均独立识别，用户更新的纯净 COMPLETE 素材覆盖六张实机卡片。</summary>
    [Theory]
    [InlineData("top", FierceEnemyDifficulty.EXPERT)]
    [InlineData("hard", FierceEnemyDifficulty.HARD)]
    [InlineData("bottom", FierceEnemyDifficulty.NORMAL)]
    public void ActualBossCards_ReadCompletionMarkersPerRegion(string view, FierceEnemyDifficulty difficulty)
    {
        var observations = BossCards(view);
        var cards = observations.Where(card => card.IsFullyVisible).ToArray();
        Assert.Equal(2, cards.Length);
        Assert.All(cards, card => { Assert.Equal(difficulty, card.Difficulty); Assert.Equal(FierceEnemyProgressState.Passed, card.State); });
        Assert.True(cards[0].Label.Y < cards[1].Label.Y);
        // 顶部实机样本底边还露出一个 HARD 标识：新契约应保留它，但绝不能赋予完成或挑战状态。
        var partial = observations.Where(card => !card.IsFullyVisible).ToArray();
        Assert.Equal(view == "top" ? 1 : 0, partial.Length);
        Assert.All(partial, card =>
        {
            Assert.Equal(FierceEnemyDifficulty.HARD, card.Difficulty);
            Assert.Equal(FierceEnemyProgressState.Unknown, card.State);
            Assert.Null(card.Challenge);
        });
    }

    /// <summary>对应难度两卡同帧编号，不要求相邻视口重叠；回归实机跨视口无重叠导致中止的问题。</summary>
    [Fact]
    public void BossPositions_MapSixSlotsWithoutFullScan()
    {
        var expert = BossCards("top"); var hard = BossCards("hard"); var normal = BossCards("bottom");
        Assert.Equal(normal[1], HistoryBossPosition.Find(normal, 1));
        Assert.Equal(normal[0], HistoryBossPosition.Find(normal, 2));
        Assert.Equal(hard[1], HistoryBossPosition.Find(hard, 3));
        Assert.Equal(hard[0], HistoryBossPosition.Find(hard, 4));
        Assert.Equal(expert[1], HistoryBossPosition.Find(expert, 5));
        Assert.Equal(expert[0], HistoryBossPosition.Find(expert, 6));
    }

    /// <summary>边缘单卡不猜其是同难度第一还是第二关；必须等两卡一起进入识别区。</summary>
    [Fact]
    public void BossPositions_PartialPairsDoNotInventSlots()
    {
        var expert = BossCards("top"); var normal = BossCards("bottom");
        Assert.Null(HistoryBossPosition.Find(BossCards("mid"), 3));
        Assert.Null(HistoryBossPosition.Find(BossCards("mid"), 4));
        Assert.Throws<InvalidOperationException>(() => HistoryBossPosition.Find([expert[0], expert[0]], 6));
    }

    /// <summary>实际底部 HARD 标签约 Y=917；必须保留定位依据，但不能从裁切卡片生成挑战坐标。</summary>
    [Fact]
    public void ActualBossBottomLabel_PreservesHeightWithoutAuthorizingChallenge()
    {
        var cards = new HistoryBossProgressRecognizer(TapToNextOcrSamples.SharedOcr)
            .Read(Frame(("history-boss-hard-edge.png", 1000, 128)), default);
        var target = HistoryBossPosition.Find(cards, 3);
        Assert.NotNull(target);
        Assert.InRange(target.Label.Y, 910, 925);
        Assert.False(target.IsFullyVisible);
        Assert.Equal(FierceEnemyProgressState.Unknown, target.State);
        Assert.Null(target.Challenge);
        var upper = HistoryBossPosition.Find(cards, 4);
        Assert.True(upper!.IsFullyVisible);
        Assert.Equal(FierceEnemyProgressState.Locked, upper.State);
    }

    /// <summary>回放同一页面实际拖动前后截图，底部目标调整后必须重新读取到亮色挑战按钮。</summary>
    [Fact]
    public async Task ActualBossHeightAdjustment_ReReadsAvailableTarget()
    {
        var frames = new Queue<IReadOnlyList<HistoryBossCard>>([BossCards("hard-edge"), BossCards("height-adjusted")]);
        var moves = new List<int>();
        var locator = new HistoryBossLocator(_ => Task.FromResult(frames.Dequeue()),
            (movement, _) => { moves.Add(movement); return Task.CompletedTask; });
        var card = await locator.FindAsync(3, default);
        Assert.Equal(new[] { -255 }, moves);
        Assert.True(card.IsFullyVisible);
        Assert.Equal(FierceEnemyProgressState.Available, card.State);
        Assert.NotNull(card.Challenge);
        Assert.InRange(card.Label.Y, 480, 520);
        Assert.InRange(card.Challenge.CenterY, 680, 730);
    }

    /// <summary>卡片标题不影响完成标记识别；擦去标题仍应读到两张已完成卡片。</summary>
    [Fact]
    public void BossCompletion_DoesNotDependOnCardTitles()
    {
        var frame = Frame(("history-boss-hard.png", 1000, 128));
        var original = new HistoryBossProgressRecognizer(TapToNextOcrSamples.SharedOcr).Read(frame, default);
        foreach (var card in original)
            for (var y = card.Label.Y - 18; y < card.Label.Y + 32; y++)
                Array.Clear(frame.Bgra, (y * 1920 + 1170) * 4, 650 * 4);
        var actual = new HistoryBossProgressRecognizer(TapToNextOcrSamples.SharedOcr).Read(frame, default);
        Assert.Equal(2, actual.Count);
        Assert.All(actual, card => Assert.Equal(FierceEnemyProgressState.Passed, card.State));
    }

    /// <summary>六关严格从底部 NORMAL 第一关到顶部 EXPERT 第二关，最后还需读取第六关完成标记。</summary>
    [Fact]
    public async Task BossRunner_ProgressesAllSixAndRechecksLast()
    {
        var completed = new HashSet<int>(); var battles = new List<int>(); var reads = 0;
        var runner = new HistoryBossProgressRunner((order, _) =>
        {
            reads++;
            return Task.FromResult(new HistoryBossNode(order, completed.Contains(order) ? FierceEnemyProgressState.Passed :
                order == 1 || completed.Contains(order - 1) ? FierceEnemyProgressState.Available : FierceEnemyProgressState.Locked));
        }, (order, _) => { battles.Add(order); completed.Add(order); return Task.FromResult(new AutoProgressBattleResult(true, "结算完成")); });
        var result = await runner.RunAsync("测试", _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(Enumerable.Range(1, 6), battles);
        Assert.Equal(12, reads); // 每关出击前与结算后各读一次，不回读更低或更高关卡。
        Assert.Equal(6, result.CompletedStages);
        Assert.Equal(0, result.CompletedWorks); // BOSS 完成不等于作品双 100%。
    }

    /// <summary>即使第六关已通过也不能掩盖低位未完成；结算后标记不变不得再次出击。</summary>
    [Fact]
    public async Task BossRunner_DoesNotSkipLowerOrRepeatUnchangedStage()
    {
        var battles = new List<int>();
        var runner = new HistoryBossProgressRunner((order, _) => Task.FromResult(new HistoryBossNode(order,
                order == 1 ? FierceEnemyProgressState.Available : FierceEnemyProgressState.Passed)),
            (order, _) => { battles.Add(order); return Task.FromResult(new AutoProgressBattleResult(true, "结算完成")); });
        Assert.False((await runner.RunAsync("测试", _ => { }, default)).Completed);
        Assert.Equal(new[] { 1 }, battles);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("取消", _ => { }, new(true)));
        Assert.Single(battles);
    }

    /// <summary>阶段顺序与作品最终证据分离；HARD 和第六 BOSS 通关后都必须返回总览。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkRunner_ReturnsAfterEachPartAndRequiresDouble100(bool finalComplete)
    {
        var calls = new List<string>(); var returns = 0;
        var runner = new HistoryWorkProgressRunner(_ => Task.FromResult(new HistoryProgressObservation(HistoryCompletion.Incomplete, HistoryCompletion.Locked)),
            _ => { calls.Add("stage"); return Task.FromResult(new AutoProgressResult(true, 1, 2, "HARD 已完成")); },
            _ => { calls.Add("boss"); return Task.FromResult(new AutoProgressResult(true, 0, 6, "六关已完成")); },
            _ => { calls.Add("return"); returns++; return Task.FromResult(new HistoryProgressObservation(HistoryCompletion.Complete,
                returns == 2 && finalComplete ? HistoryCompletion.Complete : HistoryCompletion.Incomplete)); });
        var result = await runner.RunAsync("测试作品", _ => { }, default);
        Assert.Equal(new[] { "stage", "return", "boss", "return" }, calls);
        Assert.Equal(finalComplete, result.Completed);
        Assert.Equal(finalComplete ? 1 : 0, result.CompletedWorks);
        Assert.Equal(8, result.CompletedStages);
    }

    /// <summary>双 100% 零战斗完成；关卡 100% 但 BOSS 锁定不能点击或跳过目标。</summary>
    [Theory]
    [InlineData(HistoryCompletion.Complete, true)]
    [InlineData(HistoryCompletion.Locked, false)]
    [InlineData(HistoryCompletion.Unknown, false)]
    public async Task WorkRunner_CompletedAndBlockedStatesDoNotFight(HistoryCompletion boss, bool completed)
    {
        var runner = new HistoryWorkProgressRunner(_ => Task.FromResult(new HistoryProgressObservation(HistoryCompletion.Complete, boss)),
            _ => throw new InvalidOperationException("不得推进关卡"), _ => throw new InvalidOperationException("不得推进 BOSS"),
            _ => throw new InvalidOperationException("不得点击返回"));
        var result = await runner.RunAsync("测试作品", _ => { }, default);
        Assert.Equal(completed, result.Completed);
        Assert.Equal(0, result.CompletedStages);
        Assert.NotEqual(new AutoProgressTarget(Work, null).Key, new AutoProgressTarget(null, null, Work).Key);
    }

    /// <summary>恢复后总览 100% 是真实完成证据，不需要构造一个假的最终 HARD 节点再次出击。</summary>
    [Fact]
    public async Task NodeRunner_RestoredOverviewCanConfirmCompletion()
    {
        var battles = 0; var opens = 0;
        var runner = new AutoProgressRunner((_, _) => { opens++; return Task.FromResult(StageNodeRecognition.Unidentified); },
            (_, _) => throw new InvalidOperationException("无需再次读取节点"),
            (_, _) => { battles++; return Task.FromResult(new AutoProgressBattleResult(true, "结算后离开作品", true)); },
            restoreWork: (_, _) => Task.FromResult(true));
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.True(result.Completed); Assert.Equal(1, battles); Assert.Equal(1, opens);
    }

    /// <summary>仅关卡目标不执行 BOSS，但不能绕过关卡完成证据。</summary>
    [Theory]
    [InlineData(HistoryCompletion.Complete, true)]
    [InlineData(HistoryCompletion.Unknown, false)]
    public async Task WorkRunner_StageOnlyRequiresStage100(HistoryCompletion stage, bool complete)
    {
        var runner = new HistoryWorkProgressRunner(_ => Task.FromResult(new HistoryProgressObservation(stage, HistoryCompletion.Unknown)),
            _ => throw new InvalidOperationException("不得推进关卡"),
            _ => throw new InvalidOperationException("不得推进 BOSS"),
            _ => throw new InvalidOperationException("不得返回"));
        var result = await runner.RunAsync("测试作品", _ => { }, default, attackBoss: false);
        Assert.Equal(complete, result.Completed);
        Assert.Equal(complete ? 1 : 0, result.CompletedWorks);
    }

    /// <summary>未完成关卡在执行后返回复核，未勾选 BOSS 时直接完成当前目标。</summary>
    [Fact]
    public async Task WorkRunner_StageOnlyReturnsAndVerifiesAfterFight()
    {
        var runner = new HistoryWorkProgressRunner(_ => Task.FromResult(new HistoryProgressObservation(HistoryCompletion.Incomplete, HistoryCompletion.Locked)),
            _ => Task.FromResult(new AutoProgressResult(true, 1, 2, "完成 HARD")),
            _ => throw new InvalidOperationException("不得推进 BOSS"),
            _ => Task.FromResult(new HistoryProgressObservation(HistoryCompletion.Complete, HistoryCompletion.Incomplete)));
        var result = await runner.RunAsync("测试作品", _ => { }, default, attackBoss: false);
        Assert.True(result.Completed);
        Assert.Equal(2, result.CompletedStages);
        var target = new AutoProgressTarget(null, null, Work);
        Assert.True(target.AttackHistoryBoss);
        Assert.Contains("仅关卡", (target with { AttackHistoryBoss = false }).Summary);
    }

    /// <summary>第 08 MS 小队的亮背景不能污染透明标题评分；复核完整导航观察结果。</summary>
    [Fact]
    public void Actual08thOverview_TransparentTitleAllowsLogoAndProgressCheck()
    {
        var catalog = HistoryStageCatalog.Load(Path.Combine(AppContext.BaseDirectory, "resources/common/history_stages.json"),
            Path.Combine(Templates, "history_stage"));
        using var vision = new HistoryStageVision(catalog, Templates);
        var result = vision.Inspect(Frame(("history-08th-overview.png", 0, 0)), 8, default);
        Assert.True(result.HistoryTitle);
        Assert.Equal(HistoryPageKind.Overview, result.Page);
        Assert.Equal(8, result.DetailWorkId);
        Assert.Equal(HistoryCompletion.Incomplete, result.Progress!.Stage);
        Assert.Equal(HistoryCompletion.Locked, result.Progress.Boss);
    }

    private static IReadOnlyList<HistoryBossCard> BossCards(string view)
        => new HistoryBossProgressRecognizer(TapToNextOcrSamples.SharedOcr)
            .Read(Frame(($"history-boss-{view}.png", 1000, 128)), default);

    /// <summary>原像素裁剪补回采集坐标，黑底区域不参与身份或状态判定。</summary>
    private static ScreenImage Frame(params (string Name, int X, int Y)[] parts)
    {
        var pixels = new byte[1920 * 1080 * 4];
        foreach (var part in parts)
            Paste(pixels, 1920, ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures/Navigation", part.Name))), part.X, part.Y);
        return ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080));
    }

    private static void Paste(byte[] target, int width, ScreenImage image, int x, int y)
    {
        for (var row = 0; row < image.Height; row++)
            Buffer.BlockCopy(image.Bgra, row * image.Width * 4, target, ((row + y) * width + x) * 4, image.Width * 4);
    }
}

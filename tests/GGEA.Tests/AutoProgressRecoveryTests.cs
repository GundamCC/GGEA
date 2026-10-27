using GGEA.App.Tasks;

namespace GGEA.Tests;

public sealed partial class AutoProgressFlowTests
{
    /// <summary>推图前几次点击未生效时，满两分钟重新检查并逐帧纠正；不能把发出点击当作蓝色确认。</summary>
    [Fact]
    public async Task ProgressRecovery_AutoAtTwoMinutes_RechecksUntilBlue()
    {
        using var scene = new Scene();
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualProgressTime();
        var enemy = scene.Frame(("auto_enemy", 1420, 10));
        var close = scene.Frame(("auto_close", 1420, 10));
        var all = scene.Frame(("auto_all", 1420, 10));
        var waiting = 0;
        scene.Adb.ScreenshotProvider = () =>
        {
            var clicks = scene.Adb.Taps.Count;
            if (clicks == 3 && waiting++ == 0) clock.Advance(119);
            else if (clicks == 3 && waiting == 2)
            {
                Assert.DoesNotContain(scene.Log.Entries, e => e.Message.Contains("连续 2 分钟"));
                clock.Advance(1);
            }
            if (scene.Log.Entries.Any(e => e.Message.Contains("已确认蓝色 AUTO"))) cancellation.Cancel();
            return clicks >= 5 ? all : clicks == 4 ? close : enemy;
        };
        var error = await Record.ExceptionAsync(() => scene.Run(recoveryTime: clock,
            ct: cancellation.Token, detectHome: false));
        Assert.True(error is OperationCanceledException,
            $"点击数 {scene.Adb.Taps.Count}；异常 {error}；" + string.Join("\n", scene.Log.Entries.Select(e => e.Message)));
        Assert.Equal(5, scene.Adb.Taps.Count);
        Assert.All(scene.Adb.Taps, tap => Assert.Equal((1508, 50), tap));
        Assert.Single(scene.Log.Entries, e => e.Message.Contains("连续 2 分钟"));
        Assert.Contains(scene.Log.Entries, e => e.Message.Contains("已确认蓝色 AUTO"));
    }

    /// <summary>刷本等待首次结算满两分钟才检查 AUTO；敌军→关闭→蓝色逐帧确认，之后仍等待真实结算。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FarmBattle_AutoInspectionAtTwoMinutes_ConfirmsBlue(bool alreadyBlue)
    {
        using var scene = new Scene();
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualProgressTime();
        var opening = scene.Frame(("tap", 818, 940));
        var enemy = scene.Frame(("auto_enemy", 1420, 10));
        var close = scene.Frame(("auto_close", 1420, 10));
        var all = scene.Frame(("auto_all", 1420, 10));
        var battleFrames = 0;
        scene.Adb.ScreenshotProvider = () =>
        {
            if (scene.Adb.Taps.Count == 0) return opening;
            var autoClicks = scene.Adb.Taps.Count(t => t == (1508, 50));
            if (++battleFrames == 1) clock.Advance(119);
            if (battleFrames == 2) Assert.Equal(0, autoClicks);
            if (battleFrames == 3) clock.Advance(1);
            if (scene.Log.Entries.Any(e => e.Message.Contains("已确认蓝色 AUTO"))) cancellation.Cancel();
            return alreadyBlue || autoClicks >= 2 ? all : autoClicks == 1 ? close : enemy;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.RunFarmNext(clock, cancellation.Token));
        Assert.Equal(alreadyBlue ? 0 : 2, scene.Adb.Taps.Count(t => t == (1508, 50)));
        Assert.Single(scene.Log.Entries, e => e.Message.Contains("检查 AUTO 是否为蓝色全部自动"));
        Assert.Contains(scene.Log.Entries, e => e.Message.Contains("已确认蓝色 AUTO"));
        Assert.DoesNotContain(scene.Log.Entries, e => e.Message.Contains("已停止当前任务"));
    }

    /// <summary>未知页连续两个两分钟仍未进入正常流程，立即停止；没有点击、返回证据或完成计数。</summary>
    [Fact]
    public async Task Recovery_TwoIntervalsWithoutProgress_StopsTask()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        var unknown = scene.Frame();
        scene.Adb.ScreenshotProvider = () => unknown;
        var intervals = 0;
        // 首页复核也会截图；只有主循环首次观察及首轮检查结束后才推进时间，避免把截图次数当作恢复次数。
        scene.Adb.BeforeScreenshotAsync = (_, _) =>
        {
            if (intervals == 0 || intervals == 1 && scene.Log.Entries.Any(item => item.Message.Contains("本次不点击")))
            {
                clock.Advance(120);
                intervals++;
            }
            return Task.CompletedTask;
        };
        Assert.Equal(SortieFlowResult.Stalled, await scene.Run(recoveryTime: clock));
        Assert.Equal(2, intervals);
        Assert.Equal(2, scene.Log.Entries.Count(item => item.Message.Contains("连续 2 分钟")));
        Assert.Single(scene.Log.Entries, item => item.Message.Contains("本次不点击"));
        Assert.Empty(scene.Adb.Taps);
        Assert.Contains(scene.Log.Entries, item => item.Message.Contains("已停止当前任务"));
    }

    /// <summary>真实雷电准备页出现在三个结算阶段时立即安全停止；不必等两分钟，也不能重新出击或计为完成。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FarmSettlement_UnexpectedPreparation_StopsWithoutAnotherSortie(int phase)
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        var preparation = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "farm-unexpected-preparation-ldplayer.png"));
        var opening = scene.Frame(("tap", 818, 940));
        var continued = scene.Frame(("continue", 1500, 930));
        var reward = scene.Frame(("reward", 900, 20), ("continue", 1500, 930));
        scene.Adb.ScreenshotProvider = () => scene.Adb.Taps.Count switch {
            0 => opening,
            1 when phase >= 2 => continued,
            2 when phase >= 3 => reward,
            _ => preparation
        };
        Assert.Equal(SortieFlowResult.UnexpectedPreparation, await scene.RunFarmNext(clock));
        Assert.Equal(phase, scene.Adb.Taps.Count);
        Assert.DoesNotContain(scene.Log.Entries, item => item.Message.Contains("连续 2 分钟"));
        Assert.Contains(scene.Log.Entries, item => item.Message.Contains("不计完成、不重新出击"));
    }

    /// <summary>加载与战斗中的未知动画触发低频 AUTO 检查，但不能点击或误停正常长流程。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FarmRecovery_AutomaticPhaseBeyondFourMinutes_InspectsWithoutClick(bool enteredBattle)
    {
        using var scene = new Scene();
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualProgressTime();
        var opening = scene.Frame(("tap", 818, 940));
        var animation = scene.Frame();
        var observations = 0;
        scene.Adb.ScreenshotProvider = () =>
        {
            if (enteredBattle && scene.Adb.Taps.Count == 0) return opening;
            clock.Advance(120);
            if (++observations == 4) cancellation.Cancel();
            return animation;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.RunFarmNext(clock, cancellation.Token));
        Assert.Equal(4, observations);
        Assert.Contains(scene.Log.Entries, item => item.Message.Contains("检查 AUTO 是否为蓝色全部自动"));
        Assert.Equal(enteredBattle ? new[] { scene.Center("tap", 818, 940) } : Array.Empty<(int, int)>(), scene.Adb.Taps);
    }

    /// <summary>第一次刷本恢复补点后实际出现新开场，正常流程可继续；新阶段会清除连续失败预算。</summary>
    [Fact]
    public async Task FarmRecovery_FirstRetryThenNewOpening_RunsNormally()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        var resultPage = scene.Frame(("again", 1200, 920), ("select", 1600, 920));
        var opening = scene.Frame(("tap", 818, 940));
        scene.Adb.ScreenshotProvider = () => scene.Adb.Taps.Count == 0 ? resultPage : scene.Adb.Taps.Count == 1 ? opening : resultPage;
        scene.Adb.BeforeScreenshotAsync = (count, _) => { if (count == 1) clock.Advance(120); return Task.CompletedTask; };
        Assert.Equal(SortieFlowResult.RoundCompleted, await scene.RunFarmNext(clock));
        Assert.Equal(new[] { scene.Center("again", 1200, 920), scene.Center("tap", 818, 940), scene.Center("again", 1200, 920) }, scene.Adb.Taps);
        Assert.DoesNotContain(scene.Log.Entries, item => item.Message.Contains("已停止当前任务"));
    }

    /// <summary>继续页原有一次补点无效时，满两分钟才再授权一次，不绕过双按钮真实结算。</summary>
    [Fact]
    public async Task FarmRecovery_ContinueStalled_PermitsOneFreshRetry()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        var continued = scene.Frame(("continue", 1500, 930));
        scene.Steps(scene.Frame(("tap", 818, 940)), continued, continued, continued,
            scene.Frame(("again", 1200, 920), ("select", 1600, 920)));
        var advanced = false;
        scene.Adb.BeforeScreenshotAsync = (_, _) =>
        {
            if (!advanced && scene.Adb.Taps.Count == 3) { clock.Advance(120); advanced = true; }
            return Task.CompletedTask;
        };
        Assert.Equal(SortieFlowResult.RoundCompleted, await scene.RunFarmNext(clock));
        Assert.Equal(new[] { scene.Center("tap", 818, 940), scene.Center("continue", 1500, 930),
            scene.Center("continue", 1500, 930), scene.Center("continue", 1500, 930), scene.Center("again", 1200, 920) }, scene.Adb.Taps);
        Assert.Single(scene.Log.Entries, item => item.Message.Contains("连续 2 分钟"));
    }

    /// <summary>到期同帧的失败优先于刷本再次出击恢复。</summary>
    [Fact]
    public async Task FarmRecovery_FailureBeforeRetry_Stops()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(scene.Frame(("failed", 100, 20), ("again", 1200, 920), ("select", 1600, 920)));
        scene.Adb.BeforeScreenshotAsync = (_, _) => { clock.Advance(120); return Task.CompletedTask; };
        Assert.Equal(SortieFlowResult.FailureDetected, await scene.RunFarmNext(clock));
        Assert.Equal(new[] { (1670, 980) }, scene.Adb.Taps);
        Assert.DoesNotContain(scene.Log.Entries, item => item.Message.Contains("恢复"));
    }

    /// <summary>准备页只交回原编制会话，不在观察器中直接点击出击或把本关当作完成。</summary>
    [Fact]
    public async Task Recovery_Preparation_ReturnsToOriginalSession()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(scene.Frame());
        scene.Adb.BeforeScreenshotAsync = (count, _) =>
        { if (count == 2) clock.Advance(120); return Task.CompletedTask; };
        var checks = 0;
        Assert.Equal(SortieFlowResult.ProgressResumePreparation,
            await scene.Run(recoveryTime: clock, confirmPreparation: (_, _) => { checks++; return Task.FromResult(true); }));
        Assert.Equal(2, checks);
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>刷本再次出击没有生效时两分钟后补点；旧结算不能算作新一轮，必须先确认新的 TAP TO NEXT。</summary>
    [Fact]
    public async Task FarmRecovery_StaleResult_RetriesWithoutCountingUntilNewBattle()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        var resultPage = scene.Frame(("again", 1200, 920), ("select", 1600, 920));
        scene.Steps(resultPage, scene.Frame(("tap", 818, 940)), resultPage);
        scene.Adb.BeforeScreenshotAsync = (count, _) =>
        { if (count == 2) clock.Advance(120); return Task.CompletedTask; };
        Assert.Equal(SortieFlowResult.RoundCompleted, await scene.RunFarmNext(clock));
        Assert.Equal(new[] { scene.Center("again", 1200, 920), scene.Center("tap", 818, 940),
            scene.Center("again", 1200, 920) }, scene.Adb.Taps);
        Assert.Contains(scene.Log.Entries, item => item.Message.Contains("不重复计数"));
    }

    /// <summary>旧结算一直未离开，第一次两分钟补点，第二次仍未恢复则立即停止，不等原十分钟开场超时。</summary>
    [Fact]
    public async Task FarmRecovery_PersistentOldResult_StopsWithoutCompletion()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(scene.Frame(("again", 1200, 920), ("select", 1600, 920)));
        scene.Adb.BeforeScreenshotAsync = (_, _) => { clock.Advance(120); return Task.CompletedTask; };
        Assert.Equal(SortieFlowResult.Stalled, await scene.RunFarmNext(clock));
        Assert.Equal(new[] { scene.Center("again", 1200, 920) }, scene.Adb.Taps);
        Assert.Equal(2, scene.Adb.ScreenshotCount);
    }

    /// <summary>正常下一轮保持原开场与结算点击，未满两分钟不能加载或启动恢复识别。</summary>
    [Fact]
    public async Task FarmRecovery_NormalRound_OriginalClickSequenceUnchanged()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(scene.Frame(("tap", 818, 940)), scene.Frame(("again", 1200, 920), ("select", 1600, 920)));
        Assert.Equal(SortieFlowResult.RoundCompleted, await scene.RunFarmNext(clock));
        Assert.Equal(new[] { scene.Center("tap", 818, 940), scene.Center("again", 1200, 920) }, scene.Adb.Taps);
        Assert.DoesNotContain(scene.Log.Entries, item => item.Message.Contains("恢复"));
    }

    /// <summary>剧情 MENU 点击未生效时，两分钟后重新识别并再点一次，然后继续 SKIP 与返回确认。</summary>
    [Fact]
    public async Task Recovery_MenuClickDidNotOpen_RetriesFromCurrentFrame()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("menu", 1700, 10)), scene.Frame(("menu", 1700, 10)),
            scene.Frame(("skip", 1700, 380)), scene.Frame(("title", 200, 20)));
        scene.Adb.BeforeScreenshotAsync = (count, _) =>
        { if (count == 3) clock.Advance(120); return Task.CompletedTask; };
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run(recoveryTime: clock));
        Assert.Equal(new[] { scene.Center("story", 1500, 930), scene.Center("menu", 1700, 10), scene.Center("menu", 1700, 10),
            scene.Center("skip", 1700, 380) }, scene.Adb.Taps);
        Assert.Single(scene.Log.Entries, item => item.Message.Contains("连续 2 分钟"));
    }

    /// <summary>故事点击无效不能永远困在“等待标题”；两分钟后仍须以本帧故事按钮授权重试。</summary>
    [Fact]
    public async Task Recovery_StoryClickDidNotLeave_ReopensSettlementDetection()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("story", 1500, 930)),
            scene.Frame(("title", 200, 20)));
        scene.Adb.BeforeScreenshotAsync = (count, _) =>
        { if (count == 2) clock.Advance(120); return Task.CompletedTask; };
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run(recoveryTime: clock));
        Assert.Equal(Enumerable.Repeat(scene.Center("story", 1500, 930), 2), scene.Adb.Taps);
    }

    /// <summary>恢复允许继续处理故事后的继续页，但不能遗忘已有结束证据并再等两分钟才能返回。</summary>
    [Fact]
    public async Task Recovery_ContinueAfterStory_PreservesEndEvidence()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("continue", 1500, 930)),
            scene.Frame(("title", 200, 20)));
        scene.Adb.BeforeScreenshotAsync = (count, _) =>
        { if (count == 2) clock.Advance(120); return Task.CompletedTask; };
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run(recoveryTime: clock));
        Assert.Equal(new[] { scene.Center("story", 1500, 930), scene.Center("continue", 1500, 930) }, scene.Adb.Taps);
        Assert.Single(scene.Log.Entries, item => item.Message.Contains("连续 2 分钟"));
    }

    /// <summary>漏掉结束标记后到选关页或主要关卡，恢复仅请求重新读取进度，不能虚增本轮战斗数。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_ReturnWithoutEndMarker_ResynchronizesWithoutCompletion(bool mainStage)
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(scene.Frame((mainStage ? "main_title" : "title", 200, 20)));
        scene.Adb.BeforeScreenshotAsync = (count, _) =>
        { if (count == 2) clock.Advance(120); return Task.CompletedTask; };
        Assert.Equal(mainStage ? SortieFlowResult.ProgressResumeWork : SortieFlowResult.ProgressResumeStage,
            await scene.Run(recoveryTime: clock));
        Assert.True(scene.Adb.ScreenshotCount >= 3);
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>返回确认的第二帧若被用户取消，不能提交恢复结果或继续点击。</summary>
    [Fact]
    public async Task Recovery_ConfirmationCancellation_Propagates()
    {
        using var scene = new Scene();
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualProgressTime();
        scene.Steps(scene.Frame(("title", 200, 20)));
        scene.Adb.BeforeScreenshotAsync = (count, token) =>
        {
            if (count == 2) clock.Advance(120);
            if (count == 3) cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.Run(recoveryTime: clock, ct: cancellation.Token));
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>未知画面和未关闭最终 NORMAL 奖励包均不可盲点；第二次两分钟检查仍无进展即停止。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_UnknownOrPendingReward_StopsAfterTwoIntervals(bool pendingReward)
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(pendingReward ? scene.Frame(("title", 200, 20)) : scene.Frame());
        scene.Adb.BeforeScreenshotAsync = (_, _) => { clock.Advance(120); return Task.CompletedTask; };
        Assert.Equal(SortieFlowResult.Stalled,
            await scene.Run(timeout: 5000, recoveryTime: clock, requireSeriesCompleteClose: pendingReward));
        Assert.Empty(scene.Adb.Taps);
        Assert.Contains(scene.Log.Entries, item => item.Message.Contains("连续 2 分钟"));
        Assert.Contains(scene.Log.Entries, item => item.Level == "WARN" && item.Message.Contains("已停止当前任务"));
    }

    /// <summary>健康战斗经过多个两分钟检查仍保持自动，不退出、不重复出击，最终真实结算才能完成。</summary>
    [Fact]
    public async Task Recovery_HealthyAutoBattle_KeepsWaitingWithoutClicking()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        var battle = scene.Frame(("auto_all", 1425, 10));
        var story = scene.Frame(("story", 1500, 930));
        var title = scene.Frame(("title", 200, 20));
        scene.Adb.ScreenshotProvider = () => scene.Adb.ScreenshotCount < 3 ? battle : scene.Adb.Taps.Count == 0 ? story : title;
        scene.Adb.BeforeScreenshotAsync = (_, _) => { clock.Advance(120); return Task.CompletedTask; };
        var result = await scene.Run(recoveryTime: clock);
        Assert.True(result == SortieFlowResult.ProgressStageCompleted, string.Join(" | ", scene.Log.Entries.Select(e => e.Message)));
        Assert.Equal(new[] { scene.Center("story", 1500, 930) }, scene.Adb.Taps);
        // 连续两次已达到未知画面的停止次数；健康战斗仍须继续，并最终以结算结束。
        Assert.Equal(2, scene.Log.Entries.Count(item => item.Message.Contains("保持当前战斗")));
    }

    /// <summary>健康 AUTO 在两分钟检查之间出现；超过四分钟的动画交替仍等待，且取消立即生效，不虚报完成。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_IntermittentHealthyBattleBeyondFourMinutes_RemainsCancellable(bool farm)
    {
        using var scene = new Scene();
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualProgressTime();
        var opening = scene.Frame(("tap", 818, 940));
        var battle = scene.Frame(("auto_all", 1425, 10), ("speed_on", 1550, 10));
        var animation = scene.Frame();
        // 本用例关闭独立首页复核，每次截图代表一次战斗观察；首页恢复由专门用例验证。
        var observations = 0;
        scene.Adb.ScreenshotProvider = () =>
        {
            if (farm && scene.Adb.Taps.Count == 0) return opening;
            clock.Advance(60);
            observations++;
            if (observations >= 8) cancellation.Cancel();
            // 60、240、420 秒可见 AUTO；120、180、300 秒等检查帧是隐藏控件的动画。
            return observations % 3 == 1 ? battle : animation;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => farm
            ? scene.RunFarmNext(clock, cancellation.Token)
            // 本用例验证八次虚拟时间观察后的取消，不验证真实总超时；完整图像识别在
            // 当前机器超过默认 20 秒，保留全部观察与断言，给识别留出独立的有界预算。
            : scene.Run(timeout: 60000, recoveryTime: clock, ct: cancellation.Token, detectHome: false));
        Assert.True(observations >= 8);
        Assert.DoesNotContain(scene.Log.Entries, item => item.Message.Contains("已停止当前任务"));
        if (farm) Assert.Equal(new[] { scene.Center("tap", 818, 940) }, scene.Adb.Taps);
        else Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>到达恢复时间的同一帧出现失败时仍先停止，不能被选关标题或恢复逻辑吞掉。</summary>
    [Fact]
    public async Task Recovery_TerminalFailure_HasPriority()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(scene.Frame(("failed", 100, 20), ("title", 500, 20)));
        scene.Adb.BeforeScreenshotAsync = (_, _) => { clock.Advance(120); return Task.CompletedTask; };
        Assert.Equal(SortieFlowResult.FailureDetected, await scene.Run(recoveryTime: clock));
        Assert.Equal(new[] { (1670, 980) }, scene.Adb.Taps);
        Assert.DoesNotContain(scene.Log.Entries, item => item.Message.Contains("连续 2 分钟"));
    }
}

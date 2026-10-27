using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Tasks;
using GGEA.App.Vision;
using static GGEA.Tests.MasterLeagueFlowTests;

namespace GGEA.Tests;

/// <summary>实际运行入口的恢复回归：延迟、取消与输入回执丢失不能丢失会话状态。</summary>
public sealed class MasterLeagueSessionTests
{
    /// <summary>隔离停滞诊断的模拟模板，结束时只清理本用例拥有的目录。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("ggea-league-stall-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }

    /// <summary>准备次数识别不等待战斗观察完成；同帧各项只读检查并行，最终只发送一次挑战。</summary>
    [Fact]
    public async Task Session_PreparationReadsAttemptsAlongsideBattle()
    {
        using var ocr = new OnnxTextRecognizer();
        using var attemptsStarted = new ManualResetEventSlim();
        var initial = Page(5, new(100, true, false), new(0, false, false), new(100, true, false),
            new(100, true, false), new(100, true, false));
        var frames = Enumerable.Range(1, 6).ToDictionary(i => i, i => Frame((byte)i));
        var adb = new FakeAdbService(frames[1]);
        GuardScreenshots(adb, 30);
        adb.ScreenshotProvider = () => frames[adb.Taps.Contains((1701, 989)) ? 6
            : adb.Taps.Contains((1675, 1000)) ? 3 : adb.Taps.Contains((1646, 893)) ? 2 : 1];
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>
            { [1] = initial, [6] = Completed(initial) }, null)
        {
            Battle = screen =>
            {
                if (screen.Bgra[0] == 2) Assert.True(attemptsStarted.Wait(TimeSpan.FromSeconds(10)));
                return new(screen.Bgra[0] == 3, null, screen.Bgra[0] == 3 ? Match(1520, 928, 362, 122) : null);
            },
            ChallengeRemaining = screen => { attemptsStarted.Set(); return screen.Bgra[0] == 2 ? 5 : null; }
        };
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) =>
        { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        Assert.True((await service.RunChallengesAsync("serial", vision, new(ocr), false)).Success);
        Assert.True(attemptsStarted.IsSet);
        Assert.Single(adb.Taps, tap => tap == (1675, 1000));
    }

    /// <summary>超过旧选敌/返回预算仍恢复，且未读到另一列或变更次数不会阻塞已确认零分者。</summary>
    [Fact]
    public async Task Session_DelayedPagesAndPartialData_ContinueWithoutRestart()
    {
        using var ocr = new OnnxTextRecognizer();
        var initial = Page(5, new(149000, true, false), new(0, false, false), new(null, false, false),
            new(0, false, false), new(0, false, false)) with { RefreshRemaining = null };
        var final = Completed(initial);
        var frames = Enumerable.Range(1, 11).ToDictionary(i => i, i => Frame((byte)i));
        var adb = new FakeAdbService(frames[8]);
        GuardScreenshots(adb, 140);
        var initialWait = 0; var returnWait = 0;
        adb.ScreenshotProvider = () => adb.Taps.Contains((1701, 989)) ? frames[++returnWait <= 65 ? 8 : 6]
            : adb.Taps.Contains((1675, 1000)) ? frames[3]
            : adb.Taps.Contains((1646, 893)) ? frames[2] : frames[++initialWait <= 35 ? 8 : initialWait % 2 == 0 ? 1 : 11];
        // 与下一目标无关的次数和第三列持续波动，也不能阻止第二名已确认零分者。
        var fluctuating = initial with { RefreshRemaining = 4, Opponents = initial.Opponents.Select((o, i) =>
            i == 2 ? o with { Points = 100 } : o).ToArray() };
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>
            { [1] = initial, [11] = fluctuating, [6] = final }, null);
        var delays = new List<int>();
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (ms, ct) =>
        { ct.ThrowIfCancellationRequested(); delays.Add(ms); return Task.CompletedTask; });
        Assert.True((await service.RunChallengesAsync("serial", vision, new(ocr), false)).Success);
        Assert.Equal(new[] { (429, 400), (1646, 893), (1675, 1000), (1701, 989) }, adb.Taps);
        Assert.Contains(5000, delays);
        Assert.True(initialWait > 30 && returnWait > 60);
    }

    /// <summary>挑战或刷新确认已送达而输入报错，恢复后仍只发送一次消费确认。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Session_LostActionAcknowledgement_DoesNotRepeat(bool refresh, bool captureFailure)
    {
        using var ocr = new OnnxTextRecognizer();
        var initial = Page(5, new(30000, true, false), new(refresh ? 29000 : 0, false, refresh),
            new(30000, true, false), new(30000, true, false), new(30000, true, false));
        var reset = initial with { Opponents = new[] { initial.Opponents[0], new MasterLeagueOpponent(0, false, false),
            initial.Opponents[2], initial.Opponents[3], initial.Opponents[4] } };
        var final = Completed(initial);
        var frames = Enumerable.Range(1, 8).ToDictionary(i => i, i => Frame((byte)i));
        var adb = new FakeAdbService(frames[1]);
        GuardScreenshots(adb, 40);
        adb.ScreenshotProvider = () => frames[adb.Taps.Contains((1701, 989)) ? 6 : adb.Taps.Contains((1675, 1000)) ? 3
            : adb.Taps.Contains((1646, 893)) ? 2 : adb.Taps.Contains((1148, 995)) ? 7 : adb.Taps.Contains((509, 913)) ? 4 : 1];
        adb.TapFailure = (x, y) => !captureFailure && (x, y) == (refresh ? (1148, 995) : (1675, 1000))
            ? new AdbException("模拟输入回执丢失") : null;
        var faults = 0;
        adb.ScreenshotFailure = _ => captureFailure && adb.Taps.Contains((1675, 1000)) && ++faults <= 3
            ? new AdbException("模拟设备暂时离线") : null;
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage> { [1] = initial, [6] = final, [7] = reset },
            new(29000, 5, Match(968, 935, 360, 120)));
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        Assert.True((await service.RunChallengesAsync("serial", vision, new(ocr), refresh)).Success);
        Assert.Single(adb.Taps, t => t == (1675, 1000));
        if (refresh) Assert.Single(adb.Taps, t => t == (1148, 995));
        Assert.Contains(log.Entries, entry => entry.Message.Contains("等待恢复"));
    }

    /// <summary>未知画面长期保持时进入低频等待，取消立即传播，期间没有任何点击。</summary>
    [Fact]
    public async Task Session_UnknownScreen_BacksOffAndRemainsCancellable()
    {
        using var ocr = new OnnxTextRecognizer();
        using var cancel = new CancellationTokenSource();
        var adb = new FakeAdbService(Frame(8));
        GuardScreenshots(adb, 100);
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var delays = new List<int>();
        var service = new MasterLeagueService(automation, log, (ms, ct) =>
        {
            delays.Add(ms);
            if (delays.Count == 95) cancel.Cancel();
            ct.ThrowIfCancellationRequested(); return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunChallengesAsync("serial",
            new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>(), null), new(ocr), false, cancel.Token));
        Assert.Contains(15000, delays);
        Assert.Empty(adb.Taps);
    }

    /// <summary>三页上限达到后保持等待而非退出，预算不能被新轮询重置。</summary>
    [Fact]
    public async Task Session_ContinuationLimit_WaitsWithoutExtraClicks()
    {
        using var ocr = new OnnxTextRecognizer();
        using var cancel = new CancellationTokenSource();
        var result = Frame(3); var next = Frame(9);
        var adb = new FakeAdbService(result);
        GuardScreenshots(adb, 20);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? result : next;
        var log = new TestLog(); var waits = 0;
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) =>
        { if (++waits == 12) cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunChallengesAsync("serial",
            new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>(), null), new(ocr), false, cancel.Token));
        Assert.Equal(4, adb.Taps.Count);
        Assert.Equal((1701, 989), adb.Taps[0]);
    }

    /// <summary>会话使用下载许可，下载确认后继续到结算，拒绝时不继续发出战斗动作。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Session_Download_UsesPermission(bool allow)
    {
        using var ocr = new OnnxTextRecognizer();
        var initial = Page(5, new(149000, true, false), new(0, false, false), new(0, false, false),
            new(0, false, false), new(0, false, false));
        var frames = Enumerable.Range(1, 10).ToDictionary(i => i, i => Frame((byte)i));
        var adb = new FakeAdbService(frames[1]);
        GuardScreenshots(adb, 40);
        adb.ScreenshotProvider = () => frames[adb.Taps.Contains((1701, 989)) ? 6 : adb.Taps.Contains((1149, 850)) ? 3
            : adb.Taps.Contains((1675, 1000)) ? 10 : adb.Taps.Contains((1646, 893)) ? 2 : 1];
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>
            { [1] = initial, [6] = Completed(initial) }, null);
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            new AppConfig { AllowGgeaDownload = allow });
        Assert.Equal(allow, (await service.RunChallengesAsync("serial", vision, new(ocr), false)).Success);
        Assert.Equal(allow ? 1 : 0, adb.Taps.Count(t => t == (1149, 850)));
        if (!allow) Assert.DoesNotContain((1701, 989), adb.Taps);
    }

    /// <summary>新会话同样保持结算优先；SKIP 复查时战斗已经结束，不能点击旧坐标。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Session_ResultWinsOverSkip(bool sameFrame)
    {
        using var ocr = new OnnxTextRecognizer();
        var initial = Page(5, new(149000, true, false), new(0, false, false), new(0, false, false),
            new(0, false, false), new(0, false, false));
        var frames = Enumerable.Range(1, 6).ToDictionary(i => i, i => Frame((byte)i));
        var adb = new FakeAdbService(frames[1]);
        GuardScreenshots(adb, 30);
        var battleFrames = 0;
        adb.ScreenshotProvider = () => frames[adb.Taps.Contains((1701, 989)) ? 6
            : adb.Taps.Contains((1675, 1000)) ? (++battleFrames == 1 ? 5 : 3)
            : adb.Taps.Contains((1646, 893)) ? 2 : 1];
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>
            { [1] = initial, [6] = Completed(initial) }, null)
        {
            Battle = screen => screen.Bgra[0] == 5
                ? new(sameFrame, Match(1418, 12, 184, 83), sameFrame ? Match(1520, 928, 362, 122) : null)
                : new(screen.Bgra[0] == 3, null, screen.Bgra[0] == 3 ? Match(1520, 928, 362, 122) : null)
        };
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        int? firstBattleDelay = null;
        int? preparationDelay = null;
        var service = new MasterLeagueService(automation, log, (ms, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (adb.Taps.LastOrDefault() == (1646, 893)) preparationDelay ??= ms;
            if (adb.Taps.LastOrDefault() == (1675, 1000)) firstBattleDelay ??= ms;
            return Task.CompletedTask;
        });
        Assert.True((await service.RunChallengesAsync("serial", vision, new(ocr), false)).Success);
        Assert.Equal(1000, preparationDelay);
        Assert.Equal(1000, firstBattleDelay);
        Assert.DoesNotContain((1510, 53), adb.Taps);
        Assert.Single(adb.Taps, t => t == (1701, 989));
    }
    /// <summary>脚本末页明确显示所有对手已挑战，不用虚构总分作为任务完成证据。</summary>
    private static MasterLeagueOpponentPage Completed(MasterLeagueOpponentPage page)
        => page with { Opponents = page.Opponents.Select(o => new MasterLeagueOpponent(o.Points ?? 100, true, false)).ToArray() };

    /// <summary>晋级后的全零新列表开启下一轮，同一列可以重新挑战，不依赖段位或右侧积分。</summary>
    [Fact]
    public async Task Session_PromotionResetsOpponentHistory_WithoutAnyScores()
    {
        using var ocr = new OnnxTextRecognizer();
        var before = Page(5, new(100, true, false), new(100, true, false), new(0, false, false),
            new(100, true, false), new(100, true, false));
        var after = Page(5, Enumerable.Range(0, 5).Select(_ => new MasterLeagueOpponent(0, false, false)).ToArray());
        var frames = Enumerable.Range(1, 9).ToDictionary(i => i, i => Frame((byte)i));
        var adb = new FakeAdbService(frames[1]);
        GuardScreenshots(adb, 50);
        adb.ScreenshotProvider = () =>
        {
            var returns = adb.Taps.Count(t => t == (1701, 989));
            var challenges = adb.Taps.Count(t => t == (1675, 1000));
            var battles = adb.Taps.Count(t => t == (1646, 893));
            var continues = adb.Taps.Count(t => t.X is >= 700 and <= 1200 && t.Y >= 900);
            return frames[returns >= 2 ? 6 : returns == 0 ? challenges >= 1 ? 3 : battles >= 1 ? 2 : 1
                : continues < 2 ? 9 : challenges >= 2 ? 3 : battles >= 2 ? 2 : 7];
        };
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>
            { [1] = before, [7] = after, [6] = Completed(after) }, null);
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        Assert.True((await service.RunChallengesAsync("serial", vision, new(ocr), false)).Success);
        Assert.Equal(2, adb.Taps.Count(t => t == (715, 400)));
        Assert.Equal(2, adb.Taps.Count(t => t == (1675, 1000)));
        Assert.Contains(log.Entries, e => e.Message.Contains("新的零分对手列表"));
    }

    /// <summary>实机解锁提示高于普通底部区域，必须优先处理，随后才允许读取被遮挡的对手列表。</summary>
    [Fact]
    public async Task Session_ChallengeUnlock_HandlesRaisedContinuation()
    {
        using var ocr = new OnnxTextRecognizer();
        var unlock = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/master-league-unlock-next.png"));
        var log = new TestLog();
        var recognizer = new OnnxTapToNextRecognizer(ocr, log);
        Assert.Null(recognizer.Recognize(ImageCodec.DecodePng(unlock)));
        var next = recognizer.Recognize(ImageCodec.DecodePng(unlock), region: RecognitionRegion.ProgressTapToNext);
        Assert.NotNull(next);
        Assert.InRange(next.CenterY, 835, 875);
        var complete = Completed(Page(5, Enumerable.Range(0, 5).Select(_ => new MasterLeagueOpponent(100, true, false)).ToArray()));
        var adb = new FakeAdbService(unlock);
        GuardScreenshots(adb, 8);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? unlock : Frame(6);
        // 即使背景被识别为已完成的列表，也必须先关闭覆盖其上的解锁提示。
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage> { [0] = complete, [6] = complete }, null);
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        Assert.True((await service.RunChallengesAsync("serial", vision, new(ocr), false)).Success);
        Assert.Equal(new[] { (next.CenterX, next.CenterY) }, adb.Taps);
    }

    /// <summary>关闭后仍残留数帧的弹层不能重复点击或提前选择背景对手；消失后恢复原选敌顺序。</summary>
    [Fact]
    public async Task Session_StageBoostInfo_ClosesOnceBeforeSelecting()
    {
        using var ocr = new OnnxTextRecognizer();
        var info = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/master-league-stage-boost-info.png"));
        var initial = Page(5, Enumerable.Range(0, 5).Select(_ => new MasterLeagueOpponent(0, false, false)).ToArray());
        var frames = new[] { Frame(1), Frame(2), Frame(3), Frame(6) };
        var adb = new FakeAdbService(info);
        GuardScreenshots(adb, 35);
        var residualFrames = 0;
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 || residualFrames++ < 3 ? info
            : adb.Taps.Contains((1701, 989)) ? frames[3] : adb.Taps.Contains((1675, 1000)) ? frames[2]
            : adb.Taps.Contains((1646, 893)) ? frames[1] : frames[0];
        var realVision = new MasterLeagueVision(ocr);
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>
            { [0] = initial, [1] = initial, [6] = Completed(initial) }, null)
            { StageBoostInfo = screen => realVision.FindStageBoostInfo(screen, default) };
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        Assert.True((await service.RunChallengesAsync("serial", vision, new(ocr), false)).Success);
        Assert.Equal(new[] { (960, 441), (715, 400), (1646, 893), (1675, 1000), (1701, 989) }, adb.Taps);
    }

    /// <summary>结算后旧列表先到、晋级提示后到，已选择下一目标也必须接管继续并清除上一轮记录。</summary>
    [Fact]
    public async Task Session_DelayedPromotionDuringSelection_ResumesNewRound()
    {
        using var ocr = new OnnxTextRecognizer();
        var promotion = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/master-league-promotion-next.png"));
        var log = new TestLog();
        var next = new OnnxTapToNextRecognizer(ocr, log).Recognize(ImageCodec.DecodePng(promotion), region: RecognitionRegion.ProgressTapToNext);
        Assert.NotNull(next);
        var initial = Page(5, new(0, false, false), new(100, true, false), new(0, false, false),
            new(100, true, false), new(100, true, false));
        var oldList = initial with { Opponents = initial.Opponents.Select((o, i) => i == 2 ? new MasterLeagueOpponent(100, true, false) : o).ToArray() };
        var newList = Page(5, Enumerable.Range(0, 5).Select(_ => new MasterLeagueOpponent(0, false, false)).ToArray());
        var frames = Enumerable.Range(1, 8).ToDictionary(i => i, i => Frame((byte)i));
        var adb = new FakeAdbService(frames[1]);
        GuardScreenshots(adb, 35);
        adb.ScreenshotProvider = () =>
        {
            var returns = adb.Taps.Count(t => t == (1701, 989));
            var challenges = adb.Taps.Count(t => t == (1675, 1000));
            var battles = adb.Taps.Count(t => t == (1646, 893));
            if (returns >= 2) return frames[6];
            if (returns == 0) return frames[challenges > 0 ? 3 : battles > 0 ? 2 : 1];
            if (adb.Taps.Contains((next.CenterX, next.CenterY))) return frames[challenges >= 2 ? 3 : battles >= 2 ? 2 : 7];
            return adb.Taps.Contains((143, 400)) ? promotion : frames[4];
        };
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>
            { [1] = initial, [4] = oldList, [7] = newList, [6] = Completed(newList) }, null);
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        Assert.True((await service.RunChallengesAsync("serial", vision, new(ocr), false)).Success);
        Assert.Single(adb.Taps, t => t == (next.CenterX, next.CenterY));
        Assert.Equal(2, adb.Taps.Count(t => t == (715, 400)));
        Assert.Equal(2, adb.Taps.Count(t => t == (1675, 1000)));
    }

    /// <summary>未知画面不盲点，但必须在实际第 120、240 秒执行并报告恢复检查，不能静默等五分钟。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_StallRecovery_UsesElapsedTimeAndReportsUnknown(bool networkError)
    {
        using var ocr = new OnnxTextRecognizer();
        using var cancel = new CancellationTokenSource();
        var clock = new ManualProgressTime();
        using var temp = new TempDirectory();
        var errorPath = Path.Combine(temp.Path, "error.png");
        File.WriteAllBytes(errorPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(200, 70, 210), 200, 70));
        var frame = ImageCodec.DecodePng(Frame(8));
        if (networkError) TestImageFactory.PasteBlock(frame.Bgra, 1920, 200, 70, 860, 205, 210);
        var adb = new FakeAdbService(ImageCodec.EncodePng(frame.Bgra, 1920, 1080));
        GuardScreenshots(adb, 12);
        var log = new TestLog();
        var detector = new ConnectionErrorDetector(adb, new TemplateMatcher(), log, errorPath);
        var monitor = new ExecutionStallMonitor((screen, ct) => Task.Run(() => detector.IsVisible(screen, ct), ct), log, clock);
        monitor.BeginSession();
        // 模拟共享设备边界；会话本身不再显式扫描网络错误。
        adb.BeforeScreenshotAsync = (count, ct) => count > 12
            ? Task.FromException(new InvalidDataException("会话未在 12 帧内结束"))
            : monitor.OnScreenshotAsync("serial", ImageCodec.EncodePng(frame.Bgra, 1920, 1080), ct);
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr,
            connectionErrorDetector: detector);
        var service = new MasterLeagueService(automation, log, (_, ct) =>
        {
            if (clock.GetTimestamp() < TimeSpan.FromMinutes(2).Ticks)
                Assert.DoesNotContain(log.Entries, e => e.Message.Contains("检测到网络错误弹窗"));
            clock.Advance(30);
            if (clock.GetTimestamp() >= TimeSpan.FromMinutes(5).Ticks) cancel.Cancel();
            ct.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }, timeProvider: clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunChallengesAsync("serial",
            new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>(), null), new(ocr), false, cancel.Token));
        var inspections = log.Entries.Where(e => e.Message.Contains("开始两分钟恢复检查")).ToArray();
        Assert.Equal(networkError ? 0 : 2, inspections.Length);
        var globalChecks = log.Entries.Where(e => e.Message.StartsWith("全局异常检测：")).ToArray();
        Assert.Equal(2, globalChecks.Length);
        Assert.Contains("120 秒", globalChecks[0].Message);
        Assert.Contains("240 秒", globalChecks[1].Message);
        Assert.Equal(networkError ? 0 : 2, log.Entries.Count(e => e.Message.Contains("恢复检查未发现可安全执行")));
        Assert.Equal(networkError ? 2 : 0, log.Entries.Count(e => e.Message.Contains("检测到网络错误弹窗")));
        Assert.Empty(adb.Taps);
    }

    /// <summary>错过结算导致仍保留战斗阶段时，两分钟恢复可识别真实继续提示，且不会重复发送挑战。</summary>
    [Fact]
    public async Task Session_StallRecovery_HandlesContinuationFromStaleBattlePhase()
    {
        using var ocr = new OnnxTextRecognizer();
        var clock = new ManualProgressTime();
        var promotion = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/master-league-promotion-next.png"));
        var log = new TestLog();
        var next = new OnnxTapToNextRecognizer(ocr, log).Recognize(ImageCodec.DecodePng(promotion), region: RecognitionRegion.ProgressTapToNext);
        Assert.NotNull(next);
        var initial = Page(5, Enumerable.Range(0, 5).Select(_ => new MasterLeagueOpponent(0, false, false)).ToArray());
        var initialFrame = Frame(1); var preparation = Frame(2); var done = Frame(6);
        var adb = new FakeAdbService(initialFrame);
        GuardScreenshots(adb, 18);
        adb.ScreenshotProvider = () => adb.Taps.Contains((next.CenterX, next.CenterY)) ? done
            : adb.Taps.Contains((1675, 1000)) ? promotion : adb.Taps.Contains((1646, 893)) ? preparation : initialFrame;
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage> { [1] = initial, [6] = Completed(initial) }, null);
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) =>
        { clock.Advance(30); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, timeProvider: clock);
        Assert.True((await service.RunChallengesAsync("serial", vision, new(ocr), false)).Success);
        Assert.Equal(new[] { (715, 400), (1646, 893), (1675, 1000), (next.CenterX, next.CenterY) }, adb.Taps);
        Assert.Contains(log.Entries, e => e.Message.Contains("开始两分钟恢复检查") && e.Message.Contains("Fighting"));
        Assert.Contains(log.Entries, e => e.Message.Contains("恢复检查已处理当前页面"));
    }

    /// <summary>真实五人全胜回放稳定确认后直接结束；刷新选项开启、次数和积分未知也不能继续等待或点击。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_AllVictory_CompletesWithoutFurtherActions(bool battleButtonMissing)
    {
        using var ocr = new OnnxTextRecognizer();
        var pixels = MasterLeagueVisionTests.Fixture("all-victory").Bgra.ToArray();
        // 保留选敌标题与真实胜利标记，清掉无关数字，并分别覆盖有/无对战按钮的页面。
        for (var y = 28; y < 74; y++) Array.Clear(pixels, (y * 1920 + 1100) * 4, 30 * 4);
        for (var y = 716; y < 782; y++) Array.Clear(pixels, y * 1920 * 4, 1445 * 4);
        if (battleButtonMissing)
            for (var y = 810; y < 970; y++) Array.Clear(pixels, (y * 1920 + 1445) * 4, 400 * 4);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        GuardScreenshots(adb, 3);
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var result = await service.RunChallengesAsync("serial", new MasterLeagueVision(ocr), new(ocr), true);
        Assert.True(result.Success);
        Assert.Contains("五名对手均已胜利", result.Message);
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>持续运行的回归不能自身无限挂起；超出脚本所需帧数时明确失败而非放宽断言。</summary>
    private static void GuardScreenshots(FakeAdbService adb, int maximum)
        => adb.BeforeScreenshotAsync = (count, _) => count > maximum
            ? Task.FromException(new InvalidDataException($"会话未在脚本的 {maximum} 帧内完成")) : Task.CompletedTask;
}

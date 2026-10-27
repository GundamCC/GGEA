using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>日服实机截图回归：部队权限、长空位提示以及最后一轮停止行为。</summary>
public sealed class JapaneseSortieTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ggea-ja-sortie-" + Guid.NewGuid().ToString("N"));

    /// <summary>独立外部路径避免并行用例互相切换资源语言。</summary>
    public JapaneseSortieTests() => Directory.CreateDirectory(_directory);

    /// <summary>日文观看按钮使用入口真实区域与文字校验，不能被同框的出击素材抢占。</summary>
    [Fact]
    public void Watch_JapaneseRealFrame_MatchesOnlyWatch()
    {
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "watch-mumu-ja.png")));
        var recognizer = new StageEntryButtonRecognizer();
        var area = new Roi(1470, 754, 450, 326);
        var result = recognizer.Match(screen, Resource("stage/sortie/watch.png"), area, .90, default);
        Assert.NotNull(result);
        Assert.InRange(result.CenterX, 1665, 1685);
        Assert.InRange(result.CenterY, 885, 905);
        Assert.Null(recognizer.Match(screen, Resource("stage/sortie/button_sortie.png"), area, .90, default));
    }

    /// <summary>同一实帧的两张不可出击机体均命中日文标签，普通机体不能因卡片背景相似而误命中。</summary>
    [Theory]
    [InlineData(640, true)]
    [InlineData(955, true)]
    [InlineData(325, false)]
    public void UnableToSortie_JapaneseLabel_DistinguishesUnitCards(int left, bool expected)
    {
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "unable-to-sortie-mumu-ja.png")));
        var template = ImageCodec.DecodePng(File.ReadAllBytes(Resource("stage/sortie/unable_to_sortie.png")));
        var result = new TemplateMatcher().Match(screen.Bgra, screen.Width, screen.Height,
            template.Bgra, template.Width, template.Height, .90, new Roi(left, 210, 300, 370));
        Assert.Equal(expected, result is not null);
    }

    /// <summary>实机剧情灰色背景曾命中提示；提示与左右按钮必须来自同一帧，真实空位弹窗仍可识别。</summary>
    [Fact]
    public void EmptyWarning_StoryTransitionRequiresSameFrameButtons()
    {
        var story = ImageCodec.DecodePng(Frame("progress-transition"));
        var hint = ImageCodec.DecodePng(File.ReadAllBytes(Resource("dialog/empty_slot_hint.png")));
        var matcher = new TemplateMatcher();
        Assert.NotNull(matcher.Match(story.Bgra, story.Width, story.Height, hint.Bgra,
            hint.Width, hint.Height, .85, SortieFlowService.EmptySlotHintRoi(story)));
        var adb = new FakeAdbService(Frame("progress-transition"));
        var log = new TestLog();
        using var automation = new AutomationService(adb, matcher, log);
        var service = CreateService(automation, log);
        Assert.Null(service.MatchEmptySlotDialog(story));
        Assert.NotNull(service.MatchEmptySlotDialog(ImageCodec.DecodePng(Frame("empty-warning"))));
        Assert.Empty(adb.Taps);
    }

    /// <summary>日文完整下载说明超出旧区域；真实出击入口先确认说明，再按禁止下载策略取消。</summary>
    [Fact]
    public async Task DownloadWarning_FullJapaneseSentence_CancelsThroughSortieFlow()
    {
        var adb = new FakeAdbService(Frame("download-dialog"));
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var result = await CreateService(automation, log).ExecuteAfterSortieAsync("replay", false, false, false,
            downloadDetectTimeoutMs: 3000, pollIntervalMs: 1);
        Assert.Equal(SortieFlowResult.DownloadCancelled, result);
        var tap = Assert.Single(adb.Taps);
        Assert.InRange(tap.X, 700, 850);
        Assert.InRange(tap.Y, 800, 900);
    }

    /// <summary>仅允许自由部队时，实际空槽触发一次编成，不能选择或调整系列部队。</summary>
    [Fact]
    public async Task Formation_FreeTeamOnly_PreservesSeriesTeam()
    {
        var frames = new[] { Frame("free-empty"), Frame("free-empty"), Frame("autoform"), Frame("formed") };
        var adb = new FakeAdbService(frames[0]);
        adb.ScreenshotProvider = () => frames[Math.Min(adb.Taps.Count, 3)];
        var log = new TestLog();
        var matcher = new TemplateMatcher();
        using var automation = new AutomationService(adb, matcher, log);
        var paths = new AutoFormationTemplates(Resource("stage/sortie/sortie.png"),
            Resource("stage/sortie/empty_unit_slot.png"), Resource("stage/sortie/empty_pilot_slot.png"),
            Resource("stage/sortie/unable_to_sortie.png"), Resource("stage/sortie/button_auto-form.png"),
            Resource("stage/sortie/auto-form.png"), Resource("common/button_confirm.png"), Resource("stage/sortie/locked_unit_slot.png"));
        var service = new AutoFormationService(automation, matcher, log, paths, (_, _) => Task.CompletedTask, maxPolls: 3);
        var settings = new AutoProgressSettings { AdjustSeriesTeam = false, AdjustFreeTeam = true };

        var result = await service.PrepareForSortieAsync("replay", settings, new AutoFormationSession(), _ => { }, default);

        Assert.Equal(FormationPreflightResult.Ready, result);
        Assert.Equal(3, adb.Taps.Count);
        Assert.Equal((145, 680), adb.Taps[0]);
        Assert.InRange(adb.Taps[1].X, 1200, 1350);
        Assert.InRange(adb.Taps[2].X, 1100, 1200);
        Assert.DoesNotContain((145, 445), adb.Taps);
    }

    /// <summary>完整日文提示必须位于生产区域；未允许空位出击时点击取消，不消耗 AP。</summary>
    [Fact]
    public async Task EmptyWarning_JapaneseLongText_CancelsWhenNotAllowed()
    {
        var adb = new FakeAdbService(Frame("empty-warning"));
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = CreateService(automation, log);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await service.ExecuteAfterSortieAsync("replay", true, false, false,
            downloadDetectTimeoutMs: 1000, pollIntervalMs: 1, ct: timeout.Token);

        Assert.Equal(SortieFlowResult.EmptySlotCancelled, result);
        var tap = Assert.Single(adb.Taps);
        Assert.InRange(tap.X, 700, 850);
        Assert.InRange(tap.Y, 800, 900);
    }

    /// <summary>使用第二遍独立结算截图，验证继续两次后仅按剩余次数点击再战或返回。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Settlement_ActualSecondRun_RespectsRemainingRounds(bool moreRounds)
    {
        var battle = Frame("battle");
        var frames = new[] { Frame("score"), Frame("exp"), Frame("result") };
        var adb = new FakeAdbService(battle);
        adb.ScreenshotProvider = () => adb.ScreenshotCount == 1 ? battle : frames[Math.Min(adb.Taps.Count, 2)];
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = CreateService(automation, log);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var result = await service.ExecuteNextRoundAsync("replay", moreRounds,
            tapToNextWaitTimeoutMs: 1000, tapToNextPollIntervalMs: 1, continueStartDelayMs: 1,
            continueClickDelayMs: 1, continuePollIntervalMs: 1, continueWaitTimeoutMs: 3000,
            continueRepeatIntervalMs: 1, againStartDelayMs: 1, againPollIntervalMs: 1, againWaitTimeoutMs: 3000,
            ct: timeout.Token);

        Assert.Equal(moreRounds ? SortieFlowResult.RoundCompleted : SortieFlowResult.AllRoundsCompleted, result);
        Assert.Equal(3, adb.Taps.Count);
        Assert.All(adb.Taps.Take(2), tap => Assert.InRange(tap.X, 1600, 1750));
        Assert.InRange(adb.Taps[2].X, moreRounds ? 1200 : 1600, moreRounds ? 1400 : 1750);
    }

    /// <summary>生产服务使用同一组资源解析结果，不使用人工替代识别器。</summary>
    private SortieFlowService CreateService(AutomationService automation, TestLog log)
    {
        var matcher = new TemplateMatcher();
        string Sortie(string name) => Resource("stage/sortie/" + name + ".png");
        var dialog = new DownloadDialogService(automation, matcher, log,
            Resource("dialog/download.png"), Resource("dialog/cancel.png"), .85);
        return new SortieFlowService(automation, matcher, log, dialog, Sortie("tap_to_next"),
            Sortie("double_speed_on"), Sortie("double_speed_close"), Sortie("continue"), Sortie("again"),
            Sortie("select_stage"), Sortie("again_disabled"), Sortie("failed"), Resource("dialog/empty_slot_hint.png"),
            Resource("dialog/sortie.png"), Resource("dialog/cancel.png"), Sortie("no_ap"),
            Resource("common/player_level_up.png"), Resource("common/reward.png"), Sortie("tap_to_next_reward"), .85,
            sortieDownloadTemplatePath: Sortie("download"));
    }

    /// <summary>复制日语或共用回退素材到测试独占目录，阻止全局回退重新选择其它语言。</summary>
    private string Resource(string relative)
    {
        var target = Path.Combine(_directory, relative);
        if (File.Exists(target)) return target;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var resolver = new ResourceResolver(AppContext.BaseDirectory, "ja");
        File.Copy(resolver.Resolve("resources/templates/" + relative), target);
        return target;
    }

    /// <summary>原始 ADB 截图保留完整像素。</summary>
    private static byte[] Frame(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Sortie/ja-" + name + ".png"));

    /// <summary>只回收本实例创建的临时资源。</summary>
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

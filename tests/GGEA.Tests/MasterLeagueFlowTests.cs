using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>验证真实编排调用链及消费边界；视觉数值替身与独立的实机 OCR 回放测试分工。</summary>
public sealed class MasterLeagueFlowTests
{
    /// <summary>从选敌到准备、挑战、自动结束、结算返回，必要时先刷新失败对手，最后在可挑战对手耗尽时结束。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_ConnectsCompleteCycle_WithOptionalDefeatRefresh(bool refresh)
    {
        using var ocr = new OnnxTextRecognizer();
        var initial = refresh ? Page(5, new(30000, true, false), new(29000, false, true),
            new(30000, true, false), new(30000, true, false), new(30000, true, false))
            : Page(5, new(100, true, false), new(0, false, false), new(100, true, false), new(100, true, false), new(100, true, false));
        var updated = initial.Opponents.ToArray(); updated[1] = new(refresh ? 31000 : 2000, true, false);
        var final = Page(refresh ? 4 : 5, updated);
        var reset = initial.Opponents.ToArray(); reset[1] = new(0, false, false);
        // 用户要求零分即可确认刷新，允许其他显示字段尚未更新。
        var refreshed = Page(5, reset);
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage> { [1] = initial, [6] = final, [7] = refreshed },
            new(initial.Opponents[1].Points!.Value, 5, Match(968, 935, 360, 120)));
        var adb = new FakeAdbService(Frame(1));
        var frames = Enumerable.Range(1, 7).ToDictionary(i => i, i => Frame((byte)i));
        adb.ScreenshotProvider = () => frames[adb.Taps.Contains((1701, 989)) ? 6
            : adb.Taps.Contains((1675, 1000)) ? 3 : adb.Taps.Contains((1646, 893)) ? 2
            : adb.Taps.Contains((1148, 995)) ? 7 : adb.Taps.Contains((509, 913)) ? 4 : 1];
        var log = new TestLog();
        // 连续运行测试也必须有明确脚本上限，避免回归形成无限测试进程。
        adb.BeforeScreenshotAsync = (count, _) => count > 40
            ? Task.FromException(new InvalidDataException("完整循环超出脚本帧数：" + string.Join(" | ", log.Entries.TakeLast(8).Select(e => e.Message))))
            : Task.CompletedTask;
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var result = await Service(automation, log).RunChallengesAsync("serial", vision, new(ocr), refresh);
        Assert.True(result.Success, result.Message);
        var expected = new List<(int, int)>();
        if (refresh) expected.AddRange([(509, 913), (1148, 995)]);
        expected.AddRange([(429, 400), (1646, 893), (1675, 1000), (1701, 989)]);
        Assert.Equal(expected, adb.Taps);
        Assert.DoesNotContain("150000", result.Message);
    }

    /// <summary>旧帧有 SKIP，新帧已结算时不能点旧位置；同帧两者都命中也必须结算优先。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Battle_ResultWinsOverSkip(bool sameFrame)
    {
        var adb = new FakeAdbService(Frame(1));
        adb.EnqueueScreenshots(Frame(3));
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>(), null)
        {
            Battle = screen => screen.Bgra[0] == 3 || sameFrame
                ? new(true, Match(1418, 12, 184, 83), Match(1520, 928, 362, 122))
                : new(false, Match(1418, 12, 184, 83), null)
        };
        var result = await Service(automation, log).WaitForBattleResultAsync("serial", vision, default);
        Assert.True(result.Success);
        Assert.Empty(adb.Taps);
        Assert.Equal(sameFrame ? 1 : 2, adb.ScreenshotCount);
    }

    /// <summary>缺少 SKIP 不阻塞结算；发送一次 SKIP 后，不因按钮暂时未消失而重复点击。</summary>
    [Fact]
    public async Task Battle_SkipAtMostOnce_ThenAcceptsResult()
    {
        var adb = new FakeAdbService(Frame(1));
        adb.EnqueueScreenshots(Frame(1), Frame(1), Frame(3));
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>(), null)
        {
            Battle = screen => screen.Bgra[0] == 3
                ? new(true, null, Match(1520, 928, 362, 122)) : new(false, Match(1418, 12, 184, 83), null)
        };
        Assert.True((await Service(automation, log).WaitForBattleResultAsync("serial", vision, default)).Success);
        Assert.Equal(new[] { (1510, 53) }, adb.Taps);
    }

    /// <summary>挑战后下载弹窗遵守许可；持续显示时只确认一次，并继续接收结算。</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Battle_DownloadDialog_RespectsPermissionAndDoesNotRepeat(bool allow, bool appearsDuringSkipRecheck)
    {
        var config = new AppConfig { AllowGgeaDownload = allow };
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        void Paste(string path, int x, int y)
        {
            var image = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(path)));
            for (var row = 0; row < image.Height; row++) Buffer.BlockCopy(image.Bgra, row * image.Width * 4,
                pixels, ((y + row) * 1920 + x) * 4, image.Width * 4);
        }
        Paste(config.SortieDownloadTemplate, 600, 100);
        Paste(config.DownloadTemplate, 970, 800);
        Paste(config.DownloadCancelTemplate, 570, 800);
        var dialog = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(appearsDuringSkipRecheck ? Frame(1) : dialog);
        if (appearsDuringSkipRecheck) adb.EnqueueScreenshots(dialog);
        adb.EnqueueScreenshots(dialog, Frame(3));
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new MasterLeagueService(automation, log, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, config);
        var vision = new FakeVision(new Dictionary<byte, MasterLeagueOpponentPage>(), null)
        {
            Battle = screen => screen.Bgra[0] == 3
                ? new(true, null, Match(1520, 928, 362, 122))
                : new(false, appearsDuringSkipRecheck ? Match(1418, 12, 184, 83) : null, null)
        };
        var result = await service.WaitForBattleResultAsync("serial", vision, default);
        Assert.Equal(allow, result.Success);
        var tap = Assert.Single(adb.Taps);
        var button = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(allow ? config.DownloadTemplate : config.DownloadCancelTemplate)));
        Assert.Equal((allow ? 970 : 570) + button.Width / 2, tap.X);
        Assert.Equal(800 + button.Height / 2, tap.Y);
        // 复核帧新出现下载时先撤销 SKIP；下一轮新帧才按下载许可裁决，不能沿用旧输入计划。
        Assert.Equal(appearsDuringSkipRecheck ? (allow ? 4 : 3) : (allow ? 3 : 1), adb.ScreenshotCount);
    }

    /// <summary>准备页即使标题存在，零次或未知次数也不能点击挑战。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public async Task Preparation_NoReliableAttempts_DoesNotChallenge(int? remaining)
    {
        var adb = new FakeAdbService(Frame(2));
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var result = await Service(automation, log).ChallengePreparedBattleAsync("serial", default, (_, _) => remaining);
        Assert.False(result.Success);
        Assert.Empty(adb.Taps);
    }

    /// <summary>原操作尚有未挑战者时不能刷新；关掉选项或次数耗尽时保留游戏状态。</summary>
    [Fact]
    public void Policy_RespectsUnchallengedFirst_AndRefreshOption()
    {
        var page = Page(5, new(100, false, true), new(0, false, false), new(100, true, false), new(100, true, false), new(100, true, false));
        Assert.Equal((1, false, (string?)null), MasterLeagueProgress.Choose(page, true));
        var done = page.Opponents.ToArray(); done[1] = new(100, true, false);
        page = Page(5, done);
        Assert.NotNull(MasterLeagueProgress.Choose(page, false).StopReason);
        Assert.Equal((0, true, (string?)null), MasterLeagueProgress.Choose(page, true));
        Assert.NotNull(MasterLeagueProgress.Choose(Page(0, done), true).StopReason);
    }

    /// <summary>每轮新列表与败北刷新都遵循 32415，已处理者依次跳过。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Policy_Uses32415Order(bool defeated)
    {
        var opponents = Enumerable.Range(0, 5).Select(_ => new MasterLeagueOpponent(defeated ? 100 : 0, false, defeated)).ToArray();
        foreach (var expected in new[] { 2, 1, 3, 0, 4 })
        {
            Assert.Equal((expected, defeated, (string?)null), MasterLeagueProgress.Choose(Page(5, opponents), defeated));
            opponents[expected] = new(100, true, false);
        }
        Assert.NotNull(MasterLeagueProgress.Choose(Page(5, opponents), defeated).StopReason);
    }

    /// <summary>仅替换时间；截图、页面确认和最终点击仍由真实 AutomationService 处理。</summary>
    private static MasterLeagueService Service(AutomationService automation, TestLog log)
        => new(automation, log, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

    /// <summary>模型状态显式列出积分，不伪造 OCR 对未知图片的成功结果。</summary>
    internal static MasterLeagueOpponentPage Page(int remaining, params MasterLeagueOpponent[] opponents)
        => new(remaining, opponents);

    /// <summary>测试用匹配结果仍保留实际按钮矩形和中心。</summary>
    internal static TemplateMatchResult Match(int x, int y, int width, int height) => new(new Roi(x, y, width, height), 1);

    /// <summary>真实阶段标题/按钮和 OCR 裁剪组合成页面；左上角单像素仅供视觉替身识别状态。</summary>
    internal static byte[] Frame(byte marker)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        pixels[0] = marker;
        void Paste(string path, int x, int y)
        {
            var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
            for (var row = 0; row < image.Height; row++) Buffer.BlockCopy(image.Bgra, row * image.Width * 4, pixels,
                ((y + row) * 1920 + x) * 4, image.Width * 4);
        }
        if (marker == 2) Paste(ConfigService.ResolvePath(MasterLeagueService.BattlePreparationTitleTemplate), 218, 20);
        if (marker == 3)
        {
            Paste(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/master-league-score-win.png"), 720, 275);
            Paste(ConfigService.ResolvePath(MasterLeagueService.ResultSelectOpponentTemplate), 1520, 928);
        }
        if (marker == 9) TapToNextOcrSamples.Paste(pixels, 1920, 360, 80, 780, 940);
        if (marker == 10)
        {
            var config = new AppConfig();
            Paste(ConfigService.ResolvePath(config.SortieDownloadTemplate), 600, 100);
            Paste(ConfigService.ResolvePath(config.DownloadTemplate), 968, 787);
            Paste(ConfigService.ResolvePath(config.DownloadCancelTemplate), 570, 800);
        }
        if (marker is 1 or 6 or 7) Paste(ConfigService.ResolvePath(MasterLeagueService.SelectOpponentTitleTemplate), 218, 20);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }

    /// <summary>脚本化视觉观察，让编排测试不依赖真实游戏打满十五万分；实际识别由 VisionTests 单独验证。</summary>
    internal sealed class FakeVision(IReadOnlyDictionary<byte, MasterLeagueOpponentPage> pages,
        MasterLeagueRefreshDialog? dialog) : IMasterLeagueVision
    {
        public Func<ScreenImage, MasterLeagueBattleFrame>? Battle { get; init; }
        public void Validate() { }
        public Func<ScreenImage, TemplateMatchResult?>? StageBoostInfo { get; init; }
        public TemplateMatchResult? FindStageBoostInfo(ScreenImage screen, CancellationToken ct) => StageBoostInfo?.Invoke(screen);
        public MasterLeagueOpponentPage? ReadOpponents(ScreenImage screen, CancellationToken ct)
            => pages.GetValueOrDefault(screen.Bgra[0]);
        public MasterLeagueBattleFrame ReadBattle(ScreenImage screen, CancellationToken ct)
            => Battle?.Invoke(screen) ?? new(screen.Bgra[0] == 3, null, screen.Bgra[0] == 3 ? Match(1520, 928, 362, 122) : null);
        public MasterLeagueRefreshDialog? ReadRefreshDialog(ScreenImage screen, CancellationToken ct)
            => screen.Bgra[0] == 4 ? dialog : null;
        public TemplateMatchResult? FindRefresh(ScreenImage screen, int opponent, CancellationToken ct)
            => Match(190 + 286 * opponent, 880, 66, 66);
        public TemplateMatchResult? FindBattleButton(ScreenImage screen, CancellationToken ct) => Match(1466, 833, 360, 120);
        public Func<ScreenImage, int?>? ChallengeRemaining { get; init; }
        public int? ReadChallengeRemaining(ScreenImage screen, CancellationToken ct)
            => ChallengeRemaining is not null ? ChallengeRemaining(screen) : screen.Bgra[0] == 2 ? 5 : null;
    }
}

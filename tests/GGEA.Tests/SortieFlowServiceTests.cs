using System.IO;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 出击后流程服务测试：TAP TO NEXT 先出现、下载界面先出现（允许/禁止下载）、
/// 两者都未出现、继续按钮超时、再次出击识别及剩余次数决策。
/// </summary>
public class SortieFlowServiceTests
{
    /// <summary>回放雷电错误码100：首次继续按钮的十五分钟等待也必须在统一两分钟检查时中断。</summary>
    [Fact]
    public async Task LongBattleWait_GlobalMonitorDetectsActualNetworkError()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var error = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/zh-connection-error.png"));
        var opening = CreateTapToNextScreen();
        var clock = new ManualProgressTime();
        var log = new TestLog();
        var screenshots = 0;
        var inputs = 0;
        var adb = new AdbService("unused-adb", log)
        {
            DeviceTextAsync = (args, _, _) =>
            {
                if (args.Contains("input")) inputs++;
                return Task.FromResult(string.Empty);
            },
            DeviceBytesAsync = (_, _, _) =>
            {
                if (++screenshots > 10) throw new InvalidDataException("未在统一检测期限内停止");
                clock.Advance(30);
                return Task.FromResult(screenshots == 1 ? opening : error);
            },
        };
        var path = Path.Combine(temp.Path, "network.png");
        File.Copy(ConfigService.ResolvePath("resources/templates/entry/connection_error.png"), path);
        var detector = new ConnectionErrorDetector(adb, new TemplateMatcher(), log, path);
        var monitor = new ExecutionStallMonitor((screen, ct) => Task.Run(() => detector.IsVisible(screen, ct), ct), log, clock);
        adb.ExecutionObserver = monitor;
        monitor.BeginSession();
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint,
            sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward,
            testLog: log);
        await Assert.ThrowsAsync<ConnectionErrorException>(() => service.ExecuteNextRoundAsync("serial", true,
            tapToNextClickDelayMs: 1, continueStartDelayMs: 1, continuePollIntervalMs: 1));
        Assert.Equal(1, inputs); // 仅开场继续；网络弹窗不能触发重试、返回或结算点击。
        Assert.InRange(clock.GetTimestamp(), 0, TimeSpan.FromMinutes(3).Ticks);
        Assert.Contains(log.Entries, e => e.Message.Contains("检测到网络错误弹窗"));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("等待继续按钮超时"));
        Assert.True(await detector.DetectAfterFailureAsync("serial", settleDelayMs: 0));
    }

    /// <summary>日文地形提示完整正文宽于旧区域，实帧仍按用户策略确认出击或取消。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAfterSortieAsync_JapaneseTerrain_RespectsPolicy(bool allow)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var root = Path.Combine(AppContext.BaseDirectory, "resources", "ja", "templates");
        File.Copy(Path.Combine(root, "stage", "sortie", "terrain.png"), sortie.Terrain, true);
        File.Copy(Path.Combine(root, "dialog", "sortie.png"), sortie.DialogSortie, true);
        File.Copy(Path.Combine(root, "dialog", "cancel.png"), sortie.DialogCancel, true);
        var adb = new FakeAdbService(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "terrain-mumu-ja.png")));
        if (allow) adb.EnqueueScreenshots(CreateTapToNextScreen(), CreateIconsScreen(AutoGray, 160),
            CreateIconsScreen(AutoGray, 160), CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(),
            CreateContinueScreen(), CreateAgainScreen(), CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint,
            sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward,
            sortie.TapToNextReward, terrainTemplate: sortie.Terrain);
        var result = allow ? await RunAsync(service, adb, moreRoundsRemaining: true)
            : await service.ExecuteAfterSortieAsync("serial", allowDownload: true, allowEmptySlotSortie: true,
                moreRoundsRemaining: true, allowTerrainUnsuitableSortie: false, downloadDetectTimeoutMs: 100, pollIntervalMs: 1,
                tapToNextWaitTimeoutMs: 100, tapToNextPollIntervalMs: 1);
        Assert.Equal(allow ? SortieFlowResult.RoundCompleted : SortieFlowResult.TerrainCancelled, result);
        if (!allow) Assert.Single(adb.Taps);
        Assert.InRange(adb.Taps[0].X, allow ? 1140 : 760, allow ? 1160 : 780);
        Assert.InRange(adb.Taps[0].Y, 840, 860);
    }

    /// <summary>MuMu 日文实帧的 AP 不足询问沿用取消流程，绝不点击右侧恢复确认。</summary>
    [Fact]
    public async Task ExecuteNextRoundAsync_MuMuJapaneseNoAp_CancelsWithoutRecovery()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "resources", "ja", "templates", "stage", "sortie", "no_ap.png"), sortie.NoAp, true);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "resources", "ja", "templates", "dialog", "cancel.png"), sortie.DialogCancel, true);
        var adb = new FakeAdbService(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sortie", "no-ap-mumu-ja.png")));
        var notices = new List<string>();
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint,
            sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward,
            sortie.TapToNextReward, apStopped: notices.Add);

        var result = await service.ExecuteNextRoundAsync("serial", moreRoundsRemaining: true, tapToNextPollIntervalMs: 1);

        Assert.Equal(SortieFlowResult.NoApCancelled, result);
        Assert.Contains("AP 不足", Assert.Single(notices));
        var tap = Assert.Single(adb.Taps);
        Assert.InRange(tap.X, 765, 775);
        Assert.InRange(tap.Y, 845, 855);
    }

    private const int ScreenWidth = 1920;
    private const int ScreenHeight = 1080;

    /// <summary>用户裁剪及浅色背景压力场景必须从首次、再次出击入口识别到正确文字位置。</summary>
    [Theory]
    [InlineData(false, -1, 724)]
    [InlineData(true, -1, 724)]
    [InlineData(false, 160, 724)]
    [InlineData(true, 160, 724)]
    [InlineData(false, -1, 100)]
    [InlineData(true, -1, 1300)]
    public async Task TapToNext_UserBackground_RecognizesCorrectPosition(bool nextRound, int background, int cropX)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var frame = SortieTapToNextSamples.CreateFrame(background, x: cropX);
        var adb = new FakeAdbService(frame);
        if (!nextRound) adb.EnqueueScreenshots(CreateIconsScreen(AutoGray, 160), CreateIconsScreen(AutoGray, 160));
        adb.EnqueueScreenshots(CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(),
            CreateAgainScreen(), CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie,
            sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = nextRound ? await RunNextRoundAsync(service, adb, true) : await RunAsync(service, adb, true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        var tap = adb.Taps[nextRound ? 0 : 2];
        Assert.InRange(tap.X, cropX + 214, cropX + 218);
        Assert.InRange(tap.Y, 968, 972);
        Assert.DoesNotContain((960, 540), adb.Taps);
    }

    /// <summary>初始帧与轮询帧均须优先处理真实开场文字，不能被同时命中的奖励标题提前分流。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteNextRoundAsync_TapWithSettlementSignal_RecognizesTapFirst(bool signalInInitialFrame)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var pixels = ImageCodec.DecodePng(SortieTapToNextSamples.CreateFrame());
        TestImageFactory.PasteBlock(pixels.Bgra, ScreenWidth, 162, 59, 870, 30, 88);
        var tapWithReward = ImageCodec.EncodePng(pixels.Bgra, ScreenWidth, ScreenHeight);
        var adb = new FakeAdbService(signalInInitialFrame ? tapWithReward : CreatePlainScreen());
        if (!signalInInitialFrame) adb.EnqueueScreenshots(tapWithReward);
        adb.EnqueueScreenshots(CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(),
            CreateContinueScreen(), CreateAgainScreen(), CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie,
            sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await RunNextRoundAsync(service, adb, moreRoundsRemaining: true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(4, adb.Taps.Count);
        Assert.InRange(adb.Taps[0].X, 938, 942);
        Assert.InRange(adb.Taps[0].Y, 968, 972);
    }

    /// <summary>AUTO 关闭状态（灰色图标）。</summary>
    private static readonly (byte R, byte G, byte B) AutoGray = (120, 120, 120);

    /// <summary>AUTO 敌军自动状态（红色图标）。</summary>
    private static readonly (byte R, byte G, byte B) AutoRed = (200, 50, 50);

    /// <summary>AUTO 全部自动状态（蓝色图标）。</summary>
    private static readonly (byte R, byte G, byte B) AutoBlue = (50, 150, 200);

    private static (string Download, string Cancel) CreateDialogTemplates(TempDirectory temp)
    {
        var download = Path.Combine(temp.Path, "download.png");
        var cancel = Path.Combine(temp.Path, "cancel.png");
        File.WriteAllBytes(download, ImageCodec.EncodePng(TestImageFactory.CreateBgra(362, 127, 200), 362, 127));
        File.WriteAllBytes(cancel, ImageCodec.EncodePng(TestImageFactory.CreateBgra(200, 100, 90), 200, 100));
        return (download, cancel);
    }

    private static (string Tap, string Terrain, string DownloadHint, string SpeedOn, string SpeedOff, string Continue, string Again, string SelectStage, string AgainClose, string Failed, string EmptySlotHint, string DialogSortie, string DialogCancel, string NoAp, string PlayerLevelUp, string Reward, string TapToNextReward)
        CreateSortieTemplates(TempDirectory temp)
    {
        var tap = Path.Combine(temp.Path, "tap.png");
        var terrain = Path.Combine(temp.Path, "terrain.png");
        var downloadHint = Path.Combine(temp.Path, "download_hint.png");
        var speedOn = Path.Combine(temp.Path, "speed_on.png");
        var speedOff = Path.Combine(temp.Path, "speed_off.png");
        var cont = Path.Combine(temp.Path, "continue.png");
        var again = Path.Combine(temp.Path, "again.png");
        var selectStage = Path.Combine(temp.Path, "select_stage.png");
        var againClose = Path.Combine(temp.Path, "again_close.png");
        var failed = Path.Combine(temp.Path, "failed.png");
        var emptySlotHint = Path.Combine(temp.Path, "empty_slot_hint.png");
        var dialogSortie = Path.Combine(temp.Path, "dialog_sortie.png");
        var dialogCancel = Path.Combine(temp.Path, "dialog_cancel.png");
        var noAp = Path.Combine(temp.Path, "no_ap.png");
        var playerLevelUp = Path.Combine(temp.Path, "player_level_up.png");
        var reward = Path.Combine(temp.Path, "reward.png");
        var tapToNextReward = Path.Combine(temp.Path, "tap_to_next_reward.png");
        // 开场提示验证真实文字形状，纯色占位无法测试归一化相关系数匹配。
        File.WriteAllBytes(tap, File.ReadAllBytes(ConfigService.ResolvePath("resources/templates/stage/sortie/tap_to_next.png")));
        File.WriteAllBytes(terrain, ImageCodec.EncodePng(TestImageFactory.CreateBgra(320, 150, 215), 320, 150));
        File.WriteAllBytes(downloadHint, ImageCodec.EncodePng(TestImageFactory.CreateBgra(320, 150, 0), 320, 150));
        File.Copy(ConfigService.ResolvePath("resources/templates/stage/sortie/double_speed_on.png"), speedOn);
        File.Copy(ConfigService.ResolvePath("resources/templates/stage/sortie/double_speed_close.png"), speedOff);
        File.WriteAllBytes(cont, ImageCodec.EncodePng(TestImageFactory.CreateBgra(362, 123, 128), 362, 123));
        File.WriteAllBytes(again, ImageCodec.EncodePng(TestImageFactory.CreateBgra(361, 120, 60), 361, 120));
        File.WriteAllBytes(selectStage, ImageCodec.EncodePng(TestImageFactory.CreateBgra(362, 123, 118), 362, 123));
        File.WriteAllBytes(againClose, ImageCodec.EncodePng(TestImageFactory.CreateBgra(363, 122, 88), 363, 122));
        File.WriteAllBytes(failed, ImageCodec.EncodePng(TestImageFactory.CreateBgra(317, 100, 170), 317, 100));
        File.WriteAllBytes(emptySlotHint, ImageCodec.EncodePng(TestImageFactory.CreateBgra(296, 106, 210), 296, 106));
        File.WriteAllBytes(dialogSortie, ImageCodec.EncodePng(TestImageFactory.CreateBgra(362, 123, 190), 362, 123));
        File.WriteAllBytes(dialogCancel, ImageCodec.EncodePng(TestImageFactory.CreateBgra(365, 118, 100), 365, 118));
        File.WriteAllBytes(noAp, ImageCodec.EncodePng(TestImageFactory.CreateBgra(252, 96, 250), 252, 96));
        File.WriteAllBytes(playerLevelUp, ImageCodec.EncodePng(TestImageFactory.CreateBgra(559, 74, 230), 559, 74));
        File.WriteAllBytes(reward, ImageCodec.EncodePng(TestImageFactory.CreateBgra(162, 59, 88), 162, 59));
        File.WriteAllBytes(tapToNextReward, ImageCodec.EncodePng(TestImageFactory.CreateBgra(303, 54, 185), 303, 54));
        return (tap, terrain, downloadHint, speedOn, speedOff, cont, again, selectStage, againClose, failed, emptySlotHint, dialogSortie, dialogCancel, noAp, playerLevelUp, reward, tapToNextReward);
    }

    private static SortieFlowService CreateService(
        IAdbService adb,
        string download,
        string cancel,
        string tap,
        string speedOn,
        string speedOff,
        string continueTemplate,
        string againTemplate,
        string againCloseTemplate,
        string failedTemplate,
        string emptySlotHintTemplate,
        string dialogSortieTemplate,
        string dialogCancelTemplate,
        string noApTemplate,
        string playerLevelUpTemplate,
        string rewardTemplate,
        string tapToNextRewardTemplate,
        string selectStageTemplate = "",
        string terrainTemplate = "",
        string sortieDownloadTemplate = "",
        IEnumerable<string>? globalTapTemplates = null,
        int tapToNextCheckDelayMs = 10000,
        TestLog? testLog = null, Action<string>? apStopped = null)
    {
        var matcher = new TemplateMatcher();
        var log = testLog ?? new TestLog();
        var automation = new AutomationService(adb, matcher, log, tapToNextTemplatePaths: globalTapTemplates ?? [tap],
            tapToNextCheckDelayMs: tapToNextCheckDelayMs, textOcr: TapToNextOcrSamples.SharedOcr);
        var dialogService = new DownloadDialogService(automation, matcher, log, download, cancel, threshold: 0.85);
        return new SortieFlowService(
            automation,
            matcher,
            log,
            dialogService,
            tap,
            speedOn,
            speedOff,
            continueTemplate,
            againTemplate,
            selectStageTemplate,
            againCloseTemplate,
            failedTemplate,
            emptySlotHintTemplate,
            dialogSortieTemplate,
            dialogCancelTemplate,
            noApTemplate,
            playerLevelUpTemplate,
            rewardTemplate,
            tapToNextRewardTemplate,
            threshold: 0.85,
            terrainTemplatePath: terrainTemplate,
            sortieDownloadTemplatePath: sortieDownloadTemplate,
            apStopSignal: apStopped is null ? null : new ApStopSignal(apStopped, log));
    }

    private static byte[] CreateTapToNextScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        var template = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath("resources/templates/stage/sortie/tap_to_next.png")));
        for (var y = 0; y < template.Height; y++)
            for (var x = 0; x < template.Width; x++)
            {
                var source = (y * template.Width + x) * 4;
                if (template.Bgra[source + 3] < 128) continue;
                Buffer.BlockCopy(template.Bgra, source, screen, ((940 + y) * ScreenWidth + 818 + x) * 4, 4);
            }
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    private static byte[] CreateDialogScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 362, 127, 700, 800, 200); // 下载按钮
        TestImageFactory.PasteBlock(screen, ScreenWidth, 200, 100, 1150, 800, 90); // 取消按钮
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    /// <summary>下载提示页：上方提示与下方下载/取消按钮同时出现。</summary>
    private static byte[] CreateDownloadPromptScreen(bool includeDownloadButton)
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 320, 150, 800, 120, 0);
        if (includeDownloadButton)
        {
            TestImageFactory.PasteBlock(screen, ScreenWidth, 362, 127, 700, 800, 200);
        }
        else
        {
            TestImageFactory.PasteBlock(screen, ScreenWidth, 200, 100, 1150, 800, 90);
        }
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    /// <summary>地形适性提示页：上方提示与下方出击/取消按钮同时出现。</summary>
    private static byte[] CreateTerrainPromptScreen(bool includeSortieButton)
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 320, 150, 800, 120, 215);
        if (includeSortieButton)
        {
            TestImageFactory.PasteBlock(screen, ScreenWidth, 362, 123, 700, 850, 190);
        }
        else
        {
            TestImageFactory.PasteBlock(screen, ScreenWidth, 365, 118, 950, 850, 100);
        }
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    private static byte[] CreateIconsScreen((byte R, byte G, byte B) autoColor, byte speedValue)
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        // 必须提供真实 AUTO 字形与颜色，单色块不再能授权切换。
        var autoName = autoColor == AutoBlue ? "all" : autoColor == AutoRed ? "enemy" : "close";
        var auto = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath($"resources/templates/stage/sortie/auto_{autoName}.png")));
        PasteTemplate(screen, auto, 1420, 10);
        // 速度识别必须有双箭头和真实颜色，旧灰阶块不能再代表可点击按钮。
        if (speedValue == 20) return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
        var name = speedValue == 160 ? "double_speed_close" : "double_speed_on";
        var speed = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath($"resources/templates/stage/sortie/{name}.png")));
        for (var y = 0; y < speed.Height; y++)
            Buffer.BlockCopy(speed.Bgra, y * speed.Width * 4, screen, ((30 + y) * ScreenWidth + 1630) * 4, speed.Width * 4);
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    private static byte[] CreateContinueScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 362, 123, 1550, 950, 140); // 继续按钮（右侧 450、底部 175 区域）
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    /// <summary>将完整继续按钮素材放在指定位置，验证真实模板在底部搜索区中的识别与点击。</summary>
    private static byte[] CreateContinueScreen(string templatePath, int x, int y)
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        PasteTemplate(screen, ImageCodec.DecodePng(File.ReadAllBytes(templatePath)), x, y);
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    /// <summary>合成测试画布，只拷贝模板像素，不改变真实素材。</summary>
    private static void PasteTemplate(byte[] pixels, ScreenImage template, int x, int y)
    {
        for (var row = 0; row < template.Height; row++)
            Array.Copy(template.Bgra, row * template.Width * 4, pixels, ((y + row) * ScreenWidth + x) * 4, template.Width * 4);
    }

    private static byte[] CreateAgainScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 361, 120, 1120, 930, 60); // 再次出击按钮（右侧 435~825、底部 175 区域）
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    /// <summary>结算完成页：左侧再次出击与右侧选择关卡同时出现。</summary>
    private static byte[] CreateResultScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 361, 120, 1120, 930, 60);
        // 值 118 与继续模板（128）的相似度约为 96.1%，但仍明显更接近选择关卡模板。
        TestImageFactory.PasteBlock(screen, ScreenWidth, 362, 123, 1550, 950, 118);
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    private static byte[] CreateApInsufficientScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 361, 120, 1120, 930, 88); // 再次出击按钮关闭状态（AP 不足）
        TestImageFactory.PasteBlock(screen, ScreenWidth, 362, 123, 1550, 950, 118); // 选择关卡：确认这是结算页布局
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    /// <summary>奖励页标题被横幅遮挡时，仍以当前继续按钮推进，不让灰态按钮误报 AP 不足。</summary>
    private static byte[] CreateOccludedRewardContinueScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 500, 80, 710, 20, 235); // 成就横幅遮住顶部奖励标题区域
        TestImageFactory.PasteBlock(screen, ScreenWidth, 362, 123, 1550, 950, 151); // 轻微渲染差异：继续模板置信度约 91%
        TestImageFactory.PasteBlock(screen, ScreenWidth, 363, 122, 1120, 930, 88); // 背景误命中的灰态再次出击模板
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    private static byte[] CreateFailedScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 317, 100, 800, 20, 170); // 失败提示（顶部 150 像素区域）
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    /// <summary>实物按钮构造左右双证据弹窗；同一画面由用户策略决定出击或取消。</summary>
    private static byte[] CreateEmptySlotScreen(string sortiePath, string cancelPath)
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 296, 106, 800, 200, 210);
        PasteTemplate(screen, ImageCodec.DecodePng(File.ReadAllBytes(cancelPath)), 580, 800);
        PasteTemplate(screen, ImageCodec.DecodePng(File.ReadAllBytes(sortiePath)), 980, 800);
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }
    private static byte[] CreateNoApScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 252, 96, 800, 300, 250); // AP 不足提示（正中上方 400×550）
        TestImageFactory.PasteBlock(screen, ScreenWidth, 365, 118, 950, 850, 100); // 对话框取消（正中下方 825×300）
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    private static byte[] CreatePlayerLevelUpScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 559, 74, 700, 120, 230); // 玩家升级提示（正中上方 640×315）
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    private static byte[] CreateRewardScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 162, 59, 870, 30, 88); // 奖励图标（顶部中间 230×120）
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    private static byte[] CreateDroppedUnitScreen()
    {
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TapToNextOcrSamples.Paste(screen, ScreenWidth, 303, 54, 800, 950); // 实际继续字形，底部 OCR 不接受纯色占位。
        return ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
    }

    private static byte[] CreatePlainScreen()
        => ImageCodec.EncodePng(TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20), ScreenWidth, ScreenHeight);

    /// <summary>运行首轮；含固定补点等待的场景可为真实模板匹配预留独立时间预算。</summary>
    private static async Task<SortieFlowResult> RunAsync(SortieFlowService service, FakeAdbService adb, bool moreRoundsRemaining,
        int continueWaitTimeoutMs = 2000)
        => await service.ExecuteAfterSortieAsync(
            "serial",
            allowDownload: true,
            allowEmptySlotSortie: true,
            moreRoundsRemaining,
            downloadDetectTimeoutMs: 2000,
            pollIntervalMs: 10,
            iconClickIntervalMs: 10,
            battleIconDelayMs: 10,
            tapToNextClickDelayMs: 10,
            continueStartDelayMs: 10,
            continueClickDelayMs: 10,
            continuePollIntervalMs: 10,
            continueWaitTimeoutMs: continueWaitTimeoutMs,
            continueRepeatIntervalMs: 10,
            againStartDelayMs: 10,
            againPollIntervalMs: 10,
            againWaitTimeoutMs: 2000);

    private static async Task<SortieFlowResult> RunNextRoundAsync(SortieFlowService service, FakeAdbService adb, bool moreRoundsRemaining)
        => await service.ExecuteNextRoundAsync(
            "serial",
            moreRoundsRemaining,
            tapToNextWaitTimeoutMs: 2000,
            tapToNextPollIntervalMs: 10,
            tapToNextClickDelayMs: 10,
            continueStartDelayMs: 10,
            continueClickDelayMs: 10,
            continuePollIntervalMs: 10,
            continueWaitTimeoutMs: 2000,
            continueRepeatIntervalMs: 10,
            againStartDelayMs: 10,
            againPollIntervalMs: 10,
            againWaitTimeoutMs: 2000);

    /// <summary>失败/AP 不足必须优先于继续与再次出击，覆盖出击等待和各结算阶段。</summary>
    [Theory]
    [InlineData(4, "failed")]
    [InlineData(4, "noap")]
    [InlineData(4, "disabled")]
    [InlineData(0, "failed")]
    [InlineData(1, "failed")]
    [InlineData(2, "failed")]
    [InlineData(3, "failed")]
    [InlineData(0, "noap")]
    [InlineData(1, "noap")]
    [InlineData(2, "noap")]
    [InlineData(3, "noap")]
    [InlineData(0, "disabled")]
    [InlineData(1, "disabled")]
    [InlineData(2, "disabled")]
    [InlineData(3, "disabled")]
    public async Task TerminalState_StopsBeforeNormalActions(int phase, string state)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var pixels = ImageCodec.DecodePng(state == "noap" ? CreateNoApScreen() : CreateResultScreen());
        if (state == "failed")
            TestImageFactory.PasteBlock(pixels.Bgra, ScreenWidth, 317, 100, 800, 20, 170);
        if (state == "disabled")
        {
            // 与正常再次出击模板相似度超过 90%，禁用模板仍必须独立识别。
            TestImageFactory.PasteBlock(pixels.Bgra, ScreenWidth, 363, 122, 1120, 930, 84);
        }
        var terminal = ImageCodec.EncodePng(pixels.Bgra, ScreenWidth, ScreenHeight);
        var adb = new FakeAdbService(phase == 0 ? terminal : phase == 4 ? CreatePlainScreen() : CreateTapToNextScreen());
        if (phase == 4) adb.EnqueueScreenshots(terminal);
        if (phase > 0 && phase < 4)
        {
            var icons = CreateIconsScreen(AutoGray, 160);
            var frames = new List<byte[]> { icons, icons, CreatePlainScreen() };
            if (phase == 1) frames.Add(CreatePlainScreen()); // 非第 1/5 次轮询也要拦截。
            if (phase >= 2) frames.Add(CreateContinueScreen());
            if (phase == 3) frames.AddRange(new[] { CreateRewardScreen(), CreateContinueScreen(), CreatePlainScreen() });
            frames.Add(terminal);
            adb.EnqueueScreenshots(frames.ToArray());
        }
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn,
            sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp,
            sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, sortie.SelectStage);
        var result = phase == 0 || phase == 4
            ? await RunNextRoundAsync(service, adb, true)
            : await RunAsync(service, adb, true);
        Assert.Equal(state switch
        {
            "failed" => SortieFlowResult.FailureDetected,
            "noap" => SortieFlowResult.NoApCancelled,
            _ => SortieFlowResult.NoAp,
        }, result);
        Assert.Equal(phase == 0 || phase == 4 ? 1 : 4 + Math.Max(0, phase - 1), adb.Taps.Count);
        Assert.Equal(state == "noap" ? (1132, 909) : (1670, 980), adb.Taps[^1]);
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_TapToNextFirst_RoundCompletedAndTapsInOrder()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        // AUTO 关闭 + 二倍速关闭：各点击一次；继续按钮点击两次；再次出击点击一次
        var adb = new FakeAdbService(CreateTapToNextScreen());
        adb.EnqueueScreenshots(
            CreateIconsScreen(AutoGray, 160),
            CreateIconsScreen(AutoGray, 160),
            CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(),
            CreateAgainScreen(),
            CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await RunAsync(service, adb, moreRoundsRemaining: true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(6, adb.Taps.Count);
        Assert.Equal((1508, 50), adb.Taps[0]);                      // AUTO 固定位置
        Assert.Equal((1712, 50), adb.Taps[1]);                      // 二倍速固定位置
        Assert.Equal((959, 968), adb.Taps[2]);  // TAP TO NEXT
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[3]); // 继续按钮第一次
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[4]); // 继续按钮第二次（同一位置）
        Assert.Equal((1120 + 361 / 2, 930 + 120 / 2), adb.Taps[5]); // 再次出击
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_TerrainPromptAllowed_ConfirmsSortieThenCompletesRound()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        // 旧单色空位占位图会与地形提示纯色块混淆；用真实字形验证两种提示互不抢占。
        File.Copy(ConfigService.ResolvePath("resources/templates/dialog/empty_slot_hint.png"), sortie.EmptySlotHint, true);
        var adb = new FakeAdbService(CreateTerrainPromptScreen(includeSortieButton: true));
        adb.EnqueueScreenshots(
            CreateTapToNextScreen(),
            CreateIconsScreen(AutoGray, 160),
            CreateIconsScreen(AutoGray, 160),
            CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(),
            CreateAgainScreen(), CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward,
            terrainTemplate: sortie.Terrain);

        var result = await RunAsync(service, adb, moreRoundsRemaining: true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal((700 + 362 / 2, 850 + 123 / 2), adb.Taps[0]); // 地形提示的出击确认
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_TerrainPromptNotAllowed_CancelsAndStops()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        // 旧单色空位占位图会与地形提示纯色块混淆；用真实字形验证两种提示互不抢占。
        File.Copy(ConfigService.ResolvePath("resources/templates/dialog/empty_slot_hint.png"), sortie.EmptySlotHint, true);
        var adb = new FakeAdbService(CreateTerrainPromptScreen(includeSortieButton: false));
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward,
            terrainTemplate: sortie.Terrain);

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: true, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            allowTerrainUnsuitableSortie: false, downloadDetectTimeoutMs: 2000, pollIntervalMs: 10);

        Assert.Equal(SortieFlowResult.TerrainCancelled, result);
        var tap = Assert.Single(adb.Taps);
        Assert.Equal((950 + 365 / 2, 850 + 118 / 2), tap);
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_DownloadFirstAndAllowed_RoundCompleted()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        // 下载界面 → 首张下载后截图的 TAP TO NEXT → AUTO 敌军 + 二倍速已开启 → 继续按钮 → 再次出击
        var adb = new FakeAdbService(CreateDownloadPromptScreen(includeDownloadButton: true));
        adb.EnqueueScreenshots(
            CreateTapToNextScreen(),
            CreateIconsScreen(AutoRed, 70),
            CreateIconsScreen(AutoGray, 70),
            CreateIconsScreen(AutoBlue, 70),
            CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(),
            CreateAgainScreen(),
            CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward,
            sortieDownloadTemplate: sortie.DownloadHint);

        var result = await RunAsync(service, adb, moreRoundsRemaining: true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal((700 + 362 / 2, 800 + 127 / 2), adb.Taps[0]); // 下载
        Assert.Equal((1508, 50), adb.Taps[1]); // AUTO 敌军状态第一次切换
        Assert.Equal((1508, 50), adb.Taps[2]); // AUTO 敌军状态第二次切换
        Assert.Equal((959, 968), adb.Taps[3]); // 按钮配置完成后点击 TAP TO NEXT
        Assert.Contains(adb.Taps, tap => tap == (1120 + 361 / 2, 930 + 120 / 2)); // 再次出击
    }

    /// <summary>实机成绩页没有 TAP；下载后必须根据当前继续按钮接续结算，不能空等十分钟或操作 AUTO。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadResume_ScoreOrResultWithoutOpening_ContinuesSettlement(bool alreadyResult)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var pixels = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        PasteTemplate(pixels, ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "score-continue-mumu5556.png"))), 1470, 905);
        var score = ImageCodec.EncodePng(pixels, ScreenWidth, ScreenHeight);
        var adb = new FakeAdbService(CreateDownloadPromptScreen(true));
        adb.ScreenshotProvider = () => adb.Taps.Count switch
        {
            0 => CreateDownloadPromptScreen(true),
            1 when !alreadyResult => score,
            _ => CreateResultScreen()
        };
        var log = new TestLog();
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            ConfigService.ResolvePath("resources/templates/stage/sortie/continue.png"), sortie.Again, sortie.AgainClose,
            sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp,
            sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, selectStageTemplate: sortie.SelectStage,
            sortieDownloadTemplate: sortie.DownloadHint, testLog: log);

        Assert.Equal(SortieFlowResult.RoundCompleted, await RunDownloadAsync(service));
        var expected = new List<(int, int)> { (881, 863) };
        if (!alreadyResult) expected.Add((1677, 990));
        expected.Add((1300, 990));
        Assert.Equal(expected, adb.Taps);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("下载后已识别到结算页面"));
    }

    /// <summary>下载后已在战斗时按真实战斗控件配置并等待结算，不能发送旧开场坐标或中央盲点。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadResume_ActiveBattleWithoutOpening_ContinuesRound(bool endsDuringConfiguration)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoBlue, 70);
        var adb = new FakeAdbService(CreateDownloadPromptScreen(true));
        if (endsDuringConfiguration)
            adb.EnqueueScreenshots(icons, CreateResultScreen(), CreateResultScreen(), CreateResultScreen(), CreateResultScreen());
        else adb.EnqueueScreenshots(icons, icons, icons, CreateContinueScreen(), CreateContinueScreen(),
            CreateResultScreen(), CreateResultScreen(), CreateResultScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint,
            sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward,
            sortie.TapToNextReward, selectStageTemplate: sortie.SelectStage, sortieDownloadTemplate: sortie.DownloadHint);

        Assert.Equal(SortieFlowResult.RoundCompleted, await RunDownloadAsync(service));
        Assert.Equal(endsDuringConfiguration ? new[] { (881, 863), (1300, 990) }
            : new[] { (881, 863), (1731, 1011), (1300, 990) }, adb.Taps);
    }

    /// <summary>即使全局恢复立即生效，下载开场也必须先配置 AUTO，再点击唯一的开场文字。</summary>
    [Fact]
    public async Task DownloadResume_OpeningOwnsClickBeforeGlobalRecovery()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        // 开场画面同时显示关闭的 AUTO，配置点击必须由真实按钮证据授权。
        var openingPixels = ImageCodec.DecodePng(CreateTapToNextScreen());
        PasteTemplate(openingPixels.Bgra, ImageCodec.DecodePng(File.ReadAllBytes(
            ConfigService.ResolvePath("resources/templates/stage/sortie/auto_close.png"))), 1420, 10);
        var opening = ImageCodec.EncodePng(openingPixels.Bgra, ScreenWidth, ScreenHeight);
        var adb = new FakeAdbService(CreateDownloadPromptScreen(true));
        adb.ScreenshotProvider = () => adb.Taps.Count switch
        {
            0 => CreateDownloadPromptScreen(true),
            1 => opening,
            2 => CreateIconsScreen(AutoBlue, 70),
            3 => CreateContinueScreen(),
            _ => CreateResultScreen()
        };
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint,
            sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward,
            sortie.TapToNextReward, selectStageTemplate: sortie.SelectStage,
            sortieDownloadTemplate: sortie.DownloadHint, tapToNextCheckDelayMs: 0);

        Assert.Equal(SortieFlowResult.RoundCompleted, await RunDownloadAsync(service));
        Assert.Equal(new[] { (881, 863), (1508, 50), (959, 968), (1731, 1011), (1300, 990) }, adb.Taps);
    }

    /// <summary>下载后等待保留失败优先、有限超时及取消；未知帧不能授权任何推进点击。</summary>
    [Theory]
    [InlineData("failed")]
    [InlineData("unknown")]
    [InlineData("color")]
    [InlineData("speed")]
    [InlineData("cancel")]
    public async Task DownloadResume_UnsafeOrUnknownFrame_NeverAdvances(string state)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        using var cts = new CancellationTokenSource();
        var adb = new FakeAdbService(CreateDownloadPromptScreen(true));
        var failedWithContinue = ImageCodec.DecodePng(CreateContinueScreen());
        TestImageFactory.PasteBlock(failedWithContinue.Bgra, ScreenWidth, 317, 100, 800, 20, 170);
        var unknown = state switch
        {
            "color" => CreateIconsScreen(AutoBlue, 20),
            "speed" => CreateIconsScreen(AutoGray, 70),
            _ => CreatePlainScreen()
        };
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? CreateDownloadPromptScreen(true)
            : state == "failed" ? ImageCodec.EncodePng(failedWithContinue.Bgra, ScreenWidth, ScreenHeight) : unknown;
        if (state == "cancel") adb.BeforeScreenshotAsync = (count, _) =>
        {
            if (count > 1) cts.Cancel();
            return Task.CompletedTask;
        };
        var log = new TestLog();
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint,
            sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward,
            sortie.TapToNextReward, selectStageTemplate: sortie.SelectStage,
            sortieDownloadTemplate: sortie.DownloadHint, testLog: log);

        if (state == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunDownloadAsync(service, cts.Token));
        else Assert.Equal(state == "failed" ? SortieFlowResult.FailureDetected : SortieFlowResult.TapToNextTimeout,
            await RunDownloadAsync(service, cts.Token));
        Assert.Equal(state == "failed" ? new[] { (881, 863), (1670, 980) } : new[] { (881, 863) }, adb.Taps);
        if (state is "unknown" or "color" or "speed")
            Assert.Contains(log.Entries, entry => entry.Message.Contains("继续等待（已等待"));
    }

    /// <summary>下载分支回归使用短且有限的等待；不修改生产时间预算。</summary>
    private static Task<SortieFlowResult> RunDownloadAsync(SortieFlowService service, CancellationToken ct = default)
        => service.ExecuteAfterSortieAsync("serial", allowDownload: true, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 2000, tapToNextWaitTimeoutMs: 2000, tapToNextPollIntervalMs: 10,
            pollIntervalMs: 10, iconClickIntervalMs: 10, battleIconDelayMs: 10, tapToNextClickDelayMs: 10,
            continueStartDelayMs: 10, continueClickDelayMs: 10, continuePollIntervalMs: 10,
            continueWaitTimeoutMs: 2000, continueRepeatIntervalMs: 10,
            againStartDelayMs: 10, againPollIntervalMs: 10, againWaitTimeoutMs: 2000, ct: ct);

    [Fact]
    public async Task ExecuteAfterSortieAsync_DownloadFirstAndNotAllowed_TapsCancelAndStops()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var adb = new FakeAdbService(CreateDownloadPromptScreen(includeDownloadButton: false));
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward,
            sortieDownloadTemplate: sortie.DownloadHint);

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: false, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 2000, pollIntervalMs: 10);

        Assert.Equal(SortieFlowResult.DownloadCancelled, result);
        var tap = Assert.Single(adb.Taps);
        Assert.Equal((1150 + 200 / 2, 800 + 100 / 2), tap);
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_DownloadHintAtOriginalLowerBoundary_TapsCancelAndStops()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var lowerHint = Path.Combine(temp.Path, "download_hint_lower.png");

        // 真实下载说明文字起点约为 y=383；旧 ROI 的底边会截掉该提示。
        File.WriteAllBytes(lowerHint, ImageCodec.EncodePng(TestImageFactory.CreateBgra(320, 38, 0), 320, 38));
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 320, 38, 800, 383, 0);
        TestImageFactory.PasteBlock(screen, ScreenWidth, 200, 100, 1150, 800, 90);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight));
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward,
            sortieDownloadTemplate: lowerHint);

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: false, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 2000, pollIntervalMs: 10);

        Assert.Equal(SortieFlowResult.DownloadCancelled, result);
        var tap = Assert.Single(adb.Taps);
        Assert.Equal((1150 + 200 / 2, 800 + 100 / 2), tap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAfterSortieAsync_NoInitialPrompt_ConfiguresIconsAndTapsCenterBeforeContinue(bool autoVisible)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var adb = new FakeAdbService(autoVisible ? CreateIconsScreen(AutoGray, 160) : CreatePlainScreen());
        // 没有开场提示但已显示真实 AUTO；切换后给出蓝色新帧，不能用空白页授权点击。
        adb.EnqueueScreenshots(
            CreateIconsScreen(AutoBlue, 160),
            CreateIconsScreen(AutoBlue, 160),
            CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(),
            CreateAgainScreen(), CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);
        // 此用例验证完整动作链，并非一秒性能要求；真实 1080p 匹配在基线代码上已超过一秒。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: true, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 0, pollIntervalMs: 10,
            iconClickIntervalMs: 1, battleIconDelayMs: 1,
            continueStartDelayMs: 0, continueClickDelayMs: 0, continuePollIntervalMs: 1,
            continueWaitTimeoutMs: 5000, continueRepeatIntervalMs: 1,
            againStartDelayMs: 0, againPollIntervalMs: 1, againWaitTimeoutMs: 1000,
            ct: cts.Token);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        var expected = new List<(int, int)>();
        if (autoVisible) expected.Add((1508, 50)); // 空白页不能被当作关闭而授权 AUTO 点击。
        expected.AddRange([(1712, 50), (ScreenWidth / 2, ScreenHeight / 2),
            (1550 + 362 / 2, 950 + 123 / 2), (1550 + 362 / 2, 950 + 123 / 2),
            (1120 + 361 / 2, 930 + 120 / 2)]);
        Assert.Equal(expected, adb.Taps);
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_LastRound_TapsExitAndReturnsAllRoundsCompleted()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var adb = new FakeAdbService(CreateTapToNextScreen());
        adb.EnqueueScreenshots(icons, icons, CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(), CreateAgainScreen(), CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await RunAsync(service, adb, moreRoundsRemaining: false);

        Assert.Equal(SortieFlowResult.AllRoundsCompleted, result);
        Assert.Equal(6, adb.Taps.Count);
        Assert.Equal((1670, 980), adb.Taps[^1]); // 最后一轮点击退出坐标
    }

    /// <summary>第一张截图中的短暂开场也必须参与识别，不能读完安全状态后丢掉。</summary>
    [Fact]
    public async Task ExecuteNextRoundAsync_OpeningOnlyInFirstFrame_DoesNotLoseOpening()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var opening = CreateTapToNextScreen();
        var score = CreateContinueScreen();
        var resultPage = CreateResultScreen();
        var plain = CreatePlainScreen();
        var adb = new FakeAdbService(opening);
        adb.ScreenshotProvider = () => adb.ScreenshotCount == 1 ? opening : adb.Taps.Count switch
        {
            0 => plain,
            1 => score,
            _ => resultPage,
        };
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie,
            sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, sortie.SelectStage);

        Assert.Equal(SortieFlowResult.RoundCompleted, await RunNextRoundAsync(service, adb, true));
        Assert.Equal(new[] { (959, 968), (1731, 1011), (1300, 990) }, adb.Taps);
    }

    /// <summary>漏过开场后，同帧彩色 AUTO 和速度控件可确认新战斗，之后正常处理实机成绩按钮。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteNextRoundAsync_MissedOpening_UsesBattleEvidenceBeforeScore(bool battleInInitialFrame)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoBlue, 70);
        var pixels = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        PasteTemplate(pixels, ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "score-continue-mumu5556.png"))), 1470, 905);
        var score = ImageCodec.EncodePng(pixels, ScreenWidth, ScreenHeight);
        var resultPage = CreateResultScreen();
        var plain = CreatePlainScreen();
        var adb = new FakeAdbService(plain);
        adb.ScreenshotProvider = () => adb.ScreenshotCount switch
        {
            1 when !battleInInitialFrame => plain,
            var count when count == (battleInInitialFrame ? 1 : 2) => icons,
            _ => adb.Taps.Count == 0 ? score : resultPage,
        };
        var log = new TestLog();
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            ConfigService.ResolvePath("resources/templates/stage/sortie/continue.png"), sortie.Again, sortie.AgainClose,
            sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp,
            sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, sortie.SelectStage, testLog: log);

        Assert.Equal(SortieFlowResult.RoundCompleted, await RunNextRoundAsync(service, adb, true));
        Assert.Equal(new[] { (1677, 990), (1300, 990) }, adb.Taps);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("已识别到战斗图标"));
    }

    /// <summary>单独速度按钮或仅有彩色 AUTO 都不构成新战斗证据，不能处理随后的旧成绩页。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteNextRoundAsync_IncompleteBattleControls_DoNotSkipOpening(bool onlyAuto)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var partial = CreateIconsScreen(onlyAuto ? AutoBlue : AutoGray, onlyAuto ? (byte)20 : (byte)70);
        var score = CreateContinueScreen();
        var adb = new FakeAdbService(partial);
        adb.ScreenshotProvider = () => adb.ScreenshotCount == 1 ? partial : score;
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie,
            sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, sortie.SelectStage);

        Assert.Equal(SortieFlowResult.TapToNextTimeout, await RunNextRoundAsync(service, adb, true));
        Assert.Empty(adb.Taps);
    }

    /// <summary>奖励继续点击未生效时，相似的再次出击背景不能计数；只补点一次，持续不变则停止。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteNextRoundAsync_SecondContinueNotApplied_RequiresResultPage(bool retrySucceeds)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var opening = CreateTapToNextScreen();
        var score = CreateContinueScreen();
        var reward = ImageCodec.DecodePng(score);
        TestImageFactory.PasteBlock(reward.Bgra, ScreenWidth, 162, 59, 870, 30, 88);
        var rewardContinue = ImageCodec.EncodePng(reward.Bgra, ScreenWidth, ScreenHeight);
        // 在继续页面制造高置信度再次出击干扰，右侧仍由更强的继续证据标识真实页面。
        TestImageFactory.PasteBlock(reward.Bgra, ScreenWidth, 361, 120, 1120, 930, 60);
        var falseAgain = ImageCodec.EncodePng(reward.Bgra, ScreenWidth, ScreenHeight);
        var resultPage = CreateResultScreen();
        var adb = new FakeAdbService(opening);
        adb.ScreenshotProvider = () => adb.Taps.Count switch
        {
            0 => opening,
            1 => score,
            2 => rewardContinue,
            3 => falseAgain,
            _ => retrySucceeds ? resultPage : falseAgain,
        };
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie,
            sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, sortie.SelectStage);

        var result = await RunNextRoundAsync(service, adb, true);

        Assert.Equal(retrySucceeds ? SortieFlowResult.RoundCompleted : SortieFlowResult.AgainTimeout, result);
        var expected = new List<(int, int)> { (959, 968), (1731, 1011), (1731, 1011), (1731, 1011) };
        if (retrySucceeds) expected.Add((1300, 990));
        Assert.Equal(expected, adb.Taps);
    }

    /// <summary>补点等待后页面可能已经完成或失败；必须复查新帧，不能沿用等待前的继续坐标。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteNextRoundAsync_ContinueRetryRechecksPageBeforeTap(bool failed)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var opening = CreateTapToNextScreen();
        var score = CreateContinueScreen();
        var reward = ImageCodec.DecodePng(score);
        TestImageFactory.PasteBlock(reward.Bgra, ScreenWidth, 162, 59, 870, 30, 88);
        var rewardContinue = ImageCodec.EncodePng(reward.Bgra, ScreenWidth, ScreenHeight);
        var afterDelay = failed ? CreateFailedScreen() : CreateResultScreen();
        var lateScreens = 0;
        var adb = new FakeAdbService(opening);
        adb.ScreenshotProvider = () => adb.Taps.Count switch
        {
            0 => opening,
            1 => score,
            2 => rewardContinue,
            _ => ++lateScreens <= 2 ? rewardContinue : afterDelay,
        };
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie,
            sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, sortie.SelectStage);

        Assert.Equal(failed ? SortieFlowResult.FailureDetected : SortieFlowResult.RoundCompleted,
            await RunNextRoundAsync(service, adb, true));
        Assert.Equal(new[] { (959, 968), (1731, 1011), (1731, 1011), failed ? (1670, 980) : (1300, 990) }, adb.Taps);
    }

    /// <summary>再次出击后未确认新开场或战斗时必须超时停止，旧结算页面不能授权继续或再次出击。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteNextRoundAsync_MissingTapToNext_DoesNotResumePreviousSettlement(bool delayed, bool moreRounds)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var first = CreateContinueScreen();
        var reward = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteBlock(reward, ScreenWidth, 162, 59, 870, 30, 88);
        TestImageFactory.PasteBlock(reward, ScreenWidth, 362, 123, 1550, 950, 140);
        var second = ImageCodec.EncodePng(reward, ScreenWidth, ScreenHeight);
        var resultPage = CreateResultScreen();
        var plain = CreatePlainScreen();
        var adb = new FakeAdbService(first);
        var previousRoundScreens = new[] { first, second, resultPage };
        adb.ScreenshotProvider = () => delayed && adb.ScreenshotCount <= 2
            ? plain
            : previousRoundScreens[(adb.ScreenshotCount - 1) % previousRoundScreens.Length];
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie,
            sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, sortie.SelectStage);

        var result = await service.ExecuteNextRoundAsync("serial", moreRounds,
            tapToNextWaitTimeoutMs: 2000, tapToNextPollIntervalMs: 10, continueStartDelayMs: 1,
            continueClickDelayMs: 1, continuePollIntervalMs: 10, continueWaitTimeoutMs: 2000,
            continueRepeatIntervalMs: 1, againStartDelayMs: 1, againPollIntervalMs: 10, againWaitTimeoutMs: 2000);

        Assert.Equal(SortieFlowResult.TapToNextTimeout, result);
        Assert.Empty(adb.Taps);
    }

    /// <summary>上一轮奖励、结算、空帧或选择关卡持续出现，都不能跳过新一轮开场识别。</summary>
    [Theory]
    [InlineData("reward")]
    [InlineData("result")]
    [InlineData("plain")]
    [InlineData("selectOnly")]
    public async Task ExecuteNextRoundAsync_PreviousSettlementSignals_DoNotSkipOpening(string state)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var pixels = ImageCodec.DecodePng(CreatePlainScreen());
        if (state == "selectOnly")
            TestImageFactory.PasteBlock(pixels.Bgra, ScreenWidth, 362, 123, 1550, 950, 118);
        var initial = state switch
        {
            "reward" => CreateRewardScreen(),
            "result" => CreateResultScreen(),
            _ => ImageCodec.EncodePng(pixels.Bgra, ScreenWidth, ScreenHeight),
        };
        var adb = new FakeAdbService(initial);
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie,
            sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, sortie.SelectStage);
        var result = await service.ExecuteNextRoundAsync("serial", true,
            tapToNextWaitTimeoutMs: 1000, tapToNextPollIntervalMs: 10, continueStartDelayMs: 1,
            continueClickDelayMs: 1, continuePollIntervalMs: 10, continueWaitTimeoutMs: 2000,
            continueRepeatIntervalMs: 1, againStartDelayMs: 1, againPollIntervalMs: 10, againWaitTimeoutMs: 2000);
        Assert.Equal(SortieFlowResult.TapToNextTimeout, result);
        Assert.Empty(adb.Taps);
    }

    /// <summary>点击再次出击后的过渡帧可能保留旧页面，必须等到新开场文字出现再推进。</summary>
    [Theory]
    [InlineData("continue")]
    [InlineData("reward")]
    [InlineData("result")]
    public async Task ExecuteNextRoundAsync_PreviousSettlementThenTap_WaitsForNewOpening(string state)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var previous = state switch
        {
            "continue" => CreateContinueScreen(),
            "reward" => CreateRewardScreen(),
            _ => CreateResultScreen(),
        };
        var adb = new FakeAdbService(previous);
        adb.EnqueueScreenshots(previous, CreateTapToNextScreen(), CreateContinueScreen(), CreateContinueScreen(),
            CreateRewardScreen(), CreateContinueScreen(), CreateResultScreen(), CreateResultScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie,
            sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, sortie.SelectStage);

        var result = await RunNextRoundAsync(service, adb, moreRoundsRemaining: true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(4, adb.Taps.Count);
        Assert.Equal((959, 968), adb.Taps[0]);
    }

    [Fact]
    public async Task ExecuteNextRoundAsync_MoreRounds_TapsTapToNextContinueAndAgain()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        // 再次出击后直接进入 TAP TO NEXT：不识别下载与战斗图标
        var adb = new FakeAdbService(CreateTapToNextScreen());
        adb.EnqueueScreenshots(
            CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(),
            CreateAgainScreen(),
            CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await RunNextRoundAsync(service, adb, moreRoundsRemaining: true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(4, adb.Taps.Count);
        Assert.Equal((959, 968), adb.Taps[0]);  // TAP TO NEXT
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[1]); // 继续按钮第一次
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[2]); // 继续按钮第二次
        Assert.Equal((1120 + 361 / 2, 930 + 120 / 2), adb.Taps[3]); // 再次出击
    }

    [Fact]
    public async Task ExecuteNextRoundAsync_LastRound_TapsExit()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var adb = new FakeAdbService(CreateTapToNextScreen());
        adb.EnqueueScreenshots(
            CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(),
            CreateAgainScreen(),
            CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await RunNextRoundAsync(service, adb, moreRoundsRemaining: false);

        Assert.Equal(SortieFlowResult.AllRoundsCompleted, result);
        Assert.Equal(4, adb.Taps.Count);
        Assert.Equal((1670, 980), adb.Taps[^1]); // 最后一轮点击退出坐标
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_ContinueTimeout_ReturnsContinueTimeout()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var adb = new FakeAdbService(CreateTapToNextScreen());
        adb.EnqueueScreenshots(icons, icons);
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: true, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 2000, pollIntervalMs: 10, iconClickIntervalMs: 10, battleIconDelayMs: 10,
            tapToNextClickDelayMs: 10, continueStartDelayMs: 10, continueClickDelayMs: 10, continuePollIntervalMs: 10,
            continueWaitTimeoutMs: 100, continueRepeatIntervalMs: 10);

        Assert.Equal(SortieFlowResult.ContinueTimeout, result);
        Assert.Equal(3, adb.Taps.Count); // TAP TO NEXT + AUTO + 二倍速，无继续按钮点击
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_AgainTimeout_ReturnsAgainTimeout()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var adb = new FakeAdbService(CreateTapToNextScreen());
        adb.EnqueueScreenshots(icons, icons, CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(), CreatePlainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: true, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 2000, pollIntervalMs: 10, iconClickIntervalMs: 10, battleIconDelayMs: 10,
            tapToNextClickDelayMs: 10, continueStartDelayMs: 10, continueClickDelayMs: 10, continuePollIntervalMs: 10,
            continueWaitTimeoutMs: 2000, continueRepeatIntervalMs: 10,
            againStartDelayMs: 10, againPollIntervalMs: 10, againWaitTimeoutMs: 100);

        Assert.Equal(SortieFlowResult.AgainTimeout, result);
        Assert.Equal(5, adb.Taps.Count); // TAP TO NEXT + AUTO + 二倍速 + 继续按钮两次，无再次出击点击
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_FailedOnFirstContinueCheck_TapsBackToStageSelect()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var adb = new FakeAdbService(CreateTapToNextScreen());
        adb.EnqueueScreenshots(icons, icons, CreateFailedScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: true, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 2000, pollIntervalMs: 10, iconClickIntervalMs: 10, battleIconDelayMs: 10,
            tapToNextClickDelayMs: 10, continueStartDelayMs: 10, continueClickDelayMs: 10, continuePollIntervalMs: 10,
            continueWaitTimeoutMs: 2000, continueRepeatIntervalMs: 10);

        Assert.Equal(SortieFlowResult.FailureDetected, result);
        Assert.Equal(4, adb.Taps.Count); // TAP TO NEXT + AUTO + 二倍速 + 回到选择关卡
        Assert.Equal((1670, 980), adb.Taps[^1]);
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_FailedOnFifthContinueCheck_TapsBackToStageSelect()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var plain = CreatePlainScreen();
        var adb = new FakeAdbService(CreateTapToNextScreen());
        // 继续按钮轮询：第 1~4 次未命中，第 5 次识别到失败画面
        adb.EnqueueScreenshots(icons, icons, plain, plain, plain, plain, CreateFailedScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: true, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 2000, pollIntervalMs: 10, iconClickIntervalMs: 10, battleIconDelayMs: 10,
            tapToNextClickDelayMs: 10, continueStartDelayMs: 10, continueClickDelayMs: 10, continuePollIntervalMs: 10,
            continueWaitTimeoutMs: 2000, continueRepeatIntervalMs: 10);

        Assert.Equal(SortieFlowResult.FailureDetected, result);
        Assert.Equal(4, adb.Taps.Count); // TAP TO NEXT + AUTO + 二倍速 + 回到选择关卡
        Assert.Equal((1670, 980), adb.Taps[^1]);
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_EmptySlotAllowed_ClicksDialogSortieThenCompletesRound()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        File.Copy(ConfigService.ResolvePath("resources/templates/dialog/sortie.png"), sortie.DialogSortie, true);
        File.Copy(ConfigService.ResolvePath("resources/templates/dialog/cancel.png"), sortie.DialogCancel, true);
        var sortieImage = ImageCodec.DecodePng(File.ReadAllBytes(sortie.DialogSortie));
        // 空位提示 → 点击空位出击确认 → 继续下载/TAP TO NEXT 识别 → 正常完成一轮
        var adb = new FakeAdbService(CreateEmptySlotScreen(sortie.DialogSortie, sortie.DialogCancel));
        adb.EnqueueScreenshots(
            CreateTapToNextScreen(),
            CreateIconsScreen(AutoGray, 160),
            CreateIconsScreen(AutoGray, 160),
            CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(),
            CreateAgainScreen(),
            CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: true, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 2000, pollIntervalMs: 10, iconClickIntervalMs: 10, battleIconDelayMs: 10,
            tapToNextClickDelayMs: 10, continueStartDelayMs: 10, continueClickDelayMs: 10, continuePollIntervalMs: 10,
            continueWaitTimeoutMs: 2000, continueRepeatIntervalMs: 10,
            againStartDelayMs: 10, againPollIntervalMs: 10, againWaitTimeoutMs: 2000);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(7, adb.Taps.Count);
        Assert.Equal((980 + sortieImage.Width / 2, 800 + sortieImage.Height / 2), adb.Taps[0]); // 空位出击确认
        Assert.Equal((1508, 50), adb.Taps[1]);                      // AUTO
        Assert.Equal((1712, 50), adb.Taps[2]);                      // 二倍速
        Assert.Equal((959, 968), adb.Taps[3]);  // TAP TO NEXT
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[4]); // 继续按钮第一次
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[5]); // 继续按钮第二次
        Assert.Equal((1120 + 361 / 2, 930 + 120 / 2), adb.Taps[6]); // 再次出击
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_EmptySlotNotAllowed_ClicksCancelAndReturnsEmptySlotCancelled()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        File.Copy(ConfigService.ResolvePath("resources/templates/dialog/sortie.png"), sortie.DialogSortie, true);
        File.Copy(ConfigService.ResolvePath("resources/templates/dialog/cancel.png"), sortie.DialogCancel, true);
        var cancelImage = ImageCodec.DecodePng(File.ReadAllBytes(sortie.DialogCancel));
        var adb = new FakeAdbService(CreateEmptySlotScreen(sortie.DialogSortie, sortie.DialogCancel));
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: true, allowEmptySlotSortie: false, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 2000, pollIntervalMs: 10);

        Assert.Equal(SortieFlowResult.EmptySlotCancelled, result);
        var tap = Assert.Single(adb.Taps);
        Assert.Equal((580 + cancelImage.Width / 2, 800 + cancelImage.Height / 2), tap); // 空位出击取消
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_ApInsufficient_TapsStageSelectAndReturnsNoAp()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var adb = new FakeAdbService(CreateTapToNextScreen());
        // 再次出击按钮为关闭状态（AP 不足）：再次出击模板低置信度命中，关闭模板高置信度命中
        adb.EnqueueScreenshots(icons, icons, CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(), CreateApInsufficientScreen());
        var notices = new List<string>();
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward,
            sortie.SelectStage, apStopped: notices.Add);

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: true, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 2000, pollIntervalMs: 10, iconClickIntervalMs: 10, battleIconDelayMs: 10,
            tapToNextClickDelayMs: 10, continueStartDelayMs: 10, continueClickDelayMs: 10, continuePollIntervalMs: 10,
            continueWaitTimeoutMs: 2000, continueRepeatIntervalMs: 10,
            againStartDelayMs: 10, againPollIntervalMs: 10, againWaitTimeoutMs: 2000);

        Assert.Contains("AP 不足", Assert.Single(notices));
        Assert.Equal(SortieFlowResult.NoAp, result);
        Assert.Equal(6, adb.Taps.Count); // TAP TO NEXT + AUTO + 二倍速 + 继续按钮两次 + 回到选择关卡
        Assert.Equal((1670, 980), adb.Taps[^1]);
    }

    /// <summary>REWARD 标题被横幅遮挡时，校验继续按钮和终局双按钮仍可正确区分。</summary>
    [Fact]
    public async Task ExecuteNextRoundAsync_RewardTitleOccluded_UsesContinueAndIgnoresFalseDisabledButton()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var initialContinue = CreateContinueScreen();
        var occludedRewardContinue = CreateOccludedRewardContinueScreen();
        var resultPage = CreateResultScreen();
        var opening = CreateTapToNextScreen();
        var adb = new FakeAdbService(opening);
        adb.ScreenshotProvider = () => adb.Taps.Count switch
        {
            0 => opening,
            1 => initialContinue,
            2 => occludedRewardContinue,
            _ => resultPage,
        };
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint, sortie.DialogSortie,
            sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, sortie.SelectStage);

        var result = await service.ExecuteNextRoundAsync("serial", moreRoundsRemaining: true,
            tapToNextWaitTimeoutMs: 1000, tapToNextPollIntervalMs: 10, continueStartDelayMs: 1,
            continueClickDelayMs: 1, continuePollIntervalMs: 10, continueWaitTimeoutMs: 2000,
            continueRepeatIntervalMs: 1, againStartDelayMs: 1, againPollIntervalMs: 10, againWaitTimeoutMs: 2000);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(4, adb.Taps.Count); // 新开场 + 继续页 + 横幅遮挡标题时的单次补点 + 再次出击
        Assert.Equal((959, 968), adb.Taps[0]);
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[1]);
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[2]);
        Assert.Equal((1120 + 361 / 2, 930 + 120 / 2), adb.Taps[3]);
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_NoApHint_ClicksCancelAndReturnsNoApCancelled()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var adb = new FakeAdbService(CreateNoApScreen());
        var notices = new List<string>();
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward, apStopped: notices.Add);

        var result = await service.ExecuteAfterSortieAsync(
            "serial", allowDownload: true, allowEmptySlotSortie: true, moreRoundsRemaining: true,
            downloadDetectTimeoutMs: 2000, pollIntervalMs: 10);

        Assert.Contains("AP 不足", Assert.Single(notices));
        Assert.Equal(SortieFlowResult.NoApCancelled, result);
        var tap = Assert.Single(adb.Taps);
        Assert.Equal((950 + 365 / 2, 850 + 118 / 2), tap); // AP 不足对话框取消
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_SecondContinueDelayed_TapsItWhenAppears()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var plain = CreatePlainScreen();
        var adb = new FakeAdbService(CreateTapToNextScreen());
        // 第一次点击后第二个继续按钮延迟出现（网络缓慢）：持续轮询直到识别到再点击
        adb.EnqueueScreenshots(icons, icons, CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), plain, plain, CreateContinueScreen(), CreateAgainScreen(), CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await RunAsync(service, adb, moreRoundsRemaining: true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(6, adb.Taps.Count);
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[3]); // 继续按钮第一次
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[4]); // 第二个继续按钮（延迟出现后点击）
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_PlayerLevelUpBetweenContinues_TapsScreenThenSecondContinue()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var adb = new FakeAdbService(CreateTapToNextScreen());
        // 第一次继续点击后出现玩家升级提示：点击画面关闭，等待后继续识别第二个继续按钮
        adb.EnqueueScreenshots(icons, icons, CreateContinueScreen(), CreateContinueScreen(),
            CreatePlayerLevelUpScreen(), CreateRewardScreen(), CreateContinueScreen(), CreateAgainScreen(), CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff, sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        // 生产升级弹窗关闭后固定等待 5 秒，原默认 2 秒测试预算必然先于此等待耗尽。
        // 保留真实生产等待，额外留出与其他结算测试相同的 2 秒识别预算。
        var result = await RunAsync(service, adb, moreRoundsRemaining: true,
            continueWaitTimeoutMs: GameTiming.RecognitionIntervalMs + 2000);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(7, adb.Taps.Count);
        Assert.Equal((700 + 559 / 2, 120 + 74 / 2), adb.Taps[4]); // 升级提示点击
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[5]); // 第二个继续按钮
    }

    /// <summary>真实 OFF 截图嵌入战斗控件位置后必须点击一次；蓝色 ALL 仍无需点击。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAfterSortieAsync_AutoColorState_TapsOnlyWhenOff(bool realOff)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoBlue, 160);
        if (realOff)
        {
            var screen = ImageCodec.DecodePng(icons);
            var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "Sortie", "auto-off-blue-map.png")));
            PasteTemplate(screen.Bgra, crop, 1420, 10);
            icons = ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height);
        }
        var adb = new FakeAdbService(CreateTapToNextScreen());
        adb.EnqueueScreenshots(
            icons,
            icons,
            CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(),
            CreateAgainScreen(),
            CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        var result = await RunAsync(service, adb, moreRoundsRemaining: true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(realOff ? 6 : 5, adb.Taps.Count);
        Assert.Equal(realOff ? 1 : 0, adb.Taps.Count(t => t == (1508, 50)));
        Assert.Equal((1120 + 361 / 2, 930 + 120 / 2), adb.Taps[^1]); // 再次出击
    }

    /// <summary>真实三色素材与用户 OFF 图按颜色分类，按钮周围的明亮蓝色不得参与状态统计。</summary>
    [Theory]
    [InlineData("auto_all.png", "All")]
    [InlineData("auto_enemy.png", "Enemy")]
    [InlineData("auto_close.png", "Close")]
    [InlineData("auto-off-blue-map.png", "Close")]
    public void AutoColorState_ActualCrops_IgnoreBlueSurroundings(string name, string expected)
    {
        var path = name == "auto-off-blue-map.png"
            ? Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sortie", name)
            : ConfigService.ResolvePath("resources/templates/stage/sortie/" + name);
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(path));
        var pixels = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        TestImageFactory.PasteColoredBlock(pixels, ScreenWidth, 200, 115, 1420, 0, 40, 180, 220);
        PasteTemplate(pixels, crop, 1420, 10);
        var screen = ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, ScreenWidth, ScreenHeight));
        var recognizer = new AutoButtonRecognizer(new TemplateMatcher(),
            ConfigService.ResolvePath("resources/templates/stage/sortie/auto_all.png"),
            ConfigService.ResolvePath("resources/templates/stage/sortie/auto_enemy.png"),
            ConfigService.ResolvePath("resources/templates/stage/sortie/auto_close.png"));
        Assert.Equal(expected, recognizer.Recognize(screen, new Roi(1448, 15, 120, 70))?.State.ToString());
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_RewardBetweenContinues_KeepsWaitingForSecondContinue()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var adb = new FakeAdbService(CreateTapToNextScreen());
        // 第二个继续按钮出现前先识别到奖励图标：不点击，继续等待第二个继续按钮
        adb.EnqueueScreenshots(icons, icons, CreateContinueScreen(), CreateContinueScreen(),
            CreateRewardScreen(), CreateContinueScreen(), CreateAgainScreen(), CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp,
            sortie.Reward, sortie.TapToNextReward);

        var result = await RunAsync(service, adb, moreRoundsRemaining: true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(6, adb.Taps.Count); // TAP TO NEXT + AUTO + 二倍速 + 继续×2 + 再次出击（奖励不点击）
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[4]); // 第二个继续按钮
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_SecondContinueBeforeReward_RetriesContinueUntilRewardSeen()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var adb = new FakeAdbService(CreateTapToNextScreen());
        // 尚未识别到奖励图标时视为可能停留在第一个继续界面：重试点击继续按钮，
        // 识别到奖励图标后才允许点击第二个继续按钮
        adb.EnqueueScreenshots(icons, icons, CreateContinueScreen(), CreateContinueScreen(),
            CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(),
            CreateAgainScreen(), CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp,
            sortie.Reward, sortie.TapToNextReward);

        // 固定补点等待已有 1500ms，再加多帧真实匹配，原 2000ms 在未修改的基线上也会超时。
        var result = await RunAsync(service, adb, moreRoundsRemaining: true, continueWaitTimeoutMs: 10000);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(7, adb.Taps.Count); // TAP TO NEXT + AUTO + 二倍速 + 继续(首次+重试+第二个) + 再次出击
        Assert.Equal((1550 + 362 / 2, 950 + 123 / 2), adb.Taps[5]); // 第二个继续按钮（奖励出现后才点击）
    }

    /// <summary>双按钮同帧确认结算后，继续模板即使同分也不得覆盖页面状态。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAfterSortieAsync_ResultPageSelectStageSimilarToContinue_DoesNotTapSelectStage(bool equalConfidence, bool afterSecondContinue)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var adb = new FakeAdbService(CreateTapToNextScreen());
        // 右侧选择关卡与继续模板相似度超过继续阈值；仍需结合更强的双按钮组合判定终局页。
        if (afterSecondContinue)
        {
            // 强制继续与选择关卡同分，真实走完两次继续后检查再次出击分支。
            var continued = CreateContinueScreen(sortie.SelectStage, 1550, 950);
            adb.EnqueueScreenshots(icons, icons, continued, continued, CreateRewardScreen(), continued,
                CreateResultScreen(), CreateResultScreen());
        }
        else adb.EnqueueScreenshots(
            icons, icons,
            CreateResultScreen(), CreateResultScreen(),
            CreateResultScreen(), CreateResultScreen());
        var service = CreateService(adb: adb, download: dialog.Download, cancel: dialog.Cancel,
            tap: sortie.Tap, speedOn: sortie.SpeedOn, speedOff: sortie.SpeedOff,
            continueTemplate: equalConfidence ? sortie.SelectStage : sortie.Continue, againTemplate: sortie.Again, againCloseTemplate: sortie.AgainClose,
            failedTemplate: sortie.Failed, emptySlotHintTemplate: sortie.EmptySlotHint,
            dialogSortieTemplate: sortie.DialogSortie, dialogCancelTemplate: sortie.DialogCancel,
            noApTemplate: sortie.NoAp, playerLevelUpTemplate: sortie.PlayerLevelUp,
            rewardTemplate: sortie.Reward, tapToNextRewardTemplate: sortie.TapToNextReward,
            selectStageTemplate: sortie.SelectStage);

        var result = await RunAsync(service, adb, moreRoundsRemaining: true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(afterSecondContinue ? 6 : 4, adb.Taps.Count); // TAP TO NEXT + AUTO + 二倍速 + 再次出击
        Assert.Equal((1120 + 361 / 2, 930 + 120 / 2), adb.Taps[^1]);
        Assert.Equal(afterSecondContinue ? 2 : 0, adb.Taps.Count(tap => tap == (1550 + 362 / 2, 950 + 123 / 2)));
    }

    /// <summary>继续、奖励及再次出击等待均处理全局遮罩；点击后必须重新截图，不能误判结算完成。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Settlement_GlobalTapToNext_ResumesOriginalWait(int phase)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var rewardContinue = ImageCodec.DecodePng(CreateContinueScreen());
        TestImageFactory.PasteBlock(rewardContinue.Bgra, ScreenWidth, 162, 59, 870, 30, 88);
        var frames = new List<byte[]> { CreateTapToNextScreen() };
        if (phase == 0) frames.Add(CreateTapToNextScreen());
        frames.Add(CreateContinueScreen());
        if (phase == 1) frames.Add(CreateTapToNextScreen());
        frames.Add(ImageCodec.EncodePng(rewardContinue.Bgra, ScreenWidth, ScreenHeight));
        if (phase == 2) frames.Add(CreateTapToNextScreen());
        frames.Add(CreateAgainScreen());
        var adb = new FakeAdbService(frames[0]);
        adb.ScreenshotProvider = () => frames[Math.Min(adb.Taps.Count, frames.Count - 1)];
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint,
            sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward);

        Assert.Equal(SortieFlowResult.RoundCompleted, await RunNextRoundAsync(service, adb, true));
        Assert.Equal(5, adb.Taps.Count);
        Assert.Equal((959, 968), adb.Taps[0]);
        Assert.Equal((959, 968), adb.Taps[phase + 1]);
        Assert.Equal((1120 + 361 / 2, 930 + 120 / 2), adb.Taps[^1]);
    }

    /// <summary>使用 UI 相同的 OCR 回放秘密战斗奖励；关闭遮罩后仍须确认原结算目标。</summary>
    [Fact]
    public async Task Settlement_SecretBattleReward_UsesProductionOcrWithoutTemplates()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "secret-battle-tap-mumu5556.png")));
        var screen = TestImageFactory.CreateBgra(ScreenWidth, ScreenHeight, 20);
        for (var row = 0; row < crop.Height; row++)
            Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, screen,
                ((920 + row) * ScreenWidth + 650) * 4, crop.Width * 4);
        var overlay = ImageCodec.EncodePng(screen, ScreenWidth, ScreenHeight);
        var reward = ImageCodec.DecodePng(CreateContinueScreen());
        TestImageFactory.PasteBlock(reward.Bgra, ScreenWidth, 162, 59, 870, 30, 88);
        var frames = new[] { CreateTapToNextScreen(), CreateContinueScreen(), overlay,
            ImageCodec.EncodePng(reward.Bgra, ScreenWidth, ScreenHeight), CreateAgainScreen() };
        var adb = new FakeAdbService(frames[0]);
        adb.ScreenshotProvider = () => frames[Math.Min(adb.Taps.Count, frames.Length - 1)];
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint,
            sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward, sortie.TapToNextReward,
            globalTapTemplates: []);

        Assert.Equal(SortieFlowResult.RoundCompleted, await RunNextRoundAsync(service, adb, true));
        Assert.Equal(5, adb.Taps.Count);
        Assert.Equal((960, 972), adb.Taps[2]);
        Assert.Equal((1120 + 361 / 2, 930 + 120 / 2), adb.Taps[^1]);
    }

    /// <summary>完整继续模板位于旧窄区域之外时，仍从扩展后的底部按钮区识别并点击模板中心。</summary>
    [Fact]
    public async Task ExecuteNextRoundAsync_FullContinueTemplateInBottomButtonArea_IsRecognized()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var continuePath = ConfigService.ResolvePath("resources/templates/stage/sortie/continue.png");
        const int continueX = 1220;
        const int continueY = 910;
        var continueTemplate = ImageCodec.DecodePng(File.ReadAllBytes(continuePath));
        var continueScreen = CreateContinueScreen(continuePath, continueX, continueY);
        var resultPage = CreateResultScreen();
        var opening = CreateTapToNextScreen();
        var adb = new FakeAdbService(opening);
        adb.ScreenshotProvider = () => adb.Taps.Count switch { 0 => opening, 1 => continueScreen, _ => resultPage };
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            continuePath, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint,
            sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward,
            sortie.TapToNextReward, sortie.SelectStage);

        var result = await service.ExecuteNextRoundAsync("serial", moreRoundsRemaining: true,
            tapToNextWaitTimeoutMs: 1000, tapToNextPollIntervalMs: 10, continueStartDelayMs: 1,
            continueClickDelayMs: 1, continuePollIntervalMs: 10, continueWaitTimeoutMs: 2000,
            continueRepeatIntervalMs: 1, againStartDelayMs: 1, againPollIntervalMs: 10, againWaitTimeoutMs: 2000);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(3, adb.Taps.Count);
        Assert.Equal((959, 968), adb.Taps[0]);
        Assert.Equal((continueX + continueTemplate.Width / 2, continueY + continueTemplate.Height / 2), adb.Taps[1]);
        Assert.Equal((1120 + 361 / 2, 930 + 120 / 2), adb.Taps[2]);
    }

    /// <summary>真实“选择/出击准备”按钮与继续共框；奖励等待不可把它们当继续补点，随后仍可识别真实结算。</summary>
    [Theory]
    [InlineData("stage/select.png")]
    [InlineData("stage/sortie/button_sortie_preparations.png")]
    public async Task ExecuteNextRoundAsync_SelectionButtonDuringSettlement_IsNotClicked(string relativePath)
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var opening = CreateTapToNextScreen();
        var continuePath = ConfigService.ResolvePath("resources/templates/stage/sortie/continue.png");
        var continued = CreateContinueScreen(continuePath, 1550, 950);
        var selection = CreateContinueScreen(ConfigService.ResolvePath("resources/templates/" + relativePath), 1494, 834);
        var resultPage = CreateResultScreen();
        var observations = 0;
        var adb = new FakeAdbService(opening);
        adb.ScreenshotProvider = () => adb.Taps.Count switch {
            0 => opening, 1 => continued,
            2 when ++observations <= 2 => selection,
            _ => resultPage
        };
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            continuePath, sortie.Again, sortie.AgainClose, sortie.Failed, sortie.EmptySlotHint,
            sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp, sortie.Reward,
            sortie.TapToNextReward, sortie.SelectStage);
        var result = await service.ExecuteNextRoundAsync("serial", moreRoundsRemaining: true,
            tapToNextWaitTimeoutMs: 1000, tapToNextPollIntervalMs: 10, tapToNextClickDelayMs: 1,
            continueStartDelayMs: 1, continueClickDelayMs: 1, continuePollIntervalMs: 10,
            continueWaitTimeoutMs: 2000, continueRepeatIntervalMs: 1, againStartDelayMs: 1,
            againPollIntervalMs: 10, againWaitTimeoutMs: 2000);
        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.True(observations >= 2);
        Assert.Equal(new[] { (959, 968), (1730, 1010), (1300, 990) }, adb.Taps);
    }

    [Fact]
    public async Task ExecuteAfterSortieAsync_DroppedUnitBeforeAgain_TapsOnceThenContinues()
    {
        using var temp = new TempDirectory();
        var dialog = CreateDialogTemplates(temp);
        var sortie = CreateSortieTemplates(temp);
        var icons = CreateIconsScreen(AutoGray, 160);
        var adb = new FakeAdbService(CreateTapToNextScreen());
        // 点击第二个继续按钮后出现掉落机体奖励提示：点击一次画面，继续识别再次出击
        adb.EnqueueScreenshots(icons, icons, CreateContinueScreen(), CreateContinueScreen(), CreateRewardScreen(), CreateContinueScreen(), CreateDroppedUnitScreen(), CreateDroppedUnitScreen(),
            CreateAgainScreen(), CreateAgainScreen());
        var service = CreateService(adb, dialog.Download, dialog.Cancel, sortie.Tap, sortie.SpeedOn, sortie.SpeedOff,
            sortie.Continue, sortie.Again, sortie.AgainClose, sortie.Failed,
            sortie.EmptySlotHint, sortie.DialogSortie, sortie.DialogCancel, sortie.NoAp, sortie.PlayerLevelUp,
            sortie.Reward, sortie.TapToNextReward);

        var result = await RunAsync(service, adb, moreRoundsRemaining: true);

        Assert.Equal(SortieFlowResult.RoundCompleted, result);
        Assert.Equal(7, adb.Taps.Count);
        Assert.Equal((800 + 303 / 2, 950 + 54 / 2), adb.Taps[5]); // 掉落机体奖励点击一次
        Assert.Equal((1120 + 361 / 2, 930 + 120 / 2), adb.Taps[6]); // 再次出击
    }

    /// <summary>测试用临时目录，自动清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-tests-" + Guid.NewGuid().ToString("N"));

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

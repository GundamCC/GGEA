using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 游戏入口流程测试：找到 tap to start 后点击中心；找不到时超时返回 false。
/// 登录奖励页的 TAP TO NEXT 使用底部完整文字 OCR 并点击跳过。
/// </summary>
public class GameEntryServiceTests
{
    [Fact]
    public async Task TryClickAnnouncementCloseIfVisibleAsync_ClosesStackedAnnouncementsWithTitle()
    {
        // 开始任务时每次都需要同帧标题与按钮；关闭后重查主界面。
        var closeScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteAnnouncementClose(closeScreen, x: 780, y: 940);
        PasteAnnouncement(closeScreen, x: 891, y: 30);
        var blankScreen = TestImageFactory.CreateBgra(1920, 1080, 20);

        using var temp = new TempDirectory();
        var announcementPath = Path.Combine(temp.Path, "announcement.png");
        File.WriteAllBytes(announcementPath, CreateAnnouncementPng());
        var closePath = Path.Combine(temp.Path, "close.png");
        File.WriteAllBytes(closePath, CreateAnnouncementClosePng());

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(closeScreen, 1920, 1080));
        fakeAdb.EnqueueScreenshots(
            ImageCodec.EncodePng(closeScreen, 1920, 1080),
            ImageCodec.EncodePng(blankScreen, 1920, 1080));
        var log = new TestLog();
        var service = new GameEntryService(
            new AutomationService(fakeAdb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr), log);

        var first = await service.TryClickAnnouncementCloseIfVisibleAsync("serial", closePath, announcementPath, CancellationToken.None);
        var second = await service.TryClickAnnouncementCloseIfVisibleAsync("serial", closePath, announcementPath, CancellationToken.None);
        var third = await service.TryClickAnnouncementCloseIfVisibleAsync("serial", closePath, announcementPath, CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.False(third);
        Assert.Equal(new[] { (961, 1001), (961, 1001) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task TryClickAnnouncementCloseIfVisibleAsync_WhenAbsent_LeavesScreenUntouchedAndSilent()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        // 底部存在相同按钮，但无公告标题，不得点击奖励弹窗。
        PasteAnnouncementClose(screen, x: 780, y: 940);
        using var temp = new TempDirectory();
        var announcementPath = Path.Combine(temp.Path, "announcement.png");
        File.WriteAllBytes(announcementPath, CreateAnnouncementPng());
        var closePath = Path.Combine(temp.Path, "close.png");
        File.WriteAllBytes(closePath, CreateAnnouncementClosePng());

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var log = new TestLog();
        var service = new GameEntryService(
            new AutomationService(fakeAdb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr), log);

        var closed = await service.TryClickAnnouncementCloseIfVisibleAsync("serial", closePath, announcementPath, CancellationToken.None);

        Assert.False(closed);
        Assert.Empty(fakeAdb.Taps);
        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("公告", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnterGameAsync_FindsTapToStart_ClicksCenter()
    {
        // 屏幕 200x160，目标（tap to start）在 (80,50)，大小 30x20，中心应为 (95,60)
        var screen = TestImageFactory.CreateBgra(200, 160, 20);
        TestImageFactory.PasteBlock(screen, 200, 30, 20, 80, 50, 200);
        var template = TestImageFactory.CreateBgra(30, 20, 200);

        using var temp = new TempDirectory();
        var templatePath = Path.Combine(temp.Path, "tap_to_start.png");
        File.WriteAllBytes(templatePath, ImageCodec.EncodePng(template, 30, 20));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 200, 160));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        var service = new GameEntryService(automation, new TestLog());

        var entered = await service.EnterGameAsync(
            "serial",
            templatePath,
            splashDelayMs: 50,
            networkSettleDelayMs: 10,
            new WaitOptions { TimeoutMs = 2000, PollIntervalMs = 10, Threshold = 0.9 });

        Assert.True(entered);
        Assert.Contains(fakeAdb.Taps, t => t == (95, 60));
    }

    [Fact]
    public async Task EnterGameAsync_NotFound_ReturnsFalse()
    {
        // 屏幕与模板完全不同，应超时返回 false 且不产生点击
        var screen = TestImageFactory.CreateBgra(200, 160, 20);
        var template = TestImageFactory.CreateBgra(30, 20, 200);

        using var temp = new TempDirectory();
        var templatePath = Path.Combine(temp.Path, "tap_to_start.png");
        File.WriteAllBytes(templatePath, ImageCodec.EncodePng(template, 30, 20));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 200, 160));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        var service = new GameEntryService(automation, new TestLog());

        var entered = await service.EnterGameAsync(
            "serial",
            templatePath,
            splashDelayMs: 50,
            networkSettleDelayMs: 10,
            new WaitOptions { TimeoutMs = 200, PollIntervalMs = 10, Threshold = 0.9 });

        Assert.False(entered);
        Assert.Empty(fakeAdb.Taps);
    }

    [Fact]
    public async Task EnterGameAsync_LoginRewardPresent_ClicksTapToNext()
    {
        // 屏幕 1920x1080：tap to start 在 (80,50)，TAP TO NEXT 在底部中间 (830,995)
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 30, 20, 80, 50, 200); // tap to start
        PasteTapToNext(screen, 1920, 280, 70, 830, 995, 100, 180, 240); // TAP TO NEXT

        using var temp = new TempDirectory();
        var startPath = Path.Combine(temp.Path, "tap_to_start.png");
        var nextPath = Path.Combine(temp.Path, "tap_to_next.png");
        File.WriteAllBytes(startPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(30, 20, 200), 30, 20));
        File.WriteAllBytes(nextPath, CreateTapToNextPng(280, 70, 100, 180, 240));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        var service = new GameEntryService(automation, new TestLog());

        var entered = await service.EnterGameAsync(
            "serial",
            startPath,
            splashDelayMs: 50,
            networkSettleDelayMs: 10,
            new WaitOptions { TimeoutMs = 2000, PollIntervalMs = 10, Threshold = 0.9 },
            nextPath,
            loginRewardWaitMs: 2000);

        Assert.True(entered);
        Assert.Contains(fakeAdb.Taps, t => t == (95, 60));    // tap to start
        Assert.Contains(fakeAdb.Taps, t => t == (970, 1030)); // TAP TO NEXT
    }

    [Fact]
    public async Task EnterGameAsync_NoLoginReward_ReturnsTrueWithoutExtraTap()
    {
        // 屏幕上只有 tap to start，没有 TAP TO NEXT
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 30, 20, 80, 50, 200);

        using var temp = new TempDirectory();
        var startPath = Path.Combine(temp.Path, "tap_to_start.png");
        var nextPath = Path.Combine(temp.Path, "tap_to_next.png");
        File.WriteAllBytes(startPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(30, 20, 200), 30, 20));
        File.WriteAllBytes(nextPath, CreateTapToNextPng(280, 70, 100, 180, 240));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        var service = new GameEntryService(automation, new TestLog());

        var entered = await service.EnterGameAsync(
            "serial",
            startPath,
            splashDelayMs: 50,
            networkSettleDelayMs: 10,
            new WaitOptions { TimeoutMs = 2000, PollIntervalMs = 10, Threshold = 0.9 },
            nextPath,
            loginRewardWaitMs: 200);

        Assert.True(entered);
        Assert.Single(fakeAdb.Taps);
        Assert.Equal((95, 60), fakeAdb.Taps[0]);
    }

    [Fact]
    public async Task EnterGameAsync_LoginRewardPagePresent_ClicksTapToNext()
    {
        // 公告图案位于顶部中央 ROI 外，不得触发公告关闭流程或未命中提示。
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 30, 20, 80, 50, 200); // tap to start
        PasteTapToNext(screen, 1920, 280, 70, 900, 960, 120, 200, 250); // 登录奖励页 TAP TO NEXT
        PasteAnnouncement(screen, x: 300, y: 30);

        using var temp = new TempDirectory();
        var startPath = Path.Combine(temp.Path, "tap_to_start.png");
        var loginNextPath = Path.Combine(temp.Path, "tap_to_next_login_reward.png");
        var announcementPath = Path.Combine(temp.Path, "announcement.png");
        File.WriteAllBytes(startPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(30, 20, 200), 30, 20));
        File.WriteAllBytes(loginNextPath, CreateTapToNextPng(280, 70, 120, 200, 250));
        File.WriteAllBytes(announcementPath, CreateAnnouncementPng());

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        var log = new TestLog();
        var service = new GameEntryService(automation, log);

        var entered = await service.EnterGameAsync(
            "serial",
            startPath,
            splashDelayMs: 50,
            networkSettleDelayMs: 10,
            new WaitOptions { TimeoutMs = 2000, PollIntervalMs = 10, Threshold = 0.9 },
            tapToNextTemplatePath: null,
            loginRewardWaitMs: 200,
            tapToNextLoginRewardTemplatePath: loginNextPath,
            loginRewardAnnouncementTemplatePath: announcementPath,
            loginRewardAnnouncementWaitMs: 20,
            loginRewardPageWaitMs: 2000);

        Assert.True(entered);
        Assert.Contains(fakeAdb.Taps, t => t == (95, 60));    // tap to start
        Assert.Contains(fakeAdb.Taps, t => t == (1040, 995)); // 登录奖励页 TAP TO NEXT
        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("公告", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnterGameAsync_LoginRewardDelayed_ClicksCenterAndSkipsUntilTarget()
    {
        var startScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(startScreen, 1920, 30, 20, 80, 50, 200);
        var waitingScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        var skipScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteSkip(skipScreen, x: 1650, y: 20);
        var targetScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTapToNext(targetScreen, 1920, 280, 70, 900, 960, 120, 200, 250);
        var announcementScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteAnnouncement(announcementScreen, x: 891, y: 30);
        var closeScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteAnnouncementClose(closeScreen, x: 780, y: 940);

        using var temp = new TempDirectory();
        var startPath = Path.Combine(temp.Path, "tap_to_start.png");
        var loginNextPath = Path.Combine(temp.Path, "tap_to_next_login_reward.png");
        var skipPath = Path.Combine(temp.Path, "skip.png");
        var announcementPath = Path.Combine(temp.Path, "announcement.png");
        var closePath = Path.Combine(temp.Path, "close.png");
        File.WriteAllBytes(startPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(30, 20, 200), 30, 20));
        File.WriteAllBytes(loginNextPath, CreateTapToNextPng(280, 70, 120, 200, 250));
        File.WriteAllBytes(skipPath, CreateSkipPng());
        File.WriteAllBytes(announcementPath, CreateAnnouncementPng());
        File.WriteAllBytes(closePath, CreateAnnouncementClosePng());

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(startScreen, 1920, 1080));
        fakeAdb.EnqueueScreenshots(
            ImageCodec.EncodePng(waitingScreen, 1920, 1080),
            ImageCodec.EncodePng(skipScreen, 1920, 1080),
            ImageCodec.EncodePng(skipScreen, 1920, 1080),
            ImageCodec.EncodePng(targetScreen, 1920, 1080),
            ImageCodec.EncodePng(announcementScreen, 1920, 1080),
            ImageCodec.EncodePng(closeScreen, 1920, 1080));
        var service = new GameEntryService(
            new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr),
            new TestLog());

        var entered = await service.EnterGameAsync(
            "serial",
            startPath,
            splashDelayMs: 0,
            networkSettleDelayMs: 0,
            new WaitOptions { TimeoutMs = 1000, PollIntervalMs = 1, Threshold = 0.9 },
            tapToNextTemplatePath: null,
            tapToNextLoginRewardTemplatePath: loginNextPath,
            loginRewardSkipTemplatePath: skipPath,
            loginRewardFallbackDelayMs: 0,
            loginRewardAnnouncementTemplatePath: announcementPath,
            loginRewardAnnouncementCloseTemplatePath: closePath,
            loginRewardAnnouncementWaitMs: 1000,
            loginRewardPageWaitMs: 1000);

        Assert.True(entered);
        Assert.Equal(
            new[] { (95, 60), (960, 540), (1778, 57), (1778, 57), (1040, 995), (961, 1001) },
            fakeAdb.Taps);
    }

    [Fact]
    public async Task EnterGameAsync_LoginRewardSkipOutsideTopRight_DoesNotClickSkip()
    {
        var startScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(startScreen, 1920, 30, 20, 80, 50, 200);
        var waitingScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        var outsideSkipScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteSkip(outsideSkipScreen, x: 1300, y: 20);
        var targetScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTapToNext(targetScreen, 1920, 280, 70, 900, 960, 120, 200, 250);

        using var temp = new TempDirectory();
        var startPath = Path.Combine(temp.Path, "tap_to_start.png");
        var loginNextPath = Path.Combine(temp.Path, "tap_to_next_login_reward.png");
        var skipPath = Path.Combine(temp.Path, "skip.png");
        File.WriteAllBytes(startPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(30, 20, 200), 30, 20));
        File.WriteAllBytes(loginNextPath, CreateTapToNextPng(280, 70, 120, 200, 250));
        File.WriteAllBytes(skipPath, CreateSkipPng());

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(startScreen, 1920, 1080));
        fakeAdb.EnqueueScreenshots(
            ImageCodec.EncodePng(waitingScreen, 1920, 1080),
            ImageCodec.EncodePng(outsideSkipScreen, 1920, 1080),
            ImageCodec.EncodePng(targetScreen, 1920, 1080));
        var service = new GameEntryService(
            new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr),
            new TestLog());

        await service.EnterGameAsync(
            "serial",
            startPath,
            splashDelayMs: 0,
            networkSettleDelayMs: 0,
            new WaitOptions { TimeoutMs = 1000, PollIntervalMs = 1, Threshold = 0.9 },
            tapToNextTemplatePath: null,
            tapToNextLoginRewardTemplatePath: loginNextPath,
            loginRewardSkipTemplatePath: skipPath,
            loginRewardFallbackDelayMs: 0,
            loginRewardPageWaitMs: 1000);

        Assert.Equal(new[] { (95, 60), (960, 540), (1040, 995) }, fakeAdb.Taps);
    }

    /// <summary>使用真实 TAP TO NEXT 字形生成状态机页面。</summary>
    private static byte[] CreateTapToNextPng(int width, int height, byte v1, byte v2, byte v3)
    {
        return TapToNextOcrSamples.TextPng(width, height);
    }

    /// <summary>在屏幕中粘贴真实 TAP TO NEXT 字形。</summary>
    private static void PasteTapToNext(
        byte[] screen,
        int screenWidth,
        int width,
        int height,
        int x,
        int y,
        byte v1,
        byte v2,
        byte v3)
    {
        TapToNextOcrSamples.Paste(screen, screenWidth, width, height, x, y);
    }

    /// <summary>生成三段灰度的右上角“略过”模板。</summary>
    private static byte[] CreateSkipPng()
    {
        var pixels = TestImageFactory.CreateBgra(257, 74, 90);
        TestImageFactory.PasteBlock(pixels, 257, 85, 74, 0, 0, 90);
        TestImageFactory.PasteBlock(pixels, 257, 85, 74, 85, 0, 170);
        TestImageFactory.PasteBlock(pixels, 257, 87, 74, 170, 0, 240);
        return ImageCodec.EncodePng(pixels, 257, 74);
    }

    /// <summary>把测试“略过”按钮贴到指定屏幕坐标。</summary>
    private static void PasteSkip(byte[] screen, int x, int y)
    {
        TestImageFactory.PasteBlock(screen, 1920, 85, 74, x, y, 90);
        TestImageFactory.PasteBlock(screen, 1920, 85, 74, x + 85, y, 170);
        TestImageFactory.PasteBlock(screen, 1920, 87, 74, x + 170, y, 240);
    }

    /// <summary>生成三段灰度的公告标题模板。</summary>
    private static byte[] CreateAnnouncementPng()
    {
        var pixels = TestImageFactory.CreateBgra(138, 55, 70);
        TestImageFactory.PasteBlock(pixels, 138, 46, 55, 0, 0, 70);
        TestImageFactory.PasteBlock(pixels, 138, 46, 55, 46, 0, 150);
        TestImageFactory.PasteBlock(pixels, 138, 46, 55, 92, 0, 230);
        return ImageCodec.EncodePng(pixels, 138, 55);
    }

    /// <summary>把测试公告标题贴到指定屏幕坐标。</summary>
    private static void PasteAnnouncement(byte[] screen, int x, int y)
    {
        TestImageFactory.PasteBlock(screen, 1920, 46, 55, x, y, 70);
        TestImageFactory.PasteBlock(screen, 1920, 46, 55, x + 46, y, 150);
        TestImageFactory.PasteBlock(screen, 1920, 46, 55, x + 92, y, 230);
    }

    /// <summary>生成三段灰度的公告关闭按钮模板。</summary>
    private static byte[] CreateAnnouncementClosePng()
    {
        var pixels = TestImageFactory.CreateBgra(363, 122, 80);
        TestImageFactory.PasteBlock(pixels, 363, 121, 122, 0, 0, 80);
        TestImageFactory.PasteBlock(pixels, 363, 121, 122, 121, 0, 160);
        TestImageFactory.PasteBlock(pixels, 363, 121, 122, 242, 0, 240);
        return ImageCodec.EncodePng(pixels, 363, 122);
    }

    /// <summary>把测试公告关闭按钮贴到指定屏幕坐标。</summary>
    private static void PasteAnnouncementClose(byte[] screen, int x, int y)
    {
        TestImageFactory.PasteBlock(screen, 1920, 121, 122, x, y, 80);
        TestImageFactory.PasteBlock(screen, 1920, 121, 122, x + 121, y, 160);
        TestImageFactory.PasteBlock(screen, 1920, 121, 122, x + 242, y, 240);
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

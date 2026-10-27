using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 开发服务测试：直接找到机体、单方向拖动后找到、回弹后切换方向。
/// </summary>
public class DevelopServiceTests
{
    /// <summary>验证原图优先、替补命中及可选素材缺失，不延长实际流程的等待预算。</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task MatchFirstRoadmapAsync_UsesUnfinishedOnlyAfterPrimaryMiss(bool primaryPresent, bool fallbackPresent)
    {
        using var temp = new TempDirectory();
        var primary = Path.Combine(temp.Path, "dev_0079.png");
        var fallback = Path.Combine(temp.Path, "dev_0079_unfinished.png");
        File.WriteAllBytes(primary, ImageCodec.EncodePng(TestImageFactory.CreateBgra(20, 20, 200), 20, 20));
        if (fallbackPresent)
            // 主图命中时故意使用无效替补，确保短路返回而不读取它。
            File.WriteAllBytes(fallback, primaryPresent ? new byte[] { 0 } :
                ImageCodec.EncodePng(TestImageFactory.CreateBgra(20, 20, 100), 20, 20));
        var pixels = TestImageFactory.CreateBgra(100, 100, 20);
        TestImageFactory.PasteBlock(pixels, 100, 20, 20, 40, 40, primaryPresent ? (byte)200 : (byte)100);
        var png = ImageCodec.EncodePng(pixels, 100, 100);
        using var automation = new AutomationService(new FakeAdbService(png), new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());
        var result = await service.MatchFirstRoadmapAsync(ImageCodec.DecodePng(png), primary,
            new WaitOptions { Threshold = 0.95 }, CancellationToken.None);
        Assert.Equal(primaryPresent || fallbackPresent, result is not null);
    }

    private static UiLayout CreateMainLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "navDevelop", X = 604, Y = 900, Width = 220, Height = 155 },
            new() { Name = "navHome", X = 74, Y = 900, Width = 220, Height = 155 },
        },
    };

    private static UiLayout CreateDevelopLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "firstRoadmap", X = 466, Y = 250, Width = 327, Height = 178 },
            new() { Name = "developAll", X = 1640, Y = 940, Width = 190, Height = 70 },
            new() { Name = "executeDevelop", X = 1060, Y = 960, Width = 190, Height = 70 },
            new() { Name = "executeConfirm", X = 1062, Y = 955, Width = 170, Height = 80 },
            new() { Name = "showcaseTapToNext", X = 830, Y = 995, Width = 280, Height = 70 },
            new() { Name = "roadmapBack", X = 0, Y = 0, Width = 240, Height = 130 },
            new() { Name = "developTitle", X = 240, Y = 30, Width = 620, Height = 110 },
            new() { Name = "roadmapDisplaySettingsTitle", X = 700, Y = 45, Width = 520, Height = 90 },
            new() { Name = "roadmapDisplaySettingsConfirm", X = 780, Y = 930, Width = 360, Height = 120 },
        },
    };

    private static (string Roadmap, string Ms) CreateTemplateFiles(TempDirectory temp)
    {
        var roadmap = Path.Combine(temp.Path, "roadmap.png");
        var ms = Path.Combine(temp.Path, "ms.png");
        File.WriteAllBytes(roadmap, ImageCodec.EncodePng(TestImageFactory.CreateBgra(200, 100, 200), 200, 100));
        File.WriteAllBytes(ms, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 150), 100, 100));
        return (roadmap, ms);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindAndTapMsAsync_FoundImmediately_TapsMs(bool unfinished)
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 200, 100, 519, 257, 200); // 路线图
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 500, 600, 150); // 目标机体

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        if (unfinished)
        {
            // 实际入口只出现未完成卡片时，仍点击原路线图锚点并继续机体查找。
            File.Move(files.Roadmap, Path.Combine(temp.Path, "dev_0079_unfinished.png"));
            files.Roadmap = Path.Combine(temp.Path, "dev_0079.png");
            File.WriteAllBytes(files.Roadmap, ImageCodec.EncodePng(TestImageFactory.CreateBgra(200, 100, 250), 200, 100));
        }

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            "",
            "",
            maxDragsPerDirection: 4,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10);

        Assert.NotNull(match);
        Assert.Equal(550, match!.CenterX);
        Assert.Equal(650, match.CenterY);
        Assert.Equal(new[] { (714, 977), (629, 339), (550, 650) }, fakeAdb.Taps);
        Assert.Empty(fakeAdb.PinchInDurations);
        Assert.Empty(fakeAdb.Swipes);
    }

    [Fact]
    public async Task FindAndTapMsAsync_RoadmapWaitTimedOut_TapsRecoveryPositionThenDetectsTitle()
    {
        // 首次等待的 10 秒内没有入口路线图；恢复点击后直接识别路线图标题，不能重复点击入口卡片。
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 500, 600, 150);
        TestImageFactory.PasteBlock(screen, 1920, 620, 110, 240, 30, 130);

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var title = Path.Combine(temp.Path, "title.png");
        File.WriteAllBytes(title, ImageCodec.EncodePng(TestImageFactory.CreateBgra(620, 110, 130), 620, 110));
        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            "",
            title,
            maxDragsPerDirection: 1,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            titleThreshold: 0.9,
            titleWaitMs: 500);

        Assert.NotNull(match);
        Assert.Equal(550, match!.CenterX);
        Assert.Equal(650, match.CenterY);
        Assert.Equal(new[] { (714, 977), (688, 342), (550, 650) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task FindAndTapMsAsync_DisplaySettingsVisible_ConfirmsThenContinues()
    {
        // 首次进入实际作品路线图时会出现全屏显示设置；命中专用标题后只能点击底部确定，
        // 随后继续等待真实路线图标题并执行原有机体搜索。
        var entry = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(entry, 1920, 200, 100, 519, 257, 200);
        var settings = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(settings, 1920, 390, 52, 765, 68, 170);
        var roadmap = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(roadmap, 1920, 620, 110, 240, 30, 130);
        TestImageFactory.PasteBlock(roadmap, 1920, 100, 100, 500, 600, 150);

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var title = Path.Combine(temp.Path, "title.png");
        var settingsTitle = Path.Combine(temp.Path, "settings_title.png");
        File.WriteAllBytes(title, ImageCodec.EncodePng(TestImageFactory.CreateBgra(620, 110, 130), 620, 110));
        File.WriteAllBytes(
            settingsTitle,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(390, 52, 170), 390, 52));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(entry, 1920, 1080));
        fakeAdb.EnqueueScreenshots(
            ImageCodec.EncodePng(settings, 1920, 1080),
            ImageCodec.EncodePng(settings, 1920, 1080),
            ImageCodec.EncodePng(roadmap, 1920, 1080));
        var log = new TestLog();
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), log);
        var service = new DevelopService(automation, log);

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            "",
            title,
            maxDragsPerDirection: 1,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            titleThreshold: 0.9,
            titleWaitMs: 500,
            roadmapDisplaySettingsTitleTemplatePath: settingsTitle);

        Assert.NotNull(match);
        Assert.Equal(new[] { (714, 977), (629, 339), (960, 990), (550, 650) }, fakeAdb.Taps);
        Assert.Contains(log.Entries, e => e.Message.Contains("检测到开发路线图显示设置"));
        Assert.Contains(log.Entries, e => e.Message.Contains("已识别开发页标题"));
    }

    [Fact]
    public async Task FindAndTapMsAsync_FoundAfterLeftDrag()
    {
        var noMs = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(noMs, 1920, 200, 100, 519, 257, 200);
        var withMs = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(withMs, 1920, 200, 100, 519, 257, 200);
        TestImageFactory.PasteBlock(withMs, 1920, 100, 100, 700, 400, 150);

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);

        // 先在左上角分别用两次手势确认顶部/左侧，正式向右扫描（手指左拖）后出现机体。
        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(noMs, 1920, 1080));
        fakeAdb.ScreenshotProvider = () => ImageCodec.EncodePng(
            fakeAdb.Swipes.Any(s => s.X1 > s.X2) ? withMs : noMs, 1920, 1080);
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            "",
            "",
            maxDragsPerDirection: 4,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10);

        Assert.NotNull(match);
        Assert.Equal(750, match!.CenterX);
        Assert.Equal(450, match.CenterY);
        Assert.Empty(fakeAdb.PinchInDurations);
        Assert.Equal(new[] { (0, 250), (0, 250), (850, 0), (850, 0), (-850, 0) },
            fakeAdb.Swipes.Select(s => (s.X2 - s.X1, s.Y2 - s.Y1)));
    }

    [Fact]
    public async Task FindAndTapMsAsync_BounceSwitchesToNextDirection()
    {
        // 单屏画布无机体：先探测顶部/左侧，再探测本行右侧/下一行后结束。
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 200, 100, 519, 257, 200);

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            "",
            "",
            maxDragsPerDirection: 4,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10);

        Assert.Null(match);
        Assert.Empty(fakeAdb.PinchInDurations);
        // 每个方向均换起点复核一次：下、右（归位）、左（扫描）、上（下一行）。
        Assert.Equal(new[] { (0, 250), (0, 250), (850, 0), (850, 0), (-850, 0), (-850, 0), (0, -250), (0, -250) },
            fakeAdb.Swipes.Select(s => (s.X2 - s.X1, s.Y2 - s.Y1)));
        Assert.Equal(new[] { (714, 977), (629, 339) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task FindAndTapMsAsync_TitleNotRecognized_AbortsAfterRoadmap()
    {
        // 进入路线图后页面里没有标题，配置了标题模板时应中止，不能继续缩放搜索
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 200, 100, 519, 257, 200); // 路线图
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 500, 600, 150); // 目标机体

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var title = Path.Combine(temp.Path, "title.png");
        File.WriteAllBytes(title, ImageCodec.EncodePng(TestImageFactory.CreateBgra(620, 110, 130), 620, 110));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            "",
            title,
            maxDragsPerDirection: 4,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            titleThreshold: 0.9,
            titleWaitMs: 500);

        Assert.Null(match);
        // 标题识别失败即中断：点击了 navDevelop 和第一个路线图，但未进入缩放搜索
        Assert.Equal(new[] { (714, 977), (629, 339) }, fakeAdb.Taps);
        Assert.Empty(fakeAdb.Swipes);
    }

    [Fact]
    public async Task FindAndTapMsAsync_TitleRecognized_TapsMs()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 200, 100, 519, 257, 200); // 路线图
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 500, 600, 150); // 目标机体
        TestImageFactory.PasteBlock(screen, 1920, 620, 110, 240, 30, 130);  // 开发页标题

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var title = Path.Combine(temp.Path, "title.png");
        File.WriteAllBytes(title, ImageCodec.EncodePng(TestImageFactory.CreateBgra(620, 110, 130), 620, 110));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            "",
            title,
            maxDragsPerDirection: 4,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            titleThreshold: 0.9,
            titleWaitMs: 5000);

        Assert.NotNull(match);
        Assert.Equal(550, match!.CenterX);
        Assert.Equal(650, match.CenterY);
        Assert.Equal(new[] { (714, 977), (629, 339), (550, 650) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task FindAndTapMsAsync_UnstableMatch_DoesNotTapMs()
    {
        // 第一帧出现机体匹配，但后续帧画面变化（缩放/动画中的瞬时匹配），不应点击
        var noMs = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(noMs, 1920, 200, 100, 519, 257, 200);
        var withMs = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(withMs, 1920, 200, 100, 519, 257, 200);
        TestImageFactory.PasteBlock(withMs, 1920, 100, 100, 700, 400, 150);

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);

        // 路线图确认帧后：第一帧命中机体，随后画面不再有该机体（瞬时匹配）
        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(noMs, 1920, 1080));
        fakeAdb.EnqueueScreenshots(
            ImageCodec.EncodePng(withMs, 1920, 1080),
            ImageCodec.EncodePng(noMs, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            "",
            "",
            maxDragsPerDirection: 4,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10);

        Assert.Null(match);
        // 未发生稳定匹配：从未点击机体，只在四个方向各拖动并有限复核一次。
        Assert.Equal(new[] { (714, 977), (629, 339) }, fakeAdb.Taps);
        Assert.Equal(8, fakeAdb.Swipes.Count);
    }

    [Fact]
    public async Task FindAndTapMsAsync_LargeTemplateMatched_TapsDirectlyWithoutZoom()
    {
        // 路线图保留了大缩放下的大图机体：缩放前命中大图模板，应直接点击进入开发，
        // 不再保留画布比例与方向搜索
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 200, 100, 519, 257, 200);   // 路线图
        TestImageFactory.PasteBlock(screen, 1920, 342, 348, 800, 300, 130);   // 大图机体

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var large = Path.Combine(temp.Path, "ms_large.png");
        File.WriteAllBytes(large, ImageCodec.EncodePng(TestImageFactory.CreateBgra(342, 348, 130), 342, 348));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            large,
            "",
            maxDragsPerDirection: 4,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10);

        Assert.NotNull(match);
        Assert.Equal(971, match!.CenterX);
        Assert.Equal(474, match.CenterY);
        // 未保留画布比例、未拖动搜索，直接点击大图机体
        Assert.Empty(fakeAdb.PinchInDurations);
        Assert.Empty(fakeAdb.Swipes);
        Assert.Equal(new[] { (714, 977), (629, 339), (971, 474) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task FindAndTapMsAsync_LargeTemplateNotMatched_MatchesSmallWithoutZoom()
    {
        // 画面里只有缩放后的小图机体：大图模板未命中，应回退到直接小图搜索
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 200, 100, 519, 257, 200);   // 路线图
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 500, 600, 150);   // 小图机体

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var large = Path.Combine(temp.Path, "ms_large.png");
        File.WriteAllBytes(large, ImageCodec.EncodePng(TestImageFactory.CreateBgra(342, 348, 130), 342, 348));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            large,
            "",
            maxDragsPerDirection: 4,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10);

        Assert.NotNull(match);
        Assert.Equal(550, match!.CenterX);
        Assert.Equal(650, match.CenterY);
        // 大图未命中后直接通过小图模板找到机体，始终不发送缩放输入
        Assert.Empty(fakeAdb.PinchInDurations);
        Assert.Empty(fakeAdb.Swipes);
        Assert.Equal(new[] { (714, 977), (629, 339), (550, 650) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task FindAndTapMsAsync_LargeTemplateUnstable_MatchesSmallWithoutZoom()
    {
        // 大图模板只在第一帧命中（页面动画中的瞬时匹配），确认帧不稳定，
        // 应回退到直接小图搜索，不能直接点击大图
        var screenA = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screenA, 1920, 200, 100, 519, 257, 200);  // 路线图
        var screenB = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screenB, 1920, 200, 100, 519, 257, 200);
        TestImageFactory.PasteBlock(screenB, 1920, 342, 348, 800, 300, 130);  // 瞬时大图
        var screenC = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screenC, 1920, 200, 100, 519, 257, 200);
        TestImageFactory.PasteBlock(screenC, 1920, 100, 100, 500, 600, 150);  // 小图机体

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var large = Path.Combine(temp.Path, "ms_large.png");
        File.WriteAllBytes(large, ImageCodec.EncodePng(TestImageFactory.CreateBgra(342, 348, 130), 342, 348));

        // 路线图确认帧后：第一帧出现大图机体，随后画面稳定为小图机体
        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screenA, 1920, 1080));
        fakeAdb.EnqueueScreenshots(
            ImageCodec.EncodePng(screenB, 1920, 1080),
            ImageCodec.EncodePng(screenC, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            large,
            "",
            maxDragsPerDirection: 4,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10);

        Assert.NotNull(match);
        Assert.Equal(550, match!.CenterX);
        Assert.Equal(650, match.CenterY);
        // 大图不稳定未直接点击，回退后保留画布比例并通过小图点击机体
        Assert.Empty(fakeAdb.PinchInDurations);
        Assert.Equal(new[] { (714, 977), (629, 339), (550, 650) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task FindAndTapMsAsync_SearchLoopMatchesLargeTemplateAfterDrag()
    {
        // 初始大图未命中；归位后的第一次扫描出现大图，搜索循环应命中大图模板并点击，
        // 而不是只认小图模板
        var screenA = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screenA, 1920, 200, 100, 519, 257, 200);  // 路线图
        var screenB = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screenB, 1920, 200, 100, 519, 257, 200);
        TestImageFactory.PasteBlock(screenB, 1920, 342, 348, 800, 300, 130);  // 大图机体

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var large = Path.Combine(temp.Path, "ms_large.png");
        File.WriteAllBytes(large, ImageCodec.EncodePng(TestImageFactory.CreateBgra(342, 348, 130), 342, 348));

        // 按真实扫描手势提供画面，不依赖内部的截图调用次数。
        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screenA, 1920, 1080));
        fakeAdb.ScreenshotProvider = () => ImageCodec.EncodePng(
            fakeAdb.Swipes.Any(s => s.X1 > s.X2) ? screenB : screenA, 1920, 1080);
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new DevelopService(automation, new TestLog());

        var match = await service.FindAndTapMsAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            large,
            "",
            maxDragsPerDirection: 4,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            networkSettleDelayMs: 10);

        Assert.NotNull(match);
        Assert.Equal(971, match!.CenterX);
        Assert.Equal(474, match.CenterY);
        // 顶部和左侧分别两次手势确认边界，加一次正式扫描，最终点击大图机体。
        Assert.Empty(fakeAdb.PinchInDurations);
        Assert.Equal(5, fakeAdb.Swipes.Count);
        Assert.Equal(new[] { (714, 977), (629, 339), (971, 474) }, fakeAdb.Taps);
    }

    [Fact]
    public void IsNearlySame_ParticleEffectsTolerated_ReturnsTrue()
    {
        // 两张截图只有少量粒子特效位置不同（约占 5% 采样点），应判定为“几乎相同”
        var a = TestImageFactory.CreateBgra(1920, 1080, 20);
        var b = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(a, 1920, 200, 100, 519, 257, 200);
        TestImageFactory.PasteBlock(b, 1920, 200, 100, 519, 257, 200);

        // 添加 60 个 30x30 的粒子光点，B 中整体偏移 8px 模拟粒子动画帧
        for (var i = 0; i < 60; i++)
        {
            var px = (i * 137) % 1700 + 50;
            var py = (i * 211) % 900 + 50;
            TestImageFactory.PasteBlock(a, 1920, 30, 30, px, py, 250);
            TestImageFactory.PasteBlock(b, 1920, 30, 30, px + 8, py + 8, 250);
        }

        var pngA = ImageCodec.EncodePng(a, 1920, 1080);
        var pngB = ImageCodec.EncodePng(b, 1920, 1080);

        Assert.True(DevelopService.IsNearlySame(pngA, pngB));
    }

    [Fact]
    public void IsNearlySame_ContentMoved_ReturnsFalse()
    {
        // 整块内容位移属于真实拖动，差异远超粒子容差
        // 大块内容位移（接近整屏变化），差异远超粒子容差
        var a = TestImageFactory.CreateBgra(1920, 1080, 20);
        var b = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(a, 1920, 1600, 900, 0, 0, 200);
        TestImageFactory.PasteBlock(b, 1920, 1600, 900, 60, 60, 200);

        var pngA = ImageCodec.EncodePng(a, 1920, 1080);
        var pngB = ImageCodec.EncodePng(b, 1920, 1080);

        Assert.False(DevelopService.IsNearlySame(pngA, pngB));
    }

    /// <summary>通过共享接口验证真实开发操作序列及明确结果，防止重构改变点击和失败边界。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ExecuteAsync_SuccessOrPreCancellation_PreservesInputBoundary(bool canceled, bool insufficient)
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 200, 100, 519, 257, 200); // 路线图
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 500, 600, 150); // 目标机体
        TestImageFactory.PasteBlock(screen, 1920, 190, 70, 1640, 940, 180); // 全部开发
        TestImageFactory.PasteBlock(screen, 1920, 190, 70, 1060, 960, 170); // 执行开发
        TestImageFactory.PasteBlock(screen, 1920, 170, 80, 1090, 950, 160); // 第二层确认执行
        TapToNextOcrSamples.Paste(screen, 1920, 280, 70, 830, 995); // 立绘真实继续文字
        TestImageFactory.PasteBlock(screen, 1920, 215, 105, 0, 1, 140);     // 返回按钮

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var developAll = Path.Combine(temp.Path, "develop_all.png");
        var developAllDisabled = Path.Combine(temp.Path, "develop_all_disabled.png");
        var executeDevelop = Path.Combine(temp.Path, "execute_develop.png");
        var executeConfirm = Path.Combine(temp.Path, "execute_confirm.png");
        var showcaseTap = Path.Combine(temp.Path, "showcase_tap.png");
        var backButton = Path.Combine(temp.Path, "back.png");
        File.WriteAllBytes(developAll, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 180), 190, 70));
        File.WriteAllBytes(developAllDisabled, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 90), 190, 70));
        File.Copy(GGEA.App.Config.ConfigService.ResolvePath(new GGEA.App.Config.AppConfig().DevelopExecuteTemplate), executeDevelop);
        File.Copy(GGEA.App.Config.ConfigService.ResolvePath(new GGEA.App.Config.AppConfig().DevelopExecuteConfirmTemplate), executeConfirm);
        File.WriteAllBytes(showcaseTap, ImageCodec.EncodePng(TestImageFactory.CreateBgra(280, 70, 150), 280, 70));
        File.WriteAllBytes(backButton, ImageCodec.EncodePng(TestImageFactory.CreateBgra(215, 105, 140), 215, 105));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        // 两层确认出现在不同画面；使用真实新模板，避免重叠纯色块掩盖全宽识别的歧义。
        fakeAdb.ScreenshotProvider = () =>
        {
            if (fakeAdb.Taps.Count is not (4 or 5)) return ImageCodec.EncodePng(screen, 1920, 1080);
            var frame = TestImageFactory.CreateBgra(1920, 1080, 20);
            var template = ImageCodec.DecodePng(File.ReadAllBytes(fakeAdb.Taps.Count == 4 ? (insufficient ? GGEA.App.Config.ConfigService.ResolvePath(DevelopExecutionButton.DisabledTemplate) : executeDevelop) : executeConfirm));
            for (var row = 0; row < template.Height; row++)
                Array.Copy(template.Bgra, row * template.Width * 4, frame, ((933 + row) * 1920 + 964) * 4, template.Width * 4);
            return ImageCodec.EncodePng(frame, 1920, 1080);
        };
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        IDevelopService service = new DevelopService(automation, new TestLog());

        var request = new DevelopRequest
        {
            MainLayout = CreateMainLayout(),
            DevelopLayout = CreateDevelopLayout(),
            RoadmapFirstTemplatePath = files.Roadmap,
            MsTemplatePath = files.Ms,
            MsLargeTemplatePath = "",
            DevelopAllTemplatePath = developAll,
            DevelopAllDisabledTemplatePath = developAllDisabled,
            ExecuteDevelopTemplatePath = executeDevelop,
            ExecuteConfirmTemplatePath = executeConfirm,
            ShowcaseTapToNextTemplatePath = showcaseTap,
            BackButtonTemplatePath = backButton,
            DevelopTitleTemplatePath = "",
            MaxDragsPerDirection = 4,
            DragDurationMs = 50,
            Threshold = 0.9,
            StepDelayMs = 10,
            DevelopWaitTimeoutMs = 3000,
            NetworkSettleDelayMs = 10,
        };
        if (canceled)
        {
            // 取消必须向调用方传播，且在进入游戏页面前不产生输入。
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ExecuteAsync("serial", request, new CancellationToken(true)));
            Assert.Empty(fakeAdb.Taps);
            Assert.Empty(fakeAdb.Swipes);
            return;
        }
        var result = await service.ExecuteAsync("serial", request);

        if (insufficient)
        {
            Assert.Equal(DevelopResult.UnitLimitReached, result);
            Assert.Equal(4, fakeAdb.Taps.Count); // 只返回资源不足，由编排层执行共享退出，不抢先取消。
            return;
        }
        Assert.Equal(DevelopResult.Completed, result);
        Assert.Equal(
            new[]
            {
                (714, 977), (629, 339), (550, 650), (1735, 975), (1148, 994),
                (1148, 996), (970, 1030), (120, 65), (184, 977),
            },
            fakeAdb.Taps);
    }

    /// <summary>通过共享接口验证真实开发操作序列及明确结果，防止重构改变点击和失败边界。</summary>
    [Fact]
    public async Task ExecuteAsync_DisabledDevelopAll_SkipsAndReturnsHome()
    {
        // 机体详情页中“全部开发”按钮为灰色禁用状态（今日已开发或无可用开发）
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 200, 100, 519, 257, 200); // 路线图
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 500, 600, 150); // 目标机体
        TestImageFactory.PasteBlock(screen, 1920, 190, 70, 1640, 940, 100); // 全部开发（灰色禁用）

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var developAll = Path.Combine(temp.Path, "develop_all.png");
        var developAllDisabled = Path.Combine(temp.Path, "develop_all_disabled.png");
        var executeDevelop = Path.Combine(temp.Path, "execute_develop.png");
        var executeConfirm = Path.Combine(temp.Path, "execute_confirm.png");
        var showcaseTap = Path.Combine(temp.Path, "showcase_tap.png");
        var backButton = Path.Combine(temp.Path, "back.png");
        File.WriteAllBytes(developAll, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 180), 190, 70));
        File.WriteAllBytes(developAllDisabled, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 100), 190, 70));
        File.WriteAllBytes(executeDevelop, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 170), 190, 70));
        File.WriteAllBytes(executeConfirm, ImageCodec.EncodePng(TestImageFactory.CreateBgra(170, 80, 160), 170, 80));
        File.WriteAllBytes(showcaseTap, ImageCodec.EncodePng(TestImageFactory.CreateBgra(280, 70, 150), 280, 70));
        File.WriteAllBytes(backButton, ImageCodec.EncodePng(TestImageFactory.CreateBgra(215, 105, 140), 215, 105));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        IDevelopService service = new DevelopService(automation, new TestLog());

        var result = await service.ExecuteAsync("serial", new DevelopRequest
        {
            MainLayout = CreateMainLayout(),
            DevelopLayout = CreateDevelopLayout(),
            RoadmapFirstTemplatePath = files.Roadmap,
            MsTemplatePath = files.Ms,
            MsLargeTemplatePath = "",
            DevelopAllTemplatePath = developAll,
            DevelopAllDisabledTemplatePath = developAllDisabled,
            ExecuteDevelopTemplatePath = executeDevelop,
            ExecuteConfirmTemplatePath = executeConfirm,
            ShowcaseTapToNextTemplatePath = showcaseTap,
            BackButtonTemplatePath = backButton,
            DevelopTitleTemplatePath = "",
            MaxDragsPerDirection = 4,
            DragDurationMs = 50,
            Threshold = 0.9,
            StepDelayMs = 10,
            DevelopWaitTimeoutMs = 3000,
            NetworkSettleDelayMs = 10,
        });

        Assert.Equal(DevelopResult.Unavailable, result);
        // 只进入开发页、点击路线图、点击机体并返回主界面，不点击全部开发 / 执行开发
        Assert.Equal(new[] { (714, 977), (629, 339), (550, 650), (184, 977) }, fakeAdb.Taps);
        Assert.Empty(fakeAdb.Swipes);
    }

    /// <summary>通过共享接口验证真实开发操作序列及明确结果，防止重构改变点击和失败边界。</summary>
    [Fact]
    public async Task ExecuteAsync_NoDevelopAll_ReturnsFalse()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 200, 100, 519, 257, 200); // 路线图
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 500, 600, 150); // 目标机体

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var developAll = Path.Combine(temp.Path, "develop_all.png");
        var executeDevelop = Path.Combine(temp.Path, "execute_develop.png");
        var executeConfirm = Path.Combine(temp.Path, "execute_confirm.png");
        var showcaseTap = Path.Combine(temp.Path, "showcase_tap.png");
        var backButton = Path.Combine(temp.Path, "back.png");
        File.WriteAllBytes(developAll, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 180), 190, 70));
        File.WriteAllBytes(executeDevelop, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 170), 190, 70));
        File.WriteAllBytes(executeConfirm, ImageCodec.EncodePng(TestImageFactory.CreateBgra(170, 80, 160), 170, 80));
        File.WriteAllBytes(showcaseTap, ImageCodec.EncodePng(TestImageFactory.CreateBgra(280, 70, 150), 280, 70));
        File.WriteAllBytes(backButton, ImageCodec.EncodePng(TestImageFactory.CreateBgra(80, 80, 140), 80, 80));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        IDevelopService service = new DevelopService(automation, new TestLog());

        var result = await service.ExecuteAsync("serial", new DevelopRequest
        {
            MainLayout = CreateMainLayout(),
            DevelopLayout = CreateDevelopLayout(),
            RoadmapFirstTemplatePath = files.Roadmap,
            MsTemplatePath = files.Ms,
            MsLargeTemplatePath = "",
            DevelopAllTemplatePath = developAll,
            DevelopAllDisabledTemplatePath = "",
            ExecuteDevelopTemplatePath = executeDevelop,
            ExecuteConfirmTemplatePath = executeConfirm,
            ShowcaseTapToNextTemplatePath = showcaseTap,
            BackButtonTemplatePath = backButton,
            DevelopTitleTemplatePath = "",
            MaxDragsPerDirection = 4,
            DragDurationMs = 50,
            Threshold = 0.9,
            StepDelayMs = 10,
            DevelopWaitTimeoutMs = 300,
            NetworkSettleDelayMs = 10,
        });

        Assert.Equal(DevelopResult.Failed, result);
        Assert.Equal(new[] { (714, 977), (629, 339), (550, 650) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task DevelopAndEnhanceOnceAsync_FailureRecognizesTitleOnce()
    {
        // 目标机体可找到、页面标题存在，但“全部开发”按钮缺失导致失败；
        // 失败退出前应识别一次页面标题并记入日志
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 200, 100, 519, 257, 200); // 路线图
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 500, 600, 150); // 目标机体
        TestImageFactory.PasteBlock(screen, 1920, 620, 110, 240, 30, 130);  // 开发页标题

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var developAll = Path.Combine(temp.Path, "develop_all.png");
        var executeDevelop = Path.Combine(temp.Path, "execute_develop.png");
        var executeConfirm = Path.Combine(temp.Path, "execute_confirm.png");
        var showcaseTap = Path.Combine(temp.Path, "showcase_tap.png");
        var backButton = Path.Combine(temp.Path, "back.png");
        var title = Path.Combine(temp.Path, "title.png");
        File.WriteAllBytes(developAll, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 180), 190, 70));
        File.WriteAllBytes(executeDevelop, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 170), 190, 70));
        File.WriteAllBytes(executeConfirm, ImageCodec.EncodePng(TestImageFactory.CreateBgra(170, 80, 160), 170, 80));
        File.WriteAllBytes(showcaseTap, ImageCodec.EncodePng(TestImageFactory.CreateBgra(280, 70, 150), 280, 70));
        File.WriteAllBytes(backButton, ImageCodec.EncodePng(TestImageFactory.CreateBgra(215, 105, 140), 215, 105));
        File.WriteAllBytes(title, ImageCodec.EncodePng(TestImageFactory.CreateBgra(620, 110, 130), 620, 110));

        var log = new TestLog();
        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), log);
        var service = new DevelopService(automation, log);

        var ok = await service.DevelopAndEnhanceOnceAsync(
            "serial",
            CreateMainLayout(),
            CreateDevelopLayout(),
            files.Roadmap,
            files.Ms,
            "",
            developAll,
            "",
            executeDevelop,
            executeConfirm,
            showcaseTap,
            backButton,
            title,
            maxDragsPerDirection: 4,
            dragDurationMs: 50,
            threshold: 0.9,
            stepDelayMs: 10,
            developWaitTimeoutMs: 1000,
            networkSettleDelayMs: 10,
            titleThreshold: 0.9,
            titleWaitMs: 5000);

        Assert.False(ok);
        Assert.Contains(log.Entries, e => e.Message.Contains("失败时页面标题仍可识别"));
        Assert.Contains(log.Entries, e => e.Message.Contains("已识别开发页标题"));
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

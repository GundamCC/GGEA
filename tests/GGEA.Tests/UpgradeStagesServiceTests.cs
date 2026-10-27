using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>强化培育关卡每日任务服务测试。</summary>
public class UpgradeStagesServiceTests
{
    /// <summary>主界面卡住时强化培育入口和略过流程都不能继续。</summary>
    [Fact]
    public async Task ExecuteFromMainAsync_MenuTitleMissing_StopsBeforeUpgradeStages()
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var log = new TestLog();
        var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new UpgradeStagesService(automation, new StageSkipService(automation, log), log,
            new StageMenuEntryService(automation, log, timeoutMs: 100, pollIntervalMs: 10));

        var result = await service.ExecuteFromMainAsync("serial", CreateMainLayout(), CreateStageLayout(),
            CreateLayout(), networkSettleDelayMs: 0);

        Assert.False(result.Success);
        Assert.Contains("请检查主界面是否卡顿", result.Message);
        Assert.Equal(new[] { (1000, 950) }, adb.Taps);
    }

    [Fact]
    public async Task ExecuteDailyAsync_SkipEnabled_SkipsAndReturnsForEachType()
    {
        var adb = new FakeAdbService(CreateBlankPng());
        var log = new TestLog();
        var skipService = new StageSkipService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log,
            new StageSkipOptions
            {
                SkipDialogWaitMs = 0,
                SettleAfterOkMs = 0,
                SkipResultTimeoutMs = 0,
            });
        var service = new UpgradeStagesService(
            new AutomationService(adb, new TemplateMatcher(), log),
            skipService,
            log);

        var result = await service.ExecuteDailyAsync("serial", CreateLayout(), networkSettleDelayMs: 10);

        Assert.True(result.Success, result.Message);
        foreach (var type in new[] { (395, 730), (772, 730), (1149, 730), (1525, 730) })
        {
            Assert.Contains(type, adb.Taps);
        }

        Assert.Equal(4, adb.Taps.Count(tap => tap == (1674, 710)));
        Assert.Equal(4, adb.Taps.Count(tap => tap == (120, 65)));
    }

    [Fact]
    public async Task ExecuteFromMainAsync_NavigatesThenRunsAllTypes()
    {
        var adb = new FakeAdbService(TestImageFactory.CreateStageMenuPng());
        adb.ScreenshotProvider = () => adb.Taps.Count < 2
            ? TestImageFactory.CreateStageMenuPng() : CreateBlankPng();
        var log = new TestLog();
        var skipService = new StageSkipService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log,
            new StageSkipOptions
            {
                SkipDialogWaitMs = 0,
                SettleAfterOkMs = 0,
                SkipResultTimeoutMs = 0,
            });
        var service = new UpgradeStagesService(
            new AutomationService(adb, new TemplateMatcher(), log),
            skipService,
            log);

        var result = await service.ExecuteFromMainAsync(
            "serial",
            CreateMainLayout(),
            CreateStageLayout(),
            CreateLayout(),
            networkSettleDelayMs: 10);

        Assert.True(result.Success, result.Message);
        Assert.Contains((1000, 950), adb.Taps);
        Assert.Contains((1150, 775), adb.Taps);
        Assert.Contains((395, 730), adb.Taps);
        Assert.Equal(4, adb.Taps.Count(tap => tap == (1674, 710)));
    }

    [Fact]
    public async Task ExecuteDailyAsync_SkipGray_ReturnsWithoutSkipping()
    {
        using var temp = new TempDirectory();
        var countLimitPath = Path.Combine(temp.Path, "count_limit.png");
        File.WriteAllBytes(
            countLimitPath,
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 60, 180), 160, 60));

        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 160, 60, 1590, 680, 180);
        PasteTitle(screen);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var log = new TestLog();
        var skipService = new StageSkipService(
            new AutomationService(adb, new TemplateMatcher(), log),
            log,
            new StageSkipOptions
            {
                SkipCountLimitTemplatePath = countLimitPath,
                SkipCountLimitThreshold = 0.9,
            });
        var service = new UpgradeStagesService(
            new AutomationService(adb, new TemplateMatcher(), log),
            skipService,
            log);

        var result = await service.ExecuteDailyAsync("serial", CreateLayout(), networkSettleDelayMs: 10);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain((1674, 710), adb.Taps);
        Assert.Equal(4, adb.Taps.Count(tap => tap == (120, 65)));
    }

    /// <summary>旧版页面仍须有正确标题，缺少一次略过才允许后备流程。</summary>
    private static byte[] CreateBlankPng()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTitle(screen);
        return ImageCodec.EncodePng(screen, 1920, 1080);
    }

    /// <summary>放置真实标题模板，不用空白页面绕过入口检查。</summary>
    private static void PasteTitle(byte[] pixels)
    {
        var image = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "resources/common/templates/stage/upgrade_stage.png")));
        for (var row = 0; row < image.Height; row++)
            Array.Copy(image.Bgra, row * image.Width * 4, pixels, ((20 + row) * 1920 + 220) * 4, image.Width * 4);
    }

    private static UiLayout CreateMainLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "navStage", X = 900, Y = 900, Width = 200, Height = 100 },
        },
    };

    private static UiLayout CreateStageLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "upgradeStages", X = 960, Y = 725, Width = 380, Height = 100 },
        },
    };

    private static UiLayout CreateLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "upgradeCapital", X = 270, Y = 580, Width = 250, Height = 300 },
            new() { Name = "upgradeUnit", X = 647, Y = 580, Width = 250, Height = 300 },
            new() { Name = "upgradeCharacter", X = 1024, Y = 580, Width = 250, Height = 300 },
            new() { Name = "upgradeSupport", X = 1400, Y = 580, Width = 250, Height = 300 },
            new() { Name = "upgradeBack", X = 0, Y = 0, Width = 240, Height = 130 },
            new() { Name = "upgradeSkip", X = 1594, Y = 678, Width = 160, Height = 64 },
            new() { Name = "stageSkip", X = 1594, Y = 678, Width = 160, Height = 64 },
            new() { Name = "skipDialogCancel", X = 710, Y = 820, Width = 120, Height = 60 },
            new() { Name = "skipDialogExecute", X = 1090, Y = 820, Width = 120, Height = 60 },
            new() { Name = "skipResultOk", X = 1640, Y = 960, Width = 72, Height = 56 },
            new() { Name = "skipResultTitle", X = 820, Y = 30, Width = 330, Height = 80 },
            new() { Name = "skipCountLimit", X = 1590, Y = 680, Width = 160, Height = 60 },
        },
    };

    /// <summary>测试用临时目录，自动清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-upgrade-tests-" + Guid.NewGuid().ToString("N"));

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

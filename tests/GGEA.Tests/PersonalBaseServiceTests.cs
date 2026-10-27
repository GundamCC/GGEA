using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 收获战舰服务测试：可回收、出航中（暗灰）、超时三种状态。
/// </summary>
public class PersonalBaseServiceTests
{
    /// <summary>真实失败证据裁剪在统一底部区域匹配共享完整 OK 模板，保留原阈值。</summary>
    [Fact]
    public void RewardOk_RealEvidence_MatchesWithExistingConfiguration()
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/reward_ok.png")));
        var template = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resources/common/templates/dialog/button_ok.png")));
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var y = 0; y < crop.Height; y++)
            Buffer.BlockCopy(crop.Bgra, y * crop.Width * 4, screen, ((905 + y) * 1920 + 770) * 4, crop.Width * 4);
        var matcher = new TemplateMatcher();
        var found = matcher.Match(screen, 1920, 1080, template.Bgra, template.Width, template.Height,
            0.85, RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, 1920, 1080));
        Assert.NotNull(found);
        Assert.InRange(found.CenterX, 900, 1020);
        Assert.InRange(found.CenterY, 950, 1030);
    }

    /// <summary>随包区域必须容纳两种完整回收按钮，防止换素材后静默漏检。</summary>
    [Fact]
    public void RecycleTemplates_FitShippedSearchRegion()
    {
        var root = AppContext.BaseDirectory;
        var layout = System.Text.Json.JsonSerializer.Deserialize<UiLayout>(
            File.ReadAllText(Path.Combine(root, "resources/common/ui_layout/personal_base.json")))!;
        var roi = layout.Find("claimAll")!.Roi;
        foreach (var name in new[] { "button_recycle_all.png", "button_recycle_all_disabled.png" })
        {
            var image = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(root, "resources/zh-CN/templates/base", name)));
            Assert.True(image.Width <= roi.Width && image.Height <= roi.Height);
        }
    }

    private static UiLayout CreateMainLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "navBase", X = 74, Y = 900, Width = 220, Height = 155 },
        },
    };

    private static UiLayout CreateBaseLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "battleshipCruise", X = 170, Y = 215, Width = 160, Height = 80 },
            new() { Name = "claimAll", X = 1575, Y = 830, Width = 220, Height = 90 },
            new() { Name = "claimResultOk", X = 870, Y = 945, Width = 160, Height = 70 },
        },
    };

    /// <summary>创建战舰模板与遮罩文件（100x100 灰色块 + 全白遮罩）。</summary>
    private static (string Template, string Mask) CreateBattleshipFiles(TempDirectory temp)
    {
        var templatePath = Path.Combine(temp.Path, "hexagon.png");
        var maskPath = Path.Combine(temp.Path, "hexagon_mask.png");
        File.WriteAllBytes(templatePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 150), 100, 100));
        File.WriteAllBytes(maskPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 255), 100, 100));
        return (templatePath, maskPath);
    }

    [Fact]
    public async Task HarvestBattleshipAsync_Ready_TapsInOrder()
    {
        // 顺序呈现基地、可回收、结果弹窗及关闭后的基地，验证真实状态转换。
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 400, 300, 150); // 战舰六边形
        TestImageFactory.PasteBlock(screen, 1920, 200, 70, 1590, 840, 200); // 全部回收
        var reward = (byte[])screen.Clone();
        TestImageFactory.PasteBlock(reward, 1920, 160, 70, 870, 945, 180);

        using var temp = new TempDirectory();
        var (battleshipTemplate, battleshipMask) = CreateBattleshipFiles(temp);
        var recyclePath = Path.Combine(temp.Path, "recycle.png");
        var disabledPath = Path.Combine(temp.Path, "recycle_disabled.png");
        var okPath = Path.Combine(temp.Path, "ok.png");
        File.WriteAllBytes(recyclePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(200, 70, 200), 200, 70));
        File.WriteAllBytes(disabledPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 90), 190, 70));
        File.WriteAllBytes(okPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 70, 180), 160, 70));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        fakeAdb.EnqueueScreenshots(ImageCodec.EncodePng(screen, 1920, 1080),
            ImageCodec.EncodePng(reward, 1920, 1080), ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var locator = new BattleshipLocator(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new PersonalBaseService(automation, locator, new TestLog());

        var result = await service.HarvestBattleshipAsync(
            "serial",
            CreateMainLayout(),
            CreateBaseLayout(),
            battleshipTemplate,
            battleshipMask,
            recyclePath,
            disabledPath,
            okPath,
            maxDrags: 4,
            dragDurationMs: 50,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 3000,
            threshold: 0.9, pollIntervalMs: 10);

        Assert.Equal(BattleshipHarvestStatus.Success, result.Status);
        Assert.True(result.Completed);
        Assert.Equal(new[] { (184, 977), (450, 350), (1690, 875), (950, 980) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task HarvestBattleshipAsync_NotReady_ReturnsWithoutClicking()
    {
        // 屏幕：战舰区域存在，全部回收区域是暗灰（90）
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 400, 300, 150);
        TestImageFactory.PasteBlock(screen, 1920, 190, 70, 1590, 840, 90);

        using var temp = new TempDirectory();
        var (battleshipTemplate, battleshipMask) = CreateBattleshipFiles(temp);
        var recyclePath = Path.Combine(temp.Path, "recycle.png");
        var disabledPath = Path.Combine(temp.Path, "recycle_disabled.png");
        var okPath = Path.Combine(temp.Path, "ok.png");
        File.WriteAllBytes(recyclePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 200), 190, 70));
        File.WriteAllBytes(disabledPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 90), 190, 70));
        File.WriteAllBytes(okPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 70, 180), 160, 70));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var locator = new BattleshipLocator(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new PersonalBaseService(automation, locator, new TestLog());

        var result = await service.HarvestBattleshipAsync(
            "serial",
            CreateMainLayout(),
            CreateBaseLayout(),
            battleshipTemplate,
            battleshipMask,
            recyclePath,
            disabledPath,
            okPath,
            maxDrags: 4,
            dragDurationMs: 50,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 3000,
            threshold: 0.9, pollIntervalMs: 10);

        Assert.Equal(BattleshipHarvestStatus.NotReady, result.Status);
        Assert.True(result.Completed);
        Assert.Contains("出航中", result.Message);
        Assert.Equal(new[] { (184, 977), (450, 350) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task HarvestBattleshipAsync_NoBattleship_TimesOut()
    {
        // 屏幕上没有战舰六边形，拖动后仍找不到
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);

        using var temp = new TempDirectory();
        var (battleshipTemplate, battleshipMask) = CreateBattleshipFiles(temp);
        var recyclePath = Path.Combine(temp.Path, "recycle.png");
        var disabledPath = Path.Combine(temp.Path, "recycle_disabled.png");
        var okPath = Path.Combine(temp.Path, "ok.png");
        File.WriteAllBytes(recyclePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 200), 190, 70));
        File.WriteAllBytes(disabledPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 90), 190, 70));
        File.WriteAllBytes(okPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 70, 180), 160, 70));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var locator = new BattleshipLocator(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new PersonalBaseService(automation, locator, new TestLog());

        var result = await service.HarvestBattleshipAsync(
            "serial",
            CreateMainLayout(),
            CreateBaseLayout(),
            battleshipTemplate,
            battleshipMask,
            recyclePath,
            disabledPath,
            okPath,
            maxDrags: 2,
            dragDurationMs: 50,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 300,
            threshold: 0.9, pollIntervalMs: 10);

        Assert.Equal(BattleshipHarvestStatus.Timeout, result.Status);
        Assert.False(result.Completed);
        Assert.Equal(new[] { (184, 977) }, fakeAdb.Taps);
        Assert.Equal(2, fakeAdb.Swipes.Count);
    }

    /// <summary>奖励覆盖灰色按钮时先关闭奖励；消失后重新判断，禁止把背景当成出航中。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HarvestBattleshipAsync_RewardOverlay_PrecedesDisabledState(bool staysOpen)
    {
        using var temp = new TempDirectory();
        var (ship, mask) = CreateBattleshipFiles(temp);
        var enabledPath = Path.Combine(temp.Path, "enabled.png");
        var disabledPath = Path.Combine(temp.Path, "disabled.png");
        var okPath = Path.Combine(temp.Path, "ok.png");
        File.WriteAllBytes(enabledPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 200), 190, 70));
        File.WriteAllBytes(disabledPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 90), 190, 70));
        File.WriteAllBytes(okPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 70, 180), 160, 70));
        var baseScreen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(baseScreen, 1920, 100, 100, 400, 300, 150);
        TestImageFactory.PasteBlock(baseScreen, 1920, 190, 70, 1590, 840, 90);
        var reward = (byte[])baseScreen.Clone();
        // OK 与灰色回收按钮同时存在，必须优先处理前景弹窗。
        TestImageFactory.PasteBlock(reward, 1920, 160, 70, 870, 945, 180);
        var adb = new FakeAdbService(ImageCodec.EncodePng(baseScreen, 1920, 1080));
        adb.EnqueueScreenshots(ImageCodec.EncodePng(reward, 1920, 1080));
        if (!staysOpen) adb.EnqueueScreenshots(ImageCodec.EncodePng(baseScreen, 1920, 1080));
        var log = new TestLog();
        var service = new PersonalBaseService(new AutomationService(adb, new TemplateMatcher(), log),
            new BattleshipLocator(adb, new TemplateMatcher(), log), log);
        var result = await service.HarvestBattleshipAsync("serial", CreateMainLayout(), CreateBaseLayout(),
            ship, mask, enabledPath, disabledPath, okPath, stepDelayMs: 0, networkSettleDelayMs: 0,
            waitTimeoutMs: 2200, threshold: 0.9, pollIntervalMs: 10);
        Assert.Equal(staysOpen ? BattleshipHarvestStatus.Timeout : BattleshipHarvestStatus.NotReady, result.Status);
        Assert.Equal(new[] { (184, 977), (450, 350), (950, 980) }, adb.Taps);
        if (staysOpen) Assert.DoesNotContain(log.Entries, e => e.Message.Contains("战舰出航中"));
    }

    /// <summary>回收后没出现结果弹窗，不能将点击请求当成已成功收获。</summary>
    [Fact]
    public async Task HarvestBattleshipAsync_ResultMissing_ReturnsTimeout()
    {
        using var temp = new TempDirectory();
        var (ship, mask) = CreateBattleshipFiles(temp);
        var enabledPath = Path.Combine(temp.Path, "enabled.png");
        var disabledPath = Path.Combine(temp.Path, "disabled.png");
        var okPath = Path.Combine(temp.Path, "ok.png");
        File.WriteAllBytes(enabledPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 200), 190, 70));
        File.WriteAllBytes(disabledPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(190, 70, 90), 190, 70));
        File.WriteAllBytes(okPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 70, 180), 160, 70));
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 100, 100, 400, 300, 150);
        TestImageFactory.PasteBlock(screen, 1920, 190, 70, 1590, 840, 200);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var log = new TestLog();
        var service = new PersonalBaseService(new AutomationService(adb, new TemplateMatcher(), log),
            new BattleshipLocator(adb, new TemplateMatcher(), log), log);
        var result = await service.HarvestBattleshipAsync("serial", CreateMainLayout(), CreateBaseLayout(),
            ship, mask, enabledPath, disabledPath, okPath, stepDelayMs: 0, networkSettleDelayMs: 0,
            waitTimeoutMs: 1100, threshold: 0.9, pollIntervalMs: 10);
        Assert.Equal(BattleshipHarvestStatus.Timeout, result.Status);
        Assert.False(result.Completed);
        Assert.Equal(3, adb.Taps.Count);
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

using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 礼物服务测试：有礼物可领、已领完（灰色）、无按钮三种状态。
/// </summary>
public class GiftServiceTests
{
    /// <summary>使用雷电真实底栏验证默认阈值：礼物空页只关闭，关卡底栏不能误判已领取并继续点击。</summary>
    [Theory]
    [InlineData("gift_empty_footer.png", true)]
    [InlineData("stage_footer.png", false)]
    public async Task ActualFooter_DefaultThresholdRejectsUnrelatedPage(string fixture, bool expected)
    {
        var root = AppContext.BaseDirectory;
        var frame = TestImageFactory.CreateBgra(1920, 1080, 20);
        var footer = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(root, "Fixtures", "Rewards", fixture)));
        Buffer.BlockCopy(footer.Bgra, 0, frame, 880 * 1920 * 4, footer.Bgra.Length);
        var adb = new FakeAdbService(ImageCodec.EncodePng(frame, 1920, 1080));
        using var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        var service = new GiftService(automation, new TestLog());
        var success = await service.CollectGiftsAsync("serial", CreateMainLayout(), CreateGiftLayout(),
            Path.Combine(root, "resources/zh-CN/templates/gift/button_collect_all.png"),
            Path.Combine(root, "resources/zh-CN/templates/gift/button_collect_all_disabled.png"),
            Path.Combine(root, "resources/common/templates/dialog/button_ok.png"),
            Path.Combine(root, "resources/common/templates/common/close.png"),
            stepDelayMs: 0, networkSettleDelayMs: 0, waitTimeoutMs: 200);
        Assert.Equal(expected, success);
        if (expected)
        {
            Assert.Equal(2, adb.Taps.Count);
            Assert.InRange(adb.Taps.Last().Item1, 765, 780);
            Assert.InRange(adb.Taps.Last().Item2, 988, 1000);
        }
        else Assert.Single(adb.Taps);
    }

    /// <summary>真实素材组合帧验证完整按钮可识别，关闭按钮偏离旧锚点时仍点击实际中心；非底部按钮不授权点击。</summary>
    [Theory]
    [InlineData(false, 930, true)]
    [InlineData(true, 930, true)]
    [InlineData(true, 200, false)]
    public async Task CollectGiftsAsync_FullTemplates_UsesBottomRegion(bool disabled, int closeY, bool expected)
    {
        var root = AppContext.BaseDirectory;
        var collect = Path.Combine(root, "resources/zh-CN/templates/gift/button_collect_all.png");
        var dark = Path.Combine(root, "resources/zh-CN/templates/gift/button_collect_all_disabled.png");
        var okPath = Path.Combine(root, "resources/common/templates/dialog/button_ok.png");
        var close = Path.Combine(root, "resources/common/templates/common/close.png");
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        // 这是人工组合的回归帧，非游戏实机截图；故意让按钮远离旧文字锚点。
        ScreenImage Paste(string path, int x, int y)
        {
            var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
            for (var row = 0; row < image.Height; row++)
                Buffer.BlockCopy(image.Bgra, row * image.Width * 4, screen, ((y + row) * 1920 + x) * 4, image.Width * 4);
            return image;
        }
        var collectImage = Paste(disabled ? dark : collect, 1300, 930);
        Paste(okPath, 800, 930);
        var closeImage = Paste(close, 250, closeY);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var service = new GiftService(new AutomationService(adb, new TemplateMatcher(), new TestLog()), new TestLog());
        var result = await service.CollectGiftsAsync("serial", CreateMainLayout(), CreateGiftLayout(),
            collect, dark, okPath, close, stepDelayMs: 0, networkSettleDelayMs: 0,
            waitTimeoutMs: 3000, threshold: .9);
        Assert.Equal(expected, result);
        if (expected)
            Assert.Equal((250 + closeImage.Width / 2, closeY + closeImage.Height / 2), adb.Taps.Last());
        if (disabled)
            Assert.Equal(expected ? 2 : 1, adb.Taps.Count);
        else
            Assert.Equal((1300 + collectImage.Width / 2, 930 + collectImage.Height / 2), adb.Taps[1]);
    }

    private static UiLayout CreateMainLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "navGift", X = 34, Y = 505, Width = 160, Height = 120 },
        },
    };

    private static UiLayout CreateGiftLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "collectAll", X = 1060, Y = 960, Width = 180, Height = 70 },
            new() { Name = "collectResultOk", X = 870, Y = 945, Width = 160, Height = 70 },
            new() { Name = "closePage", X = 720, Y = 965, Width = 120, Height = 60 },
        },
    };

    private static (string Collect, string Disabled, string Ok, string Close) CreateTemplateFiles(TempDirectory temp)
    {
        var collect = Path.Combine(temp.Path, "collect.png");
        var disabled = Path.Combine(temp.Path, "collect_disabled.png");
        var ok = Path.Combine(temp.Path, "ok.png");
        var close = Path.Combine(temp.Path, "close.png");
        File.WriteAllBytes(collect, ImageCodec.EncodePng(TestImageFactory.CreateBgra(180, 70, 200), 180, 70));
        File.WriteAllBytes(disabled, ImageCodec.EncodePng(TestImageFactory.CreateBgra(180, 70, 90), 180, 70));
        File.WriteAllBytes(ok, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 70, 180), 160, 70));
        File.WriteAllBytes(close, ImageCodec.EncodePng(TestImageFactory.CreateBgra(120, 60, 160), 120, 60));
        return (collect, disabled, ok, close);
    }

    [Fact]
    public async Task CollectGiftsAsync_WithGifts_TapsInOrder()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 180, 70, 1060, 960, 200); // 全部领取（亮色）
        TestImageFactory.PasteBlock(screen, 1920, 160, 70, 870, 945, 180);  // 结果 OK
        TestImageFactory.PasteBlock(screen, 1920, 120, 60, 720, 965, 160);  // 关闭

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new GiftService(automation, new TestLog());

        var ok = await service.CollectGiftsAsync(
            "serial",
            CreateMainLayout(),
            CreateGiftLayout(),
            files.Collect,
            files.Disabled,
            files.Ok,
            files.Close,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 3000,
            threshold: 0.9);

        Assert.True(ok);
        Assert.Equal(new[] { (114, 565), (1150, 995), (950, 980), (780, 995) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task CollectGiftsAsync_AlreadyCollected_OnlyCloses()
    {
        // 全部领取为灰色（已领完），没有结果弹窗，直接点击关闭
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 180, 70, 1060, 960, 90);  // 灰色全部领取
        TestImageFactory.PasteBlock(screen, 1920, 120, 60, 720, 965, 160);  // 关闭

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new GiftService(automation, new TestLog());

        var ok = await service.CollectGiftsAsync(
            "serial",
            CreateMainLayout(),
            CreateGiftLayout(),
            files.Collect,
            files.Disabled,
            files.Ok,
            files.Close,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 3000,
            threshold: 0.9);

        Assert.True(ok);
        // 只点击了礼物入口和关闭按钮，没有点击全部领取/OK
        Assert.Equal(new[] { (114, 565), (780, 995) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task CollectGiftsAsync_NoButton_ReturnsFalse()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);

        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new GiftService(automation, new TestLog());

        var ok = await service.CollectGiftsAsync(
            "serial",
            CreateMainLayout(),
            CreateGiftLayout(),
            files.Collect,
            files.Disabled,
            files.Ok,
            files.Close,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 300,
            threshold: 0.9);

        Assert.False(ok);
        Assert.Equal(new[] { (114, 565) }, fakeAdb.Taps);
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

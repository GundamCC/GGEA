using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 商店购物服务测试：免费判断、完整点击顺序、付费商品中止与钻石安全锁。
/// </summary>
public class ShopServiceTests
{
    /// <summary>免费商品使用二十秒完整预算；缺失时仍有限等待并安全返回主界面。</summary>
    [Fact]
    public async Task MissingFreeItem_DefaultWaitUsesTwentySecondBudget()
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var log = new TestLog();
        var service = new ShopService(new AutomationService(adb, new TemplateMatcher(), log),
            new DiamondSafetyChecker(adb, new TemplateMatcher(), log), log);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await service.BuyFreePremiumItemAsync("serial", CreateMainLayout(), CreateShopLayout(),
            Resource("button_premium_free_buy.png"), Resource("free.png"),
            Resource("button_confirm_buy.png"), Path.Combine(AppContext.BaseDirectory, "resources/common/templates/common/close.png"), "unused.png",
            stepDelayMs: 0, networkSettleDelayMs: 0));
        Assert.InRange(elapsed.ElapsedMilliseconds, 19500, 24000);
        Assert.Equal(new[] { (114, 832), (270, 495), (184, 977) }, adb.Taps);
    }

    private static UiLayout CreateMainLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "navShop", X = 34, Y = 772, Width = 160, Height = 120 },
            new() { Name = "navHome", X = 74, Y = 900, Width = 220, Height = 155 },
        },
    };

    private static UiLayout CreateShopLayout() => new()
    {
        Anchors = new List<UiAnchor>
        {
            new() { Name = "premiumTab", X = 180, Y = 440, Width = 180, Height = 110 },
            new() { Name = "freeItemBuy", X = 430, Y = 730, Width = 290, Height = 80 },
            new() { Name = "freeItemPriceRegion", X = 394, Y = 640, Width = 334, Height = 85 },
            new() { Name = "confirmBuy", X = 1080, Y = 950, Width = 150, Height = 90 },
            new() { Name = "resultClose", X = 880, Y = 950, Width = 160, Height = 90 },
            new() { Name = "diamondRiskRegion", X = 450, Y = 350, Width = 1050, Height = 550 },
        },
    };

    [Fact]
    public async Task BuyFreePremiumItemAsync_NoFreeText_AbortsBeforeBuy()
    {
        // 免费商品栏位已被付费商品占据：购买按钮存在但价格区没有“免费”二字
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 160, 80, 500, 730, 200);  // 购买按钮
        TestImageFactory.PasteBlock(screen, 1920, 90, 50, 650, 690, 130);   // 价格数字（非免费）

        using var temp = new TempDirectory();
        var freePath = Path.Combine(temp.Path, "free.png");
        var freeTextPath = Path.Combine(temp.Path, "free_text.png");
        var confirmPath = Path.Combine(temp.Path, "confirm.png");
        var closePath = Path.Combine(temp.Path, "close.png");
        var iconPath = Path.Combine(temp.Path, "diamond_icon.png");
        File.WriteAllBytes(freePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 80, 200), 160, 80));
        File.Copy(Resource("free.png"), freeTextPath);
        File.WriteAllBytes(confirmPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(150, 90, 180), 150, 90));
        File.WriteAllBytes(closePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 90, 160), 160, 90));
        File.WriteAllBytes(iconPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(150, 65, 150), 150, 65));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var checker = new DiamondSafetyChecker(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new ShopService(automation, checker, new TestLog());

        var ok = await service.BuyFreePremiumItemAsync(
            "serial",
            CreateMainLayout(),
            CreateShopLayout(),
            freePath,
            freeTextPath,
            confirmPath,
            closePath,
            iconPath,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 15000,
            threshold: 0.9);

        Assert.True(ok);
        // 只点击了商店入口与高级商店，绝未点击购买按钮
        Assert.Equal(new[] { (114, 832), (270, 495), (184, 977) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task BuyFreePremiumItemAsync_NoFreeItem_SafelySkips()
    {
        // 屏幕上没有免费商品购买按钮（例如今日已购买）
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);

        using var temp = new TempDirectory();
        var freePath = Path.Combine(temp.Path, "free.png");
        var freeTextPath = Path.Combine(temp.Path, "free_text.png");
        var confirmPath = Path.Combine(temp.Path, "confirm.png");
        var closePath = Path.Combine(temp.Path, "close.png");
        var iconPath = Path.Combine(temp.Path, "diamond_icon.png");
        File.WriteAllBytes(freePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 80, 200), 160, 80));
        File.Copy(Resource("free.png"), freeTextPath);
        File.WriteAllBytes(confirmPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(150, 90, 180), 150, 90));
        File.WriteAllBytes(closePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 90, 160), 160, 90));
        File.WriteAllBytes(iconPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(150, 65, 150), 150, 65));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var checker = new DiamondSafetyChecker(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new ShopService(automation, checker, new TestLog());

        var ok = await service.BuyFreePremiumItemAsync(
            "serial",
            CreateMainLayout(),
            CreateShopLayout(),
            freePath,
            freeTextPath,
            confirmPath,
            closePath,
            iconPath,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 300,
            threshold: 0.9);

        Assert.True(ok);
        Assert.Equal(new[] { (114, 832), (270, 495), (184, 977) }, fakeAdb.Taps);
    }

    [Fact]
    public async Task BuyFreePremiumItemAsync_DiamondRisk_AbortsBeforeConfirm()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 160, 80, 500, 730, 200);  // 免费商品购买
        PasteFreePrice(screen); // 实际免费文字和同栏钻石图标
        TestImageFactory.PasteBlock(screen, 1920, 150, 90, 1080, 950, 180); // 确认购买按钮
        TestImageFactory.PasteBlock(screen, 1920, 150, 65, 550, 460, 150);  // 钻石图标

        // 确认弹窗区域出现红色数量（模拟钻石将被消耗），必须中止且不能点击确认
        for (var y = 350; y < 900; y++)
        {
            for (var x = 1100; x < 1500; x++)
            {
                var offset = (y * 1920 + x) * 4;
                screen[offset] = 0;
                screen[offset + 1] = 0;
                screen[offset + 2] = 255;
            }
        }

        using var temp = new TempDirectory();
        var freePath = Path.Combine(temp.Path, "free.png");
        var freeTextPath = Path.Combine(temp.Path, "free_text.png");
        var confirmPath = Path.Combine(temp.Path, "confirm.png");
        var closePath = Path.Combine(temp.Path, "close.png");
        var iconPath = Path.Combine(temp.Path, "diamond_icon.png");
        File.WriteAllBytes(freePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 80, 200), 160, 80));
        File.Copy(Resource("free.png"), freeTextPath);
        File.WriteAllBytes(confirmPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(150, 90, 180), 150, 90));
        File.WriteAllBytes(closePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(160, 90, 160), 160, 90));
        File.WriteAllBytes(iconPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(150, 65, 150), 150, 65));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var automation = new AutomationService(fakeAdb, new TemplateMatcher(), new TestLog());
        var checker = new DiamondSafetyChecker(fakeAdb, new TemplateMatcher(), new TestLog());
        var service = new ShopService(automation, checker, new TestLog());

        var ok = await service.BuyFreePremiumItemAsync(
            "serial",
            CreateMainLayout(),
            CreateShopLayout(),
            freePath,
            freeTextPath,
            confirmPath,
            closePath,
            iconPath,
            stepDelayMs: 10,
            networkSettleDelayMs: 10,
            waitTimeoutMs: 15000,
            threshold: 0.9);

        Assert.False(ok);
        // 点击了商店入口、高级商店和免费商品购买，但绝未点击确认购买/关闭
        Assert.Equal(new[] { (114, 832), (270, 495), (580, 770) }, fakeAdb.Taps);
    }

    /// <summary>免费与钻石必须同帧出现；付费价格及其他位置的免费文字不能授权第一张卡片。</summary>
    [Theory]
    [InlineData("paid")]
    [InlineData("no_diamond")]
    [InlineData("separate_frames")]
    [InlineData("changes_to_paid")]
    public async Task PriceNotConfirmed_NeverClicksBuy(string scenario)
    {
        var first = TestImageFactory.CreateBgra(1920, 1080, 20);
        var second = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(first, Resource("button_premium_free_buy.png"), 455, 730);
        Paste(second, Resource("button_premium_free_buy.png"), 455, 730);
        if (scenario == "changes_to_paid") PasteFreePrice(first);
        if (scenario == "separate_frames") PastePricePart(first, true);
        if (scenario == "no_diamond") PastePricePart(first, false);
        if (scenario is "paid" or "changes_to_paid")
        {
            Paste(second, Path.Combine(AppContext.BaseDirectory, "Fixtures/Shop/paid_price.png"), 418, 670);
            PastePricePart(second, false, 781, 681); // 旁边商品
            PastePricePart(second, false, 1237, 975); // 底部提示
        }
        else PastePricePart(second, false);
        var adb = new FakeAdbService(ImageCodec.EncodePng(first, 1920, 1080));
        adb.EnqueueScreenshots(ImageCodec.EncodePng(second, 1920, 1080));
        var log = new TestLog();
        var service = new ShopService(new AutomationService(adb, new TemplateMatcher(), log),
            new DiamondSafetyChecker(adb, new TemplateMatcher(), log), log);
        Assert.True(await service.BuyFreePremiumItemAsync("serial", CreateMainLayout(), CreateShopLayout(),
            Resource("button_premium_free_buy.png"), Resource("free.png"),
            Resource("button_confirm_buy.png"), Path.Combine(AppContext.BaseDirectory, "resources/common/templates/common/close.png"), "unused.png",
            stepDelayMs: 0, networkSettleDelayMs: 0, waitTimeoutMs: 700));
        Assert.Equal(new[] { (114, 832), (270, 495), (184, 977) }, adb.Taps);
    }

    /// <summary>第二帧原生截图越过检测截止时，仍评估已启动截图并要求两帧一致后才点购买。</summary>
    [Fact]
    public async Task SlowNativeScreenshots_ConfirmFreePriceAcrossDetectionDeadline()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteFreePrice(screen);
        Paste(screen, Resource("button_premium_free_buy.png"), 455, 730);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080))
        {
            // 两次原生抓帧各耗时 2.8 秒；半秒复核后第二帧启动在 5 秒截止前，返回时已越过截止。
            BeforeScreenshotAsync = (count, ct) => count <= 2 ? Task.Delay(2800, ct) : Task.CompletedTask,
        };
        var log = new TestLog();
        var service = new ShopService(new AutomationService(adb, new TemplateMatcher(), log),
            new DiamondSafetyChecker(adb, new TemplateMatcher(), log), log);

        var completed = await service.BuyFreePremiumItemAsync("serial", CreateMainLayout(), CreateShopLayout(),
            Resource("button_premium_free_buy.png"), Resource("free.png"),
            Resource("button_confirm_buy.png"), Path.Combine(AppContext.BaseDirectory, "resources/common/templates/common/close.png"), "unused.png",
            stepDelayMs: 0, networkSettleDelayMs: 0, waitTimeoutMs: 5000);

        Assert.False(completed); // 模拟画面没有最终确认弹窗，安全停止于此。
        Assert.Equal(new[] { (114, 832), (270, 495), (580, 770) }, adb.Taps);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("已确认钻石价格为免费"));
        Assert.DoesNotContain(adb.Taps, tap => tap == (1155, 995));
    }

    /// <summary>测试使用分发的真实价格素材，纯色块无法代表文字识别。</summary>
    private static string Resource(string name) => Path.Combine(AppContext.BaseDirectory, "resources/common/templates/shop", name);

    /// <summary>组合相同商品价格行的两个必要信号。</summary>
    private static void PasteFreePrice(byte[] screen)
    {
        Paste(screen, Resource("free.png"), 417, 681);
    }

    /// <summary>只放置半个价格栏，模拟信号分帧或其他区域出现免费文字。</summary>
    private static void PastePricePart(byte[] screen, bool diamond, int x = 417, int y = 681)
    {
        var image = ImageCodec.DecodePng(File.ReadAllBytes(Resource("free.png")));
        var left = diamond ? 0 : image.Width - 80;
        var width = diamond ? 60 : 80;
        for (var row = 0; row < image.Height - 4; row++)
            Array.Copy(image.Bgra, (row * image.Width + left) * 4, screen, ((y + row) * 1920 + x + left) * 4, width * 4);
    }

    /// <summary>复制真实裁剪像素到测试画面指定位置。</summary>
    private static void Paste(byte[] screen, string path, int x, int y)
    {
        var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
        for (var row = 0; row < image.Height; row++)
            Array.Copy(image.Bgra, row * image.Width * 4, screen, ((y + row) * 1920 + x) * 4, image.Width * 4);
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

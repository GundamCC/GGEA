using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>真实历史截图回放及明确标记的内存变体；不连接设备，不执行真实购买。</summary>
public sealed class ShopConfirmationTests
{
    /// <summary>免费与风险场景直接走生产识别器。</summary>
    [Theory]
    [InlineData("free", PurchaseSafety.SafeFree)]
    [InlineData("glow", PurchaseSafety.SafeFree)]
    [InlineData("button_tolerance", PurchaseSafety.SafeFree)]
    [InlineData("button_below_threshold", PurchaseSafety.Unknown)]
    [InlineData("button_tolerance_risk", PurchaseSafety.RiskDetected)]
    [InlineData("glow_similar_button", PurchaseSafety.Unknown)]
    [InlineData("glow_white_button", PurchaseSafety.Unknown)]
    [InlineData("glow_disabled_button", PurchaseSafety.Unknown)]
    [InlineData("glow_missing_glyph", PurchaseSafety.Unknown)]
    [InlineData("glow_price_changed", PurchaseSafety.Unknown)]
    [InlineData("glow_statement_changed", PurchaseSafety.Unknown)]
    [InlineData("glow_risk", PurchaseSafety.RiskDetected)]
    [InlineData("no_icon", PurchaseSafety.SafeFree)]
    [InlineData("paid", PurchaseSafety.Unknown)]
    [InlineData("covered", PurchaseSafety.Unknown)]
    [InlineData("animation", PurchaseSafety.Unknown)]
    [InlineData("similar_button", PurchaseSafety.Unknown)]
    [InlineData("price_changed", PurchaseSafety.Unknown)]
    [InlineData("statement_changed", PurchaseSafety.Unknown)]
    [InlineData("item_changed", PurchaseSafety.Unknown)]
    [InlineData("risk", PurchaseSafety.RiskDetected)]
    [InlineData("risk_no_icon", PurchaseSafety.RiskDetected)]
    public void Replay_RequiresPositiveEvidence(string scenario, PurchaseSafety expected)
    {
        var png = Frame(scenario);
        var log = new TestLog();
        var checker = new DiamondSafetyChecker(new FakeAdbService(png), new TemplateMatcher(), log);
        var result = new ShopConfirmationRecognizer(checker).Evaluate(png, Environment.TickCount64,
            Layout(), Resource("button_confirm_buy.png"), DiamondPath);
        Assert.True(result.Safety == expected, $"{scenario}: {result.Safety}: {result.Reason}");
        Assert.Equal(expected == PurchaseSafety.SafeFree, result.CanConfirm(Environment.TickCount64));
    }

    /// <summary>完整入口回放：前置两帧免费不替代最终授权；最新一帧异常时绝不能点击确认。</summary>
    [Theory]
    [InlineData("free", true)]
    [InlineData("glow", true)]
    [InlineData("button_tolerance", true)]
    [InlineData("button_below_threshold", false)]
    [InlineData("button_tolerance_risk", false)]
    [InlineData("glow_similar_button", false)]
    [InlineData("glow_white_button", false)]
    [InlineData("glow_disabled_button", false)]
    [InlineData("glow_missing_glyph", false)]
    [InlineData("glow_price_changed", false)]
    [InlineData("glow_statement_changed", false)]
    [InlineData("glow_risk", false)]
    [InlineData("no_icon", true)]
    [InlineData("paid", false)]
    [InlineData("covered", false)]
    [InlineData("animation", false)]
    [InlineData("similar_button", false)]
    [InlineData("price_changed", false)]
    [InlineData("risk", false)]
    [InlineData("capture_failure", false)]
    [InlineData("missing_template", false)]
    [InlineData("bad_layout", false)]
    [InlineData("expired", false)]
    [InlineData("slow_free", true)]
    [InlineData("expired_then_free", true)]
    [InlineData("expired_then_paid", false)]
    [InlineData("always_expired", false)]
    [InlineData("expired_last_single", false)]
    [InlineData("button_moved", false)]
    [InlineData("risk_no_icon", false)]
    public async Task Service_RechecksLatestFrameBeforeConfirm(string scenario, bool success)
    {
        var list = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(list, ImageCodec.DecodePng(File.ReadAllBytes(Resource("free.png"))), 417, 681);
        Paste(list, ImageCodec.DecodePng(File.ReadAllBytes(Resource("button_premium_free_buy.png"))), 455, 730);
        var listPng = ImageCodec.EncodePng(list, 1920, 1080);
        var close = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(close, ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resources/common/templates/common/close.png"))), 250, 930);
        var adb = new FakeAdbService(listPng);
        if (scenario is "expired_then_free" or "expired_then_paid" or "always_expired" or "expired_last_single")
        {
            // 第二帧过期后必须从零建立连续证据；旧的第一帧不能与重取的单帧拼接授权。
            adb.EnqueueScreenshots(listPng, Frame("free"), Frame("free"),
                Frame(scenario == "expired_then_paid" ? "paid" : "free"),
                Frame("free"), Frame("free"), Frame("free"));
            if (scenario == "expired_then_free")
            {
                // 只保留成功所需的两帧，紧随其后的结果页证明没有额外点击或取证。
                adb = new FakeAdbService(listPng);
                adb.EnqueueScreenshots(listPng, Frame("free"), Frame("free"), Frame("free"), Frame("free"),
                    ImageCodec.EncodePng(close, 1920, 1080));
            }
            adb.BeforeScreenshotAsync = (call, ct) =>
                (scenario == "always_expired" && call >= 3 ||
                 scenario == "expired_last_single" && call >= 3 && call < 8 || call == 4)
                ? Task.Delay(PurchaseEvidence.LifetimeMs + 100, ct) : Task.CompletedTask;
        }
        else
            adb.EnqueueScreenshots(listPng, Frame("free"), Frame(scenario == "capture_failure" ? "free" : scenario),
                ImageCodec.EncodePng(close, 1920, 1080));
        if (scenario == "capture_failure")
            adb.ScreenshotFailure = call => call == 4 ? new InvalidOperationException("回放截图失败") : null;
        if (scenario == "expired")
            adb.BeforeScreenshotAsync = (call, ct) => call == 4
                ? Task.Delay(PurchaseEvidence.LifetimeMs + 100, ct) : Task.CompletedTask;
        // 回放日志中的约四秒卡顿，确认完整流程仍要求两帧免费证据后才购买。
        if (scenario == "slow_free")
            adb.BeforeScreenshotAsync = (call, ct) => call is 3 or 4
                ? Task.Delay(3800, ct) : Task.CompletedTask;
        var log = new TestLog();
        var service = new ShopService(new AutomationService(adb, new TemplateMatcher(), log),
            new DiamondSafetyChecker(adb, new TemplateMatcher(), log), log);
        var main = new UiLayout { Anchors = new()
        {
            new() { Name = "navShop", X = 34, Y = 772, Width = 160, Height = 120 },
            new() { Name = "navHome", X = 74, Y = 900, Width = 220, Height = 155 },
        }};
        var layout = Layout();
        if (scenario == "missing_template") layout.Find("confirmItem")!.Template = "missing.png";
        if (scenario == "bad_layout") layout.Find("confirmItem")!.X = -1;
        // 放宽按钮搜索范围且故意偏置中心，证明最终点击来自匹配结果而非布局中心。
        layout.Find("confirmBuy")!.X -= 10;
        layout.Find("confirmBuy")!.Width += 30;
        Assert.Equal(success, await service.BuyFreePremiumItemAsync("replay", main, layout,
            Resource("button_premium_free_buy.png"), Resource("free.png"), Resource("button_confirm_buy.png"),
            Path.Combine(AppContext.BaseDirectory, "resources/common/templates/common/close.png"), DiamondPath,
            stepDelayMs: 0, networkSettleDelayMs: 0, waitTimeoutMs: 3000));
        Assert.Equal(success ? 6 : 3, adb.Taps.Count);
        if (scenario is "always_expired" or "expired_last_single")
            Assert.Equal(8, adb.ScreenshotCount); // 两帧列表 + 最多六帧最终确认，不得无限重试。
        if (scenario == "expired_then_paid")
            Assert.Equal(5, adb.ScreenshotCount); // 付费证据立即否决，不继续寻找后面的免费帧。
        // 未命中、取证失败和风险拦截只撤销本次购买，不能提前把每日任务链报告为 ERROR。
        Assert.DoesNotContain(log.Entries, entry => entry.Level == "ERROR");
        if (success)
            Assert.Equal(new[] { (114, 832), (270, 495), (580, 765), (1149, 995), (431, 991), (184, 977) }, adb.Taps);
        else
        {
            Assert.Contains(log.Entries, entry => entry.Level == "WARN" && entry.Message.Contains("最终确认已停止"));
            var diagnostic = Assert.Single(log.Entries, entry => entry.Message.StartsWith("最终确认诊断已保存: "));
            var prefix = diagnostic.Message["最终确认诊断已保存: ".Length..];
            // 验证真实失败入口的落盘位置，防止证据再次分散到用户数据或临时目录。
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "logs", "shop"), Path.GetDirectoryName(prefix));
            Assert.True(File.Exists(prefix + ".txt"));
            Assert.Equal(scenario != "capture_failure", File.Exists(prefix + ".png"));
        }
        if (scenario == "free")
        {
            // 同一服务再次执行时商品已变为付费，不能复用上次的前置或最终免费证据。
            TestImageFactory.PasteBlock(list, 1920, 334, 85, 394, 640, 20);
            Paste(list, ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures/Shop/paid_price.png"))), 418, 670);
            adb.EnqueueScreenshots(ImageCodec.EncodePng(list, 1920, 1080));
            Assert.True(await service.BuyFreePremiumItemAsync("replay", main, layout,
                Resource("button_premium_free_buy.png"), Resource("free.png"), Resource("button_confirm_buy.png"),
                Path.Combine(AppContext.BaseDirectory, "resources/common/templates/common/close.png"), DiamondPath,
                stepDelayMs: 0, networkSettleDelayMs: 0, waitTimeoutMs: 700));
            Assert.Equal(1, adb.Taps.Count(tap => tap == (580, 765)));
            Assert.Equal(1, adb.Taps.Count(tap => tap == (1149, 995)));
        }
    }

    /// <summary>缺少模板、布局越界、截图损坏和过期证据都不能授权。</summary>
    [Theory]
    [InlineData("missing_template")]
    [InlineData("missing_anchor")]
    [InlineData("bad_layout")]
    [InlineData("invalid_png")]
    [InlineData("expired")]
    [InlineData("missing_icon_template")]
    public void InvalidEvidence_IsUnknown(string scenario)
    {
        var png = scenario == "invalid_png" ? new byte[] { 1, 2, 3 } : Frame("free");
        var layout = Layout();
        if (scenario == "missing_template") layout.Find("confirmItem")!.Template = "missing.png";
        if (scenario == "missing_anchor") layout.Anchors.Remove(layout.Find("confirmItem")!);
        if (scenario == "bad_layout") layout.Find("confirmItem")!.X = -1;
        var checker = new DiamondSafetyChecker(new FakeAdbService(png), new TemplateMatcher(), new TestLog());
        var result = new ShopConfirmationRecognizer(checker).Evaluate(png,
            Environment.TickCount64 - (scenario == "expired" ? PurchaseEvidence.LifetimeMs + 100 : 0), layout,
            Resource("button_confirm_buy.png"), scenario == "missing_icon_template" ? "missing.png" : DiamondPath);
        Assert.Equal(PurchaseSafety.Unknown, result.Safety);
        Assert.Null(result.Button);
        Assert.Equal(scenario == "expired", result.Expired);
    }

    /// <summary>有效期包含截图耗时，并且安全状态本身不能绕过时间检查。</summary>
    [Fact]
    public void Evidence_ExpiresBeforeTap()
    {
        var evidence = new PurchaseEvidence(PurchaseSafety.SafeFree, "test",
            new TemplateMatchResult(new Roi(10, 20, 30, 40), 1), 100);
        Assert.True(evidence.CanConfirm(4100));
        Assert.True(evidence.CanConfirm(5100));
        Assert.False(evidence.CanConfirm(5101));
        Assert.False(evidence.CanConfirm(99));
        Assert.False((evidence with { Safety = PurchaseSafety.Unknown }).CanConfirm(100));
        Assert.False((evidence with { Safety = PurchaseSafety.RiskDetected }).CanConfirm(100));
    }

    /// <summary>载入随产品分发的真实布局和模板，避免测试另造一套生产参数。</summary>
    private static UiLayout Layout() => new UiLayoutService(new TestLog()).Load(
        Path.Combine(AppContext.BaseDirectory, "resources/common/ui_layout/shop.json"));
    private static string Resource(string name) => Path.Combine(AppContext.BaseDirectory, "resources/common/templates/shop", name);
    private static string DiamondPath => Path.Combine(AppContext.BaseDirectory, "resources/common/templates/safety/diamond_icon.png");

    /// <summary>付费样本为真实截图；其余负向样本修改真实免费截图，模拟遮挡、文字和动画变化。</summary>
    private static byte[] Frame(string scenario)
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Shop",
            scenario.StartsWith("glow", StringComparison.Ordinal) ? "free_confirm_glow.png" :
            scenario == "paid" ? "paid_confirm.png" : "free_confirm.png"));
        // 用户反馈的原始光效帧也作为负向变体基底，避免兼容动画时放松价格与文字保护。
        if (scenario.StartsWith("glow_", StringComparison.Ordinal)) scenario = scenario[5..];
        var image = ImageCodec.DecodePng(png);
        var pixels = image.Bgra;
        // 阈值边界由当前完整模板生成：先精确放置，再施加 25/27 的明暗偏差。
        // 旧用例直接对旧模板来源截图减色；换图后基础色差不再为零，不能据此度量 90% 边界。
        // free/glow 等用例仍直接验证未修改的真实截图。
        if (scenario.StartsWith("button_tolerance", StringComparison.Ordinal) || scenario == "button_below_threshold")
        {
            var button = ImageCodec.DecodePng(File.ReadAllBytes(Resource("button_confirm_buy.png")));
            for (var i = 0; i < button.Bgra.Length; i += 4)
            for (var channel = 0; channel < 3; channel++)
                button.Bgra[i + channel] = (byte)Math.Max(0, button.Bgra[i + channel] - (scenario == "button_below_threshold" ? 27 : 25));
            Paste(pixels, button, 973, 935);
        }
        if (scenario is "no_icon" or "risk_no_icon") TestImageFactory.PasteBlock(pixels, 1920, 65, 115, 625, 575, 190);
        if (scenario == "button_moved")
            Paste(pixels, ImageCodec.DecodePng(File.ReadAllBytes(Resource("button_confirm_buy.png"))), 1086, 950);
        if (scenario == "covered") TestImageFactory.PasteBlock(pixels, 1920, 200, 80, 870, 130, 30);
        if (scenario == "similar_button") TestImageFactory.PasteBlock(pixels, 1920, 90, 35, 1105, 978, 180);
        if (scenario == "white_button") TestImageFactory.PasteBlock(pixels, 1920, 150, 90, 1080, 950, 255);
        if (scenario == "missing_glyph") TestImageFactory.PasteBlock(pixels, 1920, 28, 32, 1118, 978, 180);
        // 模拟禁用按钮：保留字形和明暗关系，但去掉可点击按钮的蓝色；颜色复核必须否决。
        if (scenario == "disabled_button")
            for (var y = 950; y < 1040; y++)
            for (var x = 1080; x < 1230; x++)
            {
                var offset = (y * 1920 + x) * 4;
                var gray = (byte)((pixels[offset] * 114 + pixels[offset + 1] * 587 + pixels[offset + 2] * 299) / 1000);
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = gray;
            }
        if (scenario == "item_changed") TestImageFactory.PasteBlock(pixels, 1920, 80, 35, 650, 230, 190);
        if (scenario == "statement_changed") TestImageFactory.PasteBlock(pixels, 1920, 20, 30, 1015, 138, 190);
        if (scenario == "price_changed")
        {
            // 把真实 200 钻石价格栏的数字裁入所需区域，保留末位 0，防止只匹配零字误放行。
            var price = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Shop/paid_price.png")));
            Paste(pixels, price, 1125, 580);
        }
        if (scenario == "animation")
            for (var i = 0; i < pixels.Length; i += 4)
                for (var c = 0; c < 3; c++) pixels[i + c] = (byte)(pixels[i + c] * 0.65);
        if (scenario is "risk" or "risk_no_icon" or "button_tolerance_risk")
            for (var y = 645; y < 670; y++)
            for (var x = 1340; x < 1430; x++)
            {
                var offset = (y * 1920 + x) * 4;
                pixels[offset] = pixels[offset + 1] = 0;
                pixels[offset + 2] = 255;
            }
        return ImageCodec.EncodePng(pixels, image.Width, image.Height);
    }

    /// <summary>复制原始像素到回放画面，不改变生产识别算法。</summary>
    private static void Paste(byte[] destination, ScreenImage source, int x, int y)
    {
        for (var row = 0; row < source.Height; row++)
            Array.Copy(source.Bgra, row * source.Width * 4, destination,
                ((y + row) * 1920 + x) * 4, source.Width * 4);
    }
}

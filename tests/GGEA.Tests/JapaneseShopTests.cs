using System.IO;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>日服实机免费礼包回归：固定红字说明不是金额风险，零价证据或红色金额异常仍须拒绝。</summary>
public sealed class JapaneseShopTests : IDisposable
{
    private readonly string _templates = Path.Combine(Path.GetTempPath(), "ggea-ja-shop-" + Guid.NewGuid().ToString("N"));

    /// <summary>复制日文素材到独立外部目录，避免切换测试进程的全局语言影响其他测试。</summary>
    public JapaneseShopTests()
    {
        Directory.CreateDirectory(_templates);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "resources/ja/templates/shop"), "*.png"))
            File.Copy(file, Path.Combine(_templates, Path.GetFileName(file)));
    }

    /// <summary>原图直接回放；负向场景仅在内存修改价格证据，不冒充新的实机截图。</summary>
    [Theory]
    [InlineData("original", PurchaseSafety.SafeFree)]
    [InlineData("cost_changed", PurchaseSafety.Unknown)]
    [InlineData("statement_changed", PurchaseSafety.Unknown)]
    [InlineData("red_cost", PurchaseSafety.RiskDetected)]
    [InlineData("red_remaining", PurchaseSafety.RiskDetected)]
    [InlineData("red_remaining_without_icon", PurchaseSafety.RiskDetected)]
    public void Confirmation_RedNoticeDoesNotHidePriceRisk(string scenario, PurchaseSafety expected)
    {
        var screen = ImageCodec.DecodePng(Fixture("ja_free_confirm.png"));
        if (scenario == "cost_changed") Fill(screen, new(1404, 585, 31, 35), 30, 30, 30);
        if (scenario == "statement_changed") Fill(screen, new(929, 140, 31, 35), 30, 30, 30);
        if (scenario == "red_cost") Fill(screen, new(1404, 585, 31, 35), 230, 40, 40);
        if (scenario.StartsWith("red_remaining", StringComparison.Ordinal))
            Fill(screen, new(1380, 645, 40, 30), 230, 40, 40);
        if (scenario == "red_remaining_without_icon") Fill(screen, new(615, 575, 75, 115), 190, 190, 190);
        var png = ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height);
        var log = new TestLog();
        var checker = new DiamondSafetyChecker(new FakeAdbService(png), new TemplateMatcher(), log);
        var result = new ShopConfirmationRecognizer(checker).Evaluate(png, Environment.TickCount64,
            Layout(), Path.Combine(_templates, "button_confirm_buy.png"),
            Path.Combine(AppContext.BaseDirectory, "resources/common/templates/safety/diamond_icon.png"));
        Assert.True(expected == result.Safety, $"{scenario}: {result.Safety}: {result.Reason}");
    }

    /// <summary>真实列表确认“無料”；把右侧真实 200 钻石价格移入首栏后，仍不得授权购买。</summary>
    [Fact]
    public void FreeList_RejectsActualPaidPrice()
    {
        var screen = ImageCodec.DecodePng(Fixture("ja_free_list.png"));
        var layout = Layout();
        var recognizer = new ShopFreePurchaseRecognizer(Path.Combine(_templates, "free.png"),
            Path.Combine(_templates, "button_premium_free_buy.png"));
        TemplateMatchResult? Find() => recognizer.Find(ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height),
            layout.Find("freeItemPriceRegion")!.Roi, layout.Find("freeItemBuy")!.Roi, .85);
        Assert.NotNull(Find());
        for (var y = 682; y < 722; y++)
            Buffer.BlockCopy(screen.Bgra, (y * screen.Width + 782) * 4,
                screen.Bgra, (y * screen.Width + 418) * 4, 303 * 4);
        Assert.Null(Find());
    }

    /// <summary>使用生产共用布局，区域调整须同时受中日文回放约束。</summary>
    private static UiLayout Layout() => new UiLayoutService(new TestLog()).Load(
        Path.Combine(AppContext.BaseDirectory, "resources/common/ui_layout/shop.json"));

    /// <summary>读取未修改的 1920×1080 日服截图。</summary>
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Shop", name));

    /// <summary>明确标记的内存故障注入，检验金额证据缺失或变红时的否决行为。</summary>
    private static void Fill(ScreenImage screen, Roi roi, byte red, byte green, byte blue)
    {
        for (var y = roi.Y; y < roi.Y + roi.Height; y++)
        for (var x = roi.X; x < roi.X + roi.Width; x++)
        {
            var offset = (y * screen.Width + x) * 4;
            screen.Bgra[offset] = blue;
            screen.Bgra[offset + 1] = green;
            screen.Bgra[offset + 2] = red;
        }
    }

    /// <summary>只清理本测试独占的临时素材，不修改随包资源。</summary>
    public void Dispose() => Directory.Delete(_templates, recursive: true);
}

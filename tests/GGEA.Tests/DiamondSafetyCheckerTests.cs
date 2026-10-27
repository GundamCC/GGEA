using System.IO;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 钻石安全锁测试：两段式检测（先图标、后红字）。
/// </summary>
public class DiamondSafetyCheckerTests
{
    /// <summary>真实商店帧配合裁切后的图标验证风险；红字为明确标记的内存故障注入，不代表实机截图。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CroppedIcon_RealShopFrame_PreservesRedRisk(bool red)
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Shop/free_confirm.png"));
        var screen = ImageCodec.DecodePng(png);
        if (red) TestImageFactory.PasteColoredBlock(screen.Bgra, screen.Width, 30, 20, 1300, 650, 255, 0, 0);
        var checker = new DiamondSafetyChecker(new FakeAdbService(png), new TemplateMatcher(), new TestLog());
        Assert.Equal(red, checker.HasRisk(screen, new Roi(450, 350, 1050, 550),
            Path.Combine(AppContext.BaseDirectory, "resources/common/templates/safety/diamond_icon.png")));
    }

    [Fact]
    public void CountRedPixels_DetectsStrictRedOnly()
    {
        var pixels = TestImageFactory.CreateBgra(200, 100, 20);
        for (var y = 10; y < 30; y++)
        {
            for (var x = 10; x < 30; x++)
            {
                var offset = (y * 200 + x) * 4;
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 255;
            }
        }

        var count = DiamondSafetyChecker.CountRedPixels(pixels, 200, new Roi(0, 0, 200, 100));

        Assert.Equal(400, count);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task IconAndRed_ReturnsTrue()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 150, 65, 550, 460, 150); // 钻石图标
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
        var iconPath = Path.Combine(temp.Path, "diamond_icon.png");
        File.WriteAllBytes(iconPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(150, 65, 150), 150, 65));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var log = new TestLog();
        var checker = new DiamondSafetyChecker(fakeAdb, new TemplateMatcher(), log);

        var risk = await checker.IsDiamondConsumptionRiskAsync(
            "serial",
            new Roi(450, 350, 1050, 550),
            iconPath);

        Assert.True(risk);
        Assert.Contains(log.Entries, entry => entry.Level == "WARN" && entry.Message.Contains("钻石安全锁触发"));
        Assert.DoesNotContain(log.Entries, entry => entry.Level == "ERROR");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NoIcon_ReturnsFalse_EvenWithRed()
    {
        // 只有红色区域但没有钻石图标：不判定为钻石消耗
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
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
        var iconPath = Path.Combine(temp.Path, "diamond_icon.png");
        File.WriteAllBytes(iconPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(150, 65, 150), 150, 65));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var checker = new DiamondSafetyChecker(fakeAdb, new TemplateMatcher(), new TestLog());

        var risk = await checker.IsDiamondConsumptionRiskAsync(
            "serial",
            new Roi(450, 350, 1050, 550),
            iconPath);

        Assert.False(risk);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task IconWithoutRed_ReturnsFalse()
    {
        // 有钻石图标但没有红色数量：仅表示风险未命中，不能证明免费。
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 150, 65, 550, 460, 150);

        using var temp = new TempDirectory();
        var iconPath = Path.Combine(temp.Path, "diamond_icon.png");
        File.WriteAllBytes(iconPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(150, 65, 150), 150, 65));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var checker = new DiamondSafetyChecker(fakeAdb, new TemplateMatcher(), new TestLog());

        var risk = await checker.IsDiamondConsumptionRiskAsync(
            "serial",
            new Roi(450, 350, 1050, 550),
            iconPath);

        Assert.False(risk);
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

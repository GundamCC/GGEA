using System.IO;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 战舰区域定位器测试：拖动直到找到六边形。
/// </summary>
public class BattleshipLocatorTests
{
    [Fact]
    public async Task FindWithPanAsync_FoundAfterDrag_ReturnsMatch()
    {
        var noHex = TestImageFactory.CreateBgra(1920, 1080, 20);
        var withHex = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(withHex, 1920, 100, 100, 400, 300, 150);

        using var temp = new TempDirectory();
        var templatePath = Path.Combine(temp.Path, "hexagon.png");
        var maskPath = Path.Combine(temp.Path, "hexagon_mask.png");
        File.WriteAllBytes(templatePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 150), 100, 100));
        File.WriteAllBytes(maskPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 255), 100, 100));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(noHex, 1920, 1080));
        var hidden = ImageCodec.EncodePng(noHex, 1920, 1080);
        var visible = ImageCodec.EncodePng(withHex, 1920, 1080);
        fakeAdb.ScreenshotProvider = () => fakeAdb.Swipes.Count == 0 ? hidden : visible;
        var locator = new BattleshipLocator(fakeAdb, new TemplateMatcher(), new TestLog());

        var match = await locator.FindWithPanAsync(
            "serial",
            templatePath,
            maskPath,
            maxDrags: 4,
            dragDurationMs: 50,
            threshold: 0.9);

        Assert.NotNull(match);
        Assert.Equal(450, match!.CenterX);
        Assert.Equal(350, match.CenterY);
        Assert.Single(fakeAdb.Swipes);
    }

    [Fact]
    public async Task FindWithPanAsync_NotFound_DragsMaxTimes()
    {
        var noHex = TestImageFactory.CreateBgra(1920, 1080, 20);

        using var temp = new TempDirectory();
        var templatePath = Path.Combine(temp.Path, "hexagon.png");
        var maskPath = Path.Combine(temp.Path, "hexagon_mask.png");
        File.WriteAllBytes(templatePath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 150), 100, 100));
        File.WriteAllBytes(maskPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 255), 100, 100));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(noHex, 1920, 1080));
        var locator = new BattleshipLocator(fakeAdb, new TemplateMatcher(), new TestLog());

        var match = await locator.FindWithPanAsync(
            "serial",
            templatePath,
            maskPath,
            maxDrags: 3,
            dragDurationMs: 50,
            threshold: 0.9);

        Assert.Null(match);
        Assert.Equal(3, fakeAdb.Swipes.Count);
    }

    /// <summary>地图初始只显示战舰时应继续拖动，再从实机画面中的相邻设施定位角色要求。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CharacterRequests_FindsCorrectLabelAfterDrag(bool fromHarvest)
    {
        var ship = GGEA.App.Config.ConfigService.ResolvePath(new GGEA.App.Config.AppConfig().BattleshipTemplate);
        var adb = new FakeAdbService(File.ReadAllBytes(ship));
        var hidden = File.ReadAllBytes(ship);
        var visible = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/character_requests_and_training.png"));
        adb.ScreenshotProvider = () => adb.Swipes.Count == 0 ? hidden : visible;
        var locator = new BattleshipLocator(adb, new TemplateMatcher(), new TestLog());

        var match = await locator.FindCharacterRequestsWithPanAsync("serial", maxDrags: 1, fromHarvest: fromHarvest);
        Assert.NotNull(match);
        Assert.InRange(match.CenterX, 186, 196);
        Assert.InRange(match.CenterY, 31, 41);
        var swipe = Assert.Single(adb.Swipes);
        Assert.Equal(fromHarvest ? -1000 : -700, swipe.X2 - swipe.X1);
        Assert.Empty(adb.Taps);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ObscuredMapButton_RestartsConfirmationWithoutDragging(bool requests)
    {
        string path = requests ? "resources/templates/base/character_requests/character_requests.png"
            : new GGEA.App.Config.AppConfig().BattleshipTemplate;
        var label = ImageCodec.DecodePng(File.ReadAllBytes(GGEA.App.Config.ConfigService.ResolvePath(path)));
        byte[] Frame(int? x)
        {
            const int width = 700, height = 200;
            var pixels = TestImageFactory.CreateBgra(width, height, 20);
            if (x is { } left)
                for (int row = 0; row < label.Height; row++)
                    Buffer.BlockCopy(label.Bgra, row * label.Width * 4, pixels, ((40 + row) * width + left) * 4, label.Width * 4);
            return ImageCodec.EncodePng(pixels, width, height);
        }
        var early = Frame(80);
        var clear = Frame(300);
        var adb = new FakeAdbService(early);
        adb.EnqueueScreenshots(early, Frame(null), clear, clear, clear);
        var locator = new BattleshipLocator(adb, new TemplateMatcher(), new TestLog());
        var result = requests ? await locator.FindCharacterRequestsWithPanAsync("serial", maxDrags: 0)
            : await locator.FindWithPanAsync("serial", path, null, maxDrags: 0);
        Assert.Equal(300, result?.X);
        Assert.Equal(6, adb.ScreenshotCount);
        Assert.Empty(adb.Swipes);
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

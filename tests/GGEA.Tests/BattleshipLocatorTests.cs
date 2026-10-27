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
        fakeAdb.EnqueueScreenshots(ImageCodec.EncodePng(withHex, 1920, 1080));
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

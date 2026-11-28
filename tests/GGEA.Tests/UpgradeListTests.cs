using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

public sealed class UpgradeListTests
{
    [Theory]
    [InlineData("ascending", UpgradeSortDirection.Ascending)]
    [InlineData("descending", UpgradeSortDirection.Ascending)]
    [InlineData("ascending", UpgradeSortDirection.Descending)]
    public async Task EnsureDirection_UsesCurrentDirectionAndConfirmsTarget(string initial, UpgradeSortDirection target)
    {
        var before = ListFrame(initial);
        var targetName = target.ToString().ToLowerInvariant();
        var after = ListFrame(targetName);
        var adb = new FakeAdbService(before);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? before : after;
        using var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        Assert.True(await new UpgradeListService(automation).EnsureDirectionAsync("device", target));
        if (initial == targetName) Assert.Empty(adb.Taps);
        else Assert.Equal((1784, initial == "ascending" ? 901 : 899), Assert.Single(adb.Taps));
    }

    [Fact]
    public async Task RequestOpensFirstLd720CardWithoutSortFieldText()
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upgrade", "filtered_ld720.png"));
        var normalized = GameResolution.ReadPng(png).Normalize(png);
        var adb = new FakeAdbService(normalized);
        using var ocr = new OnnxTextRecognizer();
        using var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        var service = new UnitUpgradeService(automation, ocr);
        await service.SelectFirstUnitAsync("device", default);
        Assert.Equal((428, 434), Assert.Single(adb.Taps));
    }

    [Theory]
    [InlineData(140, 7, 102, 53)]
    [InlineData(250, 273, 73, 32)]
    [InlineData(1081, 566, 68, 63)]
    public void RequestRequiresListTitleFirstCardAndFilter(int x, int y, int width, int height)
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upgrade", "filtered_ld720.png"));
        var native = ImageCodec.DecodePng(png);
        for (var row = y; row < y + height; row++)
            Array.Clear(native.Bgra, 4 * (row * native.Width + x), 4 * width);
        var masked = ImageCodec.EncodePng(native.Bgra, native.Width, native.Height);
        var frame = ImageCodec.DecodePng(GameResolution.ReadPng(masked).Normalize(masked));
        using var ocr = new OnnxTextRecognizer();
        using var automation = new AutomationService(new FakeAdbService([]), new TemplateMatcher(), new TestLog());
        Assert.Null(new UnitUpgradeService(automation, ocr).FindFirstUnit(frame, default));
    }

    // 两个正例来自独立实机帧，仅保留右下角按钮以隔离背景和模板来源。
    private static byte[] ListFrame(string direction)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        DismantleEntryTests.Paste(pixels, UpgradeListService.TitleTemplate, 217, 18);
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upgrade", $"sort_{direction}.png");
        var icon = ImageCodec.DecodePng(File.ReadAllBytes(path));
        for (var y = 0; y < icon.Height; y++)
            Buffer.BlockCopy(icon.Bgra, y * icon.Width * 4, pixels, ((858 + y) * 1920 + 1742) * 4, icon.Width * 4);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }
}

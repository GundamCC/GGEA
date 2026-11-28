using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

public class UpgradeMaterialTests
{
    [Fact]
    public void Allocate_UsesAvailableBronzeBeforeSilverAndGold()
    {
        MaterialStack[] stocks = [new(0, 2, true), new(1, 2, true), new(0, 4, true)];
        Assert.Equal(new[] { 2, 2, 1 }, UpgradeMaterialService.Allocate(stocks, 5));
        stocks[0] = new(0, 2, false);
        Assert.Equal(new[] { 0, 2, 3 }, UpgradeMaterialService.Allocate(stocks, 5));
        stocks[2] = new(0, 0, false);
        Assert.Null(UpgradeMaterialService.Allocate(stocks, 5));
    }

    [Fact]
    public void ReadStacks_RecognizesDimmedZeroAndFiveSelectedSilver()
    {
        var frame = ImageCodec.DecodePng(FiveSilverPreview());
        using var ocr = new OnnxTextRecognizer();
        using var automation = new AutomationService(new FakeAdbService([]), new TemplateMatcher(), new TestLog(), textOcr: ocr);
        var result = new UpgradeMaterialService(automation, ocr).ReadStacks(frame, default);
        Assert.Equal(new MaterialStack[] { new(0, 0, false), new(5, 465, true), new(0, 30, true) }, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_SubmitsOnceAndOnlyAdvancesTheLevelUpScreen(bool levelUp)
    {
        var preview = FiveSilverPreview();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        DismantleEntryTests.Paste(pixels, "resources/templates/upgrade/title_unit_info.png", 220, 25);
        DismantleEntryTests.Paste(pixels, "resources/templates/common/back.png", 10, 10);
        var unit = ImageCodec.EncodePng(pixels, 1920, 1080);
        DismantleEntryTests.Paste(pixels, "resources/templates/upgrade/title_lv_up_complete.png", 810, 240);
        TapToNextOcrSamples.Paste(pixels, 1920, 300, 60, 810, 948);
        var result = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(preview);
        var remainingPreviewFrames = 2;
        adb.ScreenshotProvider = () =>
        {
            if (adb.Taps.Count == 0 || remainingPreviewFrames-- > 0) return preview;
            return levelUp && adb.Taps.Count == 1 ? result : unit;
        };
        using var ocr = new OnnxTextRecognizer();
        using var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: ocr);
        Assert.True(await new UpgradeMaterialService(automation, ocr).ApplyAsync("test", 5, default));
        Assert.Equal(levelUp ? 2 : 1, adb.Taps.Count);
        Assert.InRange(adb.Taps[0].X, 1030, 1270);
        if (levelUp) Assert.InRange(adb.Taps[1].X, 810, 1110);
    }

    private static byte[] FiveSilverPreview()
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        DismantleEntryTests.Paste(pixels, "resources/templates/upgrade/title_lv_materials.png", 884, 64);
        DismantleEntryTests.Paste(pixels, "resources/templates/upgrade/button_apply_lv.png", 1080, 960);
        var patch = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upgrade", "material_counts.png")));
        for (var y = 0; y < patch.Height; y++)
            Buffer.BlockCopy(patch.Bgra, y * patch.Width * 4, pixels, ((260 + y) * 1920 + 492) * 4, patch.Width * 4);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }
}

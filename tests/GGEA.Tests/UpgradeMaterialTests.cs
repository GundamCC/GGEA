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
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        DismantleEntryTests.Paste(pixels, "resources/templates/upgrade/title_lv_materials.png", 884, 64);
        var patch = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upgrade", "material_counts.png")));
        for (var y = 0; y < patch.Height; y++)
            Buffer.BlockCopy(patch.Bgra, y * patch.Width * 4, pixels, ((260 + y) * 1920 + 492) * 4, patch.Width * 4);
        var frame = new ScreenImage { Bgra = pixels, Width = 1920, Height = 1080 };
        using var ocr = new OnnxTextRecognizer();
        using var automation = new AutomationService(new FakeAdbService([]), new TemplateMatcher(), new TestLog(), textOcr: ocr);
        var result = new UpgradeMaterialService(automation, ocr).ReadStacks(frame, default);
        Assert.Equal(new MaterialStack[] { new(0, 0, false), new(5, 465, true), new(0, 30, true) }, result);
    }
}

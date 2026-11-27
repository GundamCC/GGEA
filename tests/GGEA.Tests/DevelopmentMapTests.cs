using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

public class DevelopmentMapTests
{
    private static ScreenImage Canvas(string fixture, int x, int y)
    {
        var patch = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Develop", fixture + ".png")));
        var frame = new ScreenImage { Width = 1920, Height = 1080, Bgra = TestImageFactory.CreateBgra(1920, 1080, 20) };
        Paste(frame, patch, x, y);
        return frame;
    }

    private static void Paste(ScreenImage frame, ScreenImage patch, int x, int y)
    {
        for (int i = 0; i < patch.Height; i++)
            Buffer.BlockCopy(patch.Bgra, i * patch.Width * 4, frame.Bgra, ((y + i) * frame.Width + x) * 4, patch.Width * 4);
    }

    [Fact]
    public void NativeLogo_RejectsOtherThunderboltSubtitle()
    {
        var frame = Canvas("thunderbolt_band_flower_card", 461, 440);
        Assert.InRange(new DevelopmentSeriesLogo("330").Find(frame, default)!.Value.CenterX, 557, 576);
        Assert.Null(new DevelopmentSeriesLogo("320").Find(frame, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondDiagram_RequiresNativeTabAndSelectedBorder(bool alreadySelected)
    {
        var target = DevelopmentMaps.Get("1750").Diagrams.Single(d => d.Tab == 2);
        var title = ConfigService.ResolvePath("resources/templates/develop/title_develop.png");
        byte[] Frame(string fixture)
        {
            var frame = Canvas(fixture, 45, 309);
            Paste(frame, ImageCodec.DecodePng(File.ReadAllBytes(title)), 219, 19);
            return ImageCodec.EncodePng(frame.Bgra, frame.Width, frame.Height);
        }
        var idle = Frame("second_tab_idle");
        var selected = Frame("second_tab_selected");
        var adb = new FakeAdbService(idle);
        adb.ScreenshotProvider = () => alreadySelected || adb.Taps.Count > 0 ? selected : idle;
        using var auto = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        Assert.True(await new DevelopmentTabNavigator(auto).SelectAsync("test", target, title, default));
        Assert.Equal(alreadySelected ? 0 : 1, adb.Taps.Count);
        Assert.All(adb.Taps, p => { Assert.InRange(p.X, 183, 213); Assert.InRange(p.Y, 390, 430); });
        var wrong = DevelopmentMaps.Get("1750").Diagrams.Single(d => d.Tab == 1) with { Tab = 2 };
        Assert.Null(DevelopmentTabNavigator.Read(ImageCodec.DecodePng(selected), wrong, default));
    }
}

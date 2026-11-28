using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

public partial class CharacterRequestPageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObtainMethods_OnlyMovesOnDevelopmentCard(bool scroll)
    {
        var screen = Empty();
        PasteTemplate(screen, "title_obtain_methods.png", 830, 69);
        Paste(screen, ConfigService.ResolvePath("resources/templates/common/close.png"), 779, 934);
        PasteTemplate(screen, "source_move.png", 1534, 214);
        // 起始帧仅下一卡有开发标题，上一卡的移动按钮不能授权点击。
        PasteTemplate(screen, "source_develop.png", 772, 480);
        var before = Encode(screen);
        PasteTemplate(screen, "source_move.png", 1534, 495);
        var ready = Encode(screen);
        var adb = new FakeAdbService(before);
        adb.ScreenshotProvider = () => scroll && adb.Swipes.Count == 0 ? before : ready;
        using var auto = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        Assert.True(await new ObtainMethodsNavigator(auto).OpenDevelopmentAsync("device", default));
        Assert.Equal(scroll ? 1 : 0, adb.Swipes.Count);
        Assert.InRange(Assert.Single(adb.Taps).Y, 526, 545);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ObtainMethods_LandingRequiresStableColoredUnit(bool developed)
    {
        var screen = Empty();
        var title = ConfigService.ResolvePath("resources/templates/develop/title_develop.png");
        Paste(screen, title, 217, 14);
        PasteTemplate(screen, "roadmap_expand.png", 1780, 977);
        if (developed)
            Paste(screen, Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/obtain_leo_landing.png"), 910, 333);
        var adb = new FakeAdbService(Encode(screen));
        using var auto = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        using var ocr = new OnnxTextRecognizer();
        var landing = await new DeliveryDevelopmentService(auto, new TestLog(), ocr)
            .ObserveLandingAsync("device", new(title), DevelopmentRequestIndex.DeliveryTemplate("1210001700"), .86, default);
        if (developed)
        {
            Assert.InRange(landing.Unit!.Value.CenterX, 1040, 1085);
            Assert.InRange(landing.Unit.Value.CenterY, 419, 470);
        }
        else Assert.Null(landing.Unit);
        Assert.Empty(adb.Swipes);
        Assert.Empty(adb.Taps);
    }
}

using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>补点只针对稳定的第一层，不降低蓝色按钮阈值，不重试整批开发。</summary>
public sealed class DevelopConfirmationWaitTests
{
    /// <summary>验证输入丢失恢复、第二层优先、暗态及未知页停止和一次补点上限。</summary>
    [Theory]
    [InlineData("lost", 1, true)]
    [InlineData("second", 0, true)]
    [InlineData("stuck", 1, false)]
    [InlineData("disabled", 0, false)]
    [InlineData("unknown", 0, false)]
    public async Task Retry_IsBoundedAndRequiresStableFirstLayer(string scenario, int taps, bool found)
    {
        var first = ConfigService.ResolvePath("resources/templates/develop/button/button_execute_develop.png");
        var second = ConfigService.ResolvePath("resources/templates/develop/button/button_execute.png");
        byte[] Frame(string? path)
        {
            var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
            if (path is not null) DismantleEntryTests.Paste(pixels, path, 968, 934);
            return ImageCodec.EncodePng(pixels, 1920, 1080);
        }
        var a = Frame(first); var b = Frame(second); var blank = Frame(null);
        var disabled = Frame(ConfigService.ResolvePath(DevelopExecutionButton.DisabledTemplate));
        var adb = new FakeAdbService(a);
        var frames = 0;
        adb.ScreenshotProvider = () => scenario switch
        {
            "second" => b,
            "lost" => adb.Taps.Count == 0 ? a : b,
            "disabled" => disabled,
            "unknown" => frames++ == 0 ? blank : a,
            _ => a,
        };
        var auto = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        var result = await DevelopConfirmationWait.WaitAsync(auto, "device", first, second, 1000, _ => { }, retryAfterMs: 0, pollMs: 1);
        Assert.Equal(found, result is not null);
        Assert.Equal(taps, adb.Taps.Count);
        if (taps != 0) Assert.True(adb.ScreenshotCount >= 2);
    }
}

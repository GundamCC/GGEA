using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;
using System.IO;

namespace GGEA.Tests;

/// <summary>使用真实模板合成页面，验证解体循环只在明确空提示时结束且区域和点击顺序正确。</summary>
public sealed class DismantleServiceTests
{
    /// <summary>按页面内容创建截图，截图切换由输入触发，避免依赖截图调用次数。</summary>
    private static byte[] Frame(params (string Path, int X, int Y)[] items)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        foreach (var (path, x, y) in items) DismantleEntryTests.Paste(pixels,
            path == "result-title" ? Path.Combine(AppContext.BaseDirectory, "Fixtures", "Develop", "dismantle_result_title.png")
                : "resources/templates/" + path + ".png", x, y);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }

    /// <summary>零轮空结果和解体一轮再空结果均使用中间弹窗 OK，普通结果则使用底部 OK。</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Loop_StopsOnlyAtExplicitEmpty(bool oneRound, bool emptyOk)
    {
        var home = Frame(("upgrade/title_dismantle", 220, 19), ("upgrade/filter_off", 1650, 712), ("upgrade/button_auto_dismantle", 79, 848));
        var reset = Frame(("upgrade/auto_dismantle", 851, 3), ("upgrade/button_reset", 1552, 821));
        var off = Frame(("upgrade/auto_dismantle", 851, 3), ("upgrade/option/option_dev_unit_off", 111, 551));
        var on = Frame(("upgrade/auto_dismantle", 851, 3), ("upgrade/option/option_dev_unit_on", 109, 548), ("common/button_confirm", 962, 934));
        var selected = Frame(("upgrade/title_dismantle", 220, 19), ("upgrade/button_dismantle", 1491, 814));
        var confirmation = Frame(("upgrade/dismantle_confirm", 851, 2), ("upgrade/button_execute_dismantle", 962, 934));
        var result = Frame(("result-title", 851, 2), ("dialog/button_ok", 777, 931));
        var transition = Frame(("upgrade/button_execute_dismantle", 962, 934));
        var empty = emptyOk ? Frame(("dialog/no_recommended_units", 438, 470), ("dialog/button_ok", 777, 790))
            : Frame(("dialog/no_recommended_units", 438, 470));
        var adb = new FakeAdbService(home);
        var resultFrames = 0;
        adb.ScreenshotProvider = () =>
        {
            var count = adb.Taps.Count;
            if (oneRound && count is >= 4 and <= 6) return count == 4 ? selected : count == 5 ? confirmation
                : resultFrames++ == 0 ? transition : result;
            if (oneRound && count >= 7) count -= 7;
            return count switch { 0 => home, 1 => reset, 2 => off, 3 => on, 4 => empty, _ => home };
        };
        var service = new DismantleService(new AutomationService(adb, new TemplateMatcher(), new TestLog()), 150, 1);
        if (!emptyOk)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunUntilEmptyAsync("device", _ => { }));
            Assert.Equal(4, adb.Taps.Count);
            return;
        }
        Assert.Equal(oneRound ? 1 : 0, await service.RunUntilEmptyAsync("device", _ => { }));
        Assert.Equal(oneRound ? 12 : 5, adb.Taps.Count);
        Assert.Equal(oneRound ? 2 : 1, adb.Swipes.Count);
        Assert.InRange(adb.Taps[^1].Y, 780, 920);
        if (oneRound) Assert.InRange(adb.Taps[6].Y, 931, 1060);
    }

    /// <summary>空提示必须在中央区域，不能用按钮缺失或标题外的空提示推断已经清理完。</summary>
    [Theory]
    [InlineData(470, true)]
    [InlineData(50, false)]
    public async Task EmptyPrompt_RespectsRegion(int y, bool expected)
    {
        var adb = new FakeAdbService(Frame(("dialog/no_recommended_units", 438, y)));
        var service = new DismantleService(new AutomationService(adb, new TemplateMatcher(), new TestLog()), 100, 1);
        if (expected) Assert.Null(await service.WaitSelectionAsync("device"));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => service.WaitSelectionAsync("device"));
        Assert.Empty(adb.Taps);
    }

    /// <summary>共享区域按用户给定边距计算，不能把无推荐单位 OK 放到普通底部区域。</summary>
    [Fact]
    public void DialogRegions_HaveSpecifiedBounds()
    {
        Assert.Equal(new Roi(0, 290, 1920, 500), RecognitionRegions.Resolve(RecognitionRegion.DialogPrompt, 1920, 1080));
        Assert.Equal(new Roi(0, 780, 1920, 140), RecognitionRegions.Resolve(RecognitionRegion.DialogButtons, 1920, 1080));
        Assert.Equal(new Roi(1490, 795, 430, 165), RecognitionRegions.Resolve(RecognitionRegion.DismantleButton, 1920, 1080));
    }
}

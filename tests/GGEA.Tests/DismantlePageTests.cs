using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>两种近似模板必须用中心填充颜色区分，页面和区域仍然是识别前提。</summary>
public sealed class DismantlePageTests
{
    /// <summary>重置后必须识别新帧的底部确定；标题缺失或确定越界时不得继续点击。</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task Reset_RequiresTitleAndFreshBottomConfirmation(bool title, bool bottom, bool expected)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        if (title) DismantleEntryTests.Paste(pixels, "resources/templates/upgrade/sort.png", 850, 1);
        DismantleEntryTests.Paste(pixels, "resources/templates/upgrade/button_reset.png", 1552, 821);
        var before = ImageCodec.EncodePng(pixels, 1920, 1080);
        DismantleEntryTests.Paste(pixels, "resources/templates/common/button_confirm.png", 980, bottom ? 940 : 550);
        var after = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(before);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? before : after;
        var service = new DismantlePageRecognizer(new AutomationService(adb, new TemplateMatcher(), new TestLog()));
        Assert.Equal(expected, await service.ResetAndConfirmAsync("device", timeoutMs: 100));
        Assert.Equal(!title ? 0 : expected ? 2 : 1, adb.Taps.Count);
        if (title) Assert.Equal((1682, 857), adb.Taps[0]);
        if (expected) Assert.True(adb.Taps[1].Y >= 940);
    }

    /// <summary>仅解体页 ON 可点击；排序标题缺失或重置位于区域外必须停止，不点击重置。</summary>
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public async Task OpenSort_GatesInputAndLocatesReset(bool on, bool sortTitle, bool resetInside, bool expected)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        DismantleEntryTests.Paste(pixels, DismantleEntryService.DismantleTitleTemplate, 220, 19);
        DismantleEntryTests.Paste(pixels, $"resources/templates/upgrade/filter_{(on ? "on" : "off")}.png", 1649, 713);
        var before = ImageCodec.EncodePng(pixels, 1920, 1080);
        pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        DismantleEntryTests.Paste(pixels, "resources/templates/upgrade/sort.png", sortTitle ? 850 : 400, 1);
        DismantleEntryTests.Paste(pixels, "resources/templates/upgrade/button_reset.png", resetInside ? 1552 : 900, 821);
        var after = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(before);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? before : after;
        var recognizer = new DismantlePageRecognizer(new AutomationService(adb, new TemplateMatcher(), new TestLog()));
        var result = await recognizer.OpenSortAndFindResetAsync("device", timeoutMs: 100);
        Assert.Equal(expected, result is not null);
        Assert.Equal(on ? 1 : 0, adb.Taps.Count);
        if (on) Assert.Equal((1690, 750), adb.Taps[0]);
        if (expected) Assert.Equal((1682, 857), (result!.CenterX, result.CenterY));
    }

    /// <summary>真实 ON/OFF 素材、图标缺失及区域外图标都不得混淆。</summary>
    [Theory]
    [InlineData("filter_on", 1649, true, DismantleFilterState.On)]
    [InlineData("filter_off", 1650, true, DismantleFilterState.Off)]
    [InlineData("filter_on", 1450, true, DismantleFilterState.Unknown)]
    [InlineData(null, 1649, true, DismantleFilterState.Unknown)]
    [InlineData("filter_on", 1649, false, DismantleFilterState.Unknown)]
    public async Task Observe_RequiresTitleAndUsesBlueFill(string? icon, int x, bool title, DismantleFilterState expected)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        if (title) DismantleEntryTests.Paste(pixels, DismantleEntryService.DismantleTitleTemplate, 220, 19);
        DismantleEntryTests.Paste(pixels, "resources/templates/upgrade/button_auto_dismantle.png", 79, 848);
        if (icon is not null) DismantleEntryTests.Paste(pixels, $"resources/templates/upgrade/{icon}.png", x, 713);
        var png = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(png);
        var recognizer = new DismantlePageRecognizer(new AutomationService(adb, new TemplateMatcher(), new TestLog()));
        var result = await recognizer.RecognizeAsync(png);
        if (!title) Assert.Null(result);
        else
        {
            Assert.NotNull(result);
            Assert.NotNull(result.AutoButton);
            Assert.Equal(expected, result.FilterState);
        }
        Assert.Empty(adb.Taps);
    }
}

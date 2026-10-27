using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;
namespace GGEA.Tests;

/// <summary>解体入口必须有单位上限及主界面恢复证据，标题仅限左上区域。</summary>
public sealed class DismantleEntryTests
{
    /// <summary>真实标题素材出现在区域内才成功，区域外不得继续输入。</summary>
    [Theory]
    [InlineData(250, 30, true)]
    [InlineData(850, 40, true)]
    [InlineData(1100, 30, false)]
    [InlineData(250, 160, false)]
    public async Task Entry_ChecksBothTitlesAndClicksMatchedEntry(int x, int y, bool expected)
    {
        var template = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(DismantleEntryService.TitleTemplate)));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var row = 0; row < template.Height; row++)
            Array.Copy(template.Bgra, row * template.Width * 4, pixels, ((y + row) * 1920 + x) * 4, template.Width * 4);
        Paste(pixels, DismantleEntryService.EntryTemplate, 805, 861);
        var upgrade = ImageCodec.EncodePng(pixels, 1920, 1080);
        var dismantlePixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(dismantlePixels, DismantleEntryService.DismantleTitleTemplate, 220, 19);
        var dismantle = ImageCodec.EncodePng(dismantlePixels, 1920, 1080);
        var adb = new FakeAdbService(upgrade);
        adb.ScreenshotProvider = () => adb.Taps.Count >= 2 ? dismantle : upgrade;
        var service = new DismantleEntryService(new AutomationService(adb, new TemplateMatcher(), new TestLog()));
        Assert.Equal(expected, await service.OpenAfterRecoveryAsync("device", DevelopResult.UnitLimitReached, true, timeoutMs: 100));
        Assert.Equal((415, 1034), adb.Taps[0]);
        Assert.Equal(expected ? 2 : 1, adb.Taps.Count);
        if (expected) Assert.Equal((935, 899), adb.Taps[1]);
        Assert.Empty(adb.Swipes);
    }

    /// <summary>金币不足、恢复失败或用户取消都不能点击解体入口。</summary>
    [Theory]
    [InlineData(DevelopResult.InsufficientCoins, true, false)]
    [InlineData(DevelopResult.UnitLimitReached, false, false)]
    [InlineData(DevelopResult.Failed, true, false)]
    [InlineData(DevelopResult.UnitLimitReached, true, true)]
    public async Task Entry_RejectsUnconfirmedHomeOrOtherReason(DevelopResult reason, bool home, bool canceled)
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var service = new DismantleEntryService(new AutomationService(adb, new TemplateMatcher(), new TestLog()));
        var run = () => service.OpenAfterRecoveryAsync("device", reason, home, new CancellationToken(canceled));
        if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
        else Assert.False(await run());
        Assert.Empty(adb.Taps);
        Assert.Equal(0, adb.ScreenshotCount);
    }

    /// <summary>界面标题区域固定锚定左上角，小截图只裁切边界。</summary>
    [Fact]
    public void PageTitleRegion_IsTopLeftAndClipped()
    {
        Assert.Equal(new Roi(0, 0, 1000, 120), RecognitionRegions.Resolve(RecognitionRegion.PageTitle, 1920, 1080));
        Assert.Equal(new Roi(0, 0, 800, 90), RecognitionRegions.Resolve(RecognitionRegion.PageTitle, 800, 90));
    }

    /// <summary>入口缺失不点击，解体标题缺失不误报到达解体页。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_MissingControlOrDestinationStops(bool entryPresent)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, DismantleEntryService.TitleTemplate, 39, 18);
        if (entryPresent) Paste(pixels, DismantleEntryService.EntryTemplate, 805, 861);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var service = new DismantleEntryService(new AutomationService(adb, new TemplateMatcher(), new TestLog()));
        Assert.False(await service.OpenAfterRecoveryAsync("device", DevelopResult.UnitLimitReached, true, timeoutMs: 100));
        Assert.Equal(entryPresent ? 2 : 1, adb.Taps.Count);
    }

    /// <summary>在测试画面上放置真实素材，避免用固定伪匹配绕过区域限制。</summary>
    internal static void Paste(byte[] pixels, string path, int x, int y)
    {
        var template = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(path)));
        for (var row = 0; row < template.Height; row++)
            Array.Copy(template.Bgra, row * template.Width * 4, pixels, ((y + row) * 1920 + x) * 4, template.Width * 4);
    }
}

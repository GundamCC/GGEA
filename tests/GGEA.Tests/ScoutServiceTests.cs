using System.IO;
using GGEA.App.Config;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>验证同帧标题与按钮、区域限制、单次点击及失败时禁止输入。</summary>
public sealed class ScoutServiceTests
{
    /// <summary>使用随产品复制的真实素材及开发布局，覆盖资源接入。</summary>
    private static string TitlePath => ConfigService.ResolvePath(ScoutService.TitleTemplate);
    private static string ButtonPath => ConfigService.ResolvePath(ScoutService.ButtonTemplate);
    private static UiLayout Layout => new UiLayoutService(new TestLog()).Load(
        Path.Combine(AppContext.BaseDirectory, "resources/common/ui_layout/develop.json"));

    /// <summary>仅标题和按钮同时在各自区域内才允许点击，目标超出区域不能误报。</summary>
    [Theory]
    [InlineData(218, 16, 1495, 832, true)]
    [InlineData(900, 400, 1495, 832, false)]
    [InlineData(218, 16, 900, 400, false)]
    [InlineData(218, 16, 1495, 981, false)]
    [InlineData(218, 16, -1, -1, false)]
    [InlineData(-1, -1, 1495, 832, false)]
    public async Task StartAsync_RequiresBothTargetsInTheirRegions(int titleX, int titleY,
        int buttonX, int buttonY, bool expected)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        Paste(pixels, TitlePath, titleX, titleY);
        Paste(pixels, ButtonPath, buttonX, buttonY);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var result = await CreateService(adb).StartAsync("test", TitlePath, ButtonPath, Layout);
        Assert.Equal(expected, result is not null);
        if (expected)
        {
            var button = ImageCodec.DecodePng(File.ReadAllBytes(ButtonPath));
            Assert.Equal((buttonX + button.Width / 2, buttonY + button.Height / 2), Assert.Single(adb.Taps));
        }
        else Assert.Empty(adb.Taps);
        Assert.Empty(adb.Swipes);
        Assert.Equal(1, adb.ScreenshotCount);
    }

    /// <summary>模拟一帧画面；负坐标表示不放置目标，越界部分裁掉以测试底边限制。</summary>
    private static void Paste(byte[] pixels, string path, int x, int y)
    {
        if (x < 0 || y < 0) return;
        var template = ImageCodec.DecodePng(File.ReadAllBytes(path));
        for (var row = 0; row < template.Height && y + row < 1080; row++)
            Buffer.BlockCopy(template.Bgra, row * template.Width * 4, pixels,
                ((y + row) * 1920 + x) * 4, template.Width * 4);
    }

    /// <summary>缺失标准区域时立即拒绝，不能悄悄扩大搜索范围。</summary>
    [Fact]
    public async Task StartAsync_MissingRegionFailsBeforeCapture()
    {
        var adb = new FakeAdbService([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(adb).StartAsync("test", TitlePath, ButtonPath, new UiLayout()));
        Assert.Equal(0, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>不支持的分辨率必须报告异常，不能按错误坐标点击。</summary>
    [Fact]
    public async Task StartAsync_UnsupportedResolutionFails()
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(960, 540, 0), 960, 540));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(adb).StartAsync("test", TitlePath, ButtonPath, Layout));
        Assert.Empty(adb.Taps);
    }

    /// <summary>取消和截图失败必须传播，且不产生点击。</summary>
    [Fact]
    public async Task StartAsync_PropagatesCancellationAndCaptureFailure()
    {
        var adb = new FakeAdbService([]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(adb).StartAsync("test", TitlePath, ButtonPath, Layout, ct: cancellation.Token));
        adb.ScreenshotFailure = _ => new InvalidOperationException("模拟截图失败");
        await Assert.ThrowsAsync<ScreenshotCaptureException>(() =>
            CreateService(adb).StartAsync("test", TitlePath, ButtonPath, Layout));
        Assert.Empty(adb.Taps);
    }

    /// <summary>复用产品匹配器与只读截图服务，不启用任何全局自动点击。</summary>
    private static ScoutService CreateService(FakeAdbService adb) =>
        new(new AutomationService(adb, new TemplateMatcher(), new TestLog()));
}

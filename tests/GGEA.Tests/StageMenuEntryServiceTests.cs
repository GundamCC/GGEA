using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>关卡菜单入口的真实素材匹配、标题区域、等待与失败边界测试。</summary>
public sealed class StageMenuEntryServiceTests
{
    /// <summary>标题在统一区域内出现时放行；加载帧未命中应继续有限等待。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenFromHomeAsync_TitleAppears_ConfirmsWithoutExtraTaps(bool delayed)
    {
        var title = TestImageFactory.CreateStageMenuPng();
        var adb = new FakeAdbService(delayed ? BlankFrame() : title);
        if (delayed) adb.EnqueueScreenshots(title);
        var log = new TestLog();
        var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new StageMenuEntryService(automation, log, timeoutMs: 2000, pollIntervalMs: 10);

        var result = await service.OpenFromHomeAsync("serial", MainLayout(), settleDelayMs: 0);

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { (1000, 950) }, adb.Taps);
        Assert.Equal(delayed ? 2 : 1, adb.ScreenshotCount);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("识别成功: title_stage.png"));
    }

    /// <summary>区域外的相同标题不能授权后续流程；等待时也不能点击全局继续提示。</summary>
    [Fact]
    public async Task OpenFromHomeAsync_TitleOutsideRegion_StopsAndWarnsAboutHomeLag()
    {
        var pixels = ImageCodec.DecodePng(TestImageFactory.CreateStageMenuPng(titleY: 200)).Bgra;
        var nextPath = System.IO.Path.Combine(AppContext.BaseDirectory, "resources/common/templates/entry/tap_to_next.png");
        var next = ImageCodec.DecodePng(System.IO.File.ReadAllBytes(nextPath));
        for (var row = 0; row < next.Height; row++)
            Buffer.BlockCopy(next.Bgra, row * next.Width * 4, pixels,
                ((950 + row) * 1920 + 800) * 4, next.Width * 4);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var log = new TestLog();
        var automation = new AutomationService(adb, new TemplateMatcher(), log, [nextPath], tapToNextCheckDelayMs: 0);
        var result = await new StageMenuEntryService(automation, log, timeoutMs: 100, pollIntervalMs: 10)
            .OpenFromHomeAsync("serial", MainLayout(), settleDelayMs: 0);

        Assert.False(result.Success);
        Assert.Equal(StageNavigationFailure.StageMenuNotRecognized, result.Failure);
        Assert.Contains("请检查主界面是否卡顿", result.Message);
        Assert.Equal(new[] { (1000, 950) }, adb.Taps);
        Assert.Contains(log.Entries, entry => entry.Level == "WARN" && entry.Message == result.Message);
    }

    /// <summary>素材缺失时在入口点击前停止，资源问题不能误报主界面卡顿。</summary>
    [Fact]
    public async Task OpenFromHomeAsync_MissingTemplate_StopsBeforeTap()
    {
        var adb = new FakeAdbService(BlankFrame());
        var log = new TestLog();
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"), "title_stage.png");
        var result = await new StageMenuEntryService(new AutomationService(adb, new TemplateMatcher(), log), log, missing)
            .OpenFromHomeAsync("serial", MainLayout(), settleDelayMs: 0);

        Assert.False(result.Success);
        Assert.Contains("缺少关卡界面标题素材", result.Message);
        Assert.DoesNotContain("卡顿", result.Message);
        Assert.Empty(adb.Taps);
        Assert.Equal(0, adb.ScreenshotCount);
    }

    /// <summary>取消等待应向上传递，不能继续点击或返回伪成功。</summary>
    [Fact]
    public async Task OpenFromHomeAsync_CancelDuringWait_StopsWithoutFurtherInput()
    {
        using var cancel = new CancellationTokenSource();
        var adb = new FakeAdbService(BlankFrame())
        {
            BeforeScreenshotAsync = (_, _) => { cancel.Cancel(); return Task.CompletedTask; },
        };
        var log = new TestLog();
        var service = new StageMenuEntryService(new AutomationService(adb, new TemplateMatcher(), log), log);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.OpenFromHomeAsync("serial", MainLayout(), settleDelayMs: 0, ct: cancel.Token));
        Assert.Equal(new[] { (1000, 950) }, adb.Taps);
    }

    /// <summary>入口未加载时的合成空画面。</summary>
    private static byte[] BlankFrame()
        => ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080);

    /// <summary>提供确定的主界面关卡入口点击位置。</summary>
    private static UiLayout MainLayout() => new()
    {
        Anchors = [new() { Name = "navStage", X = 900, Y = 900, Width = 200, Height = 100 }],
    };
}

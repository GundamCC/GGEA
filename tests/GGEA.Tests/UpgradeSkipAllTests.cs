using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>一次略过以 MuMu 原帧裁剪验证识别、点击顺序和失败停止边界。</summary>
public sealed class UpgradeSkipAllTests
{
    /// <summary>真实亮态命中、暗态拒绝；执行只在公共底部区域命中。</summary>
    [Fact]
    public async Task RealCrops_MatchEnabledButtonsOnly()
    {
        var log = new TestLog();
        var automation = new AutomationService(new FakeAdbService(Screen()), new TemplateMatcher(), log);
        var wait = new WaitOptions { Roi = UpgradeStagesService.SkipAllRoi, Threshold = .90 };
        var match = await automation.MatchImageAsync(Screen((Fixture("skip-all.png"), 1320, 850)),
            Template("stage/skip_all.png"), wait);
        Assert.NotNull(match);
        Assert.InRange(match.CenterX, 1500, 1540);
        Assert.InRange(match.CenterY, 890, 930);
        Assert.Null(await automation.MatchImageAsync(Screen((Fixture("skip-all-disabled.png"), 1320, 850)),
            Template("stage/skip_all.png"), wait));
        Assert.NotNull(await automation.MatchImageAsync(Screen((Fixture("skip-all-disabled.png"), 1320, 850)),
            Template("stage/skip_all_disabled.png"), wait));
        Assert.Null(await automation.MatchImageAsync(Screen((Fixture("skip-all.png"), 1320, 850)),
            Template("stage/skip_all_disabled.png"), wait));
        var executeWait = new WaitOptions { Region = RecognitionRegion.BottomButtons, Threshold = .90 };
        Assert.NotNull(await automation.MatchImageAsync(Screen((Fixture("execute.png"), 0, 880)),
            Template("dialog/execute.png"), executeWait));
        Assert.Null(await automation.MatchImageAsync(Screen((Fixture("execute.png"), 0, 200)),
            Template("dialog/execute.png"), executeWait));
    }

    /// <summary>真实暗态是不可略过证据；无需旧版布局，也不发送任何点击。</summary>
    [Fact]
    public async Task DisabledButton_SkipsWithoutLegacyClicks()
    {
        var adb = new FakeAdbService(Screen((Fixture("title.png"), 0, 0),
            (Fixture("skip-all-disabled.png"), 1320, 850)));
        var result = await Service(adb).ExecuteDailyAsync("serial", new UiLayout(), 0);
        Assert.True(result.Success);
        Assert.Contains("不可用", result.Message);
        Assert.Empty(adb.Taps);
    }

    /// <summary>新分支成功时只点一次略过、执行、结果 OK，不进入任何旧关卡。</summary>
    [Fact]
    public async Task ExecuteDaily_RealFrames_CompletesWithoutLegacyClicks()
    {
        var page = Screen((Fixture("title.png"), 0, 0), (Fixture("skip-all.png"), 1320, 850));
        var adb = new FakeAdbService(page);
        adb.ScreenshotProvider = () => adb.Taps.Count switch
        {
            0 => page,
            1 => Screen((Fixture("execute.png"), 0, 880)),
            2 => Screen((Fixture("result.png"), 820, 30)),
            _ => Screen((Fixture("title.png"), 0, 0)),
        };
        Assert.True((await Service(adb).ExecuteDailyAsync("serial", Layout(), 0)).Success);
        Assert.Equal(3, adb.Taps.Count);
        Assert.InRange(adb.Taps[0].X, 1500, 1540);
        Assert.InRange(adb.Taps[1].Y, 950, 1040);
        Assert.Equal((1676, 988), adb.Taps[2]);
    }

    /// <summary>标题缺失不点击；已点击一次略过而执行缺失时也不能回退旧流程。</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task MissingEvidence_StopsWithoutFallback(bool titlePresent, int expectedTaps)
    {
        var page = titlePresent
            ? Screen((Fixture("title.png"), 0, 0), (Fixture("skip-all.png"), 1320, 850))
            : Screen((Fixture("skip-all.png"), 1320, 850));
        var adb = new FakeAdbService(page);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? page : Screen();
        Assert.False((await Service(adb).ExecuteDailyAsync("serial", Layout(), 0)).Success);
        Assert.Equal(expectedTaps, adb.Taps.Count);
    }

    /// <summary>取消发生于执行确认前，不能发送消费点击。</summary>
    [Fact]
    public async Task CancelledAfterSkipAll_DoesNotExecute()
    {
        var adb = new FakeAdbService(Screen((Fixture("title.png"), 0, 0),
            (Fixture("skip-all.png"), 1320, 850)));
        using var cancel = new CancellationTokenSource();
        adb.BeforeScreenshotAsync = (_, _) => { if (adb.Taps.Count > 0) cancel.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(adb).ExecuteDailyAsync("serial", Layout(), 0, cancel.Token));
        Assert.Single(adb.Taps);
    }

    /// <summary>保留真实结果标题及返回确认，仅缩短测试等待预算。</summary>
    private static UpgradeStagesService Service(FakeAdbService adb)
    {
        var log = new TestLog();
        var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var skip = new StageSkipService(automation, log, new StageSkipOptions
        {
            SkipResultTemplatePath = Template("stage/skip/skip_result.png"),
            PostSkipPageTemplatePath = Template("stage/upgrade_stage.png"),
            SkipResultTimeoutMs = 1000, SettleAfterOkMs = 0,
        });
        return new(automation, skip, log, recognitionTimeoutMs: 1000, pollIntervalMs: 10);
    }

    /// <summary>新分支不依赖旧关卡入口，但复用已确认的结果页布局。</summary>
    private static UiLayout Layout() => new() { Anchors = [
        new() { Name = "skipDialogCancel", X = 770, Y = 850 },
        new() { Name = "skipResultOk", X = 1676, Y = 988 },
        new() { Name = "skipResultTitle", X = 820, Y = 30, Width = 330, Height = 80 }] };

    /// <summary>模板与原帧夹具均从构建输出读取。</summary>
    private static string Template(string file) => Path.Combine(AppContext.BaseDirectory, "resources/common/templates", file);
    private static string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures/Upgrade", file);

    /// <summary>原帧局部放回原坐标，其余像素清空，避免引入无关游戏内容。</summary>
    private static byte[] Screen(params (string File, int X, int Y)[] patches)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        foreach (var patch in patches)
        {
            var image = ImageCodec.DecodePng(File.ReadAllBytes(patch.File));
            for (var row = 0; row < image.Height; row++)
                Array.Copy(image.Bgra, row * image.Width * 4, pixels,
                    ((patch.Y + row) * 1920 + patch.X) * 4, image.Width * 4);
        }
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }
}

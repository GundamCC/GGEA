using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>强化培育的返回标题检查不依赖玩家升级或次数上限。</summary>
public sealed class UpgradePostSkipTests
{
    /// <summary>只接受左上角指定标题，其他位置和主要关卡标题均不能证明返回强化培育。</summary>
    [Theory]
    [InlineData("upgrade_stage.png", 220, 20, true)]
    [InlineData("upgrade_stage.png", 900, 200, false)]
    [InlineData("main_stage.png", 220, 20, false)]
    public async Task ReturnTitle_UsesExpectedTemplateAndRoi(string file, int x, int y, bool expected)
    {
        var adb = new FakeAdbService(Screen(file, x, y));
        var service = Service(adb);
        Assert.Equal(expected, await service.ConfirmPostSkipAsync("serial", new()));
        Assert.Empty(adb.Taps);
    }

    /// <summary>OK 后实际进入专用标题分支，缺失升级文件不影响强化培育略过成功。</summary>
    [Fact]
    public async Task SkipCurrent_AfterOkConfirmsUpgradePageWithoutRankDetection()
    {
        var adb = new FakeAdbService(Screen("skip/skip_result.png", 820, 30));
        adb.EnqueueScreenshots(Screen("upgrade_stage.png", 220, 20));
        var layout = new UiLayout { Anchors = [
            new() { Name = "stageSkip", X = 1674, Y = 710 },
            new() { Name = "skipDialogCancel", X = 770, Y = 850 },
            new() { Name = "skipDialogExecute", X = 1150, Y = 850 },
            new() { Name = "skipResultOk", X = 1676, Y = 988 },
            new() { Name = "skipResultTitle", X = 820, Y = 30, Width = 330, Height = 80 }] };
        Assert.True((await Service(adb).SkipCurrentAsync("serial", layout)).Success);
        Assert.Equal(new[] { (1674, 710), (1150, 850), (1676, 988) }, adb.Taps);
    }

    /// <summary>无升级流程也必须响应取消，不继续确认或点击。</summary>
    [Fact]
    public async Task Cancelled_StopsBeforeScreenshot()
    {
        var adb = new FakeAdbService(Screen("upgrade_stage.png", 220, 20));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(adb).ConfirmPostSkipAsync("serial", new(), cancel.Token));
        Assert.Equal(0, adb.ScreenshotCount);
    }

    /// <summary>故意使用不存在的升级素材，任何意外访问都会使测试失败。</summary>
    private static StageSkipService Service(FakeAdbService adb)
    {
        var log = new TestLog();
        return new(new AutomationService(adb, new TemplateMatcher(), log), log, new()
        {
            PostSkipPageTemplatePath = Template("upgrade_stage.png"),
            PlayerRankUpTemplatePath = "must-not-load-rank.png",
            PlayerRankUpNextTemplatePath = "must-not-load-next.png",
            SkipResultTemplatePath = Template("skip/skip_result.png"),
            SkipResultTimeoutMs = 400, SkipDialogWaitMs = 0, SettleAfterOkMs = 0,
        });
    }

    /// <summary>定位构建分发的实际模板。</summary>
    private static string Template(string file) => Path.Combine(AppContext.BaseDirectory, "resources/common/templates/stage", file);

    /// <summary>将实机标题模板放入指定位置，验证区域边界而非固定匹配坐标。</summary>
    private static byte[] Screen(string file, int x, int y)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        var image = ImageCodec.DecodePng(File.ReadAllBytes(Template(file)));
        for (var row = 0; row < image.Height; row++)
            Array.Copy(image.Bgra, row * image.Width * 4, pixels, ((y + row) * 1920 + x) * 4, image.Width * 4);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }
}

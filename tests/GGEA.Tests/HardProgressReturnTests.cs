using System.IO;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

public sealed partial class AutoProgressFlowTests
{
    /// <summary>当前功能解放原帧应在正常推图轮询中被点击继续；随后仍须真实故事和返回页才能计为完成。</summary>
    [Fact]
    public async Task FeatureUnlock_ProgressFlow_ContinuesWithoutStallRecovery()
    {
        using var scene = new Scene();
        scene.Steps(ReturnFixture("feature-unlock-tap-mumu5558.png", 0, 780),
            scene.Frame(("story", 1500, 850)), scene.Frame(("title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(2, scene.Adb.Taps.Count);
        Assert.InRange(scene.Adb.Taps[0].Y, 840, 880);
        Assert.Equal(scene.Center("story", 1500, 850), scene.Adb.Taps[1]);
        Assert.DoesNotContain(scene.Log.Entries, entry => entry.Message.Contains("连续 2 分钟"));
    }

    /// <summary>HARD 结算返回阶段也使用推图 300 像素区域，继续提示不能被误计为已返回选关。</summary>
    [Fact]
    public async Task FeatureUnlock_HardReturn_ContinuesThenConfirmsTitle()
    {
        using var scene = new Scene();
        scene.Steps(ReturnFixture("feature-unlock-tap-mumu5558.png", 0, 780),
            ReturnFixture("select-stage-z-mumu5558.png"));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.ConfirmHardReturn(new ManualProgressTime()));
        var tap = Assert.Single(scene.Adb.Taps);
        Assert.InRange(tap.Y, 840, 880);
        Assert.Equal(2, scene.Adb.ScreenshotCount);
    }

    /// <summary>回放实际失败帧的页首；12 秒后出现选关标题仍应接续，不受原入口 10 秒等待影响。</summary>
    [Fact]
    public async Task HardReturn_TransitionThenRealTitle_ConfirmsBeforeContinuing()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        var transition = ReturnFixture("hard-return-transition-mumu5558.png");
        var ready = ReturnFixture("select-stage-z-mumu5558.png");
        scene.Adb.BeforeScreenshotAsync = (_, _) => { clock.Advance(6); return Task.CompletedTask; };
        scene.Adb.ScreenshotProvider = () => scene.Adb.ScreenshotCount <= 2 ? transition : ready;
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.ConfirmHardReturn(clock));
        Assert.Equal(3, scene.Adb.ScreenshotCount);
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>持续过渡必须有限停止；返回点击不能把不存在的选关页或下一关计为成功。</summary>
    [Fact]
    public async Task HardReturn_MissingTitle_TimesOutWithoutClicking()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Adb.ScreenshotProvider = () => ReturnFixture("hard-return-transition-mumu5558.png");
        scene.Adb.BeforeScreenshotAsync = (_, _) => { clock.Advance(30); return Task.CompletedTask; };
        Assert.Equal(SortieFlowResult.ProgressTimeout, await scene.ConfirmHardReturn(clock));
        Assert.Equal(2, scene.Adb.ScreenshotCount);
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>新等待中的用户取消必须传播；终止结果不能因画面已是选关页而被覆盖。</summary>
    [Fact]
    public async Task HardReturn_CancellationAndFailure_DoNotBecomeCompletion()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        Assert.Equal(SortieFlowResult.FailureDetected,
            await scene.ConfirmHardReturn(clock, result: SortieFlowResult.FailureDetected));
        Assert.Equal(0, scene.Adb.ScreenshotCount);
        using var cancellation = new CancellationTokenSource();
        scene.Adb.ScreenshotProvider = () => { cancellation.Cancel(); return ReturnFixture("select-stage-z-mumu5558.png"); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.ConfirmHardReturn(clock, cancellation.Token));
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>返回阶段仍拒绝不支持的截图规格，不能因标题可见而继续后续固定坐标识别。</summary>
    [Fact]
    public async Task HardReturn_UnsupportedResolution_StopsBeforeRecognition()
    {
        using var scene = new Scene();
        scene.Adb.ScreenshotProvider = () => ImageCodec.EncodePng(TestImageFactory.CreateBgra(1280, 720, 0), 1280, 720);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scene.ConfirmHardReturn(new ManualProgressTime()));
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>实机原像素的出击准备必须通过文字及外框复核，不误认成出击或观看。</summary>
    [Fact]
    public void HardReturn_RealPreparation_StillRecognizesCorrectEntry()
    {
        var screen = ImageCodec.DecodePng(ReturnFixture("preparation-z-mumu5558.png", 1470, 754));
        var buttons = new StageEntryButtonRecognizer();
        var area = new Roi(1470, 754, 450, 326);
        string PathFor(string name) => ConfigService.ResolvePath($"resources/templates/stage/sortie/{name}.png");
        var entry = buttons.Match(screen, PathFor("button_sortie_preparations"), area, .90, default);
        Assert.NotNull(entry);
        Assert.InRange(entry.CenterX, 1670, 1685);
        Assert.InRange(entry.CenterY, 885, 900);
        Assert.Null(buttons.Match(screen, PathFor("button_sortie"), area, .90, default));
        Assert.Null(buttons.Match(screen, PathFor("watch"), area, .90, default));
    }

    /// <summary>只恢复实机裁剪的原坐标，其余区域补黑，不生成或改写游戏字形。</summary>
    private static byte[] ReturnFixture(string name, int x = 0, int y = 0)
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Sortie", name)));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        // 完整返回页由真实标题和真实入口裁剪组成；仅页首不能代表内容已经加载。
        if (name == "select-stage-z-mumu5558.png")
        {
            var entry = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures/Sortie", "preparation-z-mumu5558.png")));
            for (var row = 0; row < entry.Height; row++)
                Buffer.BlockCopy(entry.Bgra, row * entry.Width * 4, pixels, ((754 + row) * 1920 + 1470) * 4, entry.Width * 4);
        }
        for (var row = 0; row < crop.Height; row++)
            Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, pixels, ((y + row) * 1920 + x) * 4, crop.Width * 4);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }

}

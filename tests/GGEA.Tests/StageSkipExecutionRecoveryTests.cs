using System.IO;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

public partial class StageSkipServiceTests
{
    /// <summary>浅色数字的局部改变也必须撤销恢复资格，不能让整块相似度将其稀释。</summary>
    [Fact]
    public void ExecutionRecovery_SmallLowContrastApChange_RejectsIdentity()
    {
        var recognizer = new SkipExecutionDialogRecognizer();
        var original = recognizer.Recognize(ImageCodec.DecodePng(ExecutionDialogFrame()), default)!;
        var changed = ImageCodec.DecodePng(ExecutionDialogFrame());
        for (var y = 602; y < 622; y++)
        for (var x = 1015; x < 1027; x++)
        for (var c = 0; c < 3; c++)
        {
            var i = (y * 1920 + x) * 4 + c;
            changed.Bgra[i] = (byte)(changed.Bgra[i] + 16);
        }
        Assert.True(recognizer.IsSameSelection(original, original, default));
        var candidate = Assert.IsType<SkipExecutionDialog>(recognizer.Recognize(changed, default));
        Assert.False(recognizer.IsSameSelection(original, candidate, default));
    }

    /// <summary>恢复复核恰遇迟到结果时，不使用旧执行坐标；下一轮按结果页正常收尾。</summary>
    [Fact]
    public async Task ExecutionRecovery_ResultArrivesDuringConfirmation_DoesNotRetry()
    {
        var dialog = ExecutionDialogFrame();
        var resultFrame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Sortie/skip-result-narrative-mumu5556.png"));
        var adb = new FakeAdbService(dialog);
        var log = new TestLog();
        var recoveryFrames = 0;
        adb.ScreenshotProvider = () => log.Entries.Any(x => x.Message.Contains("开始迟到结果恢复")) && ++recoveryFrames >= 2
            ? resultFrame : dialog;
        var result = await CreateService(adb, log, ExecutionRecoveryOptions()).SkipCurrentAsync("serial", CreateLayout());
        Assert.True(result.Success);
        Assert.Equal(3, adb.Taps.Count);
        Assert.Equal((1676, 988), adb.Taps[2]);
        Assert.DoesNotContain(log.Entries, x => x.Message.Contains("补点执行一次"));
    }

    /// <summary>第一次执行丢失后按同一弹窗补点；成功才点结果 OK，仍未生效则取消并失败。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecutionRecovery_UnchangedDialog_RetriesOnceThenCompletesOrCancels(bool succeeds)
    {
        var dialog = ExecutionDialogFrame();
        var resultFrame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Sortie/skip-result-narrative-mumu5556.png"));
        var adb = new FakeAdbService(dialog);
        adb.ScreenshotProvider = () => succeeds && adb.Taps.Count >= 3 ? resultFrame : dialog;
        var log = new TestLog();
        var service = CreateService(adb, log, ExecutionRecoveryOptions());
        var result = await service.SkipCurrentAsync("serial", CreateLayout());
        Assert.Equal(succeeds, result.Success);
        Assert.Equal(4, adb.Taps.Count); // 略过、首次执行、一次补点、OK 或取消。
        Assert.Equal(adb.Taps[1], adb.Taps[2]);
        Assert.InRange(adb.Taps[1].Item1, 1100, 1200);
        if (succeeds) Assert.Equal((1676, 988), adb.Taps[3]);
        else
        {
            Assert.InRange(adb.Taps[3].Item1, 700, 800);
            Assert.InRange(adb.Taps[3].Item2, 800, 900);
        }
        Assert.Single(log.Entries, x => x.Message.Contains("补点执行一次"));
        if (!succeeds) Assert.Contains("补点一次后仍未生效", result.Message);
    }

    /// <summary>次数或 AP 变化后即使仍有执行按钮，也不能沿用原消费授权。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ExecutionRecovery_ChangedIdentity_NeverRetries(int changedRegion)
    {
        var original = ExecutionDialogFrame();
        var changed = ExecutionDialogFrame(changedRegion);
        var adb = new FakeAdbService(original);
        adb.ScreenshotProvider = () => adb.Taps.Count < 2 ? original : changed;
        var result = await CreateService(adb, new TestLog(), ExecutionRecoveryOptions())
            .SkipCurrentAsync("serial", CreateLayout());
        Assert.False(result.Success);
        Assert.Equal(2, adb.Taps.Count);
    }

    /// <summary>恢复复核期间用户取消不能补点，也不能替用户点击弹窗取消。</summary>
    [Fact]
    public async Task ExecutionRecovery_CancelDuringFreshConfirmation_NoRetry()
    {
        var dialog = ExecutionDialogFrame();
        var adb = new FakeAdbService(dialog);
        var log = new TestLog();
        using var cts = new CancellationTokenSource();
        var recoveryFrames = 0;
        adb.ScreenshotProvider = () =>
        {
            if (log.Entries.Any(x => x.Message.Contains("开始迟到结果恢复")) && ++recoveryFrames == 2) cts.Cancel();
            return dialog;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService(adb, log, ExecutionRecoveryOptions())
            .SkipCurrentAsync("serial", CreateLayout(), cts.Token));
        Assert.Equal(2, adb.Taps.Count);
    }

    /// <summary>已经离开原弹窗后再次出现相同弹窗，也不能重新消费；未知帧只等待结果。</summary>
    [Fact]
    public async Task ExecutionRecovery_LeftDialogThenReturned_DoesNotReexecute()
    {
        var dialog = ExecutionDialogFrame();
        var unknown = CreateBlankPng();
        var adb = new FakeAdbService(dialog);
        var observedAfterExecution = 0;
        adb.ScreenshotProvider = () => adb.Taps.Count == 2 && ++observedAfterExecution == 1 ? unknown : dialog;
        Assert.False((await CreateService(adb, new TestLog(), ExecutionRecoveryOptions()).SkipCurrentAsync("serial", CreateLayout())).Success);
        Assert.Equal(2, adb.Taps.Count);
    }

    private static StageSkipOptions ExecutionRecoveryOptions() => new()
    {
        RecoverUnchangedExecutionDialog = true,
        SkipResultTemplatePath = ConfigService.ResolvePath("resources/templates/stage/skip/skip_result.png"),
        SkipDialogWaitMs = 0, SkipResultTimeoutMs = 1500, LateResultRecoveryTimeoutMs = 1500, SettleAfterOkMs = 0,
    };

    /// <summary>合成帧使用真实按钮与独立纹理模拟各块身份，不包含用户截图或私人数据。</summary>
    private static byte[] ExecutionDialogFrame(int changedRegion = -1)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 200);
        foreach (var (name, x, y) in new[] { ("execute", 970, 790), ("cancel", 590, 790) })
        {
            var part = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath($"resources/templates/dialog/{name}.png")));
            for (var row = 0; row < part.Height; row++)
                Buffer.BlockCopy(part.Bgra, row * part.Width * 4, pixels, ((y + row) * 1920 + x) * 4, part.Width * 4);
        }
        var regions = new[] { new Roi(850, 210, 220, 60), new Roi(900, 440, 170, 70), new Roi(940, 590, 120, 55), new Roi(1330, 590, 120, 55) };
        for (var index = 0; index < regions.Length; index++)
        {
            var region = regions[index];
            var random = new Random(1234 + index + (changedRegion == index ? 100 : 0));
            for (var y = region.Y; y < region.Y + region.Height; y++)
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var i = (y * 1920 + x) * 4;
                var value = (byte)random.Next(30, 220);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
            }
        }
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }
}

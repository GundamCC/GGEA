using System.IO;
using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>使用实机弹窗确认双证据门禁；半张弹窗与缺失素材不能授权返回主页。</summary>
public sealed class DateUpdateDialogRecognizerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ggea-date-update-" + Guid.NewGuid().ToString("N"));

    /// <summary>复制到资源树外，隔离全局语言解析，分别验证简中和日文实机素材。</summary>
    public DateUpdateDialogRecognizerTests()
    {
        Directory.CreateDirectory(_directory);
        foreach (var language in new[] { "ja", "zh-CN" })
        {
            Directory.CreateDirectory(Path.Combine(_directory, language));
            foreach (var name in new[] { "date_update_title.png", "date_update_home.png" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, $"resources/{language}/templates/common", name), Path.Combine(_directory, language, name));
        }
    }

    /// <summary>仅删除本用例拥有的独立临时素材。</summary>
    public void Dispose() => Directory.Delete(_directory, true);

    /// <summary>日期更新走异常诊断链路：正常取帧不扫描，失败后新截图或停滞帧检查均能中断恢复。</summary>
    [Theory]
    [InlineData(false, "ja")]
    [InlineData(true, "ja")]
    [InlineData(false, "zh-CN")]
    [InlineData(true, "zh-CN")]
    public async Task ActualDialog_InterruptsWithoutRecovery(bool afterFailure, string language)
    {
        var popup = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            $"Fixtures/Sortie/{(language == "ja" ? "ja" : "zh")}-date-update.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var row = 0; row < popup.Height; row++)
            Buffer.BlockCopy(popup.Bgra, row * popup.Width * 4, pixels,
                ((row + 180) * 1920 + 420) * 4, popup.Width * 4);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var matcher = new TemplateMatcher();
        var log = new TestLog();
        var detector = new ConnectionErrorDetector(adb, matcher, log,
            Path.Combine(_directory, "missing.png"), dateUpdate: Create(language));
        using var automation = new AutomationService(adb, matcher, log, connectionErrorDetector: detector);
        var normal = await automation.CaptureScreenAsync("serial");
        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("检测到日期更新弹窗"));
        if (afterFailure)
            Assert.True(await detector.DetectAfterFailureAsync("serial", settleDelayMs: 0));
        else
            await Assert.ThrowsAsync<ConnectionErrorException>(() => automation.GlobalScreens.CheckAbnormalScreenAsync(normal));
        Assert.Equal(afterFailure ? 2 : 1, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
        Assert.Empty(adb.Swipes);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("检测到日期更新弹窗"));
    }
    /// <summary>将原始弹窗置于实际屏幕中心，验证按钮位置和标题/按钮缺失时的拒绝行为。</summary>
    [Theory]
    [InlineData(true, true, "ja")]
    [InlineData(false, true, "ja")]
    [InlineData(true, false, "ja")]
    [InlineData(true, true, "zh-CN")]
    [InlineData(false, true, "zh-CN")]
    [InlineData(true, false, "zh-CN")]
    public void ActualDialog_RequiresTitleAndHome(bool title, bool home, string language)
    {
        var popup = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            $"Fixtures/Sortie/{(language == "ja" ? "ja" : "zh")}-date-update.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var row = 0; row < popup.Height; row++)
        {
            if ((!title && row < 120) || (!home && row > 600)) continue;
            Buffer.BlockCopy(popup.Bgra, row * popup.Width * 4, pixels,
                ((row + 180) * 1920 + 420) * 4, popup.Width * 4);
        }
        var recognizer = Create(language);
        var result = recognizer.Recognize(ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080)));
        if (!title || !home) Assert.Null(result);
        else
        {
            Assert.NotNull(result);
            Assert.InRange(result.CenterX, 940, 980);
            Assert.InRange(result.CenterY, 830, 890);
        }
    }

    /// <summary>未采集的语言素材不触发点击，取消在任何识别之前生效。</summary>
    [Fact]
    public void MissingTemplatesAndCancellation_FailClosed()
    {
        var screen = ImageCodec.DecodePng(TestImageFactory.CreateStageMenuPng());
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        Assert.Null(new DateUpdateDialogRecognizer(missing, missing).Recognize(screen));
        Assert.Throws<OperationCanceledException>(() => Create().Recognize(screen, new CancellationToken(true)));
    }

    /// <summary>独立物理路径避免其他并行用例切换当前语言。</summary>
    private DateUpdateDialogRecognizer Create(string language = "ja") => new(
        Path.Combine(_directory, language, "date_update_title.png"), Path.Combine(_directory, language, "date_update_home.png"));
}

using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>验证任务失败后的网络错误识别只接受顶部居中的指定区域。</summary>
public class ConnectionErrorDetectorTests
{
    /// <summary>数据更新与网络错误一样只在异常入口扫描；正常取帧不隐式中断，诊断本身不点击。</summary>
    [Theory]
    [InlineData("ja", false)]
    [InlineData("ja", true)]
    [InlineData("zh-CN", false)]
    [InlineData("zh-CN", true)]
    public async Task DataUpdate_AbnormalDiagnosticsStopWithoutActions(string language, bool nextRound)
    {
        using var temp = new TempDirectory();
        var title = Path.Combine(temp.Path, "data_update_title.png");
        var message = Path.Combine(temp.Path, "data_update_message.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, $"resources/{language}/templates/{(language == "zh-CN" ? "dialog" : "entry")}/data_update_title.png"), title);
        File.Copy(Path.Combine(AppContext.BaseDirectory, $"resources/{language}/templates/entry/data_update_message.png"), message);
        var adb = new FakeAdbService(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            $"Fixtures/Navigation/{(language == "ja" ? "ja" : "zh")}-data-update.png")));
        var matcher = new TemplateMatcher();
        var log = new TestLog();
        var detector = new ConnectionErrorDetector(adb, matcher, log, Path.Combine(temp.Path, "missing.png"), title, message);
        var automation = new AutomationService(adb, matcher, log, connectionErrorDetector: detector);
        var screen = await automation.CaptureScreenAsync("serial");
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("检测到游戏数据更新弹窗"));
        if (nextRound) Assert.True(await detector.DetectAfterFailureAsync("serial", settleDelayMs: 0));
        else await Assert.ThrowsAsync<ConnectionErrorException>(() => automation.GlobalScreens.CheckAbnormalScreenAsync(screen));
        Assert.Equal(nextRound ? 2 : 1, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
        Assert.Empty(adb.Swipes);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("检测到游戏数据更新弹窗"));
    }

    /// <summary>仅有更新标题或仅有正文时不得拦截普通页面；缺少任一同帧证据都拒绝判定。</summary>
    [Theory]
    [InlineData("ja", true)]
    [InlineData("ja", false)]
    [InlineData("zh-CN", true)]
    [InlineData("zh-CN", false)]
    public void DataUpdate_RequiresTitleAndMessage(string language, bool titleOnly)
    {
        using var temp = new TempDirectory();
        var title = Path.Combine(temp.Path, "title.png");
        var message = Path.Combine(temp.Path, "message.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, $"resources/{language}/templates/{(language == "zh-CN" ? "dialog" : "entry")}/data_update_title.png"), title);
        File.Copy(Path.Combine(AppContext.BaseDirectory, $"resources/{language}/templates/entry/data_update_message.png"), message);
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, $"Fixtures/Navigation/{(language == "ja" ? "ja" : "zh")}-data-update.png"));
        var screen = ImageCodec.DecodePng(png);
        // 用原弹窗底色覆盖文字，检验大面积相似底色不会成为有效证据。
        TestImageFactory.PasteBlock(screen.Bgra, 1920, titleOnly ? 450 : 100, titleOnly ? 90 : 55,
            titleOnly ? 735 : 910, titleOnly ? 480 : 210, titleOnly ? (byte)196 : (byte)178);
        var detector = new ConnectionErrorDetector(new FakeAdbService([]), new TemplateMatcher(), new TestLog(),
            Path.Combine(temp.Path, "missing.png"), title, message);
        Assert.False(detector.IsVisible(screen));
    }

    /// <summary>回放日文连接错误原帧，完整长标题仍以原90%阈值识别，禁止点击返回按钮。</summary>
    [Fact]
    public async Task JapaneseActualDialog_IsRecognizedWithoutClick()
    {
        using var temp = new TempDirectory();
        var template = Path.Combine(temp.Path, "ja.png");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "resources/ja/templates/entry/connection_error.png"), template);
        var adb = new FakeAdbService(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/ja-connection-error.png")));
        var detector = new ConnectionErrorDetector(adb, new TemplateMatcher(), new TestLog(), template);
        Assert.True(await detector.IsVisibleAsync("replay"));
        Assert.Empty(adb.Taps);
    }

    /// <summary>正常画面仍可完成识图点击；用户预先取消时不能截图或误报连接错误。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automation_NormalOrCancelled_PreservesBehavior(bool cancelled)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "connection_error.png");
        File.WriteAllBytes(path, CreateTemplatePng());
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTemplate(screen, 860, 500); // 错误搜索区域以外的相同图案可作为普通业务目标。
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var log = new TestLog();
        var matcher = new TemplateMatcher();
        var detector = new ConnectionErrorDetector(adb, matcher, log, path);
        var automation = new AutomationService(adb, matcher, log, connectionErrorDetector: detector);
        using var cts = new CancellationTokenSource();
        if (cancelled) cts.Cancel();
        var options = new WaitOptions { TimeoutMs = 1000, Roi = new Roi(825, 450, 270, 200) };
        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                automation.ClickImageAsync("serial", path, options, cts.Token));
            Assert.Equal(0, adb.ScreenshotCount);
            Assert.Empty(adb.Taps);
        }
        else
        {
            Assert.True(await automation.ClickImageAsync("serial", path, options, cts.Token));
            Assert.Single(adb.Taps);
        }
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("检测到网络错误弹窗"));
    }

    /// <summary>常规原语只识别本次目标；网络错误扫描由异常入口触发，不能偷偷回到逐帧检查。</summary>
    [Theory]
    [InlineData("wait")]
    [InlineData("overlay")]
    [InlineData("disappear")]
    [InlineData("click")]
    [InlineData("screenshot")]
    public async Task Automation_ConnectionErrorIsCheckedOnlyAfterAbnormalFlow(string operation)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "connection_error.png");
        File.WriteAllBytes(path, CreateTemplatePng());
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTemplate(screen, 860, 205);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var log = new TestLog();
        var matcher = new TemplateMatcher();
        var detector = new ConnectionErrorDetector(adb, matcher, log, path);
        var automation = new AutomationService(adb, matcher, log, [path], 0, detector);
        var target = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(target, ImageCodec.EncodePng(TestImageFactory.CreateBgra(40, 40, 110), 40, 40));
        var options = new WaitOptions { TimeoutMs = 1, PollIntervalMs = 1, Threshold = .99, AllowGlobalContinuation = false };

        switch (operation)
        {
            case "wait": Assert.Null(await automation.WaitForImageAsync("serial", target, options)); break;
            case "overlay": await automation.WaitForImageOrTapToNextAsync("serial", target, options); break;
            // 把仍在画面上的标题当作普通等待目标：不启动异常扫描，也不能误报已消失。
            case "disappear": Assert.False(await automation.WaitImageDisappearAsync("serial", path, options)); break;
            case "click": Assert.False(await automation.ClickIfExistsAsync("serial", target, options)); break;
            default: await automation.ScreenshotAsync("serial"); break;
        }
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("检测到网络错误弹窗"));
        await Assert.ThrowsAsync<ConnectionErrorException>(() => automation.GlobalScreens.CheckAbnormalScreenAsync(ImageCodec.DecodePng(ImageCodec.EncodePng(screen, 1920, 1080))));
        Assert.Empty(adb.Taps);
        Assert.Empty(adb.Swipes);
        Assert.Single(log.Entries, e => e.Message.Contains("检测到网络错误弹窗"));
    }

    /// <summary>后续轮次目标等待失败后才诊断网络弹窗；异常诊断不点击弹窗或把失败改报成功。</summary>
    [Fact]
    public async Task NextRound_ConnectionErrorIsDiagnosedAfterTimeout()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "connection_error.png");
        File.WriteAllBytes(path, CreateTemplatePng());
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        PasteTemplate(screen, 860, 205);
        adb.EnqueueScreenshots(ImageCodec.EncodePng(screen, 1920, 1080));
        var matcher = new TemplateMatcher();
        var log = new TestLog();
        var detector = new ConnectionErrorDetector(adb, matcher, log, path);
        var automation = new AutomationService(adb, matcher, log, connectionErrorDetector: detector);
        var target = Path.Combine(temp.Path, "target.png");
        File.WriteAllBytes(target, ImageCodec.EncodePng(TestImageFactory.CreateBgra(40, 40, 110), 40, 40));
        var download = new DownloadDialogService(automation, matcher, log, target, target, 0.9);
        var sortie = new SortieFlowService(automation, matcher, log, download,
            target, target, target, target, target, target, target, target, target, target, target, target, target, target, target, 0.9);

        Assert.Equal(SortieFlowResult.TapToNextTimeout, await sortie.ExecuteNextRoundAsync(
            "serial", moreRoundsRemaining: true, tapToNextWaitTimeoutMs: 10, tapToNextPollIntervalMs: 1));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("检测到网络错误弹窗"));
        Assert.True(await detector.DetectAfterFailureAsync("serial", settleDelayMs: 0));
        Assert.Empty(adb.Taps);
    }

    /// <summary>网络错误必须使用严格阈值，排除日志中 84.2% 的相似标题误命中。</summary>
    [Fact]
    public void MatchThreshold_RejectsKnownFalsePositiveConfidence()
    {
        Assert.Equal(0.90, ConnectionErrorDetector.MatchThreshold);
        Assert.True(ConnectionErrorDetector.MatchThreshold > 0.842);
    }

    /// <summary>覆盖原顶部位置及 ADB 实测弹窗标题位置，防止搜索范围遗漏真实标题。</summary>
    [Theory]
    [InlineData(50)]
    [InlineData(205)]
    public async Task DetectAfterFailureAsync_TemplateInsideTopCenter_ReturnsTrue(int y)
    {
        using var temp = new TempDirectory();
        var templatePath = Path.Combine(temp.Path, "connection_error.png");
        File.WriteAllBytes(templatePath, CreateTemplatePng());

        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTemplate(screen, x: 860, y: y);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var detector = new ConnectionErrorDetector(adb, new TemplateMatcher(), new TestLog(), templatePath);

        var visible = await detector.DetectAfterFailureAsync("serial", settleDelayMs: 0);

        Assert.True(visible);
        Assert.Equal(1, adb.ScreenshotCount);
        Assert.Equal(new Roi(750, 0, 420, 350), ConnectionErrorDetector.CreateSearchRoi(1920, 1080));
    }

    /// <summary>扩大高度后仍排除水平范围以外及搜索区域下方的相似标题。</summary>
    [Theory]
    [InlineData(300, 50)]
    [InlineData(860, 350)]
    public async Task IsVisibleAsync_TemplateOutsideTopCenter_ReturnsFalse(int x, int y)
    {
        using var temp = new TempDirectory();
        var templatePath = Path.Combine(temp.Path, "connection_error.png");
        File.WriteAllBytes(templatePath, CreateTemplatePng());

        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTemplate(screen, x: x, y: y);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var detector = new ConnectionErrorDetector(adb, new TemplateMatcher(), new TestLog(), templatePath);

        Assert.False(await detector.IsVisibleAsync("serial"));
    }

    /// <summary>使用三段灰度生成非恒定模板，保证金字塔匹配结果具有唯一性。</summary>
    private static byte[] CreateTemplatePng()
    {
        var template = TestImageFactory.CreateBgra(200, 70, 80);
        TestImageFactory.PasteBlock(template, 200, 70, 70, 0, 0, 80);
        TestImageFactory.PasteBlock(template, 200, 70, 70, 70, 0, 160);
        TestImageFactory.PasteBlock(template, 200, 60, 70, 140, 0, 240);
        return ImageCodec.EncodePng(template, 200, 70);
    }

    /// <summary>把测试模板贴入指定屏幕位置。</summary>
    private static void PasteTemplate(byte[] screen, int x, int y)
    {
        TestImageFactory.PasteBlock(screen, 1920, 70, 70, x, y, 80);
        TestImageFactory.PasteBlock(screen, 1920, 70, 70, x + 70, y, 160);
        TestImageFactory.PasteBlock(screen, 1920, 60, 70, x + 140, y, 240);
    }

    /// <summary>为模板文件提供隔离目录，测试完成后尽力清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 临时文件清理失败不改变识别测试结论。
            }
        }
    }
}

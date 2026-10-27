using System.IO;
using GGEA.App.Vision;
using GGEA.App.Automation;

namespace GGEA.Tests;

/// <summary>主界面检测器测试：区分按钮状态、确认连续帧，并限定返回按钮的搜索范围。</summary>
public class MainScreenDetectorTests
{
    /// <summary>诊断需要低分候选时仍必须按业务阈值复查大 ROI，不能用零阈值绕过精确目标。</summary>
    [Fact]
    public async Task DetectButton_LargeRoi_UsesThresholdVerificationForCandidates()
    {
        using var temp = new TempDirectory();
        var templates = CreateTemplates(temp.Path);
        var template = TestImageFactory.CreateBgra(48, 48, 0);
        for (var y = 0; y < 48; y++)
            for (var x = 0; x < 48; x++)
                for (var c = 0; c < 3; c++) template[(y * 48 + x) * 4 + c] = (byte)(((x + y) % 2) * 255);
        File.WriteAllBytes(templates.Activated, ImageCodec.EncodePng(template, 48, 48));
        var screen = TestImageFactory.CreateBgra(320, 120, 20);
        TestImageFactory.PasteBlock(screen, 320, 48, 48, 0, 0, 127);
        for (var y = 0; y < 48; y++)
            Array.Copy(template, y * 48 * 4, screen, ((y + 9) * 320 + 101) * 4, 48 * 4);
        var detector = new MainScreenDetector(new FakeAdbService(ImageCodec.EncodePng(screen, 320, 120)),
            new TemplateMatcher(), new TestLog(), new MainScreenDetectorOptions(templates.Activated,
                templates.Inactive, templates.Back, new Roi(0, 0, 320, 120), new Roi(0, 0, 100, 80), .99, 2, 0));
        var result = await detector.DetectButtonAsync("serial");
        Assert.Equal(MainScreenButtonState.Activated, result.State);
        Assert.Equal((101, 9), (result.Match!.X, result.Match.Y));
    }

    /// <summary>目标在其它页面本来就不存在；单帧探测不能自行宣告流程失败或消耗取证配额。</summary>
    [Fact]
    public async Task Probes_NotFound_DoNotWriteFailureEvidence()
    {
        using var temp = new TempDirectory();
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(400, 300, 20), 400, 300));
        var log = new TestLog();
        var directory = Path.Combine(temp.Path, "evidence");
        var detector = CreateDetector(adb, CreateTemplates(temp.Path), log);
        Assert.Equal(MainScreenButtonState.NotFound, (await detector.DetectButtonAsync("serial")).State);
        Assert.False(await detector.IsMainScreenAsync("serial"));
        Assert.Null(await detector.FindBackButtonAsync("serial"));
        Assert.False(Directory.Exists(directory));
        Assert.Empty(log.Entries);
    }

    /// <summary>点击特效未命中后仍须连续两帧确认；恢复成功不保存误报，也不占用后续真实故障的取证配额。</summary>
    [Fact]
    public async Task WaitForMainScreenAsync_TransientEffect_DiscardsEvidenceAndStillConfirmsConsecutiveFrames()
    {
        using var temp = new TempDirectory();
        var templates = CreateTemplates(temp.Path);
        var effect = ImageCodec.EncodePng(TestImageFactory.CreateBgra(400, 300, 20), 400, 300);
        var screen = TestImageFactory.CreateBgra(400, 300, 20);
        TestImageFactory.PasteBlock(screen, 400, 40, 30, 20, 20, 200);
        var activated = ImageCodec.EncodePng(screen, 400, 300);
        var adb = new FakeAdbService(effect);
        adb.EnqueueScreenshots(activated, effect, activated, activated);
        var log = new TestLog();
        var directory = Path.Combine(temp.Path, "evidence");
        var detector = CreateNavigator(adb, templates, log, directory);

        Assert.True(await detector.WaitForMainScreenAsync("serial", 3000, 1));
        Assert.Equal(5, adb.ScreenshotCount);
        Assert.False(Directory.Exists(directory));
        Assert.DoesNotContain(log.Entries, entry => entry.Level is "WARN" or "ERROR");

        adb.ScreenshotProvider = () => effect;
        // 全量测试负载下首帧 PNG 解码可能超过 100 毫秒；确保预算足以取得真实失败帧。
        Assert.False(await detector.WaitForMainScreenAsync("serial", 3000, 1));
        Assert.Single(Directory.GetFiles(directory));
    }

    /// <summary>超时只保存最后的真实失败帧，取证不再额外截图；取消和截图异常不提交过渡帧。</summary>
    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("capture-error")]
    public async Task WaitForMainScreenAsync_EvidenceOnlyOnTimeout(string outcome)
    {
        using var temp = new TempDirectory();
        using var cts = new CancellationTokenSource();
        var templates = CreateTemplates(temp.Path);
        var first = ImageCodec.EncodePng(TestImageFactory.CreateBgra(400, 300, 20), 400, 300);
        var last = ImageCodec.EncodePng(TestImageFactory.CreateBgra(400, 300, 30), 400, 300);
        var adb = new FakeAdbService(first);
        adb.EnqueueScreenshots(last);
        adb.BeforeScreenshotAsync = (count, ct) =>
        {
            if (count == 2 && outcome == "cancel") { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
            if (count >= 2 && outcome == "capture-error") throw new GGEA.App.Device.AdbException("测试截图失败");
            return Task.CompletedTask;
        };
        var log = new TestLog();
        var directory = Path.Combine(temp.Path, "evidence");
        var detector = CreateNavigator(adb, templates, log, directory);
        if (outcome == "timeout")
        {
            Assert.False(await detector.WaitForMainScreenAsync("serial", 1000, 1, cts.Token));
            Assert.True(adb.ScreenshotCount >= 2);
            using var zip = System.IO.Compression.ZipFile.OpenRead(Assert.Single(Directory.GetFiles(directory)));
            using var frame = new MemoryStream();
            using (var stream = zip.GetEntry("frame.png")!.Open()) stream.CopyTo(frame);
            Assert.Equal(last, frame.ToArray());
            Assert.Single(log.Entries, entry => entry.Message.Contains("识图失败证据已保存"));
        }
        else
        {
            if (outcome == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detector.WaitForMainScreenAsync("serial", 3000, 1, cts.Token));
            else
                await Assert.ThrowsAsync<ScreenshotCaptureException>(() => detector.WaitForMainScreenAsync("serial", 3000, 1, cts.Token));
            Assert.False(Directory.Exists(directory));
            Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("识图失败证据") || entry.Message.Contains("等待主界面超时"));
        }
    }

    /// <summary>为过渡帧回归场景启用真实本地证据写入，与产品共享同一检测和取证链路。</summary>
    private static MainScreenNavigator CreateNavigator(FakeAdbService adb, Templates templates, TestLog log, string? directory = null)
        => new(new MainScreenDetector(adb, new TemplateMatcher(), log,
            new MainScreenDetectorOptions(templates.Activated, templates.Inactive, templates.Back,
                new Roi(0, 0, 80, 80), new Roi(0, 0, 100, 80), 0.99, 2, 0)), adb, log,
            directory is null ? null : new RecognitionEvidence(directory, log));
    [Fact]
    public async Task DetectButtonAsync_ActivatedTemplateFound_ReturnsActivated()
    {
        var screen = TestImageFactory.CreateBgra(400, 300, 20);
        TestImageFactory.PasteBlock(screen, 400, 40, 30, 20, 20, 200);

        using var temp = new TempDirectory();
        var templates = CreateTemplates(temp.Path);
        var detector = CreateDetector(ImageCodec.EncodePng(screen, 400, 300), templates);

        var detection = await detector.DetectButtonAsync("serial");

        Assert.Equal(MainScreenButtonState.Activated, detection.State);
        Assert.NotNull(detection.Match);
        Assert.Equal(20, detection.Match!.X);
        Assert.Equal(20, detection.Match.Y);
    }

    [Fact]
    public async Task DetectButtonAsync_InactiveTemplateFound_ReturnsInactive()
    {
        var screen = TestImageFactory.CreateBgra(400, 300, 20);
        TestImageFactory.PasteBlock(screen, 400, 40, 30, 120, 80, 120);

        using var temp = new TempDirectory();
        var templates = CreateTemplates(temp.Path);
        var detector = CreateDetector(ImageCodec.EncodePng(screen, 400, 300), templates);

        var detection = await detector.DetectButtonAsync("serial");

        Assert.Equal(MainScreenButtonState.Inactive, detection.State);
        Assert.NotNull(detection.Match);
        Assert.Equal(120, detection.Match!.X);
        Assert.Equal(80, detection.Match.Y);
    }

    [Fact]
    public async Task IsMainScreenAsync_SecondFrameIsInactive_ReturnsFalse()
    {
        var activatedScreen = TestImageFactory.CreateBgra(400, 300, 20);
        TestImageFactory.PasteBlock(activatedScreen, 400, 40, 30, 20, 20, 200);
        var inactiveScreen = TestImageFactory.CreateBgra(400, 300, 20);
        TestImageFactory.PasteBlock(inactiveScreen, 400, 40, 30, 20, 20, 120);

        using var temp = new TempDirectory();
        var templates = CreateTemplates(temp.Path);
        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(activatedScreen, 400, 300));
        fakeAdb.EnqueueScreenshots(ImageCodec.EncodePng(inactiveScreen, 400, 300));
        var detector = CreateDetector(fakeAdb, templates);

        var isMain = await detector.IsMainScreenAsync("serial");

        Assert.False(isMain);
        Assert.Equal(2, fakeAdb.ScreenshotCount);
    }

    [Fact]
    public async Task FindBackButtonAsync_BackTemplateInRoi_ReturnsMatch()
    {
        var screen = TestImageFactory.CreateBgra(400, 300, 20);
        TestImageFactory.PasteBlock(screen, 400, 30, 20, 10, 10, 80);

        using var temp = new TempDirectory();
        var templates = CreateTemplates(temp.Path);
        var detector = CreateDetector(ImageCodec.EncodePng(screen, 400, 300), templates);

        var match = await detector.FindBackButtonAsync("serial");

        Assert.NotNull(match);
        Assert.Equal(10, match!.X);
        Assert.Equal(10, match.Y);
    }

    [Fact]
    public async Task WaitForMainScreenAsync_Success_LogsContinuousConfirmation()
    {
        var screen = TestImageFactory.CreateBgra(400, 300, 20);
        TestImageFactory.PasteBlock(screen, 400, 40, 30, 20, 20, 200);

        using var temp = new TempDirectory();
        var templates = CreateTemplates(temp.Path);
        var log = new TestLog();
        var detector = CreateNavigator(new FakeAdbService(ImageCodec.EncodePng(screen, 400, 300)), templates, log);

        var isMain = await detector.WaitForMainScreenAsync(
            "serial",
            timeoutMs: 3000,
            pollIntervalMs: 10);

        Assert.True(isMain);
        Assert.Contains(log.Entries, entry => entry.Level == "INFO" && entry.Message.Contains("已连续确认主界面"));
    }

    [Fact]
    public async Task WaitForMainScreenAsync_Timeout_LogsActionableHintWithoutSpam()
    {
        var screen = TestImageFactory.CreateBgra(400, 300, 20);

        using var temp = new TempDirectory();
        var templates = CreateTemplates(temp.Path);
        var log = new TestLog();
        var detector = CreateNavigator(new FakeAdbService(ImageCodec.EncodePng(screen, 400, 300)), templates, log);

        var isMain = await detector.WaitForMainScreenAsync(
            "serial",
            timeoutMs: 100,
            pollIntervalMs: 10);

        Assert.False(isMain);
        Assert.Contains(log.Entries, entry => entry.Level == "WARN" && entry.Message.Contains("当前弹窗"));
        Assert.Single(log.Entries, entry => entry.Message.StartsWith("等待主界面…"));
    }

    /// <summary>未命中时保存实际帧和实际缓存模板，不再截图；低分候选不得放行。</summary>
    [Fact]
    public async Task FailureEvidence_PreservesFrameAndScores()
    {
        using var temp = new TempDirectory();
        var templates = CreateTemplates(temp.Path);
        var png = ImageCodec.EncodePng(TestImageFactory.CreateBgra(400, 300, 20), 400, 300);
        var adb = new FakeAdbService(png);
        var log = new TestLog();
        var directory = Path.Combine(temp.Path, "evidence");
        var detector = CreateNavigator(adb, templates, log, directory);
        Assert.False(await detector.WaitForMainScreenAsync("serial", 500, 1000));
        Assert.Equal(1, adb.ScreenshotCount);
        using var zip = System.IO.Compression.ZipFile.OpenRead(Assert.Single(Directory.GetFiles(directory)));
        using var frame = new MemoryStream();
        using (var stream = zip.GetEntry("frame.png")!.Open()) stream.CopyTo(frame);
        Assert.Equal(png, frame.ToArray());
        using var metadata = zip.GetEntry("diagnostic.json")!.Open();
        using var json = System.Text.Json.JsonDocument.Parse(metadata);
        var candidates = json.RootElement.GetProperty("candidates");
        Assert.Equal(2, candidates.GetArrayLength());
        Assert.False(candidates[0].GetProperty("accepted").GetBoolean());
        Assert.True(candidates[0].GetProperty("best").GetProperty("Confidence").GetDouble() < 0.99);
        using var template = new MemoryStream();
        using (var stream = zip.GetEntry("template-0.png")!.Open()) stream.CopyTo(template);
        Assert.Equal(File.ReadAllBytes(templates.Activated), template.ToArray());
    }

    /// <summary>返回按钮未命中且证据目录不可写时仍返回未找到，不产生额外截图。</summary>
    [Fact]
    public async Task EvidenceWriteFailure_DoesNotChangeDetection()
    {
        using var temp = new TempDirectory();
        var templates = CreateTemplates(temp.Path);
        var blocked = Path.Combine(temp.Path, "blocked");
        File.WriteAllText(blocked, "file");
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(400, 300, 20), 400, 300));
        var log = new TestLog();
        var detector = CreateNavigator(adb, templates, log, blocked);
        Assert.False(await detector.WaitForMainScreenAsync("serial", 500, 1000));
        Assert.Equal(1, adb.ScreenshotCount);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("证据保存失败"));
    }

    /// <summary>同类证据限频，不同类型轮转后最多保留五份；每份都是有效 ZIP。</summary>
    [Fact]
    public async Task Evidence_IsThrottledAndBounded()
    {
        using var temp = new TempDirectory();
        var log = new TestLog();
        var writer = new RecognitionEvidence(temp.Path, log);
        var png = ImageCodec.EncodePng([0, 0, 0, 255], 1, 1);
        var screen = ImageCodec.DecodePng(png);
        await writer.SaveAsync("home", "serial", png, screen);
        await writer.SaveAsync("home", "serial", png, screen);
        Assert.Single(Directory.GetFiles(temp.Path));
        Assert.Single(log.Entries);
        for (var i = 0; i < 8; i++) await writer.SaveAsync("case-" + i, "serial", png, screen);
        Assert.Equal(5, Directory.GetFiles(temp.Path).Length);
        foreach (var path in Directory.GetFiles(temp.Path))
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            Assert.NotNull(zip.GetEntry("frame.png"));
            Assert.NotNull(zip.GetEntry("diagnostic.json"));
        }
    }

    /// <summary>创建三种状态模板，使用不同灰度值保证测试能明确区分。</summary>
    private static Templates CreateTemplates(string directory)
    {
        var activated = Path.Combine(directory, "home_activated.png");
        var inactive = Path.Combine(directory, "home_inactive.png");
        var back = Path.Combine(directory, "back.png");
        File.WriteAllBytes(activated, ImageCodec.EncodePng(TestImageFactory.CreateBgra(40, 30, 200), 40, 30));
        File.WriteAllBytes(inactive, ImageCodec.EncodePng(TestImageFactory.CreateBgra(40, 30, 120), 40, 30));
        File.WriteAllBytes(back, ImageCodec.EncodePng(TestImageFactory.CreateBgra(30, 20, 80), 30, 20));
        return new Templates(activated, inactive, back);
    }

    /// <summary>使用统一 ROI 和双帧确认参数创建检测器。</summary>
    private static MainScreenDetector CreateDetector(byte[] screen, Templates templates, TestLog? log = null)
        => CreateDetector(new FakeAdbService(screen), templates, log);

    /// <summary>使用调用方提供的假 ADB 创建检测器，便于模拟帧变化。</summary>
    private static MainScreenDetector CreateDetector(FakeAdbService adb, Templates templates, TestLog? log = null)
        => new(
            adb,
            new TemplateMatcher(),
            log ?? new TestLog(),
            new MainScreenDetectorOptions(
                templates.Activated,
                templates.Inactive,
                templates.Back,
                new Roi(0, 0, 300, 200),
                new Roi(0, 0, 100, 80),
                Threshold: 0.90,
                ConfirmationFrames: 2,
                ConfirmationIntervalMs: 0));

    /// <summary>测试所需的三种模板路径。</summary>
    private sealed record Templates(string Activated, string Inactive, string Back);

    /// <summary>测试用临时目录，自动清理。</summary>
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
                // 清理失败不影响测试结论
            }
        }
    }
}

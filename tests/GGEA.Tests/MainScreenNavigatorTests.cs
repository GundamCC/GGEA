using System.IO;
using System.IO.Compression;
using GGEA.App.Automation;
using GGEA.App.Device;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>通过真实流程服务覆盖恢复、启动等待、错误优先级与总预算，不只测试检测器内部回调。</summary>
public class MainScreenNavigatorTests
{
    /// <summary>实际旧配置升级后不再叠加十五秒；点击后的未命中必须清空连续帧，仍取新帧确认。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_LegacyDefaults_ConfirmsFreshFramesWithoutStackedSleeps(bool loading)
    {
        using var fixture = loading
            ? new Fixture("active", "active", "missing", "active", "missing", "active", "active")
            : new Fixture("active", "active", "active", "active");
        var path = Path.Combine(fixture.DirectoryPath, "legacy.json");
        File.WriteAllText(path, "{\"mainScreenConfirmationIntervalMs\":5000,\"mainScreenBackSettleDelayMs\":5000}");
        var config = new GGEA.App.Config.ConfigService(fixture.Log).Load(path);
        var detector = new MainScreenDetector(fixture.Adb, new TemplateMatcher(), fixture.Log,
            fixture.Options with { ConfirmationIntervalMs = config.MainScreenConfirmationIntervalMs });
        var navigator = new MainScreenNavigator(detector, fixture.Adb, fixture.Log);
        Assert.True(await navigator.RecoverAsync("serial", 6000, config.MainScreenBackSettleDelayMs, 165, 1032));
        Assert.Equal(loading ? 7 : 4, fixture.Adb.ScreenshotCount);
        Assert.Equal((165, 1032), Assert.Single(fixture.Adb.Taps));
    }

    /// <summary>路线图没有主界面按钮属于正常导航，慢返回和点击特效不能提前告警或绕过连续确认。</summary>
    [Fact]
    public async Task Recovery_Transitions_ConfirmAgainAfterActivationWithoutWarnings()
    {
        using var fixture = new Fixture("back", "missing", "missing", "inactive", "active", "missing",
            "active", "active", "missing", "active", "active");
        Assert.True(await fixture.Navigator.RecoverAsync("serial", 3000, 0, 165, 1032, pollIntervalMs: 1));
        Assert.Equal(11, fixture.Adb.ScreenshotCount);
        Assert.Equal(new[] { (25, 20), (40, 235), (165, 1032) }, fixture.Adb.Taps);
        Assert.False(Directory.Exists(fixture.EvidencePath));
        Assert.DoesNotContain(fixture.Log.Entries, e => e.Level is "WARN" or "ERROR");
    }

    /// <summary>失败分支仅超时取证；用户取消、捕获异常与其它弹窗退出不把中间未命中当作故障。</summary>
    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("capture-error")]
    public async Task Recovery_OnlyTimeoutWritesEvidence(string outcome)
    {
        using var fixture = new Fixture("back", "missing");
        using var cts = new CancellationTokenSource();
        fixture.Adb.BeforeScreenshotAsync = (count, ct) =>
        {
            if (count >= 3 && outcome == "capture-error") throw new AdbException("测试截图失败");
            if (count == 3 && outcome == "cancel") { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.CompletedTask;
        };
        // 返回点击会清除动作前帧；超时分支须留足预算取得动作后的有效截图。
        Task<bool> Run() => fixture.Navigator.RecoverAsync("serial", 3000,
            0, 165, 1032, cts.Token, pollIntervalMs: 1);
        if (outcome == "timeout")
        {
            Assert.False(await Run());
            using var zip = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(fixture.EvidencePath)));
            using var frame = new MemoryStream();
            using (var stream = zip.GetEntry("frame.png")!.Open()) stream.CopyTo(frame);
            Assert.Equal(Fixture.Screen("missing"), frame.ToArray());
            Assert.Single(fixture.Log.Entries, e => e.Message.Contains("识图失败证据已保存"));
        }
        else
        {
            if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
            else await Assert.ThrowsAsync<ScreenshotCaptureException>(Run);
            Assert.False(Directory.Exists(fixture.EvidencePath));
            Assert.DoesNotContain(fixture.Log.Entries, e => e.Message.Contains("等待主界面超时"));
        }
        Assert.Equal((25, 20), Assert.Single(fixture.Adb.Taps));
    }

    /// <summary>启动时出现专用终止弹窗，即使已有未命中帧，也不会取证、导航或宣告主界面成功。</summary>
    [Fact]
    public async Task Startup_StopGuard_DiscardsPendingFrame()
    {
        using var fixture = new Fixture("missing", "active");
        var checks = 0;
        Assert.False(await fixture.Navigator.WaitForMainScreenAsync("serial", 3000, 1,
            stopWhen: _ => Task.FromResult(++checks == 2)));
        Assert.Equal(1, fixture.Adb.ScreenshotCount);
        Assert.Empty(fixture.Adb.Taps);
        Assert.False(Directory.Exists(fixture.EvidencePath));
        Assert.DoesNotContain(fixture.Log.Entries, e => e.Level is "WARN" or "ERROR");
    }

    /// <summary>同一个总预算必须覆盖慢截图，不允许导航子阶段重新获得数十秒预算。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitBudget_CancelsSlowCaptureAndPostClickWait(bool recover)
    {
        using var fixture = new Fixture("active", "active", "missing");
        fixture.Adb.BeforeScreenshotAsync = (count, ct) =>
            count == (recover ? 3 : 1) ? Task.Delay(5000, ct) : Task.CompletedTask;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = recover
            ? await fixture.Navigator.RecoverAsync("serial", 500, 0, 165, 1032, pollIntervalMs: 1)
            : await fixture.Navigator.WaitForMainScreenAsync("serial", 500, 1);
        Assert.False(result);
        Assert.True(timer.ElapsedMilliseconds < 2500, $"等待预算失效：{timer.ElapsedMilliseconds}ms");
        Assert.Equal(recover ? 1 : 0, fixture.Adb.Taps.Count);
        // 点击前成功帧不能伪装成点击后的失败证据，截图完全没取得时也不能伪造画面。
        Assert.False(Directory.Exists(fixture.EvidencePath));
    }

    /// <summary>公告探测期间页面已改变时，必须重新识图而非点击探测前的返回坐标。</summary>
    [Fact]
    public async Task Recovery_AnnouncementProbeChangesPage_DiscardsOldBackCoordinates()
    {
        using var fixture = new Fixture("back", "active", "active", "active", "active", "active");
        Assert.True(await fixture.Navigator.RecoverAsync("serial", 3000, 0, 165, 1032,
            closeAnnouncement: _ => Task.FromResult(false), pollIntervalMs: 1));
        Assert.Equal((165, 1032), Assert.Single(fixture.Adb.Taps));
    }

    /// <summary>最后是未选中状态时超时也必须保留这张真实帧，不回退保存先前的空白帧。</summary>
    [Fact]
    public async Task Wait_InactiveTimeout_PreservesLatestObservation()
    {
        using var fixture = new Fixture("missing", "inactive");
        Assert.False(await fixture.Navigator.WaitForMainScreenAsync("serial", 500, 1));
        using var zip = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(fixture.EvidencePath)));
        using var frame = new MemoryStream();
        using (var stream = zip.GetEntry("frame.png")!.Open()) stream.CopyTo(frame);
        Assert.Equal(Fixture.Screen("inactive"), frame.ToArray());
        Assert.Empty(fixture.Adb.Taps);
    }

    /// <summary>主界面检测和通用自动化共享捕获失败重试，不得把损坏 PNG 当未命中。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_InvalidPng_RetriesOrStopsWithoutActions(bool recovers)
    {
        using var fixture = new Fixture("active", "active");
        fixture.Adb.ScreenshotProvider = () => recovers && fixture.Adb.ScreenshotCount > 1
            ? Fixture.Screen("active") : [];
        if (recovers) Assert.True(await fixture.Navigator.WaitForMainScreenAsync("serial", 3000, 1));
        else await Assert.ThrowsAsync<ScreenshotCaptureException>(() => fixture.Navigator.WaitForMainScreenAsync("serial", 3000, 1));
        Assert.Equal(3, fixture.Adb.ScreenshotCount);
        Assert.Empty(fixture.Adb.Taps);
        Assert.False(Directory.Exists(fixture.EvidencePath));
    }

    /// <summary>只读探测不扫描错误；主界面等待超时后才诊断，已知网络弹窗不写成普通识图失败。</summary>
    [Theory]
    [InlineData("probe")]
    [InlineData("recover")]
    [InlineData("automation")]
    public async Task ConnectionError_IsDiagnosedOnlyAfterNavigationTimeout(string operation)
    {
        using var fixture = new Fixture("error");
        var errorPath = Path.Combine(fixture.DirectoryPath, "error.png");
        File.WriteAllBytes(errorPath, ImageCodec.EncodePng(TestImageFactory.CreateBgra(20, 20, 255), 20, 20));
        var guard = new ConnectionErrorDetector(fixture.Adb, new TemplateMatcher(), fixture.Log, errorPath);
        var detector = new MainScreenDetector(fixture.Adb, new TemplateMatcher(), fixture.Log, fixture.Options, guard);
        var navigator = new MainScreenNavigator(detector, fixture.Adb, fixture.Log,
            new RecognitionEvidence(fixture.EvidencePath, fixture.Log));
        if (operation == "recover")
        {
            // 超时场景不能同时给出已激活主界面；正常命中本就不应触发异常扫描。
            var screen = ImageCodec.DecodePng(Fixture.Screen("error"));
            TestImageFactory.PasteBlock(screen.Bgra, 400, 40, 30, 20, 220, 20);
            var png = ImageCodec.EncodePng(screen.Bgra, 400, 300);
            fixture.Adb.ScreenshotProvider = () => png;
            await Assert.ThrowsAsync<ConnectionErrorException>(() => navigator.WaitForMainScreenAsync("serial", 100, 1));
            Assert.Contains(fixture.Log.Entries, e => e.Message.Contains("检测到网络错误弹窗"));
        }
        else
        {
            if (operation == "probe") await detector.DetectButtonAsync("serial");
            else await new AutomationService(fixture.Adb, new TemplateMatcher(), fixture.Log,
                connectionErrorDetector: guard).ScreenshotAsync("serial");
            Assert.Equal(1, fixture.Adb.ScreenshotCount);
            Assert.DoesNotContain(fixture.Log.Entries, e => e.Message.Contains("检测到网络错误弹窗"));
        }
        Assert.Empty(fixture.Adb.Taps);
        Assert.False(Directory.Exists(fixture.EvidencePath));
    }

    /// <summary>每个用例独占模板、日志和设备序列，避免测试并发共享帧或诊断限频状态。</summary>
    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "ggea-navigation-" + Guid.NewGuid().ToString("N"));
        public string EvidencePath => Path.Combine(DirectoryPath, "evidence");
        public FakeAdbService Adb { get; }
        public TestLog Log { get; } = new();
        public MainScreenDetectorOptions Options { get; }
        public MainScreenNavigator Navigator { get; }

        /// <summary>创建小尺寸且互不重叠的导航 ROI，保留真实 PNG 编解码与匹配路径。</summary>
        public Fixture(params string[] states)
        {
            Directory.CreateDirectory(DirectoryPath);
            foreach (var (name, width, height, value) in new[] { ("active", 40, 30, 200), ("inactive", 40, 30, 120), ("back", 30, 20, 80) })
                File.WriteAllBytes(Path.Combine(DirectoryPath, name + ".png"), ImageCodec.EncodePng(TestImageFactory.CreateBgra(width, height, (byte)value), width, height));
            Options = new MainScreenDetectorOptions(Path.Combine(DirectoryPath, "active.png"), Path.Combine(DirectoryPath, "inactive.png"),
                Path.Combine(DirectoryPath, "back.png"), new Roi(0, 200, 100, 80), new Roi(0, 0, 100, 80), 0.99, 2, 1);
            Adb = new FakeAdbService(Screen(states[0]));
            Adb.EnqueueScreenshots(states.Skip(1).Select(Screen).ToArray());
            Navigator = new MainScreenNavigator(new MainScreenDetector(Adb, new TemplateMatcher(), Log, Options), Adb, Log,
                new RecognitionEvidence(EvidencePath, Log));
        }

        /// <summary>生成明确的单页状态；错误帧同时包含可点击图案，用于验证错误优先级。</summary>
        public static byte[] Screen(string state)
        {
            var pixels = TestImageFactory.CreateBgra(400, 300, 20);
            if (state is "active" or "inactive" or "error")
                TestImageFactory.PasteBlock(pixels, 400, 40, 30, 20, 220, (byte)(state == "inactive" ? 120 : 200));
            if (state is "back" or "error") TestImageFactory.PasteBlock(pixels, 400, 30, 20, 10, 10, 80);
            if (state == "error") TestImageFactory.PasteBlock(pixels, 400, 20, 20, 200, 100, 255);
            return ImageCodec.EncodePng(pixels, 400, 300);
        }

        /// <summary>只清理本用例创建的目录。</summary>
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}

using GGEA.App.Automation;
using GGEA.App.Device;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>以单调时钟验证统一设备入口，不缩短两分钟生产阈值，也不依赖某段流程主动检测。</summary>
public sealed class ExecutionStallMonitorTests
{
    /// <summary>简中实机更新弹窗通过默认资源路径进入全局停滞诊断，正常取帧不扫描或点击。</summary>
    [Fact]
    public async Task ChineseDataUpdate_UsesGlobalStallDiagnostics()
    {
        var clock = new ManualProgressTime();
        var log = new TestLog();
        var png = System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/zh-data-update.png"));
        var detector = new ConnectionErrorDetector(new FakeAdbService(png), new TemplateMatcher(), log,
            System.IO.Path.Combine(AppContext.BaseDirectory, "resources/zh-CN/templates/entry/connection_error.png"));
        var checks = 0;
        var monitor = new ExecutionStallMonitor((screen, ct) =>
        {
            checks++;
            return Task.FromResult(detector.IsVisible(screen, ct));
        }, log, clock);
        var adb = Device(monitor, png);
        monitor.BeginSession();
        await adb.ScreenshotAsync("device");
        clock.Advance(119);
        await adb.ScreenshotAsync("device");
        Assert.Equal(0, checks);
        clock.Advance(1);
        await Assert.ThrowsAsync<ConnectionErrorException>(() => adb.ScreenshotAsync("device"));
        Assert.Equal(1, checks);
        Assert.Contains(log.Entries, entry => entry.Message.Contains("检测到游戏数据更新弹窗"));
    }

    /// <summary>普通取图、自动化取图与主界面只读识别都由同一个设备入口拦截。</summary>
    [Theory]
    [InlineData("adb")]
    [InlineData("automation")]
    [InlineData("home")]
    public async Task EveryCapturePath_ChecksAfterTwoMinutes(string caller)
    {
        var clock = new ManualProgressTime();
        var calls = 0;
        var monitor = new ExecutionStallMonitor((_, _) => { calls++; return Task.FromResult(true); }, new TestLog(), clock);
        var adb = Device(monitor, Frame(20));
        monitor.BeginSession();
        await adb.ScreenshotAsync("device");
        clock.Advance(119);
        await adb.ScreenshotAsync("device");
        Assert.Equal(0, calls);
        clock.Advance(1);
        using var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        await Assert.ThrowsAsync<ConnectionErrorException>(async () =>
        {
            if (caller == "adb") await adb.ScreenshotAsync("device");
            else if (caller == "automation") await automation.ScreenshotAsync("device");
            else await new MainScreenDetector(adb, new TemplateMatcher(), new TestLog()).DetectButtonAsync("device");
        });
        Assert.Equal(1, calls);
        // 已确认的错误不能因一次检查后的限频而放行下一帧。
        await Assert.ThrowsAsync<ConnectionErrorException>(() => adb.ScreenshotAsync("device"));
        Assert.Equal(1, calls);
    }

    /// <summary>重复点击及小范围闪烁不延后检测；大幅动画没有输入也不代表下一步已经完成。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetriesOrAnimation_DoNotHideStall(bool repeatInput)
    {
        var clock = new ManualProgressTime();
        var checks = 0;
        var monitor = new ExecutionStallMonitor((_, _) => { checks++; return Task.FromResult(false); }, new TestLog(), clock);
        monitor.BeginSession();
        var png = Frame(20);
        var adb = Device(monitor, png);
        await adb.ScreenshotAsync("device");
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(30);
            if (repeatInput) await adb.TapAsync("device", 10, 10);
            else adb.DeviceBytesAsync = (_, _, _) => Task.FromResult(Frame((byte)(i % 2 == 0 ? 220 : 20)));
            await adb.ScreenshotAsync("device");
        }
        Assert.Equal(1, checks);
        clock.Advance(119);
        await adb.ScreenshotAsync("device");
        Assert.Equal(1, checks);
        clock.Advance(1);
        await adb.ScreenshotAsync("device");
        Assert.Equal(2, checks);
    }

    /// <summary>输入后的实际页面变化重新计时；仅有输入成功回执不能重置卡顿窗口。</summary>
    [Fact]
    public async Task InputThenChangedPage_RenewsProgress()
    {
        var clock = new ManualProgressTime();
        var checks = 0;
        var monitor = new ExecutionStallMonitor((_, _) => { checks++; return Task.FromResult(false); }, new TestLog(), clock);
        var adb = Device(monitor, Frame(20));
        monitor.BeginSession();
        await adb.ScreenshotAsync("device");
        clock.Advance(110);
        await adb.TapAsync("device", 10, 10);
        adb.DeviceBytesAsync = (_, _, _) => Task.FromResult(Frame(200));
        await adb.ScreenshotAsync("device");
        clock.Advance(119);
        await adb.ScreenshotAsync("device");
        Assert.Equal(0, checks);
        clock.Advance(1);
        await adb.ScreenshotAsync("device");
        Assert.Equal(1, checks);
    }

    /// <summary>新任务与新设备不继承旧计时；取消先于诊断，空闲不检查。</summary>
    [Fact]
    public async Task SessionAndDeviceIsolation_PreservesCancellation()
    {
        var clock = new ManualProgressTime();
        var checks = 0;
        var monitor = new ExecutionStallMonitor((_, _) => { checks++; return Task.FromResult(true); }, new TestLog(), clock);
        var png = Frame(20);
        monitor.BeginSession();
        await monitor.OnScreenshotAsync("a", png, default);
        clock.Advance(120);
        await monitor.OnScreenshotAsync("b", png, default);
        Assert.Equal(0, checks);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.OnScreenshotAsync("a", png, new CancellationToken(true)));
        Assert.Equal(0, checks);
        monitor.EndSession();
        await monitor.OnScreenshotAsync("a", png, default);
        monitor.BeginSession();
        await monitor.OnScreenshotAsync("a", png, default);
        Assert.Equal(0, checks);
    }

    /// <summary>已结束任务的迟到诊断不影响后续会话；没有后台计时器或自动输入。</summary>
    [Fact]
    public async Task LateDiagnosis_CannotStopNewSession()
    {
        var clock = new ManualProgressTime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new ExecutionStallMonitor((_, _) => { entered.SetResult(); return result.Task; }, new TestLog(), clock);
        var png = Frame(20);
        monitor.BeginSession();
        await monitor.OnScreenshotAsync("a", png, default);
        clock.Advance(120);
        var pending = monitor.OnScreenshotAsync("a", png, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.EndSession(); monitor.BeginSession();
        result.SetResult(true);
        await pending;
        await monitor.OnScreenshotAsync("a", png, default);
    }

    /// <summary>只替换进程 I/O，仍走生产 ADB 截图和输入路径，避免测试绕过接入点。</summary>
    private static AdbService Device(ExecutionStallMonitor monitor, byte[] png) => new("unused-adb", new TestLog())
    {
        ExecutionObserver = monitor,
        DeviceTextAsync = (_, _, _) => Task.FromResult(string.Empty),
        DeviceBytesAsync = (_, _, _) => Task.FromResult(png),
    };

    /// <summary>用清晰不同的页面验证计时语义，不以相似度猜测具体业务状态。</summary>
    private static byte[] Frame(byte color) => ImageCodec.EncodePng(TestImageFactory.CreateBgra(320, 180, color), 320, 180);
}

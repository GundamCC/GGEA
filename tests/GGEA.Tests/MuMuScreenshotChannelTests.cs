using GGEA.App.Device;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>隔离外部能力验证截图通道选择、设备生命周期、取消与误截桌面的防护。</summary>
public sealed class MuMuScreenshotChannelTests
{
    /// <summary>支持增强时直接使用原生图像，不调用备用截图。</summary>
    [Fact]
    public async Task AvailableNative_DoesNotCaptureAdb()
    {
        var fixture = new Fixture { NativeWorks = true };
        Assert.Equal(fixture.Png, await fixture.Channel.CaptureAsync("device", default));
        Assert.Equal(0, fixture.AdbCalls);
    }

    /// <summary>增强缺失时自动回退，同次启动不会每帧重试或重复告警。</summary>
    [Fact]
    public async Task UnavailableNative_FallsBackAndCachesPerBoot()
    {
        var fixture = new Fixture();
        Assert.Equal(fixture.Png, await fixture.Channel.CaptureAsync("device", default));
        await fixture.Channel.CaptureAsync("device", default);
        Assert.Equal(1, fixture.NativeCalls);
        Assert.Equal(2, fixture.AdbCalls);
        fixture.Boot = Guid.NewGuid().ToString();
        await fixture.Channel.CaptureAsync("device", default);
        Assert.Equal(2, fixture.NativeCalls);
    }

    /// <summary>冷却结束重新探测，原生恢复后直接切回；不同设备不共享失败记录。</summary>
    [Fact]
    public async Task RetryAndOtherDevice_ProbeNativeAgain()
    {
        var fixture = new Fixture();
        await fixture.Channel.CaptureAsync("one", default);
        fixture.NativeWorks = true;
        await fixture.Channel.CaptureAsync("two", default);
        fixture.Now = fixture.Now.AddMinutes(6);
        await fixture.Channel.CaptureAsync("one", default);
        Assert.Equal(3, fixture.NativeCalls);
        Assert.Equal(1, fixture.AdbCalls);
    }

    /// <summary>游戏不在默认显示屏时不得调用默认 ADB 截图；缓存期间换屏可重新尝试原生。</summary>
    [Fact]
    public async Task IndependentDisplay_DoesNotCaptureDesktop()
    {
        var fixture = new Fixture { Display = 2 };
        await Assert.ThrowsAsync<AdbException>(() => fixture.Channel.CaptureAsync("device", default));
        Assert.Equal(0, fixture.AdbCalls);
        fixture.Display = 0;
        await fixture.Channel.CaptureAsync("device", default);
        fixture.Display = 2;
        fixture.NativeWorks = true;
        await fixture.Channel.CaptureAsync("device", default);
        Assert.Equal(1, fixture.AdbCalls);
        Assert.Equal(3, fixture.NativeCalls);
    }

    /// <summary>不能仅凭 PNG 签名放行；失败不能写入成功回退缓存。</summary>
    [Fact]
    public async Task CorruptAdbImage_IsRejectedWithoutCaching()
    {
        var fixture = new Fixture();
        fixture.Png[29] ^= 1;
        await Assert.ThrowsAsync<AdbException>(() => fixture.Channel.CaptureAsync("device", default));
        await Assert.ThrowsAsync<AdbException>(() => fixture.Channel.CaptureAsync("device", default));
        Assert.Equal(2, fixture.NativeCalls);
    }

    /// <summary>截图期间切屏或重启时丢弃本帧，不继续识图。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedTargetDuringCapture_IsRejected(bool reboot)
    {
        var fixture = new Fixture();
        fixture.AfterAdb = () => { if (reboot) fixture.Boot = Guid.NewGuid().ToString(); else fixture.Display = 2; };
        await Assert.ThrowsAsync<AdbException>(() => fixture.Channel.CaptureAsync("device", default));
    }

    /// <summary>取消原生探测不触发备用截图，也不会把取消当作能力缺失。</summary>
    [Fact]
    public async Task Cancellation_DoesNotFallback()
    {
        var fixture = new Fixture { NativeCanceled = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Channel.CaptureAsync("device", default));
        Assert.Equal(0, fixture.AdbCalls);
    }

    /// <summary>无法取得启动身份时允许安全帧回退，但不能跨请求缓存判断。</summary>
    [Fact]
    public async Task UnknownBoot_DoesNotCache()
    {
        var fixture = new Fixture { Boot = "unknown" };
        await fixture.Channel.CaptureAsync("device", default);
        await fixture.Channel.CaptureAsync("device", default);
        Assert.Equal(2, fixture.NativeCalls);
    }

    /// <summary>两个通道都失败时保留失败范围和原因，后续请求重新探测而非返回旧帧。</summary>
    [Fact]
    public async Task BothChannelsFail_ReportsFailureAndRetries()
    {
        var fixture = new Fixture { AdbFails = true };
        var error = await Assert.ThrowsAsync<AdbException>(() => fixture.Channel.CaptureAsync("device", default));
        Assert.Contains("测试 SDK 不可用", error.Message);
        Assert.Contains("测试 ADB 不可用", error.Message);
        fixture.AdbFails = false;
        await fixture.Channel.CaptureAsync("device", default);
        Assert.Equal(2, fixture.NativeCalls);
    }

    /// <summary>截图期间收到取消时不得返回图像或记住成功回退。</summary>
    [Fact]
    public async Task CanceledDuringFallback_DiscardsImage()
    {
        using var cancel = new CancellationTokenSource();
        var fixture = new Fixture { AfterAdb = cancel.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Channel.CaptureAsync("device", cancel.Token));
        fixture.AfterAdb = null;
        await fixture.Channel.CaptureAsync("device", default);
        Assert.Equal(2, fixture.NativeCalls);
    }

    /// <summary>可控截图与时钟，不依赖宿主 MuMu、进程或实际等待。</summary>
    private sealed class Fixture
    {
        public byte[] Png { get; } = ImageCodec.EncodePng([20, 30, 40, 255], 1, 1);
        public string Boot { get; set; } = Guid.NewGuid().ToString();
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public int Display { get; set; }
        public bool NativeWorks { get; set; }
        public bool NativeCanceled { get; set; }
        public bool AdbFails { get; set; }
        public int NativeCalls { get; private set; }
        public int AdbCalls { get; private set; }
        public Action? AfterAdb { get; set; }
        public MuMuScreenshotChannel Channel { get; }

        /// <summary>替换外部 I/O，保留生产通道决策和真实 PNG 解码。</summary>
        public Fixture()
        {
            Channel = new MuMuScreenshotChannel((_, _) =>
            {
                NativeCalls++;
                if (NativeCanceled) throw new OperationCanceledException();
                return NativeWorks ? Task.FromResult(Png) : throw new AdbException("测试 SDK 不可用");
            }, (_, _) =>
            {
                AdbCalls++;
                if (AdbFails) throw new AdbException("测试 ADB 不可用");
                AfterAdb?.Invoke();
                return Task.FromResult(Png);
            }, (_, _) => Task.FromResult(Display), (_, _) => Task.FromResult(Boot), new TestLog(), () => Now);
        }
    }
}

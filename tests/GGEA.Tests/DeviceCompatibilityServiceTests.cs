using GGEA.App.Device;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>自动化运行前的画面规格检查测试。</summary>
public class DeviceCompatibilityServiceTests
{
    /// <summary>首次失败可自动恢复，但分辨率不兼容不靠重试放行。</summary>
    [Fact]
    public async Task TransientFailure_RetriesSameDeviceAndRecovers()
    {
        var adb = CreateAdb(1920, 1080);
        adb.ScreenshotFailure = count => count < 3 ? new AdbException("starting") : null;
        Assert.True((await new DeviceCompatibilityService(adb).CheckAsync("selected")).IsCompatible);
        Assert.Equal(3, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>持续失败给出未执行任务的准备结果，不抛成每日任务执行错误。</summary>
    [Fact]
    public async Task PersistentFailure_ReturnsNotReadyWithoutInput()
    {
        var adb = CreateAdb(1920, 1080);
        adb.ScreenshotFailure = _ => new AdbException("offline");
        var result = await new DeviceCompatibilityService(adb).CheckAsync("selected");
        Assert.False(result.IsCompatible);
        Assert.Contains("本次未执行任务", result.Message);
        Assert.Equal(3, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>用户中止后不再重试，也不返回虚假的兼容结果。</summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Cancellation_StopsReadinessAttempts()
    {
        using var cts = new CancellationTokenSource();
        var adb = CreateAdb(1920, 1080);
        adb.ScreenshotFailure = _ => { cts.Cancel(); return new AdbException("offline"); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeviceCompatibilityService(adb).CheckAsync("selected", cts.Token));
        Assert.Equal(1, adb.ScreenshotCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task CheckAsync_1920By1080_ReturnsCompatible()
    {
        var adb = CreateAdb(1920, 1080);
        var service = new DeviceCompatibilityService(adb);

        var result = await service.CheckAsync("serial");

        Assert.True(result.IsCompatible);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1080, 1920)]
    [Trait("Category", "Smoke")]
    public async Task CheckAsync_OtherResolution_ReturnsClearDiagnostic(int width, int height)
    {
        var adb = CreateAdb(width, height);
        var service = new DeviceCompatibilityService(adb);

        var result = await service.CheckAsync("serial");

        Assert.False(result.IsCompatible);
        Assert.Contains($"{width}×{height}", result.Message);
        Assert.Contains("1920×1080", result.Message);
    }

    /// <summary>创建返回指定尺寸纯色 PNG 的测试 ADB。</summary>
    private static FakeAdbService CreateAdb(int width, int height)
    {
        var pixels = TestImageFactory.CreateBgra(width, height, 20);
        return new FakeAdbService(ImageCodec.EncodePng(pixels, width, height));
    }
}

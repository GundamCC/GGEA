using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Device;

/// <summary>设备画面兼容性结果；自动化目前以固定横屏基准制作模板和坐标。</summary>
public sealed record DeviceCompatibilityResult(
    bool IsCompatible,
    int Width,
    int Height,
    string Message);

/// <summary>
/// 在执行业务自动化前验证实际 ADB 截图尺寸，防止在未校准分辨率上继续点击固定坐标。
/// </summary>
public sealed class DeviceCompatibilityService
{
    /// <summary>当前模板与坐标数据的基准宽度。</summary>
    public const int RequiredWidth = 1920;

    /// <summary>当前模板与坐标数据的基准高度。</summary>
    public const int RequiredHeight = 1080;

    private readonly IAdbService _adb;

    public DeviceCompatibilityService(IAdbService adb)
    {
        _adb = adb;
    }

    /// <summary>启动准备最多尝试三次且共用三十秒预算；设备抖动可自动恢复，取消不伪装为不兼容。</summary>
    public async Task<DeviceCompatibilityResult> CheckAsync(
        string serial,
        CancellationToken ct = default)
    {
        var callerToken = ct;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ct = timeout.Token;
        byte[] png = [];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (attempt > 0) await Task.Delay(GGEA.App.Automation.GameTiming.RecognitionIntervalMs, ct);
                png = await _adb.ScreenshotAsync(serial, ct);
                ct.ThrowIfCancellationRequested();
                break;
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                return new(false, 0, 0, L.Text("设备画面准备超时，本次未执行任务；请确认模拟器和游戏已打开后重试"));
            }
            catch (AdbException ex)
            {
                callerToken.ThrowIfCancellationRequested();
                if (attempt == 2)
                    return new(false, 0, 0, L.Format($"设备画面尚未就绪，自动重试后仍不可用，本次未执行任务：{ex.Message}"));
            }
        }
        callerToken.ThrowIfCancellationRequested();
        var screen = await Task.Run(() => ImageCodec.DecodePng(png), callerToken);
        if (screen.Width == RequiredWidth && screen.Height == RequiredHeight)
        {
            return new DeviceCompatibilityResult(
                true,
                screen.Width,
                screen.Height,
                L.Format($"设备画面兼容：{screen.Width}×{screen.Height}"));
        }

        return new DeviceCompatibilityResult(
            false,
            screen.Width,
            screen.Height,
            L.Format($"当前截图为 {screen.Width}×{screen.Height}，自动化仅支持 {RequiredWidth}×{RequiredHeight} 横屏"));
    }
}

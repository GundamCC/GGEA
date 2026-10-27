using GGEA.App.Device;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Automation;

/// <summary>识别共用的只读截图入口：仅有限重试传输和解码，弹窗诊断由掌握流程异常的调用方启动。</summary>
public sealed class ScreenshotCapture(IAdbService adb, IGgeaLog log)
{
    /// <summary>每次返回一张有效新帧；正常截图不扫描网络、日期或数据更新弹窗，取消不降级为未命中。</summary>
    public async Task<(byte[] Png, ScreenImage Screen)> CaptureAsync(string serial, CancellationToken ct = default,
        bool retryOnFailure = true)
    {
        var frame = await CaptureImageAsync(serial, ct, retryOnFailure);
        ct.ThrowIfCancellationRequested();
        return frame;
    }

    /// <summary>观察只使用本次捕获的新帧；回调禁止输入，具体购买及出击门禁仍由业务流程持有。</summary>
    internal async Task<(ScreenImage Screen, T Observation)> CaptureAndObserveAsync<T>(string serial,
        Func<ScreenImage, CancellationToken, Task<T>> observe, CancellationToken ct, bool retryOnFailure)
    {
        var frame = await CaptureImageAsync(serial, ct, retryOnFailure);
        var observation = await Task.Run(() => observe(frame.Screen, ct), ct);
        ct.ThrowIfCancellationRequested();
        return (frame.Screen, observation);
    }

    /// <summary>重试仅覆盖传输与解码；识别回调的代码或资源错误不能被误报成截图失败而重新执行。</summary>
    private async Task<(byte[] Png, ScreenImage Screen)> CaptureImageAsync(string serial, CancellationToken ct,
        bool retryOnFailure)
    {
        // 普通等待允许只读重试；购买最终授权要求任一截图异常立即撤销，不能静默续接授权链。
        var maxAttempts = retryOnFailure ? 3 : 1;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var png = await adb.ScreenshotAsync(serial, ct);
                ct.ThrowIfCancellationRequested();
                var screen = ImageCodec.DecodePng(png);
                ct.ThrowIfCancellationRequested();
                return (png, screen);
            }
            catch (Exception ex) when (ex is AdbException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                log.Warn($"截图捕获失败（{attempt}/{maxAttempts}）: {ex.Message}");
                if (attempt == maxAttempts)
                {
                    var message = retryOnFailure
                        ? "连续截图捕获失败，已停止自动化识别，无法判断页面状态"
                        : "截图捕获失败，本次取证未完成，无法判断页面状态";
                    // 普通识别重试耗尽才确认无法继续；单帧安全取证失败由任务层决定停止范围。
                    if (retryOnFailure) log.Error(message);
                    else log.Warn(message);
                    throw new ScreenshotCaptureException(message, ex);
                }
                await Task.Delay(100, ct);
            }
        }
    }
}

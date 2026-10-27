using System.Collections.Concurrent;
using GGEA.App.Logging;

namespace GGEA.App.Device;

/// <summary>按实际截图能力选择通道；品牌不等于 SDK 可用，回退必须证明默认显示屏属于目标游戏。</summary>
internal sealed class MuMuScreenshotChannel(
    Func<string, CancellationToken, Task<byte[]>> nativeCapture,
    Func<string, CancellationToken, Task<byte[]>> adbCapture,
    Func<string, CancellationToken, Task<int>> readDisplay,
    Func<string, CancellationToken, Task<string>> readBootId,
    IGgeaLog log,
    Func<DateTimeOffset>? clock = null)
{
    private readonly ConcurrentDictionary<string, (Guid Boot, DateTimeOffset RetryAt)> _fallback = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>回退状态只属于同次设备启动，五分钟后重新探测；缓存期间仍每帧校验显示屏。</summary>
    internal async Task<byte[]> CaptureAsync(string serial, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var boot = await ReadIdentityAsync(serial, ct);
        var cached = boot != Guid.Empty && _fallback.TryGetValue(serial, out var state)
            && state.Boot == boot && state.RetryAt > _clock();
        if (!cached) _fallback.TryRemove(serial, out _);
        AdbException? nativeError = null;
        if (!cached)
        {
            try
            {
                var native = await nativeCapture(serial, ct);
                ct.ThrowIfCancellationRequested();
                return native;
            }
            catch (AdbException ex) { nativeError = ex; }
        }
        ct.ThrowIfCancellationRequested();
        try
        {
            // screencap 的默认物理屏与 Android 的逻辑显示编号不能直接互换；非零屏不猜测 -d 参数。
            if (await readDisplay(serial, ct) != 0)
            {
                if (cached)
                {
                    _fallback.TryRemove(serial, out _);
                    var native = await nativeCapture(serial, ct);
                    ct.ThrowIfCancellationRequested();
                    return native;
                }
                throw new AdbException("目标游戏位于独立显示屏，默认 ADB 截图不能证明属于该游戏");
            }
            var png = await adbCapture(serial, ct);
            AdbScreenshotOutput.ValidateRecoveredImage(png);
            ct.ThrowIfCancellationRequested();
            // 截图期间切屏或设备重启时丢弃本帧，不能把旧设备/桌面图送入识图。
            if (await readDisplay(serial, ct) != 0 || (boot != Guid.Empty && await ReadIdentityAsync(serial, ct) != boot))
                throw new AdbException("截图期间目标游戏显示屏或设备启动身份发生变化");
            ct.ThrowIfCancellationRequested();
            if (!cached)
            {
                if (boot != Guid.Empty) _fallback[serial] = (boot, _clock().AddMinutes(5));
                log.Warn($"截图通道=ADB（MuMu 原生通道不可用），serial={serial}；目标游戏位于默认显示屏，PNG 完整性验证通过，五分钟后重新探测原生能力。原因: {nativeError?.Message}");
            }
            return png;
        }
        catch (AdbException ex)
        {
            _fallback.TryRemove(serial, out _);
            throw new AdbException($"MuMu 截图通道不可用：原生={nativeError?.Message ?? "此前不可用"}；ADB 回退={ex.Message}；本次截图停止", ex);
        }
    }

    /// <summary>无法取得启动身份时仍可探测截图，但不能缓存通道选择；取消必须立即传播。</summary>
    private async Task<Guid> ReadIdentityAsync(string serial, CancellationToken ct)
    {
        try { return Guid.TryParse(await readBootId(serial, ct), out var id) ? id : Guid.Empty; }
        catch (AdbException) { return Guid.Empty; }
    }
}

using GGEA.App.Device;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Automation;

/// <summary>会话级异常看门狗：所有设备截图共用计时，不依赖具体流程主动调用或注册阶段。</summary>
internal sealed class ExecutionStallMonitor(
    Func<ScreenImage, CancellationToken, Task<bool>> detectError, IGgeaLog log, TimeProvider? time = null)
    : IAdbExecutionObserver
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _inspectionGate = new(1, 1);
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);
    private bool _active;
    private long _generation;
    private long _started;

    /// <summary>每个设备独立保留最后帧和输入前基准；重复输入不能覆盖尚未观察到变化的基准。</summary>
    private sealed class DeviceState(long started)
    {
        internal long LastProgress = started;
        internal long LastInspection = started;
        internal byte[]? LastFrame;
        internal byte[]? BeforeInput;
        internal bool KnownError;
    }

    /// <summary>唯一执行租约开始时清空旧任务计时；空闲期间不检查、不启动后台截图。</summary>
    internal void BeginSession()
    {
        lock (_sync) { _generation++; _devices.Clear(); _started = _time.GetTimestamp(); _active = true; }
    }

    /// <summary>释放租约即撤销本轮观察；迟到的识别结果不能终止后续任务。</summary>
    internal void EndSession()
    {
        lock (_sync) { _generation++; _active = false; _devices.Clear(); }
    }

    /// <summary>只有“已发输入后画面有明显变化”才推迟异常扫描；这不是购买、通关或点击授权证据。</summary>
    public void OnInputCompleted(string serial)
    {
        lock (_sync)
            if (_active && _devices.TryGetValue(serial, out var state) && state.BeforeInput is null)
                state.BeforeInput = state.LastFrame;
    }

    /// <summary>正常帧只检查单调时钟；到期复用当前帧诊断，已知错误才抛出，健康长战斗继续等待。</summary>
    public async Task OnScreenshotAsync(string serial, byte[] png, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 同一会话的并行取图也必须等待诊断收束，不能抢先交付另一张未确认的帧。
        await _inspectionGate.WaitAsync(ct);
        try
        {
            long generation;
            double stalledSeconds;
            DeviceState state;
            lock (_sync)
            {
                if (!_active) return;
                generation = _generation;
                var now = _time.GetTimestamp();
                if (!_devices.TryGetValue(serial, out state!))
                    _devices.Add(serial, state = new DeviceState(_devices.Count == 0 ? _started : now));
                if (state.BeforeInput is { } before && HasSceneChange(before, png))
                {
                    state.LastProgress = now;
                    state.BeforeInput = null;
                }
                state.LastFrame = png;
                if ((!state.KnownError && _time.GetElapsedTime(state.LastProgress, now) < Interval) ||
                    _time.GetElapsedTime(state.LastInspection, now) < Interval)
                {
                    // 一旦确认错误，在下一次低频复核解除前不交付画面给后续动作。
                    if (state.KnownError) throw new ConnectionErrorException();
                    return;
                }
                stalledSeconds = _time.GetElapsedTime(state.LastProgress, now).TotalSeconds;
                state.LastInspection = now;
            }
            ct.ThrowIfCancellationRequested();
            log.Info($"全局异常检测：连续 {stalledSeconds:F0} 秒未观察到输入后的页面推进，检查网络、数据更新及日期更新弹窗。");
            var found = await detectError(ImageCodec.DecodePng(png), ct);
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_active || generation != _generation) return;
                state.KnownError = found;
                if (found) throw new ConnectionErrorException();
            }
            log.Info("全局异常检测未发现已知错误，保留当前流程继续等待；未执行恢复点击。");
        }
        finally { _inspectionGate.Release(); }
    }

    /// <summary>稀疏采样只用于诊断节流；小范围转圈、闪烁不能替代页面推进，更不能授权业务动作。</summary>
    private static bool HasSceneChange(byte[] before, byte[] current)
    {
        var a = ImageCodec.DecodePng(before);
        var b = ImageCodec.DecodePng(current);
        // 尺寸突变是兼容性问题，不能据此延后异常检查。
        if (a.Width != b.Width || a.Height != b.Height) return false;
        const int columns = 32, rows = 18;
        var changed = 0;
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var x = (2 * column + 1) * a.Width / (2 * columns);
            var y = (2 * row + 1) * a.Height / (2 * rows);
            var offset = (y * a.Width + x) * 4;
            var difference = Math.Abs(a.Bgra[offset] - b.Bgra[offset]) +
                Math.Abs(a.Bgra[offset + 1] - b.Bgra[offset + 1]) +
                Math.Abs(a.Bgra[offset + 2] - b.Bgra[offset + 2]);
            if (difference > 45) changed++;
        }
        return changed > columns * rows / 10;
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using GGEA.App.Vision;

namespace GGEA.App.Device;

/// <summary>自动化组合根唯一的截图和输入边界。一次执行内每个 serial 绑定自己的游戏屏及分辨率。</summary>
internal sealed class AutomationDevice(AdbService transport) : IAdbService
{
    internal readonly record struct Target(string Serial, int Display, ulong? Physical, GameResolution Resolution);
    private readonly ConcurrentDictionary<string, Target> _targets = new(StringComparer.Ordinal);
    internal AdbService Transport => transport;
    internal IAdbExecutionObserver? ExecutionObserver { get; set; }

    // 主窗口执行租约互斥。新操作可采用用户在上次任务结束后选择的新尺寸。
    internal void BeginOperation() => _targets.Clear();

    private async Task<(Target Target, byte[] Png)> CaptureAsync(string serial, CancellationToken ct)
    {
        var (png, display, physical) = await transport.CaptureScreenshotAsync(serial, ct);
        var size = GameResolution.ReadPng(png);
        if (!size.IsSupported) throw new InvalidOperationException(size.UnsupportedMessage);
        var target = new Target(serial, display, physical, size);
        if (_targets.GetOrAdd(serial, target) != target)
            throw new AdbException("游戏显示屏或分辨率已改变，请重新开始操作");
        return (target, png);
    }

    public async Task<byte[]> ScreenshotAsync(string serial, CancellationToken ct = default)
    {
        var (target, raw) = await CaptureAsync(serial, ct);
        var png = await Task.Run(() => target.Resolution.Normalize(raw), ct);
        if (ExecutionObserver is { } observer) await observer.OnScreenshotAsync(serial, png, ct);
        return png;
    }

    // 调试页保留非标准原图供诊断；仅四档受支持画面可以生成输入授权。
    internal async Task<(Target? Target, byte[] Png)> PreviewAsync(string serial, CancellationToken ct)
    {
        var (png, display, physical) = await transport.CaptureScreenshotAsync(serial, ct);
        var size = GameResolution.ReadPng(png);
        if (!size.IsSupported) return (null, png);
        return (new Target(serial, display, physical, size), await Task.Run(() => size.Normalize(png), ct));
    }

    internal void BindPreview(Target target) => _targets[target.Serial] = target;

    public async Task TapAsync(string serial, int x, int y, CancellationToken ct = default)
    {
        var (target, _) = await CaptureAsync(serial, ct);
        var point = target.Resolution.ToDevice(x, y);
        await transport.FrameInputAsync(serial, target.Display, target.Physical,
            ["tap", Number(point.X), Number(point.Y)], ct);
        ExecutionObserver?.OnInputCompleted(serial);
    }

    public async Task SwipeAsync(string serial, int x1, int y1, int x2, int y2, int durationMs, CancellationToken ct = default)
    {
        var (target, _) = await CaptureAsync(serial, ct);
        var first = target.Resolution.ToDevice(x1, y1);
        var last = target.Resolution.ToDevice(x2, y2);
        await transport.FrameInputAsync(serial, target.Display, target.Physical,
            ["swipe", Number(first.X), Number(first.Y), Number(last.X), Number(last.Y), Number(durationMs)], ct);
        ExecutionObserver?.OnInputCompleted(serial);
    }

    public async Task DragAsync(string serial, int x1, int y1, int x2, int y2, int durationMs, CancellationToken ct = default)
    {
        var (target, _) = await CaptureAsync(serial, ct);
        var first = target.Resolution.ToDevice(x1, y1);
        var last = target.Resolution.ToDevice(x2, y2);
        await transport.DragFrameAsync(serial, target.Display, target.Physical, first.X, first.Y, last.X, last.Y, durationMs, ct);
        ExecutionObserver?.OnInputCompleted(serial);
    }

    public async Task PinchInAsync(string serial, int durationMs = 500, CancellationToken ct = default)
    {
        var (target, _) = await CaptureAsync(serial, ct);
        if (target.Display != 0) throw new AdbException("独立显示屏不支持原始双指事件");
        // 原始触摸轴本身以设备量程生成两指轨迹，不再换算为基准像素。
        await transport.PinchInAsync(serial, durationMs, ct);
        ExecutionObserver?.OnInputCompleted(serial);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    public Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken ct = default) => transport.ListDevicesAsync(ct);
    public Task<string> GetBootIdAsync(string serial, CancellationToken ct = default) => transport.GetBootIdAsync(serial, ct);
    public Task<string> GetSystemPropertyAsync(string serial, string property, CancellationToken ct = default) => transport.GetSystemPropertyAsync(serial, property, ct);
    public Task<string> ConnectAsync(string host, int port, CancellationToken ct = default) => transport.ConnectAsync(host, port, ct);
    public Task LaunchAppAsync(string serial, string package, CancellationToken ct = default) => transport.LaunchAppAsync(serial, package, ct);
    public async Task KeyEventAsync(string serial, string keyCode, CancellationToken ct = default)
    {
        await transport.KeyEventAsync(serial, keyCode, ct);
        ExecutionObserver?.OnInputCompleted(serial);
    }
}

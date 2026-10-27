namespace GGEA.App.Device;

/// <summary>
/// ADB 操作接口，方便测试时用假实现替换。
/// </summary>
public interface IAdbService
{
    /// <summary>列出所有 ADB 设备。</summary>
    Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken ct = default);

    /// <summary>对指定设备截图，返回 PNG 字节。</summary>
    Task<byte[]> ScreenshotAsync(string serial, CancellationToken ct = default);

    /// <summary>在指定设备上点击坐标。</summary>
    Task TapAsync(string serial, int x, int y, CancellationToken ct = default);

    /// <summary>在指定设备上滑动。</summary>
    Task SwipeAsync(string serial, int x1, int y1, int x2, int y2, int durationMs, CancellationToken ct = default);

    /// <summary>在指定设备上执行一次双指向内捏合，用于缩小可缩放画布。</summary>
    Task PinchInAsync(string serial, int durationMs = 500, CancellationToken ct = default);

    /// <summary>向指定设备发送按键事件（如 KEYCODE_HOME）。</summary>
    Task KeyEventAsync(string serial, string keyCode, CancellationToken ct = default);

    /// <summary>读取设备系统属性（如 sys.boot_completed）。</summary>
    Task<string> GetSystemPropertyAsync(string serial, string property, CancellationToken ct = default);

    /// <summary>读取本次 Android 启动的唯一标识，用于确认多个 ADB 地址是否指向同一实例。</summary>
    Task<string> GetBootIdAsync(string serial, CancellationToken ct = default);

    /// <summary>通过 monkey 打开指定包名的应用。</summary>
    Task LaunchAppAsync(string serial, string package, CancellationToken ct = default);

    /// <summary>通过 adb connect 连接指定 host:port 设备，返回 adb 输出。</summary>
    Task<string> ConnectAsync(string host, int port, CancellationToken ct = default);
}

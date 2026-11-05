using GGEA.App.Localization;
namespace GGEA.App.Device;

/// <summary>
/// 单个 ADB 设备信息。
/// </summary>
public sealed record AdbDevice(string Serial, string State, string Model)
{
    /// <summary>设备所属模拟器品牌（如 MuMu / 雷电 / BlueStacks）；为空表示未知。</summary>
    public string? Brand { get; init; }

    /// <summary>本次 Android 启动身份；仅用于证明多个 ADB 地址属于同一实例，空值不能作为证据。</summary>
    public Guid BootId { get; init; }

    /// <summary>设备是否处于可操作状态（state 为 device）。</summary>
    public bool IsAvailable => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    /// <summary>离线连接仍可能保留型号，不能把已发现误报为可操作。</summary>
    public bool IsOffline => string.Equals(State, "offline", StringComparison.OrdinalIgnoreCase);

    /// <summary>仅带明确端口的 IP 设备支持定向断开重连；不对 USB 或模拟器别名猜端口。</summary>
    public bool CanReconnect => IsOffline && Serial.Contains(':') &&
        System.Net.IPEndPoint.TryParse(Serial, out var endpoint) && endpoint.Port > 0;

    /// <summary>界面下拉框显示的文本：品牌前缀 + 序列号 + 型号。</summary>
    public string Display
    {
        get
        {
            var brandPart = string.IsNullOrEmpty(Brand) ? string.Empty : $"{Brand} · ";
            var infoPart = string.IsNullOrEmpty(Model) ? $"({State})" : $"({Model})";
            var statePart = IsAvailable ? string.Empty : IsOffline ? L.Text(" · 离线 (offline)") : $" · {State}";
            return $"{brandPart}{Serial} {infoPart}{statePart}";
        }
    }

    /// <summary>让 WPF 下拉框直接显示友好文本。</summary>
    public override string ToString() => Display;
}

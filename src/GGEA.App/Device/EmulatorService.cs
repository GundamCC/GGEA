using GGEA.App.Automation;
using GGEA.App.Logging;

namespace GGEA.App.Device;

/// <summary>一键启动参数：模拟器类型、启动命令、游戏包名与各阶段等待超时。</summary>
public sealed record EmulatorLaunchOptions
{
    /// <summary>模拟器类型：mumu / ld / bs / custom。</summary>
    public string EmulatorType { get; init; } = EmulatorTypes.Custom;

    /// <summary>模拟器启动程序；已知模拟器可由适配器在为空时自动探测。</summary>
    public required string EmulatorPath { get; init; }

    /// <summary>模拟器启动参数；为空时使用对应档案的默认值。</summary>
    public string EmulatorArgs { get; init; } = string.Empty;

    /// <summary>目标游戏 Android 包名。</summary>
    public required string GamePackage { get; init; }

    /// <summary>等待设备上线的总超时（毫秒）。</summary>
    public int DeviceWaitTimeoutMs { get; init; } = 180000;

    /// <summary>等待 Android 启动完成的总超时（毫秒）。</summary>
    public int BootCompletedTimeoutMs { get; init; } = 180000;

    /// <summary>设备与启动状态轮询间隔（毫秒）。</summary>
    public int PollIntervalMs { get; init; } = GameTiming.RecognitionIntervalMs;

    /// <summary>确定设备并完成系统启动检查后、打开游戏前执行的可选准备操作。</summary>
    public Func<string, CancellationToken, Task>? BeforeGameLaunchAsync { get; init; }
}

/// <summary>模拟器启动结果；失败时 Serial 为空并携带可直接展示的诊断。</summary>
public sealed record EmulatorStartResult(bool Success, string? Serial, string Message)
{
    public static EmulatorStartResult Fail(string message) => new(false, null, message);
    public static EmulatorStartResult Ok(string serial, string message) => new(true, serial, message);
}

/// <summary>
/// 模拟器启动协调器。实际品牌差异由内置适配器负责，本类只选择并调用当前适配器。
/// </summary>
public sealed class EmulatorService
{
    private readonly Func<string?, IEmulatorAdapter> _adapterFactory;

    /// <summary>创建按启动选项动态选择适配器的服务；测试可注入进程与 MuMu 状态函数。</summary>
    public EmulatorService(
        IAdbService adb,
        IGgeaLog log,
        Action<string, string>? emulatorLauncher = null,
        Func<string?>? mumuPathResolver = null,
        Func<string, int, CancellationToken, Task<bool>>? mumuRunningCheck = null)
    {
        _adapterFactory = type => EmulatorAdapterFactory.Create(
            type,
            adb,
            log,
            emulatorLauncher,
            mumuPathResolver,
            mumuRunningCheck);
    }

    /// <summary>使用已经为当前配置创建的适配器，保证设备刷新与启动采用同一套规则。</summary>
    public EmulatorService(IEmulatorAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapterFactory = _ => adapter;
    }

    /// <summary>启动模拟器、绑定唯一设备并打开游戏。</summary>
    public Task<EmulatorStartResult> StartAndOpenGameAsync(
        EmulatorLaunchOptions options,
        CancellationToken ct = default)
        => _adapterFactory(options.EmulatorType).StartAndOpenGameAsync(options, ct);
}

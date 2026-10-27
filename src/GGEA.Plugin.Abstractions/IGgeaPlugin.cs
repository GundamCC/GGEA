using System.Windows;

namespace GGEA.Plugin.Abstractions;

/// <summary>可信本机插件契约。宿主串行调用生命周期；后台工作必须在停用和退出时等待收尾。</summary>
public interface IGgeaPlugin : IAsyncDisposable
{
    /// <summary>仅初始化本机状态；此时没有设备授权。失败后宿主仍调用 DisposeAsync。</summary>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken);

    /// <summary>在 UI 线程创建一次设置编辑器；取消窗口时不得保存编辑内容。无设置可返回 null。</summary>
    IPluginSettingsEditor? CreateSettingsEditor();

    /// <summary>接收用户确认的设备；null 表示停用，必须等待旧设备操作及后台任务结束。</summary>
    Task SetDeviceAsync(IPluginDevice? device, CancellationToken cancellationToken);
}

/// <summary>设置视图归当前设置窗口所有，保存失败应抛异常，不得报告成功。</summary>
public interface IPluginSettingsEditor
{
    FrameworkElement View { get; }
    void Save();
}

/// <summary>插件独立存储与日志入口；不暴露主窗口、全局配置或其他插件的状态。</summary>
public interface IPluginContext
{
    string PluginDirectory { get; }
    string UiLanguage { get; }
    string? ReadSettings();
    void WriteSettings(string json);
    void Log(PluginLogLevel level, string message);
}

/// <summary>日志语义与宿主一致，Error 只用于明确终止的操作。</summary>
public enum PluginLogLevel { Debug, Info, Warning, Error }

/// <summary>仅绑定当前确认设备的命令入口；切换后旧入口失效，插件不能借此访问其他设备。</summary>
public interface IPluginDevice
{
    string Serial { get; }
    string GamePackage { get; }
    string GameLanguage { get; }
    Guid BootId { get; }
    /// <summary>只接受 shell/push/pull 的设备命令，宿主负责选择设备、超时及取消；不是安全沙箱。</summary>
    Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

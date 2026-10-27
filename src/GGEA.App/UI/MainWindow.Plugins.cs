using GGEA.App.Device;
using GGEA.App.Localization;
using GGEA.App.Plugins;

namespace GGEA.App.UI;

public partial class MainWindow
{
    /// <summary>设备准备共用窗口任务与执行互斥，插件失败不能被当成连接成功。</summary>
    private async Task SyncDeviceServicesAsync(string? serial, CancellationToken ct = default, bool newlyStarted = false)
    {
        await SyncPluginsAsync(serial, ct, newlyStarted);
    }

    private PluginManager? _plugins;
    private Task? _pluginShutdown;

    /// <summary>只传递用户确认的设备快照；通道捕获旧服务，不能因配置切换而指向另一个设备。</summary>
    private async Task SyncPluginsAsync(string? serial, CancellationToken ct, bool newlyStarted = false)
    {
        if (_plugins is null || !_plugins.Entries.Any(entry => entry.Instance is not null)) return;
        var adb = _adb;
        if (serial is null || adb is null) { await _plugins.SetDeviceAsync(null, ct); return; }
        var selected = (DeviceCombo.ItemsSource as IEnumerable<AdbDevice>)?
            .FirstOrDefault(device => device.Serial == serial && device.IsAvailable);
        if (selected is null && !newlyStarted) { await _plugins.SetDeviceAsync(null, ct); return; }
        var bootId = selected?.BootId ?? Guid.Empty;
        // 启动服务已确认唯一的新设备，但下拉列表尚未刷新；在打开游戏前交付该目标，不能静默跳过插件。
        if (newlyStarted && !Guid.TryParse(await adb.GetBootIdAsync(serial, ct), out bootId))
            throw new AdbException(L.Text("所选设备已重启或身份发生变化，请刷新并重新选择目标设备"));
        var lease = new PluginDevice(serial, _config.GamePackage, _config.GameLanguage, bootId,
            (args, token) => adb.RunTextAsync(["-s", serial, .. args], args[0] is "push" or "pull" ? 60000 : 20000, token), _config.AdbPath);
        await _plugins.SetDeviceAsync(lease, ct);
    }

    /// <summary>超时仅停止等待，保留正在进行的清理；再次关窗等待同一任务，绝不并发 Dispose。</summary>
    private async Task<bool> StopPluginsAsync()
    {
        if (_plugins is null) return true;
        if (_pluginShutdown is null || _pluginShutdown.IsFaulted || _pluginShutdown.IsCanceled)
            _pluginShutdown = _plugins.DisposeAsync().AsTask();
        try { await _pluginShutdown.WaitAsync(TimeSpan.FromSeconds(15)); return true; }
        catch (Exception ex)
        {
            _logger?.Warn($"插件尚未完成退出清理（{ex.GetType().Name}）；请稍后再次关闭窗口。");
            return false;
        }
    }
}

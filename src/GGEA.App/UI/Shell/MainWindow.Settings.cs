using System.Windows;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Localization;

namespace GGEA.App.UI;

/// <summary>应用设置并重新加载窗口使用的服务。</summary>
public partial class MainWindow
{
    /// <summary>打开设置窗口；保存后重建 ADB 服务并刷新设备。</summary>
    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => SettingsButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task SettingsButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginHostOperation();
        if (execution is null) return;

        if (_logger is null)
        {
            return;
        }

        if (_plugins is not null)
        {
            try { await _plugins.SetDeviceAsync(null, _windowTasks.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.Warn($"插件清理失败（{ex.GetType().Name}），可在设置中停用，重启后生效。"); }
        }
        AcknowledgeStartupUpdate();
        var settings = new SettingsWindow(new ConfigService(_logger).Load(ConfigService.ResolvePath(DefaultConfigFile)), _logger, allowUpdateInstall: true,
            availableUpdate: _startupUpdate, saveUpdatePreference: SaveUpdatePreference, plugins: _plugins,
            automaticUpdateInProgress: () => _automaticDownload is not null) { Owner = this };
        var saved = settings.ShowDialog();
        if (saved != true)
        {
            await SyncPluginsAsync((DeviceCombo.SelectedItem as AdbDevice)?.Serial, _windowTasks.Token);
            return;
        }

        // 应用新配置：重建依赖 ADB 路径的服务；旧会话仅借用窗口 OCR。
        _automation?.Dispose();
        _config = settings.Config;
        RestoreDeliveryOptions();
        _textOcr?.Dispose();
        _textOcr = null;
        ConfigService.ApplyResourceLanguage(_config);
        CompleteKnownEmulatorPaths();
        InitializeTaskServices(_logger);
        UpdateFooterText();
        RefreshTemplateList();
        _logger.Info(L.Format($"设置已应用：区服 {_config.GameServer}，游戏语言 {_config.GameLanguage}，ADB 路径 {_config.AdbPath}"));

        await RefreshDevicesAsync();
    }
}

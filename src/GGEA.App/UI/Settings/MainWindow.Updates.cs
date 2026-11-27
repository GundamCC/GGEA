using GGEA.App.Localization;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Automation;
using System.IO;
using GGEA.App.Config;
using GGEA.App.Updates;

namespace GGEA.App.UI;

public partial class MainWindow
{
    /// <summary>更新交接后禁止新业务；关闭仍通过窗口任务收尾和插件释放流程。</summary>
    private bool _updatePending;

    private bool _startupUpdateChecked;
    private bool _startupUpdateAcknowledged;
    private UpdateRelease? _startupUpdate;
    private TaskCompletionSource? _waitingForIdle;
    private CancellationTokenSource? _automaticDownload;

    /// <summary>每次启动仅检查一次，不持有设备执行权；关闭时由窗口任务生命周期取消并等待。</summary>
    private async Task CheckStartupUpdateAsync()
    {
        if (_startupUpdateChecked || _logger is null || _windowTasks.IsStopping) return;
        _startupUpdateChecked = true;
        using var client = new OnlineUpdateClient(AppContext.BaseDirectory, status =>
        {
            if (status.IsWarning) _logger.Warn(status.Message);
            else _logger.Info(status.Message);
        });
        try
        {
            _startupUpdate = await client.CheckAsync(UpdateLauncher.CurrentVersion, _windowTasks.Token);
            if (_windowTasks.IsStopping || _startupUpdate is null) return;
            _logger.Info(L.Format($"发现新版本 v{_startupUpdate.Version}，可在设置中下载更新。"));
            RefreshUpdateBadge();
            if (!_config.AutomaticUpdates || UpdateLauncher.InstallationUnavailableReason(AppContext.BaseDirectory) is not null) return;
            if (((App)Application.Current).UpdateDownloadUnavailableReason() is { } reason)
            {
                _logger.Info(reason);
                return;
            }
            var update = _startupUpdate;
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
            _automaticDownload = pending;
            string? package = null;
            try
            {
                // 下载、复用和重建暂存包不占用设备。只在交接独立更新器时持有窗口执行权。
                package = await Task.Run(() => client.DownloadAsync(update, new Progress<double>(), pending.Token), pending.Token);
                while (!_updatePending && _config.AutomaticUpdates)
                {
                    pending.Token.ThrowIfCancellationRequested();
                    using var lease = TryBeginHostOperation(false);
                    if (lease is not null)
                    {
                        await ((App)Application.Current).StartUpdateAsync(package, update.Version.ToString(4), false, pending.Token);
                        package = null; // 已交给独立更新器。
                        CloseForUpdate();
                        break;
                    }
                    _waitingForIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    await _waitingForIdle.Task.WaitAsync(pending.Token);
                }
            }
            catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
            finally
            {
                _automaticDownload = null;
                _waitingForIdle = null;
                if (package is not null) await Task.Run(() => UpdatePackageDownloader.DeleteDownload(Path.GetDirectoryName(package)!));
            }
        }
        catch (OperationCanceledException) when (_windowTasks.IsStopping) { }
        catch (Exception ex)
        {
            // 启动检查失败只记录可恢复提示；不弹错误、不影响设备任务或旧版使用。
            _logger.Warn(L.Format($"启动更新检查未完成（{ex.GetType().Name}），可稍后在设置中重试。"));
        }
    }

    /// <summary>新版只显示角标，不打断用户；本次启动点过设置后，即使检查稍后完成也不重新点亮。</summary>
    private void RefreshUpdateBadge()
    {
        var unread = _startupUpdate is not null && !_startupUpdateAcknowledged;
        UpdateBadge.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.ToolTip = unread ? L.Format($"发现新版本 v{_startupUpdate!.Version}，点击设置查看") : L.Text("设置");
        AutomationProperties.SetName(SettingsButton, unread ? L.Text("设置，有新版本") : L.Text("设置"));
    }

    /// <summary>打开设置即视为已读，关闭或取消设置不再次提示同一启动检查结果。</summary>
    private void AcknowledgeStartupUpdate()
    {
        _startupUpdateAcknowledged = _startupUpdate is not null;
        RefreshUpdateBadge();
    }

    /// <summary>磁盘保存成功后同步主窗口配置，后续其他配置保存不会恢复旧的更新选项。</summary>
    private void SaveUpdatePreference(bool enabled)
    {
        UpdatePreferences.SaveAutomaticUpdates(enabled,
            Path.Combine(AppContext.BaseDirectory, "config", "ggea.config.json"));
        _config.AutomaticUpdates = enabled;
        if (!enabled) _automaticDownload?.Cancel();
    }

    /// <summary>事件先释放执行租约，随后正常关闭，保证设备任务和 插件 完成收尾。</summary>
    private void CloseForUpdate()
    {
        _updatePending = true;
        _startupUpdate = null;
        UpdateBusyState();
        _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.ApplicationIdle);
    }
}

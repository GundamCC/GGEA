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

    // 每次检查独立持有客户端，窗口关闭时仍由任务生命周期取消。测试可注入受控的发布源响应。
    internal Func<Action<UpdateSourceStatus>, OnlineUpdateClient> OpenUpdateClient { get; set; } =
        report => new OnlineUpdateClient(AppContext.BaseDirectory, report);

    private async Task MonitorUpdatesAsync()
    {
        await CheckStartupUpdateAsync();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (await timer.WaitForNextTickAsync(_windowTasks.Token))
            if (_config.AutomaticUpdates && !_updatePending) await CheckStartupUpdateAsync();
    }

    internal void RefreshLiveData()
    {
        _startupUpdate = null;
        RefreshUpdateBadge();
        _automation?.Dispose();
        _textOcr?.Dispose();
        _textOcr = null;
        _config = new ConfigService(_logger!).Load(ConfigService.ResolvePath(DefaultConfigFile));
        ConfigService.ApplyResourceLanguage(_config);
        ReloadLimitedActivities();
        ReloadFierceEnemyUnits();
        InitializeTaskServices(_logger!);
        RefreshTemplateList();
        UpdateFooterText();
        _logger?.Info(L.Format($"已应用 v{UpdateLauncher.CurrentVersion} 配置与素材。"));
    }
    private bool _startupUpdateAcknowledged;
    private UpdateRelease? _startupUpdate;
    private TaskCompletionSource? _waitingForIdle;
    private CancellationTokenSource? _automaticDownload;

    /// <summary>每轮检查独立完成，不持有设备执行权；关闭时由窗口任务生命周期取消并等待。</summary>
    private async Task CheckStartupUpdateAsync()
    {
        if (_logger is null || _windowTasks.IsStopping || ((App)Application.Current).HasUpdateForExit) return;
        using var client = OpenUpdateClient(status =>
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
                        if (await ((App)Application.Current).TryApplyLiveDataAsync(package, update.Version.ToString(4), pending.Token))
                        {
                            RefreshLiveData();
                            break;
                        }
                        ((App)Application.Current).InstallOnExit(package, update.Version);
                        package = null;
                        _logger.Info(L.Text("更新已就绪，退出后安装，下次打开即可使用。"));
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

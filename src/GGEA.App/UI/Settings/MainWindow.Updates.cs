using GGEA.App.Localization;
using System.Windows;
using System.Windows.Automation;
using System.IO;
using GGEA.App.Config;
using GGEA.App.Updates;

namespace GGEA.App.UI;

public partial class MainWindow
{

    // 每次检查独立持有客户端，窗口关闭时仍由任务生命周期取消。测试可注入受控的发布源响应。
    internal Func<Action<UpdateSourceStatus>, OnlineUpdateClient> OpenUpdateClient { get; set; } =
        report => new OnlineUpdateClient(UpdateLauncher.ContentRoot, report);

    private async Task MonitorUpdatesAsync()
    {
        await CheckStartupUpdateAsync();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (await timer.WaitForNextTickAsync(_windowTasks.Token))
            if (_config.AutomaticUpdates) await CheckStartupUpdateAsync();
    }

    // 其他窗口也可完成后台更新。仅在本窗口任务空闲时切换目录、刷新控件和识图缓存。
    private async Task MonitorInstalledVersionAsync()
    {
        var root = UpdateLauncher.UserRoot;
        var marker = GGEA.Updater.UpdateFiles.SafePath(root, ".ggea-update/active.json");
        DateTime seen = DateTime.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(_windowTasks.Token))
        {
            var stamp = File.GetLastWriteTimeUtc(marker);
            if (stamp == seen) continue;
            using var lease = TryBeginHostOperation(false);
            if (lease is null) continue;
            try
            {
                var selected = await Task.Run(() => VersionStore.SelectedDirectory(root, verifyProgram: true), _windowTasks.Token);
                if (!Path.TrimEndingDirectorySeparator(selected).Equals(Path.TrimEndingDirectorySeparator(UpdateLauncher.ContentRoot), StringComparison.OrdinalIgnoreCase))
                {
                    var old = GGEA.Updater.UpdateManifest.ReadInstallation(UpdateLauncher.ContentRoot);
                    var next = GGEA.Updater.UpdateManifest.Read(selected);
                    if (UpdateLauncher.Adopt(selected, next, GGEA.Updater.UpdateInstaller.CanApplyLiveData(old, next)))
                        RefreshLiveData();
                }
                seen = stamp;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _logger?.Warn(ex.Message);
                seen = stamp;
            }
        }
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
            if (!_config.AutomaticUpdates || UpdateLauncher.InstallationUnavailableReason(UpdateLauncher.ContentRoot) is not null) return;
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
                while (_config.AutomaticUpdates)
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

                        _logger.Info(L.Format($"v{UpdateLauncher.PreparedVersion} 已安装，下次打开 GGEA 即可使用。"));
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
            Path.Combine(GGEA.App.Config.ConfigService.UserDirectory, "config", "ggea.config.json"));
        _config.AutomaticUpdates = enabled;
        if (!enabled) _automaticDownload?.Cancel();
    }

}

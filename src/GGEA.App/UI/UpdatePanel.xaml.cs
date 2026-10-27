using GGEA.App.Localization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GGEA.App.Logging;
using GGEA.App.Updates;

namespace GGEA.App.UI;

/// <summary>设置页更新交互：检查、下载、取消和安装分别有明确状态，窗口关闭取消网络操作。</summary>
public partial class UpdatePanel : UserControl
{
    private IGgeaLog? _log;
    /// <summary>安装器就绪后由所属窗口退出，组件不决定应用生命周期。</summary>
    public event EventHandler? InstallationReady;

    /// <summary>构造仅创建控件，不发起网络请求。</summary>
    public UpdatePanel() => InitializeComponent();

    private CancellationTokenSource? _updateCancellation;
    private UpdateRelease? _availableUpdate;
    private UpdateDownloadSession _downloadSession = new();
    private string? _preparedPackage => _downloadSession.PreparedPackage;
    private Action<bool>? _saveAutoInstall;
    private bool _autoInstallUpdates = true;
    private bool _updateClosed;
    private bool _allowUpdateInstall;

    /// <summary>只有独立更新器确认就绪后才通知主窗口正常关闭。</summary>
    public bool UpdateRequested { get; private set; }

    /// <summary>显示真实程序集版本；首次初始化期间只允许检查，避免重入主窗口启动链。</summary>
    public void Initialize(IGgeaLog log, bool allowInstall, UpdateRelease? available = null,
        bool autoInstallUpdates = true, Action<bool>? saveAutoInstall = null)
    {
        _log = log;
        _allowUpdateInstall = allowInstall;
        _autoInstallUpdates = autoInstallUpdates;
        AutoInstallUpdatesCheckBox.IsChecked = autoInstallUpdates;
        _saveAutoInstall = saveAutoInstall;
        RefreshSourceSummary();
        UpdateVersionText.Text = L.Format($"当前版本 v{UpdateLauncher.CurrentVersion.ToString(4)} · 正式版 / GitHub 优先");
        UpdateStatusText.Text = L.Text("检查新版本后，点击下载更新。");
        Unloaded += (_, _) =>
        {
            _updateClosed = true;
            _updateCancellation?.Cancel();
            if (!UpdateRequested && _preparedPackage is not null)
                UpdatePackageDownloader.DeleteDownload(Path.GetDirectoryName(_preparedPackage)!);
        };
        if (available is not null)
        {
            ShowAvailableUpdate(available);
            EndUpdateOperation();
        }
    }

    /// <summary>检查不阻塞 WPF；取消、超时、无新版本和服务错误均显示实际结果。</summary>
    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateCancellation is not null) return;
        _availableUpdate = null;
        NotesHeading.Visibility = Visibility.Collapsed;
        UpdateNotesText.Text = "";
        if (_preparedPackage is not null) UpdatePackageDownloader.DeleteDownload(Path.GetDirectoryName(_preparedPackage)!);
        _downloadSession = new();
        using var cancellation = BeginUpdateOperation(L.Text("正在连接 GitHub 检查更新…"));
        using var client = new OnlineUpdateClient(AppContext.BaseDirectory, ReportSourceStatus);
        try
        {
            _availableUpdate = await client.CheckAsync(UpdateLauncher.CurrentVersion, cancellation.Token);
            if (_updateClosed) return;
            if (_availableUpdate is null) UpdateStatusText.Text = L.Text("当前已是最新版本，或本地版本高于最新正式版。");
            else
            {
                ShowAvailableUpdate(_availableUpdate);
            }
        }
        catch (OperationCanceledException) { if (!_updateClosed) UpdateStatusText.Text = cancellation.IsCancellationRequested ? L.Text("已取消检查。") : L.Text("检查超时，请确认能访问 GitHub 后重试。"); }
        catch (Exception ex) { ReportUpdateFailure(L.Text("检查更新"), ex); }
        finally { EndUpdateOperation(); }
    }

    /// <summary>打开设置时直接展示启动检查结果，避免再次请求网络或丢失发布说明。</summary>
    private void ShowAvailableUpdate(UpdateRelease update)
    {
        _availableUpdate = update;
        NotesHeading.Visibility = Visibility.Visible;
        SourceStatusText.Text = L.Format($"当前更新来源：{update.SourceName} · GitHub 优先，连接失败时尝试备用源。");
        UpdateStatusText.Text = L.Format($"发现 v{update.Version}，完整包 {update.PackageSize / 1024d / 1024d:F1} MB。");
        UpdateNotesText.Text = string.IsNullOrWhiteSpace(update.Notes) ? L.Text("本次发布未填写更新说明。") : update.Notes;
        var reason = UpdateLauncher.InstallationUnavailableReason(AppContext.BaseDirectory);
        if (reason is not null) UpdateStatusText.Text += "\n" + reason;
        else if (!_allowUpdateInstall) UpdateStatusText.Text += L.Text("\n请完成首次设置后，再从设置页安装更新。");
    }

    /// <summary>用户点击下载授权本次流程；选项在下载期间锁定，取消或校验失败不会触发自动安装。</summary>
    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateCancellation is not null || _availableUpdate is null) return;
        if (_preparedPackage is not null) { await InstallPreparedUpdateAsync(); return; }
        using var cancellation = BeginUpdateOperation(L.Text("正在下载完整更新包…"));
        using var client = new OnlineUpdateClient(AppContext.BaseDirectory, ReportSourceStatus);
        try
        {
            await _downloadSession.RunAsync(token => client.DownloadAsync(_availableUpdate, new Progress<double>(percent =>
            {
                if (_updateClosed || _preparedPackage is not null) return;
                UpdateProgress.IsIndeterminate = false;
                UpdateProgress.Value = percent;
                UpdateStatusText.Text = percent < 100 ? L.Format($"正在下载… {percent:F0}%") : L.Text("下载完成，正在校验并解压…");
            }), token), StartPreparedUpdateAsync, _autoInstallUpdates, cancellation.Token);
            if (!_updateClosed && !UpdateRequested)
                UpdateStatusText.Text = L.Text("下载与校验完成。点击“重启并安装”继续；用户配置、AP 方案和已采集坐标会保留。");
        }
        catch (OperationCanceledException) { if (!_updateClosed) UpdateStatusText.Text = cancellation.IsCancellationRequested ? L.Text("已取消下载，当前版本未修改。") : L.Text("下载超时或连接中断，请重试。"); }
        catch (Exception ex) { ReportUpdateFailure(_preparedPackage is null ? L.Text("下载更新") : L.Text("启动更新器"), ex); }
        finally
        {
            if (_updateClosed && !UpdateRequested && _preparedPackage is not null)
                UpdatePackageDownloader.DeleteDownload(Path.GetDirectoryName(_preparedPackage)!);
            EndUpdateOperation();
        }
    }

    /// <summary>主窗口持有设置执行租约；确认交接后由主窗口走既有关闭清理，禁止直接 Shutdown。</summary>
    private async Task InstallPreparedUpdateAsync()
    {
        if (MessageBox.Show(Window.GetWindow(this), L.Text("将关闭 GGEA 并安装更新，完成后自动启动。\n\n已保存的设置会保留；尚未保存的设置不会保存。是否继续？"),
                L.Text("重启并安装"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        using var cancellation = BeginUpdateOperation(L.Text("正在启动独立更新器…"));
        try
        {
            await StartPreparedUpdateAsync(_preparedPackage!, cancellation.Token);
        }
        catch (OperationCanceledException) { if (!_updateClosed) UpdateStatusText.Text = L.Text("更新器交接已取消或超时，当前版本未修改。"); }
        catch (Exception ex) { ReportUpdateFailure(L.Text("启动更新器"), ex); }
        finally { EndUpdateOperation(); }
    }

    /// <summary>自动与手动安装共用交接；主窗口保持租约，更新器就绪后才正常退出。</summary>
    private async Task StartPreparedUpdateAsync(string package, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateClosed || !_allowUpdateInstall) throw new OperationCanceledException(cancellationToken);
        UpdateStatusText.Text = L.Text("下载与校验完成，正在启动更新器并准备重启…");
        await UpdateLauncher.StartAsync(AppContext.BaseDirectory, package, _availableUpdate!.Version.ToString(4), false, cancellationToken);
        UpdateRequested = true;
        InstallationReady?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>只在保存成功后改变选项；设置窗口取消不撤回已明确即时保存的更新偏好。</summary>
    private void AutoInstallUpdatesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AutoInstallUpdatesCheckBox.IsChecked == true;
        try
        {
            _saveAutoInstall?.Invoke(enabled);
            _autoInstallUpdates = enabled;
        }
        catch (Exception ex)
        {
            AutoInstallUpdatesCheckBox.IsChecked = _autoInstallUpdates;
            ReportUpdateFailure(L.Text("保存更新选项"), ex);
        }
    }

    /// <summary>分离文字以复用统一复选框模板，同时保留点击文字切换；下载期间不能绕过禁用状态。</summary>
    private void AutoInstallUpdatesLabel_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!AutoInstallUpdatesCheckBox.IsEnabled) return;
        AutoInstallUpdatesCheckBox.IsChecked = AutoInstallUpdatesCheckBox.IsChecked != true;
        AutoInstallUpdatesCheckBox_Click(sender, e);
        e.Handled = true;
    }

    /// <summary>操作期间仍允许关闭窗口和取消；避免同时发起多个下载或检查。</summary>
    private CancellationTokenSource BeginUpdateOperation(string message)
    {
        _updateCancellation = new CancellationTokenSource();
        CheckUpdateButton.IsEnabled = false;
        DownloadUpdateButton.IsEnabled = false;
        AutoInstallUpdatesCheckBox.IsEnabled = false;
        CancelUpdateButton.Visibility = Visibility.Visible;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = true;
        UpdateStatusText.Text = message;
        return _updateCancellation;
    }

    /// <summary>可安装性由正式包身份决定；不让开发目录出现可点击的覆盖安装按钮。</summary>
    private void EndUpdateOperation()
    {
        _updateCancellation = null;
        if (_updateClosed) return;
        CheckUpdateButton.IsEnabled = true;
        AutoInstallUpdatesCheckBox.IsEnabled = true;
        CancelUpdateButton.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Collapsed;
        DownloadUpdateButton.Content = _preparedPackage is null ? L.Text("下载更新") : L.Text("重启并安装");
        DownloadUpdateButton.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        DownloadUpdateButton.IsEnabled = _allowUpdateInstall && _availableUpdate is not null &&
            UpdateLauncher.InstallationUnavailableReason(AppContext.BaseDirectory) is null;
    }

    /// <summary>更新失败有重试和手动下载路径，记录 WARN，不改变任务安全状态。</summary>
    private void ReportUpdateFailure(string operation, Exception exception)
    {
        _log?.Warn(L.Format($"{operation}失败：{exception.GetType().Name}"));
        if (!_updateClosed) UpdateStatusText.Text = L.Format($"{operation}失败：{exception.Message}\n可稍后重试，或打开 GitHub 发布页手动下载。");
    }

    /// <summary>显示当前备用源状态，不把空列表伪装成已有可用镜像。</summary>
    private void RefreshSourceSummary()
    {
        var count = new UpdateSourceStore(AppContext.BaseDirectory).Load().Length;
        SourceStatusText.Text = count == 0 ? L.Text("当前更新来源：GitHub。")
            : L.Format($"GitHub 优先 · 已有 {count} 个备用源，连接失败时自动尝试。");
    }

    /// <summary>切换来源独立于下载百分比显示；正常切换为 INFO，可恢复连接异常为 WARN。</summary>
    private void ReportSourceStatus(UpdateSourceStatus status)
    {
        if (_updateClosed) return;
        SourceStatusText.Text = status.Message;
        if (status.IsWarning) _log?.Warn(status.Message);
        else _log?.Info(status.Message);
    }

    /// <summary>取消仅作用于检查、下载或交接，不会在替换文件过程中中断事务。</summary>
    private void CancelUpdateButton_Click(object sender, RoutedEventArgs e) => _updateCancellation?.Cancel();

    /// <summary>浏览器只打开固定的官方发布页，不打开远程更新说明内的任意链接。</summary>
    private void OpenReleasesButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(GitHubUpdateClient.ReleasesUrl) { UseShellExecute = true }); }
        catch (Exception ex) { ReportUpdateFailure(L.Text("打开 GitHub"), ex); }
    }
}

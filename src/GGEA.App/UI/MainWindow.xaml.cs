using System.ComponentModel;
using System.IO;
using System.Windows;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Localization;
using GGEA.App.Logging;

namespace GGEA.App.UI;

/// <summary>窗口构造、启动和关闭期间的资源管理。</summary>
public partial class MainWindow : Window
{
    private const string DefaultConfigFile = "config/ggea.config.json";
    private GgeaLogger? _logger;
    private AppConfig _config = new();
    private bool _windowClosed;
    private bool _windowClosing;
    private readonly WindowTaskLifetime _windowTasks = new();

    public MainWindow()
    {
        InitializeComponent();
        ConnectDailyView();
        ConnectScoutView();
        ConnectBallFactoryView();
        ConnectApConsumptionView();
        ConnectStagePositionView();
        ConnectAutoFarmView();
        ConnectAutoProgressView();
        ConnectFierceEnemyAssaultView();
        ConnectMasterLeagueView();
        ConnectScreenshotView();
        AutoProgressView.AutoProgressQueueList.ItemsSource = _autoProgressQueue;
        LoadAutoProgressSettings();
        CopyrightText.Text = AppCopyright;
        RewardView.RewardTaskList.ItemsSource = _rewardTasks;
        StagePositionView.StagePositionList.ItemsSource = _stagePositionItems;
        // 位置调整与截图操作属于开发调试工具，仅开发版（DEBUG）显示，发布版隐藏。
#if DEBUG
        StagePositionTab.Visibility = Visibility.Visible;
        ScreenshotTab.Visibility = Visibility.Visible;
#endif
        InitializeDailyTasks();
        InitializeFierceEnemySelection();
        InitializeRewardTasks();
        Loaded += Window_Loaded;
        Closed += Window_Closed;
        Closing += Window_Closing;
    }

    /// <summary>将异常处理也纳入活动任务，防止关闭日志后仍有事件续体写入。</summary>
    private Task RunWindowTaskAsync(Func<Task> action) => _windowTasks.RunAsync(async () =>
    {
        try { await action(); }
        catch (OperationCanceledException) { /* 取消已由业务或关窗流程呈现。 */ }
        catch (Exception ex) { _logger?.Error(L.Format($"操作失败: {ex.Message}")); }
    });

    /// <summary>加载配置、服务和设备，并纳入窗口退出等待。</summary>
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => Window_LoadedCoreAsync(sender, e));
        await RunWindowTaskAsync(MonitorUpdatesAsync);
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task Window_LoadedCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null) return;

        try
        {
            var baseDir = AppContext.BaseDirectory;

            // 初始化日志与配置
            _logger = new GgeaLogger(Path.Combine(baseDir, "logs"));
            _logger.Logged += OnLog;
            var configPath = Path.Combine(baseDir, DefaultConfigFile);
            _config = ((App)Application.Current).StartupConfig;
            // 首次启动及旧配置迁移均固定本次选择；以后不因系统语言变化而覆盖用户偏好。
            try
            {
                _config = new ConfigService(_logger).Update(configPath, latest =>
                    latest.UiLanguage = UiLanguages.Resolve(latest.UiLanguage, System.Globalization.CultureInfo.CurrentUICulture));
            }
            catch (Exception ex) { _logger.Warn(L.Format($"界面语言保存失败：{ex.Message}")); }
            ConfigService.ApplyResourceLanguage(_config);
            if (_config.LimitedEventDaily?.Index < _config.FierceEnemyDaily?.Index)
            {
                RestoreLimitedDaily();
                RestoreFierceEnemyDailyTask();
            }
            else
            {
                RestoreFierceEnemyDailyTask();
                RestoreLimitedDaily();
            }
            ((App)Application.Current).SetTheme(_config.IsDarkMode);
            UpdateThemeButton();
            _plugins = new Plugins.PluginManager(baseDir, _logger);
            await _plugins.InitializeAsync(typeof(App).Assembly.GetName().Version!, Localization.L.Language, _windowTasks.Token);
            if (!_config.AdbDiscoveryCompleted)
            {
                _logger.Info(L.Text("首次启动：正在检索本机模拟器 ADB…"));
                IReadOnlyList<AdbCandidate> candidates;
                try { candidates = await Task.Run(AdbDiscovery.Discover); }
                catch (Exception ex)
                {
                    _logger.Warn(L.Format($"自动检索 ADB 失败，请手动选择：{ex.Message}"));
                    candidates = Array.Empty<AdbCandidate>();
                }
                void ApplyDiscovery(AppConfig latest)
                {
                    if (latest.AdbDiscoveryCompleted) return;
                    AdbDiscovery.Apply(latest, candidates);
                    if (string.IsNullOrWhiteSpace(latest.EmulatorPath))
                    {
                        var type = EmulatorTypes.DetectFromAdbPath(latest.AdbPath) ?? EmulatorTypes.Custom;
                        latest.EmulatorPath = AdbDiscovery.FindLauncher(latest.AdbPath, type) ?? "";
                    }
                }
                ApplyDiscovery(_config);
                try { _config = new ConfigService(_logger).Update(configPath, ApplyDiscovery); }
                catch (Exception ex) { _logger.Warn(L.Format($"ADB 检索结果保存失败，下次启动会重试：{ex.Message}")); }
                if (candidates.Count == 0 && !File.Exists(_config.AdbPath))
                {
                    // 初始化仍持有执行锁；此时弹出模态窗口会让主界面和设置一直不可操作。
                    // 先完成初始化并释放执行锁，用户随后可从设置中选择 ADB。
                    _logger.Warn(L.Text("未找到模拟器 ADB，请在设置中浏览选择 adb.exe（BlueStacks 为 HD-Adb.exe）并保存。"));
                }
                else
                {
                    _logger.Info(L.Format($"ADB 检索完成：找到 {candidates.Count} 个路径。"));
                    if (candidates.Count > 1 && candidates[0].LastRunUtc is null)
                        _logger.Warn(L.Text("发现多个 ADB，系统未提供可读取的执行记录，已选择一个候选；可在设置的 ADB 下拉框中确认。设备检测将继续。"));
                }
            }
            CompleteKnownEmulatorPaths();

            _environmentDiagnostics = new EnvironmentDiagnostics(Path.Combine(baseDir, "logs"));
            _logger.Info(await _environmentDiagnostics.CaptureAsync("startup", _config.AdbPath, _config.EmulatorPath));

            ConfigService.ApplyResourceLanguage(_config);

            // 初始化服务
            InitializeTaskServices(_logger);

            UpdateFooterText();
            _logger.Info(L.Text("GGEA 启动完成"));
            _logger.Info(L.Format($"配置文件: {configPath}"));

            RefreshTemplateList();
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"启动失败: {ex.Message}"));
        }
    }

    /// <summary>先停止业务并等待收尾，再释放插件；超时保持关闭闸门，允许再次关窗等待。</summary>
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_windowClosed) return;
        e.Cancel = true;
        if (_windowClosing) return;
        _windowClosing = true;
        IsEnabled = false;
        AccountStatusText.Text = L.Text("正在停止并关闭…");
        _dailyCts?.Cancel();
        _autoFarmCts?.Cancel();
        _scoutCts?.Cancel();
        _ballFactoryCts?.Cancel();
        _autoProgressCts?.Cancel();
        _startCts?.Cancel();
        if (!await _windowTasks.StopAsync(TimeSpan.FromSeconds(15)))
        {
            // 超时不能谎报完成或释放业务仍在使用的资源；允许再次关窗重试等待。
            _logger?.Warn(L.Text("任务尚未完成收尾，窗口保持停止状态；请稍后再次关闭窗口。"));
            AccountStatusText.Text = L.Text("仍在等待任务退出，请稍后再次关闭窗口");
            _windowClosing = false;
            return;
        }
        if (!await StopPluginsAsync())
        {
            AccountStatusText.Text = L.Text("插件清理未完成，请稍后再次关闭窗口");
            _windowClosing = false;
            return;
        }
        _windowClosed = true;
        // 没有会话时清理可能同步完成，延后关闭以免重入 WPF 的 Closing 事件。
        _ = Dispatcher.BeginInvoke(Close);
    }

    /// <summary>会话已释放后关闭日志。</summary>
    private void Window_Closed(object? sender, EventArgs e)
    {
        _automation?.Dispose();
        _textOcr?.Dispose();
        _notifications.Dispose();
        if (_logger is not null)
        {
            _logger.Logged -= OnLog;
            _logger.Dispose();
        }
    }

    /// <summary>日志事件：切回 UI 线程后追加到列表并自动滚动。</summary>
    private void OnLog(object? sender, LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add(entry);
            while (LogList.Items.Count > 500)
            {
                LogList.Items.RemoveAt(0);
            }

            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        });
    }
}

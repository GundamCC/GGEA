using GGEA.App.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Path = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Logging;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.App.UI;

/// <summary>
/// 主窗口：设备管理、截图操作、AP 消耗计划、模板采集与匹配测试、日志展示。
/// 所有 ADB 操作均为异步执行，不阻塞 UI 主线程。
/// </summary>
public partial class MainWindow : Window
{
    private const string DefaultConfigFile = "config/ggea.config.json";
    /// <summary>更新交接后禁止新业务；关闭仍通过窗口任务收尾和插件释放流程。</summary>
    private bool _updatePending;
    /// <summary>设备刷新时每个适配器最多探测前 3 个实例，避免空端口拖慢刷新。</summary>
    private const int EmulatorScanMaxInstances = 3;

    // 从程序集元数据读取完整四段显示版本，避免修订号被截断而与项目配置不一致。
    private static readonly string AppVersion =
        typeof(MainWindow).Assembly.GetName().Version?.ToString(4) ?? L.Text("未知");

    // 底部版权声明固定归属作者，年份随运行时间自动更新，无需每年手动修改界面资源。
    private static readonly string AppCopyright = $"© {DateTime.Now.Year} GundamC";

    /// <summary>出击按钮模板位于 stage/sortie，统一按 90% 置信度识别。</summary>
    private const double SortieTemplateThreshold = 0.90;

    /// <summary>主界面确认后固定点击主画面入口的坐标，用于收敛底部导航的焦点状态。</summary>
    private const int MainScreenActivationTapX = 165;
    private const int MainScreenActivationTapY = 1032;

    // 覆盖层配色
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x7B));
    private static readonly Brush PreviewBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));

    private RecognitionEvidence? _recognitionEvidence;
    private EnvironmentDiagnostics? _environmentDiagnostics;
    private GgeaLogger? _logger;
    private AppConfig _config = new();
    private readonly WindowsNotificationService _notifications = new();
    // 设备执行权保证同一时刻只有一个独立任务；网络错误提醒与 finally 共用本轮通知状态。
    private TaskEndNotification? _activeTaskEndNotification;
    private readonly ApStopNotification _apStopNotification = new();
    private AdbService? _adb;
    private bool _windowClosed;
    private bool _windowClosing;
    private bool _refreshingDeviceSelection;
    private AutomationService? _automation;
    private IEmulatorAdapter? _emulatorAdapter;
    private EmulatorService? _emulator;
    private DeviceCompatibilityService? _deviceCompatibility;
    private GameEntryService? _gameEntry;
    private MainScreenNavigator? _mainScreenNavigator;
    private ConnectionErrorDetector? _connectionErrorDetector;
    private BattleshipLocator? _battleshipLocator;
    private PersonalBaseService? _personalBaseService;
    private ShopService? _shopService;
    private GiftService? _giftService;
    private IDevelopService? _developService;
    private StageService? _stageService;
    // 不同设备与导航服务共用无设备状态的模型；窗口业务全部退出后才能释放原生会话。
    private OnnxTextRecognizer? _textOcr;
    private StageSkipService? _stageSkipService;
    private ApConsumptionService? _apConsumptionService;
    private UpgradeStagesService? _upgradeStagesService;
    private MissionRewardService? _missionRewardService;
    private DownloadDialogService? _downloadDialogService;
    private SortieFlowService? _sortieFlowService;
    private ApConsumptionProfileService? _apProfileService;
    private ApConsumptionProfilesData _apProfiles = new();
    private UiLayoutService? _uiLayoutService;
    private UiLayout _mainLayout = new();
    private UiLayout _personalBaseLayout = new();
    private UiLayout _shopLayout = new();
    private UiLayout _giftLayout = new();
    private UiLayout _developLayout = new();
    private UiLayout _stageLayout = new();
    private UiLayout _upgradeStagesLayout = new();
    private UiLayout _missionLayout = new();
    private List<HardStageEntry> _hardStages = new();
    private StageWorkPositionsData _stagePositions = new();
    private readonly ObservableCollection<ApConsumptionWorkItem> _apWorkItems = new();
    private readonly ObservableCollection<DailyTaskItem> _dailyTasks = new();
    private readonly ObservableCollection<RewardTaskItem> _rewardTasks = new();
    private readonly ObservableCollection<StagePositionItem> _stagePositionItems = new();
    private readonly TemplateMatcher _matcher = new();

    private ScreenImage? _currentScreen;
    private Roi? _selectedRoi;
    private TemplateMatchResult? _lastMatch;
    private Point? _selectedPoint;
    private Point? _swipeStart;
    private Point? _swipeEnd;

    private readonly WindowTaskLifetime _windowTasks = new();
    private bool _busy;
    private readonly DeviceExecutionGate _executionGate = new();
    private ExecutionStallMonitor? _executionMonitor;
    private bool _dragging;
    private bool _dailyRunning;
    private bool _autoFarmRunning;
    /// <summary>招募循环独占设备；其按钮运行中只负责中止，不创建第二个循环。</summary>
    private bool _scoutRunning;
    private CancellationTokenSource? _scoutCts;
    /// <summary>工厂定位流程持有设备执行权，取消后等待整个流程收尾再释放。</summary>
    private bool _ballFactoryRunning;
    private CancellationTokenSource? _ballFactoryCts;
    private bool _starting;
    private CancellationTokenSource? _dailyCts;
    private CancellationTokenSource? _autoFarmCts;
    private CancellationTokenSource? _startCts;
    private string? _preferredDeviceSerial;
    /// <summary>与用户选择绑定的启动身份，防止串口被另一实例复用或去重别名导致误选。</summary>
    private Guid _preferredDeviceBootId;
    private Point _dragStart;
    private Point _dragCurrent;
    private Point _apDragStart;
    private ApConsumptionWorkItem? _apDraggedItem;
    private DragPreviewAdorner? _apDragAdorner;
    private ListBoxItem? _apDraggedContainer;
    private DropIndicatorAdorner? _apDropIndicator;
    private Point _dailyDragStart;
    private DailyTaskItem? _dailyDraggedItem;
    private DragPreviewAdorner? _dailyDragAdorner;
    private ListBoxItem? _dailyDraggedContainer;
    private DropIndicatorAdorner? _dailyDropIndicator;
    private bool _dailyDragMoved;
    private bool _loadingApProfile;

    /// <summary>切换明暗主题并更新按钮含义；运行中的任务与当前界面状态保持不变。</summary>
    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.ToggleTheme();
        var themeConfig = SnapshotConfig();
        themeConfig.IsDarkMode = app.IsDarkMode;
        if (!_executionGate.IsHeld) _config = themeConfig;
        UpdateThemeButton();
        // 切换即保存，退出或异常结束后也能恢复；未初始化的设计/测试窗口不写磁盘。
        if (_logger is not null)
        {
            try { new ConfigService(_logger).Save(themeConfig, ConfigService.ResolvePath(DefaultConfigFile)); }
            catch (Exception ex) { _logger.Warn(L.Format($"主题设置保存失败: {ex.GetType().Name}")); }
        }
    }

    /// <summary>应用主题恢复后同步灯泡外观和提示。</summary>
    private void UpdateThemeButton()
    {
        var app = (App)Application.Current;
        BulbGlass.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            app.IsDarkMode ? "Brush.CardAlt" : "Brush.TextDim");
        BulbRays.Visibility = app.IsDarkMode ? Visibility.Collapsed : Visibility.Visible;
        var label = app.IsDarkMode ? L.Text("切换到日间模式") : L.Text("切换到夜间模式");
        ThemeToggleButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(ThemeToggleButton, label);
    }

    public MainWindow()
    {
        InitializeComponent();
        AutoProgressQueueList.ItemsSource = _autoProgressQueue;
        LoadAutoProgressSettings();
        // 仅锁的持有者登记窗口身份，重复启动发布版时不会选中并存的开发版。
        SourceInitialized += (_, _) => App.RegisterSingleInstanceWindow(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        CopyrightText.Text = AppCopyright;
        ApWorkList.ItemsSource = _apWorkItems;
        DailyTaskList.ItemsSource = _dailyTasks;
        RewardTaskList.ItemsSource = _rewardTasks;
        StagePositionList.ItemsSource = _stagePositionItems;
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

    /// <summary>初始化每日任务默认列表；顺序即执行顺序，用户可在界面上拖拽调整。</summary>
    private void InitializeDailyTasks()
    {
        _dailyTasks.Add(new DailyTaskItem
        {
            Kind = DailyTaskKind.HarvestBattleship,
            Name = L.Text("回收战舰（个人基地）"),
            IsChecked = true,
        });
        _dailyTasks.Add(new DailyTaskItem
        {
            Kind = DailyTaskKind.ShopFreeGift,
            Name = L.Text("商店购买免费礼包"),
            IsChecked = true,
        });
        _dailyTasks.Add(new DailyTaskItem
        {
            Kind = DailyTaskKind.ClaimRewards,
            Name = L.Text("领取奖励"),
            IsChecked = true,
        });
        _dailyTasks.Add(new DailyTaskItem
        {
            Kind = DailyTaskKind.DevelopOnce,
            Name = L.Text("开发&强化一次"),
            IsChecked = true,
        });
        _dailyTasks.Add(new DailyTaskItem
        {
            Kind = DailyTaskKind.UpgradeStages,
            Name = L.Text("强化培育关卡"),
            IsChecked = true,
        });
        _dailyTasks.Add(new DailyTaskItem
        {
            Kind = DailyTaskKind.ConsumeAp,
            Name = L.Text("消耗AP"),
            IsChecked = true,
        });

        // 订阅勾选与能力状态变化，保持“全选”与当前可执行任务同步。
        foreach (var task in _dailyTasks)
        {
            task.PropertyChanged += DailyTask_StateChanged;
        }

        UpdateDailySelectAllState();
    }

    /// <summary>每日任务“全选”只影响当前模拟器支持的任务，避免把禁用项重新写成隐式勾选。</summary>
    private void DailySelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;

        var selectAll = DailySelectAllCheckBox.IsChecked == true;
        foreach (var task in _dailyTasks.Where(task => task.IsSupported && IsDailyTaskAvailable(task)))
        {
            task.IsChecked = selectAll;
        }
    }

    /// <summary>任一任务的勾选或支持状态变化时，同步更新“全选”勾选框。</summary>
    private void DailyTask_StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DailyTaskItem.IsChecked) &&
            sender is DailyTaskItem { Kind: DailyTaskKind.FierceEnemySkip }) SaveFierceEnemyDailyTask();
        if (e.PropertyName is nameof(DailyTaskItem.IsChecked) or nameof(DailyTaskItem.IsSupported))
        {
            UpdateDailySelectAllState();
        }
    }

    /// <summary>仅当所有受支持任务都已勾选时，“全选”显示为勾选状态。</summary>
    private void UpdateDailySelectAllState()
    {
        var supportedTasks = _dailyTasks.Where(task => task.IsSupported && IsDailyTaskAvailable(task)).ToList();
        DailySelectAllCheckBox.IsChecked = supportedTasks.Count > 0 && supportedTasks.All(task => task.IsChecked);
    }

    /// <summary>初始化奖励页默认选项；领取顺序即列表顺序。</summary>
    private void InitializeRewardTasks()
    {
        _rewardTasks.Add(new RewardTaskItem
        {
            Kind = RewardTaskKind.MailReward,
            Name = L.Text("领取邮件奖励"),
            IsChecked = true,
        });
        _rewardTasks.Add(new RewardTaskItem
        {
            Kind = RewardTaskKind.MissionReward,
            Name = L.Text("领取每日使命奖励"),
            IsChecked = true,
        });
    }

    /// <summary>窗口统一拥有关卡与继续文字的 CPU OCR；重建自动化服务时仍借用同一模型。</summary>
    private OnnxTextRecognizer GetTextOcr() => _textOcr ??= new OnnxTextRecognizer(
        ConfigService.ResolvePath($"resources/ocr/{OnnxTextRecognizer.ModelFileName}"));

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
        await RunWindowTaskAsync(CheckStartupUpdateAsync);
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
            try { new ConfigService(_logger).Save(_config, configPath); }
            catch (Exception ex) { _logger.Warn(L.Format($"界面语言保存失败：{ex.Message}")); }
            ConfigService.ApplyResourceLanguage(_config);
            RestoreFierceEnemyDailyTask();
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
                AdbDiscovery.Apply(_config, candidates);
                if (string.IsNullOrWhiteSpace(_config.EmulatorPath))
                {
                    var type = EmulatorTypes.DetectFromAdbPath(_config.AdbPath) ?? EmulatorTypes.Custom;
                    _config.EmulatorPath = AdbDiscovery.FindLauncher(_config.AdbPath, type) ?? "";
                }
                try { new ConfigService(_logger).Save(_config, configPath); }
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
            _adb = new AdbService(_config.AdbPath, _logger, _config.CommandTimeoutMs, _config.ScreenshotTimeoutMs, _environmentDiagnostics, _config.EmulatorPath, _config.PreferMuMuNativeScreenshot, _config.GamePackage);
            _deviceCompatibility = new DeviceCompatibilityService(_adb);
            _connectionErrorDetector = CreateConnectionErrorDetector();
            ConfigureExecutionMonitoring();
            _automation = new AutomationService(_adb, _matcher, _logger, connectionErrorDetector: _connectionErrorDetector,
                skipTemplatePath: ConfigService.ResolvePath(_config.LoginRewardSkipTemplate), textOcr: GetTextOcr());
            _emulatorAdapter = EmulatorAdapterFactory.Create(_config.EmulatorType, _adb, _logger, mumuPathResolver: () => EmulatorLocator.ResolveMuMuManager(_config.AdbPath, _config.EmulatorPath));
            _emulator = new EmulatorService(_emulatorAdapter);
            _gameEntry = new GameEntryService(_automation, _logger);
            _mainScreenNavigator = CreateMainScreenNavigator();
            _uiLayoutService = new UiLayoutService(_logger);
            ReloadLayouts();
            _battleshipLocator = new BattleshipLocator(_adb, _matcher, _logger);
            _personalBaseService = new PersonalBaseService(_automation, _battleshipLocator, _logger);
            _shopService = new ShopService(_automation, new DiamondSafetyChecker(_adb, _matcher, _logger), _logger);
            _giftService = new GiftService(_automation, _logger);
            _developService = new DevelopService(_automation, _logger, _config.DevelopMsFixedScales);
            ApplyEmulatorCapabilities();
            _stageService = CreateStageService();
            _upgradeStagesService = new UpgradeStagesService(_automation, CreateStageSkipService(forUpgradeStages: true), _logger);
            _missionRewardService = new MissionRewardService(_automation, _logger);
            _downloadDialogService = CreateDownloadDialogService();
            _sortieFlowService = CreateSortieFlowService();
            _apProfileService = new ApConsumptionProfileService(_logger);
            LoadStageWorks();
            LoadApProfiles();

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

    /// <summary>刷新按钮：重新检测 ADB 设备。</summary>
    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => RefreshButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task RefreshButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null) return;

        await RefreshDevicesAsync();
    }

    /// <summary>重连纳入窗口生命周期与设备互斥，只处理点击时明确选中的离线地址。</summary>
    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
        => await RunWindowTaskAsync(() => ReconnectButton_ClickCoreAsync(sender, e));

    /// <summary>恢复通道后刷新身份并要求重新选择，避免端口复用时自动操作其他账号。</summary>
    private async Task ReconnectButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null || _adb is null || DeviceCombo.SelectedItem is not AdbDevice selected || !selected.CanReconnect)
            return;
        ConnectionStateText.Text = L.Text("正在重连…");
        try
        {
            // 重连不继承旧的启动身份，也不允许刷新后自动绑定重用该端口的新实例。
            _refreshingDeviceSelection = true;
            try { DeviceCombo.SelectedItem = null; }
            finally { _refreshingDeviceSelection = false; }
            _preferredDeviceSerial = selected.Serial;
            _preferredDeviceBootId = selected.BootId;
            await SyncDeviceServicesAsync(null, ct: _windowTasks.Token);
            await _adb.ReconnectDeviceAsync(selected, _windowTasks.Token);
            if (await RefreshDevicesAsync(_windowTasks.Token, preserveSelection: false))
                ConnectionStateText.Text = L.Text("重连成功，请重新选择设备");
        }
        catch (OperationCanceledException)
        {
            ConnectionStateText.Text = L.Text("重连已取消");
        }
        catch (Exception ex)
        {
            _logger?.Warn(L.Format($"设备重连未完成 [{selected.Serial}]: {ex.Message}"));
            ConnectionStateText.Text = L.Format($"重连未完成: {ex.Message}");
        }
    }

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
        var settings = new SettingsWindow(SnapshotConfig(), _logger, allowUpdateInstall: true,
            availableUpdate: _startupUpdate, saveUpdatePreference: SaveUpdatePreference, plugins: _plugins) { Owner = this };
        var saved = settings.ShowDialog();
        if (settings.UpdateRequested)
        {
            CloseForUpdate();
            return;
        }
        if (saved != true)
        {
            await SyncPluginsAsync((DeviceCombo.SelectedItem as AdbDevice)?.Serial, _windowTasks.Token);
            return;
        }

        // 应用新配置：重建依赖 ADB 路径的服务；旧会话仅借用窗口 OCR。
        _automation?.Dispose();
        _config = settings.Config;
        _textOcr?.Dispose();
        _textOcr = null;
        ConfigService.ApplyResourceLanguage(_config);
        CompleteKnownEmulatorPaths();
        _adb = new AdbService(_config.AdbPath, _logger, _config.CommandTimeoutMs, _config.ScreenshotTimeoutMs, _environmentDiagnostics, _config.EmulatorPath, _config.PreferMuMuNativeScreenshot, _config.GamePackage);
        _deviceCompatibility = new DeviceCompatibilityService(_adb);
        _connectionErrorDetector = CreateConnectionErrorDetector();
        ConfigureExecutionMonitoring();
        _automation = new AutomationService(_adb, _matcher, _logger, connectionErrorDetector: _connectionErrorDetector,
                skipTemplatePath: ConfigService.ResolvePath(_config.LoginRewardSkipTemplate), textOcr: GetTextOcr());
        _emulatorAdapter = EmulatorAdapterFactory.Create(_config.EmulatorType, _adb, _logger, mumuPathResolver: () => EmulatorLocator.ResolveMuMuManager(_config.AdbPath, _config.EmulatorPath));
        _emulator = new EmulatorService(_emulatorAdapter);
        _gameEntry = new GameEntryService(_automation, _logger);
        _mainScreenNavigator = CreateMainScreenNavigator();
        ReloadLayouts();
        _battleshipLocator = new BattleshipLocator(_adb, _matcher, _logger);
        _personalBaseService = new PersonalBaseService(_automation, _battleshipLocator, _logger);
        _shopService = new ShopService(_automation, new DiamondSafetyChecker(_adb, _matcher, _logger), _logger);
        _giftService = new GiftService(_automation, _logger);
        _developService = new DevelopService(_automation, _logger, _config.DevelopMsFixedScales);
        ApplyEmulatorCapabilities();
        _stageService = CreateStageService();
        _upgradeStagesService = new UpgradeStagesService(_automation, CreateStageSkipService(forUpgradeStages: true), _logger);
        _missionRewardService = new MissionRewardService(_automation, _logger);
        _downloadDialogService = CreateDownloadDialogService();
        _sortieFlowService = CreateSortieFlowService();
        _apProfileService = new ApConsumptionProfileService(_logger);
        LoadStageWorks();
        LoadApProfiles();
        UpdateFooterText();
        RefreshTemplateList();
        _logger.Info(L.Format($"设置已应用：区服 {_config.GameServer}，游戏语言 {_config.GameLanguage}，ADB 路径 {_config.AdbPath}"));

        await RefreshDevicesAsync();
    }

    /// <summary>
    /// 为已知模拟器补全配套程序路径。BlueStacks 的 HD-Adb 与 HD-Player 必须来自同一安装，
    /// 旧配置在升级后也可直接获得新的自动探测能力。
    /// </summary>
    private void CompleteKnownEmulatorPaths()
    {
        if (string.Equals(_config.EmulatorType, EmulatorTypes.Custom, StringComparison.OrdinalIgnoreCase))
        {
            // 兼容旧配置：此前可能只保存路径而未保存类型，升级后按路径恢复正确适配器。
            _config.EmulatorType = EmulatorTypes.DetectFromAdbPath(_config.AdbPath)
                                   ?? EmulatorTypes.DetectFromEmulatorPath(_config.EmulatorPath)
                                   ?? EmulatorTypes.Custom;
        }

        if (EmulatorTypes.IsBlueStacks(_config.EmulatorType))
        {
            var installation = EmulatorLocator.FindBlueStacksNearPaths(
                                   _config.AdbPath,
                                   _config.EmulatorPath)
                               ?? EmulatorLocator.FindBlueStacksInstallation();
            if (installation is not null)
            {
                _config.AdbPath = installation.AdbPath;
                _config.EmulatorPath = installation.LauncherPath;
            }
        }
        else if (EmulatorTypes.IsMuMu(_config.EmulatorType) &&
                 string.IsNullOrWhiteSpace(_config.EmulatorPath))
        {
            _config.EmulatorPath = EmulatorLocator.ResolveMuMuManager(_config.AdbPath, _config.EmulatorPath) ?? string.Empty;
        }
    }

    /// <summary>开发任务使用通用 ADB，不再按品牌禁用；保留用户已有的勾选意愿。</summary>
    private void ApplyEmulatorCapabilities()
    {
        var developTask = _dailyTasks.FirstOrDefault(task => task.Kind == DailyTaskKind.DevelopOnce);
        if (developTask is null) return;
        developTask.IsSupported = true;
        developTask.UnsupportedReason = null;
    }

    /// <summary>刷新与手动选择共用连接文案和设备能力更新，避免显示旧设备状态。</summary>
    private void UpdateSelectedDeviceState(AdbDevice? selected)
    {
        ConnectionStateText.Text = selected?.IsAvailable == true ? L.Text("已连接") :
            selected?.IsOffline == true ? L.Text("设备离线") : selected is null ? L.Text("未连接") : L.Format($"设备不可用 ({selected.State})");
        AccountStatusText.Text = selected?.IsAvailable == true ? L.Text("设备已连接") : L.Text("等待连接设备");
        if (selected is not null)
            DeviceStatusText.Text = selected.CanReconnect ? L.Text("ADB 连接失效，请点击重连") :
                selected.IsAvailable ? L.Text("目标设备已选中") : L.Text("请检查模拟器与 ADB 状态");
        else
            DeviceStatusText.Text = DeviceCombo.Items.Count == 0 ? L.Text("未检测设备") : L.Text("请在设备列表中选择目标设备");
        UpdateBusyState();
        ApplyEmulatorCapabilities();
    }

    /// <summary>一键启动：启动模拟器 → 等待设备上线 → 打开游戏。</summary>
    private async void StartGameButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => StartGameButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task StartGameButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        if (_starting)
        {
            _logger?.Info(L.Text("用户请求中止启动…"));
            StartGameButton.Content = L.Text("正在停止…");
            StartGameButton.IsEnabled = false;
            _startCts?.Cancel();
            return;
        }

        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (_adb is null || string.IsNullOrWhiteSpace(_config.GamePackage))
        {
            _logger?.Warn(L.Text("请先在设置中配置游戏包名"));
            return;
        }

        // 离线目标不能进入默认实例启动分支；直接说明可执行的恢复操作。
        if (DeviceCombo.SelectedItem is AdbDevice unavailable && !unavailable.IsAvailable)
        {
            UpdateSelectedDeviceState(unavailable);
            _logger?.Warn(L.Format($"所选设备不可用 [{unavailable.Serial}, {unavailable.State}]；请先恢复连接"));
            return;
        }

        // 当前选中的可用设备优先：直接在该模拟器上打开游戏，不再启动默认 MuMu0。
        if (DeviceCombo.SelectedItem is AdbDevice selectedDevice && selectedDevice.IsAvailable)
        {
            _starting = true;
            _startCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
            var ct = _startCts.Token;
            StartGameButton.Content = L.Text("中止");
            UpdateBusyState();
            ConnectionStateText.Text = L.Format($"正在打开游戏: {selectedDevice.Serial}");
            try
            {
                // 列表是上次刷新的快照；操作前重新确认目标在线，绝不回退其他设备。
                if (!(await _adb.ListDevicesAsync(ct)).Any(device =>
                        device.IsAvailable && device.Serial == selectedDevice.Serial))
                    throw new AdbException(L.Text("所选设备已离线，请刷新并确认目标设备；未切换其他实例"));
                if (selectedDevice.BootId != Guid.Empty &&
                    (!Guid.TryParse(await _adb.GetBootIdAsync(selectedDevice.Serial, ct), out var bootId) || bootId != selectedDevice.BootId))
                    throw new AdbException(L.Text("所选设备已重启或身份发生变化，请刷新并重新选择目标设备"));
                await SyncDeviceServicesAsync(selectedDevice.Serial, ct: ct);
                await _adb.LaunchAppAsync(selectedDevice.Serial, _config.GamePackage, ct);
                _preferredDeviceSerial = selectedDevice.Serial;
                await OpenGameAndEnterAsync(selectedDevice.Serial, ct);
                await RefreshDevicesAsync(ct);
            }
            catch (OperationCanceledException)
            {
                ConnectionStateText.Text = L.Text("已中止启动");
                _logger?.Warn(L.Text("启动游戏已中止"));
            }
            catch (Exception ex)
            {
                _logger?.Error(L.Format($"打开游戏失败: {ex.Message}"));
                ConnectionStateText.Text = L.Format($"打开游戏失败: {ex.Message}");
            }
            finally
            {
                _starting = false;
                _startCts?.Dispose();
                _startCts = null;
                StartGameButton.Content = L.Text("启动并打开游戏");
                UpdateBusyState();
            }

            return;
        }

        if (_emulator is null)
        {
            _logger?.Warn(L.Text("未配置模拟器启动服务"));
            return;
        }

        _starting = true;
        _startCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var launchCt = _startCts.Token;
        StartGameButton.Content = L.Text("中止");
        UpdateBusyState();
        ConnectionStateText.Text = L.Text("正在启动模拟器…");
        try
        {
            // 没有选择不等于允许使用默认实例，尤其是原目标刚离线的情况。
            if (!string.IsNullOrEmpty(_preferredDeviceSerial) ||
                (await _adb.ListDevicesAsync(launchCt)).Any(device => device.IsAvailable))
                throw new AdbException(L.Text("请先在设备列表中明确选择目标；原目标离线时不会自动启动或操作其他实例"));
            var options = new EmulatorLaunchOptions
            {
                EmulatorType = _config.EmulatorType,
                EmulatorPath = _config.EmulatorPath,
                EmulatorArgs = _config.EmulatorArgs,
                GamePackage = _config.GamePackage,
                DeviceWaitTimeoutMs = _config.DeviceWaitTimeoutMs,
                BootCompletedTimeoutMs = _config.BootCompletedTimeoutMs,
                BeforeGameLaunchAsync = (serial, ct) => SyncDeviceServicesAsync(serial, ct: ct, newlyStarted: true),
            };

            var result = await _emulator.StartAndOpenGameAsync(options, launchCt);
            ConnectionStateText.Text = result.Success
                ? L.Format($"已打开游戏: {_config.GamePackage}")
                : L.Format($"启动失败: {result.Message}");
            _logger?.Info(L.Format($"一键启动完成: {result.Message}"));

            if (result.Success)
            {
                _preferredDeviceSerial = result.Serial;
                Guid.TryParse(await _adb.GetBootIdAsync(result.Serial!, launchCt), out _preferredDeviceBootId);
                await OpenGameAndEnterAsync(result.Serial!, launchCt);
            }

            await RefreshDevicesAsync(launchCt);
        }
        catch (OperationCanceledException)
        {
            ConnectionStateText.Text = L.Text("已中止启动");
            _logger?.Warn(L.Text("启动游戏已中止"));
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"一键启动失败: {ex.Message}"));
            ConnectionStateText.Text = L.Format($"启动失败: {ex.Message}");
        }
        finally
        {
            _starting = false;
            _startCts?.Dispose();
            _startCts = null;
            StartGameButton.Content = L.Text("启动并打开游戏");
            UpdateBusyState();
        }
    }

    /// <summary>打开游戏并执行入口流程（tap to start → 确认主界面），支持取消。</summary>
    private async Task OpenGameAndEnterAsync(string serial, CancellationToken ct)
    {
        if (_gameEntry is null)
        {
            return;
        }

        // 两个启动入口都先打开游戏并等待显示建立，再检查真实游戏画面；原生截图不能用于启动前检查。
        await Task.Delay(Math.Max(0, _config.SplashDelayMs), ct);
        if (!await EnsureDeviceCompatibilityAsync(serial, ct))
        {
            return;
        }

        var templatePath = ConfigService.ResolvePath(_config.TapToStartTemplate);
        var wait = new WaitOptions
        {
            TimeoutMs = _config.TapToStartTimeoutMs,
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = _config.DefaultMatchThreshold,
        };
        var entered = await _gameEntry.EnterGameAsync(
            serial: serial,
            tapToStartTemplatePath: templatePath,
            splashDelayMs: 0, // 启动画面等待已在兼容性截图前完成，避免重复等待。
            networkSettleDelayMs: 1500,
            waitOptions: wait,
            tapToNextTemplatePath: ConfigService.ResolvePath(_config.TapToNextTemplate),
            loginRewardWaitMs: _config.LoginRewardWaitMs,
            tapToNextLoginRewardTemplatePath: ConfigService.ResolvePath(_config.TapToNextLoginRewardTemplate),
            loginRewardSkipTemplatePath: ConfigService.ResolvePath(_config.LoginRewardSkipTemplate),
            loginRewardFallbackDelayMs: _config.LoginRewardFallbackDelayMs,
            loginRewardAnnouncementTemplatePath: ConfigService.ResolvePath(_config.LoginRewardAnnouncementTemplate),
            loginRewardAnnouncementCloseTemplatePath: ConfigService.ResolvePath(_config.LoginRewardAnnouncementCloseTemplate),
            loginRewardAnnouncementWaitMs: _config.LoginRewardAnnouncementWaitMs,
            loginRewardPageWaitMs: _config.LoginRewardPageWaitMs,
            ct: ct);

        if (await IsHumanVerificationVisibleAsync(serial, ct))
        {
            ConnectionStateText.Text = L.Text("检测到人机验证，请检查网络状况");
            _logger?.Warn(L.Text("检测到人机验证，请检查网络状况"));
            return;
        }

        if (await IsDataSyncConfirmVisibleAsync(serial, ct))
        {
            ConnectionStateText.Text = L.Text("检测到确认解除数据同步，请检查当前状况");
            _logger?.Warn(L.Text("检测到确认解除数据同步，请检查当前状况"));
            return;
        }

        if (await IsDownloadPromptVisibleAsync(serial, ct))
        {
            ConnectionStateText.Text = L.Text("检测到下载确认，请检查当前状况");
            _logger?.Warn(L.Text("检测到下载确认，请检查当前状况"));
            return;
        }

        if (entered && _mainScreenNavigator is not null)
        {
            var (isMain, humanVerification, dataSyncConfirm, downloadPrompt) =
                await WaitForMainScreenOrVerificationAsync(serial, ct);
            if (humanVerification)
            {
                ConnectionStateText.Text = L.Text("检测到人机验证，请检查网络状况");
                return;
            }

            if (dataSyncConfirm)
            {
                ConnectionStateText.Text = L.Text("检测到确认解除数据同步，请检查当前状况");
                return;
            }

            if (downloadPrompt)
            {
                ConnectionStateText.Text = L.Text("检测到下载确认，请检查当前状况");
                return;
            }

            ConnectionStateText.Text = isMain
                ? L.Text("已进入主界面")
                : L.Text("已进入游戏，但未确认主界面");
        }
        else
        {
            ConnectionStateText.Text = entered
                ? L.Text("已进入游戏")
                : L.Text("已打开游戏，但未完成 tap to start");
        }
    }

    /// <summary>等待主界面，同时轮询人机验证；命中验证时立即中止。</summary>
    private async Task<(bool IsMain, bool HumanVerification, bool DataSyncConfirm, bool DownloadPrompt)> WaitForMainScreenOrVerificationAsync(
        string serial,
        CancellationToken ct)
    {
        if (_mainScreenNavigator is null)
        {
            return (false, false, false, false);
        }

        var humanVerification = false;
        var dataSyncConfirm = false;
        var downloadPrompt = false;
        // 特殊状态由 UI 呈现；等待预算、连续帧与超时诊断归统一流程服务所有。
        async Task<bool> StopWhenAsync(CancellationToken token)
        {
            if (await IsHumanVerificationVisibleAsync(serial, token))
            {
                humanVerification = true;
                _logger?.Warn(L.Text("检测到人机验证，请检查网络状况"));
                return true;
            }
            if (await IsDataSyncConfirmVisibleAsync(serial, token))
            {
                dataSyncConfirm = true;
                _logger?.Warn(L.Text("检测到确认解除数据同步，请检查当前状况"));
                return true;
            }
            if (await IsDownloadPromptVisibleAsync(serial, token))
            {
                downloadPrompt = true;
                _logger?.Warn(L.Text("检测到下载确认，请检查当前状况"));
                return true;
            }
            return false;
        }
        var isMain = await _mainScreenNavigator.WaitForMainScreenAsync(serial,
            _config.MainScreenWaitMs, ct: ct, stopWhen: StopWhenAsync);
        return (isMain, humanVerification, dataSyncConfirm, downloadPrompt);
    }

    /// <summary>识别“我们需要确认您是人类”人机验证标题。</summary>
    private async Task<bool> IsHumanVerificationVisibleAsync(string serial, CancellationToken ct)
    {
        if (_automation is null)
        {
            return false;
        }

        var wait = new WaitOptions
        {
            Threshold = 0.80,
        };
        var match = await _automation.FindImageAsync(
            serial,
            ConfigService.ResolvePath(_config.HumanVerificationTemplate),
            wait,
            ct);
        return match is not null;
    }

    /// <summary>识别“确认解除数据同步”弹窗标题。</summary>
    private async Task<bool> IsDataSyncConfirmVisibleAsync(string serial, CancellationToken ct)
    {
        if (_automation is null)
        {
            return false;
        }

        var wait = new WaitOptions
        {
            Threshold = 0.80,
        };
        var match = await _automation.FindImageAsync(
            serial,
            ConfigService.ResolvePath(_config.DataSyncConfirmTemplate),
            wait,
            ct);
        return match is not null;
    }

    /// <summary>识别“下载”确认弹窗标题。</summary>
    private async Task<bool> IsDownloadPromptVisibleAsync(string serial, CancellationToken ct)
    {
        if (_automation is null)
        {
            return false;
        }

        var wait = new WaitOptions
        {
            Threshold = 0.80,
        };
        var match = await _automation.FindImageAsync(
            serial,
            ConfigService.ResolvePath(_config.DownloadPromptTemplate),
            wait,
            ct);
        return match is not null;
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

    /// <summary>刷新 ADB 设备列表。</summary>
    private async Task<bool> RefreshDevicesAsync(CancellationToken ct = default, bool preserveSelection = true)
    {
        using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _windowTasks.Token);
        ct = refreshCts.Token;
        ct.ThrowIfCancellationRequested();
        if (_adb is null || _emulatorAdapter is null)
        {
            return false;
        }

        UpdateBusyState();
        DeviceStatusText.Text = L.Text("正在检测设备…");
        try
        {
            // 端口探测、去重与品牌归属都由当前适配器处理，UI 只消费标准设备列表。
            var previousSerial = _preferredDeviceSerial ??
                                 (DeviceCombo.SelectedItem as AdbDevice)?.Serial;
            var devices = (await _emulatorAdapter.DiscoverDevicesAsync(EmulatorScanMaxInstances, ct))
                .ToList();
            AdbDevice? selected;
            _refreshingDeviceSelection = true;
            try
            {
                DeviceCombo.ItemsSource = devices;
                selected = preserveSelection
                    ? EmulatorDeviceSelector.SelectForRefresh(devices, previousSerial, _preferredDeviceBootId)
                    : null;
                DeviceCombo.SelectedItem = selected;
            }
            finally
            {
                _refreshingDeviceSelection = false;
            }

            // 目标离线时保留意图，重复刷新不能把丢失目标转换成一次新的默认启动。
            if (selected is not null)
            {
                _preferredDeviceSerial = selected.Serial;
                _preferredDeviceBootId = selected.BootId;
            }
            UpdateSelectedDeviceState(selected);
            DeviceStatusText.Text = devices.Count == 0
                ? _emulatorAdapter.NoDeviceMessage
                : selected is null ? devices.All(device => device.IsOffline)
                    ? L.Text("设备离线，请选择目标后重连") : L.Text("请在设备列表中选择目标设备")
                : L.Format($"检测到 {devices.Count} 个设备");

            try
            {
                await SyncDeviceServicesAsync(selected?.Serial, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Error(L.Format($"同步设备服务失败: {ex.Message}"));
                ConnectionStateText.Text = L.Text("设备服务处理失败，请检查日志与设置");
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"设备检测失败: {ex.Message}"));
            ConnectionStateText.Text = L.Text("连接失败");
            AccountStatusText.Text = L.Text("等待连接设备");
            return false;
        }
        finally
        {
            UpdateBusyState();
        }
    }

    /// <summary>用户手动切换设备时，记录为后续每日任务使用的设备。</summary>
    private async void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunWindowTaskAsync(() => DeviceCombo_SelectionChangedCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task DeviceCombo_SelectionChangedCoreAsync(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingDeviceSelection) return;
        using var execution = TryBeginExecution();
        if (execution is null)
        {
            // 即使程序触发 SelectionChanged，也恢复会话目标；不能只依赖控件禁用。
            _refreshingDeviceSelection = true;
            try
            {
                DeviceCombo.SelectedItem = DeviceCombo.Items.Cast<AdbDevice>()
                    .FirstOrDefault(device => device.Serial == _preferredDeviceSerial);
            }
            finally { _refreshingDeviceSelection = false; }
            return;
        }
        var selected = DeviceCombo.SelectedItem as AdbDevice;
        _preferredDeviceSerial = selected?.Serial;
        _preferredDeviceBootId = selected?.BootId ?? Guid.Empty;
        UpdateSelectedDeviceState(selected);
        try
        {
            await SyncDeviceServicesAsync(selected?.IsAvailable == true ? selected.Serial : null, ct: _windowTasks.Token);
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"切换设备服务失败: {ex.Message}"));
            ConnectionStateText.Text = L.Text("设备服务处理失败，请检查日志与设置");
        }
    }

    /// <summary>招募操作纳入窗口任务生命周期，关闭时取消并等待收尾。</summary>
    private async void ScoutStartButton_Click(object sender, RoutedEventArgs e)
        => await RunWindowTaskAsync(StartScoutAsync);

    /// <summary>持有设备执行权运行招募循环；再次点击中止，并在退出后统一释放状态。</summary>
    private async Task StartScoutAsync()
    {
        if (_scoutRunning)
        {
            ScoutStartButton.Content = L.Text("正在停止…");
            _scoutCts?.Cancel();
            UpdateBusyState();
            return;
        }
        using var execution = TryBeginExecution();
        if (execution is null) return;
        if (_automation is null || !TryGetDeviceSerial(out var serial))
        {
            ScoutStatusText.Text = L.Text("请先连接并选择模拟器。");
            return;
        }
        _scoutRunning = true;
        _scoutCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var ct = _scoutCts.Token;
        var completed = 0;
        var insufficientResources = false;
        ScoutStartButton.Content = L.Text("中止招募");
        ScoutStatusText.Text = L.Text("正在检查招募模板…");
        UpdateBusyState();
        var endNotification = BeginTaskEndNotification(L.Text("自动招募"));
        try
        {
            var templates = new ScoutTemplates(
                ConfigService.ResolvePath(ScoutService.TitleTemplate),
                ConfigService.ResolvePath(ScoutService.ButtonTemplate),
                ConfigService.ResolvePath(ScoutService.ExecuteHintTemplate),
                ConfigService.ResolvePath(ScoutService.ExecuteTemplate),
                ConfigService.ResolvePath(ScoutService.NextTemplate),
                ConfigService.ResolvePath(ScoutService.NoCharactersTemplate),
                ConfigService.ResolvePath(ScoutService.ClosedButtonTemplate));
            var result = await new ScoutService(_automation).RunAsync(serial, templates, _developLayout, (count, message) =>
            {
                completed = count;
                ScoutStatusText.Text = L.Format($"已完成 {count} 轮 · {message}");
                _logger?.Info(message);
            }, _config.DevelopTitleThreshold, ct);
            insufficientResources = result == ScoutResult.InsufficientResources;
            if (insufficientResources)
            {
                // 恢复期间保留设备执行权和取消令牌，禁止其他任务插入或恢复后重新招募。
                var recovered = await EnsureMainScreenAsync(serial, ct);
                ScoutStatusText.Text = L.Format($"招募资源不足，自动招募已中止，已完成 {completed} 轮；")
                    + (recovered ? L.Text("已返回主界面。") : L.Text("未能确认主界面，请检查游戏画面。"));
                if (recovered) _logger?.Info(ScoutStatusText.Text);
                else _logger?.Warn(ScoutStatusText.Text);
            }
            else
            {
                ScoutStatusText.Text = L.Format($"自动招募完成，已完成 {completed} 轮，没有可招募的角色。");
                endNotification.Succeeded = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ScoutStatusText.Text = insufficientResources
                ? L.Format($"招募资源不足，自动招募已中止，已完成 {completed} 轮；主界面恢复已取消。")
                : L.Format($"自动招募已中止，已完成 {completed} 轮。");
            _logger?.Info(ScoutStatusText.Text);
        }
        catch (Exception ex)
        {
            ScoutStatusText.Text = insufficientResources
                ? L.Format($"招募资源不足，自动招募已中止，已完成 {completed} 轮；主界面恢复失败：{ex.Message}")
                : L.Format($"自动招募已停止，已完成 {completed} 轮：{ex.Message}");
            _logger?.Error(ScoutStatusText.Text);
        }
        finally
        {
            _scoutRunning = false;
            _scoutCts.Dispose();
            _scoutCts = null;
            ScoutStartButton.Content = L.Text("开始招募");
            UpdateBusyState();
            CompleteTaskEndNotification(endNotification, ScoutStatusText.Text);
        }
    }

    /// <summary>截图并显示到界面。</summary>
    private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => ScreenshotButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task ScreenshotButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (!TryGetDeviceSerial(out var serial))
        {
            return;
        }

        UpdateBusyState();
        try
        {
            var png = await _adb!.ScreenshotAsync(serial, _windowTasks.Token);
            _currentScreen = await Task.Run(() => ImageCodec.DecodePng(png));

            ScreenImage.Source = _currentScreen.Bitmap;
            OverlayCanvas.Width = _currentScreen.Width;
            OverlayCanvas.Height = _currentScreen.Height;
            ClearSelection();
            _logger!.Info(L.Format($"截图完成: {_currentScreen.Width}x{_currentScreen.Height}"));
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"截图失败: {ex.Message}"));
        }
        finally
        {
            UpdateBusyState();
        }
    }

    /// <summary>点击截图中选中的坐标点。</summary>
    private async void TapButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => TapButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task TapButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (!TryGetDeviceSerial(out var serial) || _adb is null)
        {
            return;
        }

        if (_selectedPoint is not Point point)
        {
            _logger?.Warn(L.Text("请先在截图上单击选择坐标点"));
            return;
        }

        UpdateBusyState();
        try
        {
            await _adb.TapAsync(serial, (int)Math.Round(point.X), (int)Math.Round(point.Y), _windowTasks.Token);
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"点击失败: {ex.Message}"));
        }
        finally
        {
            UpdateBusyState();
        }
    }

    /// <summary>把当前选中点设为滑动起点。</summary>
    private void SwipeStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPoint is not Point point)
        {
            _logger?.Warn(L.Text("请先在截图上单击选择坐标点"));
            return;
        }

        _swipeStart = point;
        SwipeStartButton.Content = L.Format($"起点 {point.X:0},{point.Y:0}");
        DrawOverlay();
        _logger?.Info(L.Format($"滑动起点已设置: ({point.X:0}, {point.Y:0})"));
    }

    /// <summary>把当前选中点设为滑动终点。</summary>
    private void SwipeEndButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPoint is not Point point)
        {
            _logger?.Warn(L.Text("请先在截图上单击选择坐标点"));
            return;
        }

        _swipeEnd = point;
        SwipeEndButton.Content = L.Format($"终点 {point.X:0},{point.Y:0}");
        DrawOverlay();
        _logger?.Info(L.Format($"滑动终点已设置: ({point.X:0}, {point.Y:0})"));
    }

    /// <summary>执行起点到终点的滑动。</summary>
    private async void SwipeButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => SwipeButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task SwipeButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (!TryGetDeviceSerial(out var serial) || _adb is null)
        {
            return;
        }

        if (_swipeStart is not Point start || _swipeEnd is not Point end)
        {
            _logger?.Warn(L.Text("请先设置滑动起点与终点"));
            return;
        }

        if (!int.TryParse(SwipeDurationBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration))
        {
            duration = 500;
        }

        UpdateBusyState();
        try
        {
            await _adb.SwipeAsync(
                serial,
                (int)Math.Round(start.X), (int)Math.Round(start.Y),
                (int)Math.Round(end.X), (int)Math.Round(end.Y),
                Math.Clamp(duration, 0, 60000), _windowTasks.Token);
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"滑动失败: {ex.Message}"));
        }
        finally
        {
            UpdateBusyState();
        }
    }

    /// <summary>把当前 ROI 保存为 PNG 模板。</summary>
    private void SaveTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentScreen is null)
        {
            _logger?.Warn(L.Text("请先截图再保存模板"));
            return;
        }

        if (_selectedRoi is not Roi roi)
        {
            _logger?.Warn(L.Text("请先在截图上拖选 ROI"));
            return;
        }

        var name = TemplateNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _logger?.Warn(L.Text("请先输入模板名称"));
            return;
        }

        try
        {
            // 处理文件名中的非法字符
            var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var directory = ConfigService.Resources.ResolveWritePath(_config.TemplateDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, safeName + ".png");

            var cropped = ImageCodec.Crop(_currentScreen.Bitmap, roi);
            ImageCodec.SavePng(cropped, path);
            _logger!.Info(L.Format($"模板已保存: {path}"));

            RefreshTemplateList();
            TemplateNameBox.Clear();
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"保存模板失败: {ex.Message}"));
        }
    }

    /// <summary>用当前截图测试所选模板的匹配结果。</summary>
    private async void TestTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => TestTemplateButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task TestTemplateButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (_currentScreen is null)
        {
            _logger?.Warn(L.Text("请先截图再测试模板"));
            return;
        }

        if (TemplateCombo.SelectedItem is not TemplateFileItem item)
        {
            _logger?.Warn(L.Text("请先选择模板文件"));
            return;
        }

        if (!double.TryParse(ThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
        {
            threshold = _config.DefaultMatchThreshold;
        }

        UpdateBusyState();
        try
        {
            var screen = _currentScreen;
            var template = await Task.Run(() => ImageCodec.DecodePng(File.ReadAllBytes(item.Path)));
            var result = await Task.Run(() => _matcher.Match(
                screen.Bgra, screen.Width, screen.Height,
                template.Bgra, template.Width, template.Height,
                threshold));

            _lastMatch = result;
            if (result is null)
            {
                SelectionStatusText.Text = L.Format($"匹配失败: 未找到置信度 ≥ {threshold:P1} 的目标");
                _logger?.Warn(SelectionStatusText.Text);
            }
            else
            {
                SelectionStatusText.Text = L.Format($"匹配成功: {result}");
                _logger?.Info(L.Format($"模板测试成功: {item.Name} -> {result}"));
            }

            DrawOverlay();
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"模板测试失败: {ex.Message}"));
        }
        finally
        {
            UpdateBusyState();
        }
    }

    /// <summary>按下鼠标：记录拖选起点。</summary>
    private void ScreenImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentScreen is null)
        {
            return;
        }

        _dragging = true;
        _dragStart = _dragCurrent = e.GetPosition(ScreenImage);
        ScreenImage.CaptureMouse();
    }

    /// <summary>移动鼠标：拖选时实时刷新预览框。</summary>
    private void ScreenImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragCurrent = e.GetPosition(ScreenImage);
        DrawOverlay();
    }

    /// <summary>松开鼠标：小距离视为选点，否则视为框选 ROI。</summary>
    private void ScreenImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ScreenImage.ReleaseMouseCapture();

        var point = e.GetPosition(ScreenImage);
        if (Math.Abs(point.X - _dragStart.X) < 6 && Math.Abs(point.Y - _dragStart.Y) < 6)
        {
            // 单击：选择坐标点
            _selectedPoint = point;
            _selectedRoi = null;
            _lastMatch = null;
            SelectionStatusText.Text = L.Format($"已选择点: ({point.X:0}, {point.Y:0})");
        }
        else
        {
            // 拖拽：框选 ROI
            var roi = Roi.FromPoints(_dragStart.X, _dragStart.Y, point.X, point.Y);
            _selectedRoi = roi;
            _selectedPoint = null;
            _lastMatch = null;
            SelectionStatusText.Text = L.Format($"ROI: {roi}  中心: ({roi.CenterX}, {roi.CenterY})");
        }

        DrawOverlay();
    }

    /// <summary>获取当前选中的设备序列号。</summary>
    private bool TryGetDeviceSerial(out string serial)
    {
        serial = string.Empty;
        if (DeviceCombo.SelectedItem is not AdbDevice { IsAvailable: true } device)
        {
            _logger?.Warn(L.Text("请先刷新并选择可用的模拟器设备"));
            return false;
        }

        serial = device.Serial;
        return true;
    }

    /// <summary>
    /// 验证当前截图符合 1920×1080 横屏基准。不兼容时仅阻止业务自动化，
    /// 手动截图仍保持可用，方便用户调整模拟器设置后再次检查。
    /// </summary>
    private async Task<bool> EnsureDeviceCompatibilityAsync(string serial, CancellationToken ct)
    {
        if (_deviceCompatibility is null)
        {
            return false;
        }

        AccountStatusText.Text = L.Text("正在检查设备画面…");
        var result = await _deviceCompatibility.CheckAsync(serial, ct);
        if (result.IsCompatible)
        {
            _logger?.Info(result.Message);
            return true;
        }

        _logger?.Warn(result.Message);
        ConnectionStateText.Text = result.Width == 0 ? L.Text("等待设备就绪") : L.Text("设备画面不兼容");
        AccountStatusText.Text = result.Message;
        return false;
    }

    /// <summary>刷新模板文件下拉列表。</summary>
    private void RefreshTemplateList()
    {
        var directory = ConfigService.ResolvePath(_config.TemplateDirectory);
        if (!ConfigService.Resources.DirectoryExists(directory))
        {
            TemplateCombo.ItemsSource = Array.Empty<TemplateFileItem>();
            return;
        }

        var items = ConfigService.Resources.GetFiles(directory, "*.png", SearchOption.AllDirectories)
            .Select(f => new TemplateFileItem(f, Path.GetFileName(f)))
            .OrderBy(i => i.Name)
            .ToArray();
        TemplateCombo.ItemsSource = items;
        TemplateCombo.SelectedItem = items.LastOrDefault();
    }

    /// <summary>清空截图上的所有选择标记。</summary>
    private void ClearSelection()
    {
        _selectedRoi = null;
        _selectedPoint = null;
        _swipeStart = null;
        _swipeEnd = null;
        _lastMatch = null;
        SwipeStartButton.Content = L.Text("设起点");
        SwipeEndButton.Content = L.Text("设终点");
        DrawOverlay();
    }

    /// <summary>重绘截图覆盖层（ROI、匹配结果、起点/终点）。</summary>
    private void DrawOverlay()
    {
        OverlayCanvas.Children.Clear();

        // 拖拽过程中的实时预览
        if (_dragging)
        {
            var preview = Roi.FromPoints(_dragStart.X, _dragStart.Y, _dragCurrent.X, _dragCurrent.Y);
            AddRect(preview, PreviewBrush, 1, new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), dash: true);
        }

        // 已确认的 ROI
        if (_selectedRoi is Roi roi)
        {
            AddRect(roi, AccentBrush, 2, new SolidColorBrush(Color.FromArgb(40, 76, 141, 255)), dash: true);
        }

        // 匹配成功的高亮框
        if (_lastMatch is TemplateMatchResult match)
        {
            AddRect(match.Region, SuccessBrush, 2, new SolidColorBrush(Color.FromArgb(40, 52, 199, 123)), dash: true);
        }

        // 滑动起点与终点
        if (_swipeStart is Point start)
        {
            AddPoint(start, Brushes.DeepSkyBlue);
        }

        if (_swipeEnd is Point end)
        {
            AddPoint(end, Brushes.Orange);
        }

        // 当前选中点
        if (_selectedPoint is Point point)
        {
            AddPoint(point, AccentBrush);
        }
    }

    /// <summary>
    /// 自动刷取：按“刷取次数”循环执行——匹配并点击右下角出击按钮，
    /// 处理下载界面/TAP TO NEXT/战斗图标/继续按钮，识别“再次出击”后按剩余次数
    /// 点击再次出击（继续下一轮）或退出坐标（全部完成）。
    /// </summary>
    private async void AutoFarmStartButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => AutoFarmStartButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task AutoFarmStartButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        // 运行中：点击按钮 = 中止自动刷本
        if (_autoFarmRunning)
        {
            _logger?.Info(L.Text("用户请求中止自动刷本…"));
            AutoFarmStartButton.Content = L.Text("正在停止…");
            AutoFarmStartButton.IsEnabled = false;
            _autoFarmCts?.Cancel();
            return;
        }

        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (_automation is null || _matcher is null || _logger is null || _sortieFlowService is null)
        {
            _logger?.Warn(L.Text("自动刷本依赖尚未初始化"));
            return;
        }

        if (!TryGetDeviceSerial(out var serial))
        {
            return;
        }

        _autoFarmRunning = true;
        _autoFarmCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var ct = _autoFarmCts.Token;
        AutoFarmStartButton.Content = L.Text("中止");
        UpdateBusyState();
        var endNotification = BeginTaskEndNotification(L.Text("自动刷本"), distinguishResult: true);
        try
        {
            if (!await EnsureDeviceCompatibilityAsync(serial, ct))
            {
                AutoFarmStatusText.Text = L.Text("当前模拟器画面不是 1920×1080 横屏，已停止自动刷取。");
                return;
            }

            // 读取刷取次数（输入框，仅数字，范围 1~999；非法或为空按默认 10 处理）
            var rounds = int.TryParse(AutoFarmCountBox.Text?.Trim(), out var count)
                ? Math.Clamp(count, 1, 999)
                : 10;
            var remaining = rounds;
            var round = 0;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                round++;
                SortieFlowResult result;
                if (round == 1)
                {
                    AutoFarmStatusText.Text = L.Format($"第 {round}/{rounds} 轮：正在识别出击按钮…");

                    // 第一轮从出击准备页开始：匹配右下角出击按钮并点击
                    var png = await _automation.ScreenshotAsync(serial, ct);
                    var screen = ImageCodec.DecodePng(png);
                    var roi = new Roi(
                        (int)(screen.Width * 0.75),
                        (int)(screen.Height * 0.75),
                        (int)(screen.Width * 0.25),
                        (int)(screen.Height * 0.25));
                    var sortie = MatchTemplateInRoi(
                        screen,
                        ConfigService.ResolvePath(_config.SortieTemplate),
                        roi);
                    if (sortie is null)
                    {
                        _logger.Warn(L.Text("未识别到出击按钮，请确认当前处于出击准备页面"));
                        await ReportAutoFarmFailureAsync(
                            serial,
                            L.Text("未识别到出击按钮，请确认处于出击准备页面。"),
                            ct);
                        return;
                    }

                    _logger.Info(
                        L.Format($"识别到出击按钮: 中心 ({sortie.CenterX},{sortie.CenterY})，置信度 {sortie.Confidence:P1}"));
                    AutoFarmStatusText.Text = L.Format($"第 {round}/{rounds} 轮：已识别出击按钮，正在点击…");
                    await _automation.TapAsync(serial, sortie.CenterX, sortie.CenterY, ct);
                    AutoFarmStatusText.Text = L.Format($"第 {round}/{rounds} 轮：等待关卡完成 × {rounds}");

                    // 首轮完整流程：并发识别下载界面与 TAP TO NEXT，配置战斗图标
                    result = await _sortieFlowService.ExecuteAfterSortieAsync(
                        serial,
                        allowDownload: _config.AllowGgeaDownload,
                        allowEmptySlotSortie: EmptySlotSortieCheckBox.IsChecked == true,
                        allowTerrainUnsuitableSortie: TerrainUnsuitableSortieCheckBox.IsChecked == true,
                        moreRoundsRemaining: remaining > 1,
                        ct: ct, recoverySortieTemplate: ConfigService.ResolvePath(_config.SortieTemplate));
                }
                else
                {
                    // 再次出击后的轮次：确认 TAP TO NEXT 或已进入战斗的同帧控件，沿用上轮图标配置。
                    AutoFarmStatusText.Text = L.Format($"第 {round}/{rounds} 轮：等待关卡完成 × {rounds}");
                    result = await _sortieFlowService.ExecuteNextRoundAsync(
                        serial,
                        moreRoundsRemaining: remaining > 1,
                        ct: ct,
                        allowDownload: _config.AllowGgeaDownload,
                        allowEmptySlotSortie: EmptySlotSortieCheckBox.IsChecked == true,
                        allowTerrainUnsuitableSortie: TerrainUnsuitableSortieCheckBox.IsChecked == true,
                        recoverySortieTemplate: ConfigService.ResolvePath(_config.SortieTemplate));
                }

                // 只有真实结算结果才消耗本地轮次；异常返回准备页不代表本轮完成。
                if (result is SortieFlowResult.RoundCompleted or SortieFlowResult.AllRoundsCompleted) remaining--;

                switch (result)
                {
                    case SortieFlowResult.RoundCompleted:
                        if (remaining > 0)
                        {
                            AutoFarmStatusText.Text = L.Format($"第 {round}/{rounds} 轮完成，已点击再次出击，等待下一轮…");
                            continue;
                        }

                        AutoFarmStatusText.Text = L.Format($"全部 {rounds} 轮刷取完成。");
                        endNotification.Succeeded = true;
                        return;
                    case SortieFlowResult.AllRoundsCompleted:
                        AutoFarmStatusText.Text = L.Format($"全部 {rounds} 轮刷取完成，已退出。");
                        endNotification.Succeeded = true;
                        return;
                    case SortieFlowResult.DownloadCancelled:
                        AutoFarmStatusText.Text = L.Text("检测到下载界面，未允许下载，已点击取消。");
                        return;
                    case SortieFlowResult.UnexpectedPreparation:
                        await ReportAutoFarmFailureAsync(serial, L.Text("结算尚未确认却回到出击准备页，已停止刷本；本轮未计完成，也未重新出击。"), ct);
                        return;
                    case SortieFlowResult.Stalled:
                        // 不再进入失败后的额外诊断或恢复，保留未知画面供用户检查。
                        AutoFarmStatusText.Text = L.Text("连续两次 2 分钟检查仍无正常流程进展，自动刷本已停止，保留当前画面。");
                        return;
                    case SortieFlowResult.TapToNextTimeout:
                        await ReportAutoFarmFailureAsync(
                            serial,
                            L.Text("等待出击加载或关卡开场超时，流程中断；请检查下载进度、游戏画面及出击是否成功。"),
                            ct);
                        return;
                    case SortieFlowResult.ContinueTimeout:
                        await ReportAutoFarmFailureAsync(serial, L.Text("等待继续按钮超时，流程中断。"), ct);
                        return;
                    case SortieFlowResult.AgainTimeout:
                        await ReportAutoFarmFailureAsync(serial, L.Text("等待再次出击按钮超时，流程中断。"), ct);
                        return;
                    case SortieFlowResult.FailureDetected:
                        await ReportAutoFarmFailureAsync(
                            serial,
                            L.Text("自动刷本失败，已回到选择关卡，请调整部队配置。"),
                            ct);
                        return;
                    case SortieFlowResult.EmptySlotCancelled:
                        AutoFarmStatusText.Text = L.Text("出击阵容存在空位且未勾选空位出击，已点击取消。");
                        return;
                    case SortieFlowResult.EmptySlotTimeout:
                        await ReportAutoFarmFailureAsync(
                            serial,
                            L.Text("未识别到空位出击对话框按钮，流程中断。"),
                            ct);
                        return;
                    case SortieFlowResult.TerrainCancelled:
                        AutoFarmStatusText.Text = L.Text("检测到地形适性提示且未勾选强制出击，已点击取消。");
                        return;
                    case SortieFlowResult.TerrainTimeout:
                        await ReportAutoFarmFailureAsync(
                            serial,
                            L.Text("未识别到地形适性对话框按钮，流程中断。"),
                            ct);
                        return;
                    case SortieFlowResult.NoAp:
                        AutoFarmStatusText.Text = L.Text("AP 不足，无法继续刷本，已回到选择关卡。");
                        return;
                    case SortieFlowResult.NoApCancelled:
                        AutoFarmStatusText.Text = L.Text("AP 不足，无法出击，已点击取消。");
                        return;
                    case SortieFlowResult.NoApTimeout:
                        await ReportAutoFarmFailureAsync(
                            serial,
                            L.Text("未识别到 AP 不足对话框取消按钮，流程中断。"),
                            ct);
                        return;
                    default:
                        await ReportAutoFarmFailureAsync(
                            serial,
                            L.Text("出击后未识别到地形、空位、AP 不足、下载提示或 TAP TO NEXT，流程中断。"),
                            ct);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            AutoFarmStatusText.Text = L.Text("自动刷本已中止。");
            endNotification.Canceled = true;
            _logger?.Warn(L.Text("自动刷本已被用户中止"));
        }
        catch (Exception ex)
        {
            _logger.Error(L.Format($"自动刷本执行失败: {ex.Message}"));
            if (ex is ConnectionErrorException)
            {
                AutoFarmStatusText.Text = ReportConnectionError(L.Text("自动刷本"));
                return;
            }
            // 捕获失败不能进入页面恢复流程；没有有效画面时立即停止控制动作。
            if (ex is ScreenshotCaptureException)
            {
                AutoFarmStatusText.Text = L.Text("截图捕获失败，自动刷本已停止。");
                return;
            }
            await ReportAutoFarmFailureAsync(serial, L.Text("执行失败，请查看日志。"), ct);
        }
        finally
        {
            _autoFarmRunning = false;
            _autoFarmCts?.Dispose();
            _autoFarmCts = null;
            AutoFarmStartButton.Content = L.Text("开始");
            UpdateBusyState();
            CompleteTaskEndNotification(endNotification, AutoFarmStatusText.Text);
        }
    }

    /// <summary>显示刷本失败原因；若稳定画面确认网络错误，则用网络错误覆盖并终止当前链。</summary>
    private async Task ReportAutoFarmFailureAsync(
        string serial,
        string failureMessage,
        CancellationToken ct)
    {
        AutoFarmStatusText.Text = failureMessage;
        if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("自动刷本"), ct))
        {
            AutoFarmStatusText.Text = L.Text("检测到网络错误或游戏数据更新，自动刷本任务链已中止。");
        }
    }

    /// <summary>在指定区域匹配模板；模板加载失败时返回 null。</summary>
    private TemplateMatchResult? MatchTemplateInRoi(ScreenImage screen, string templatePath, Roi roi)
    {
        try
        {
            var template = ImageCodec.DecodePng(File.ReadAllBytes(templatePath));
            return _matcher.Match(
                screen.Bgra,
                screen.Width,
                screen.Height,
                template.Bgra,
                template.Width,
                template.Height,
                SortieTemplateThreshold,
                roi);
        }
        catch (Exception ex)
        {
            _logger?.Warn(L.Format($"模板不可用，本次无法判断对应目标: {templatePath}: {ex.Message}"));
            return null;
        }
    }

    /// <summary>每日任务与工厂共用开发参数来源，调用方只替换目标素材，不复制识别配置。</summary>
    private DevelopRequest CreateDevelopRequest() => new()
    {
        MainLayout = _mainLayout,
        DevelopLayout = _developLayout,
        RoadmapFirstTemplatePath = ConfigService.ResolvePath(_config.RoadmapFirstTemplate),
        MsTemplatePath = ConfigService.ResolvePath(_config.DevelopMsTemplate),
        MsLargeTemplatePath = string.IsNullOrWhiteSpace(_config.DevelopMsLargeTemplate) ? "" : ConfigService.ResolvePath(_config.DevelopMsLargeTemplate),
        DevelopAllTemplatePath = ConfigService.ResolvePath(_config.DevelopAllTemplate),
        DevelopAllDisabledTemplatePath = ConfigService.ResolvePath(_config.DevelopAllDisabledTemplate),
        ExecuteDevelopTemplatePath = ConfigService.ResolvePath(_config.DevelopExecuteTemplate),
        ExecuteConfirmTemplatePath = ConfigService.ResolvePath(_config.DevelopExecuteConfirmTemplate),
        ShowcaseTapToNextTemplatePath = ConfigService.ResolvePath(_config.DevelopShowcaseTapTemplate),
        BackButtonTemplatePath = ConfigService.ResolvePath(_config.DevelopBackTemplate),
        DevelopTitleTemplatePath = ConfigService.ResolvePath(_config.DevelopTitleTemplate),
        Threshold = _config.DevelopMsThreshold,
        TitleThreshold = _config.DevelopTitleThreshold,
        TitleWaitMs = _config.DevelopTitleWaitMs,
        RoadmapDisplaySettingsTitleTemplatePath = ConfigService.ResolvePath(_config.DevelopRoadmapDisplaySettingsTitleTemplate),
    };

    /// <summary>每日开发与铁球工厂共用退出顺序，正常主界面导航仍由现有识别服务负责。</summary>
    private Task<bool> RecoverDevelopmentResourcesAsync(string serial, string reason, Action<string> report, CancellationToken ct)
        => new DevelopmentResourceRecovery(_automation!, (device, token) => EnsureMainScreenAsync(device, token))
            .RecoverAsync(serial, message => { report(message); _logger!.Info(message); }, ct, reason: reason);

    /// <summary>工厂入口纳入窗口生命周期；关窗时取消并等待收尾。</summary>
    private async void BallFactoryStartButton_Click(object sender, RoutedEventArgs e)
        => await RunWindowTaskAsync(StartBallFactoryAsync);

    /// <summary>从非战斗页面开始完整铁球工厂循环；共享导航、开发与解体服务，统一响应中止。</summary>
    private async Task StartBallFactoryAsync()
    {
        if (_ballFactoryRunning)
        {
            BallFactoryStartButton.Content = L.Text("正在停止…");
            _ballFactoryCts?.Cancel();
            UpdateBusyState();
            return;
        }
        using var execution = TryBeginExecution();
        if (execution is null) return;
        CommitBallFactoryGp();
        if (_automation is null || _logger is null || !TryGetDeviceSerial(out var serial))
        {
            BallFactoryStatusText.Text = L.Text("请先连接并选择模拟器。");
            return;
        }
        var targetGp = _ballFactoryTargetGp;
        _ballFactoryRunning = true;
        _ballFactoryCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var ct = _ballFactoryCts.Token;
        BallFactoryStartButton.Content = L.Text("中止制造");
        BallFactoryStatusText.Text = L.Text("正在检查设备画面…");
        UpdateBusyState();
        var endNotification = BeginTaskEndNotification(L.Text("铁球工厂"));
        try
        {
            if (!await EnsureDeviceCompatibilityAsync(serial, ct))
            {
                BallFactoryStatusText.Text = L.Text("设备画面不兼容，已停止，请检查设备状态。");
                return;
            }
            // 铁球固定使用原生立绘两档识别，不继承每日任务自定义截图的多尺度开关。
            IDevelopService develop = new DevelopService(_automation, _logger, fixedUnitScales: true);
            var factory = new BallFactoryService(develop,
                (device, token) => EnsureMainScreenAsync(device, token, allowAnnouncement: true));
            DismantleService.ValidateTemplates();
            void Report(string message) { BallFactoryStatusText.Text = message; _logger.Info(message); }
            var request = CreateDevelopRequest();
            var runner = new BallFactoryRunner(
                (quantity, locateTarget, token) => factory.DevelopBatchAsync(_automation, serial, quantity, request, Report, token, locateTarget),
                (state, token) => RecoverDevelopmentResourcesAsync(serial, state.GetBlockedReason()!, Report, token),
                async token =>
                {
                    // 清理既用于单位上限，也用于全部开发结束；均需正常恢复主界面后才进入。
                    if (!await EnsureMainScreenAsync(serial, token, allowAnnouncement: true))
                        throw new InvalidOperationException(L.Text("解体前未能恢复主界面。"));
                    if (!await new DismantleEntryService(_automation).OpenFromHomeAsync(serial, token))
                        throw new InvalidOperationException(L.Text("未能进入解体页面。"));
                    var rounds = await new DismantleService(_automation).RunUntilEmptyAsync(serial, Report, token);
                    if (!await EnsureMainScreenAsync(serial, token, allowAnnouncement: true))
                        throw new InvalidOperationException(L.Text("解体结束后未能恢复主界面，停止后续开发。"));
                    return rounds;
                });
            var outcome = await runner.RunAsync(targetGp, Report, ct);
            BallFactoryStatusText.Text = outcome.Completed
                ? L.Format($"铁球工厂完成：已开发 {outcome.DevelopedUnits} 台，共 {outcome.CompletedBatches} 批；解体 {outcome.DismantleRounds} 轮，已确认无推荐单位并返回主界面。目标 {targetGp} GP，实际到账以游戏为准。")
                : L.Format($"铁球工厂已停止：{outcome.StopReason} 已完成 {outcome.CompletedBatches} 批、开发 {outcome.DevelopedUnits} 台、解体 {outcome.DismantleRounds} 轮。");
            if (outcome.Completed) _logger.Info(BallFactoryStatusText.Text);
            else _logger.Warn(BallFactoryStatusText.Text);
            endNotification.Succeeded = outcome.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            BallFactoryStatusText.Text = L.Text("铁球工厂已中止。");
            _logger.Info(BallFactoryStatusText.Text);
        }
        catch (Exception ex)
        {
            BallFactoryStatusText.Text = L.Format($"铁球工厂已停止：{ex.Message}");
            _logger.Error(BallFactoryStatusText.Text);
        }
        finally
        {
            _ballFactoryRunning = false;
            _ballFactoryCts.Dispose();
            _ballFactoryCts = null;
            BallFactoryStartButton.Content = L.Text("开始制造");
            UpdateBusyState();
            CompleteTaskEndNotification(endNotification, BallFactoryStatusText.Text);
        }
    }

    // 仅保存本窗口最后确认的输入；定位阶段只记录目标，不据此声称已获得 GP。
    private int _ballFactoryTargetGp = 30000;

    /// <summary>GP 只接受十进制整数；编辑期间允许清空或暂时低于下限，以便逐位输入。</summary>
    private void BallFactoryGpBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => c is >= '0' and <= '9');

    /// <summary>空格不经过 TextInput；Enter 提交当前值，其他编辑快捷键保持原行为。</summary>
    private void BallFactoryGpBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;
        if (e.Key == Key.Enter)
        {
            CommitBallFactoryGp();
            e.Handled = true;
        }
    }

    /// <summary>粘贴与键入采用相同字符规则，拒绝小数、负数与混合文本。</summary>
    private void BallFactoryGpBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is not string text ||
            text.Length == 0 || !text.All(c => c is >= '0' and <= '9'))
            e.CancelCommand();
    }

    /// <summary>立即提示无效范围；XAML 初始化期间提示控件可能尚未创建。</summary>
    private void BallFactoryGpBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (BallFactoryGpHint is null) return;
        var valid = TryReadBallFactoryGp(out _);
        BallFactoryGpHint.Text = valid ? "" : L.Text("请输入 20–100000 的整数；离开输入框时将恢复上次有效值。");
        BallFactoryGpHint.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        BallFactoryGpHint.SetResourceReference(TextBlock.ForegroundProperty, valid ? "Brush.TextDim" : "Brush.Warn");
    }

    /// <summary>统一校验范围与溢出，不将非法文本变为可执行的目标。</summary>
    private bool TryReadBallFactoryGp(out int value) =>
        int.TryParse(BallFactoryGpBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
        value is >= 20 and <= 100000;

    /// <summary>离开输入框时完成编辑，禁止越界值作为页面的已确认目标。</summary>
    private void BallFactoryGpBox_LostFocus(object sender, RoutedEventArgs e) => CommitBallFactoryGp();

    /// <summary>有效值取最近的 20 的倍数，等距时向上；先校验范围，空白、越界或溢出恢复最后确认值。</summary>
    private void CommitBallFactoryGp()
    {
        // 两端均为 20 的倍数，范围内舍入不会越界；使用整数运算避免浮点舍入差异。
        if (TryReadBallFactoryGp(out var value)) _ballFactoryTargetGp = (value + 10) / 20 * 20;
        BallFactoryGpBox.Text = _ballFactoryTargetGp.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>刷取次数输入框：仅允许输入数字。</summary>
    private void AutoFarmCountBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    /// <summary>刷取次数输入框：禁止粘贴包含非数字的内容。</summary>
    private void AutoFarmCountBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)) &&
            (e.DataObject.GetData(typeof(string)) as string)?.All(char.IsDigit) == true)
        {
            return;
        }

        e.CancelCommand();
    }

    /// <summary>每日任务：按用户拖拽后的列表顺序，依次执行已勾选的任务。</summary>
    private async void DailyStartButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => DailyStartButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task DailyStartButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        // 任务运行中：点击按钮 = 中止当前任务
        if (_dailyRunning)
        {
            _logger?.Info(L.Text("用户请求中止每日任务…"));
            DailyStartButton.Content = L.Text("正在停止…");
            DailyStartButton.IsEnabled = false;
            _dailyCts?.Cancel();
            return;
        }

        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (_automation is null ||
        _personalBaseService is null ||
        _shopService is null ||
        _giftService is null ||
        _developService is null ||
        _stageService is null ||
        _stageSkipService is null ||
        _apConsumptionService is null ||
        _upgradeStagesService is null ||
        _missionRewardService is null)
        {
            return;
        }

        if (!TryGetDeviceSerial(out var serial))
        {
            return;
        }

        // 领取每日使命奖励：勾选后随“领取奖励”任务按队列顺序执行一次，
        // 并在每日任务队列最后再执行一次（共两次）；不勾选“领取奖励”时不自动执行。
        var missionRewardChecked = _rewardTasks.Any(reward =>
            reward.Kind == RewardTaskKind.MissionReward && reward.IsChecked);

        // 固化本次执行顺序：按当前列表顺序过滤已勾选任务，防止运行期间拖拽影响导航上下文。
        var dailyPlan = DailyTaskPlanner.BuildDailyPlan(_dailyTasks.Where(IsDailyTaskAvailable), missionRewardChecked);
        if (dailyPlan.Count == 0)
        {
            _logger?.Warn(L.Text("请至少勾选一个每日任务"));
            return;
        }

        var hasApTask = dailyPlan.Any(task => task.Kind == DailyTaskKind.ConsumeAp);
        var apPlan = hasApTask
            ? ApConsumptionPlanner.BuildExecutionPlan(_apWorkItems)
            : Array.Empty<ApConsumptionWorkItem>();
        if (hasApTask && apPlan.Count == 0)
        {
            _logger?.Warn(L.Text("消耗AP：请在 AP消耗 TAB 中至少勾选一个 Hard 关卡"));
            ApPlanStatusText.Text = L.Text("尚未选择需要执行的 Hard 关卡。");
            return;
        }

        // 每次点击“任务开始”都建立独立 AP 链；前一次完成或中止的游标不得带入新任务。
        var apTaskChain = hasApTask
            ? new ApConsumptionTaskChain(serial, apPlan)
            : null;

        var hasRewardTask = dailyPlan.Any(task => task.Kind == DailyTaskKind.ClaimRewards);
        IReadOnlyList<RewardTaskItem> rewardPlan = hasRewardTask
            ? RewardTaskPlanner.BuildExecutionPlan(_rewardTasks)
            : Array.Empty<RewardTaskItem>();
        if (hasRewardTask && rewardPlan.Count == 0 && !missionRewardChecked)
        {
            _logger?.Warn(L.Text("领取奖励：请在 奖励 TAB 中至少勾选一项奖励"));
            RewardPlanStatusText.Text = L.Text("尚未选择需要领取的奖励。");
            return;
        }

        _dailyRunning = true;
        _dailyCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var ct = _dailyCts.Token;
        DailyStartButton.Content = L.Text("中止任务   ■");
        UpdateBusyState();
        AccountStatusText.Text = L.Text("开始执行每日任务…");
        var elapsed = Stopwatch.StartNew();
        var summary = new TaskQueueSummary(dailyPlan.Count);
        // AP 只在独立执行时发送专属提醒；组合每日队列仍由原有队列汇总统一提醒。
        var endNotification = dailyPlan.Count == 1 && hasApTask
            ? BeginTaskEndNotification(L.Text("AP 消耗"))
            : null;
        try
        {
            if (!await EnsureDeviceCompatibilityAsync(serial, ct))
            {
                return;
            }

            // 普通队列提前结束也进入收尾；安全停止和异常仍保留原有停止边界。
            var queueOutcome = await DailyQueueRunner.RunAsync(async () =>
            {
                var allowInitialAnnouncement = true;
                foreach (var task in dailyPlan.Where(task => task.Kind != DailyTaskKind.ClaimMissionReward))
                {
                    ct.ThrowIfCancellationRequested();

                    // 每个任务前都重新确认主界面，避免在上一任务的子页面直接操作下一任务。
                    if (!await EnsureMainScreenAsync(serial, ct, allowInitialAnnouncement))
                    {
                        AccountStatusText.Text = L.Text("未能确认主界面，任务中止");
                        if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
                        {
                            AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
                            return DailyQueueOutcome.Unsafe;
                        }
                        return DailyQueueOutcome.Interrupted;
                    }

                    AccountStatusText.Text = L.Format($"执行：{task.Name}…");
                    // 默认失败，只有服务明确返回成功才能计入成功；奖励子项合并为一个队列项。
                    var taskSucceeded = false;
                    var failedSubtasks = new List<string>();
                    var developmentResourcesInsufficient = false;
                    allowInitialAnnouncement = false;

                    switch (task.Kind)
                    {
                        case DailyTaskKind.HarvestBattleship:
                        {
                            var harvestResult = await _personalBaseService.HarvestBattleshipAsync(
                                serial,
                                _mainLayout,
                                _personalBaseLayout,
                                ConfigService.ResolvePath(_config.BattleshipTemplate),
                                string.IsNullOrWhiteSpace(_config.BattleshipMask)
                                    ? null
                                    : ConfigService.ResolvePath(_config.BattleshipMask),
                                ConfigService.ResolvePath(_config.RecycleAllTemplate),
                                ConfigService.ResolvePath(_config.RecycleAllDisabledTemplate),
                                ConfigService.ResolvePath(_config.HarvestOkTemplate),
                                ct: ct);
                            _logger!.Info(L.Format($"回收战舰: {harvestResult.Message}"));
                            // 出航中暂不可回收属于安全略过；只有定位或状态确认失败才计为失败。
                            taskSucceeded = harvestResult.Completed;
                            break;
                        }

                        case DailyTaskKind.ShopFreeGift:
                        {
                            var shopOk = await _shopService.BuyFreePremiumItemAsync(
                                serial,
                                _mainLayout,
                                _shopLayout,
                                ConfigService.ResolvePath(_config.ShopFreeBuyTemplate),
                                ConfigService.ResolvePath(_config.ShopFreeTextTemplate),
                                ConfigService.ResolvePath(_config.ShopConfirmBuyTemplate),
                                ConfigService.ResolvePath(_config.ShopResultCloseTemplate),
                                ConfigService.ResolvePath(_config.DiamondIconTemplate),
                                ct: ct);
                            if (!shopOk)
                            {
                                // 商店可能仍停在购买弹窗；禁止交给通用导航空等或继续其他任务。
                                summary.Record(task.Name, false, L.Text("商店未安全完成"));
                                // 仅失败后做只读弹窗诊断；网络/日期更新都不能授权购买或导航。
                                if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
                                {
                                    AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
                                    return DailyQueueOutcome.Unsafe;
                                }
                                AccountStatusText.Text = L.Text("商店未完成，每日任务链已停止；请检查并手动关闭当前弹窗后重新开始");
                                _logger!.Warn(AccountStatusText.Text);
                                return DailyQueueOutcome.Unsafe;
                            }
                            _logger!.Info(L.Text("商店购物: 完成或已安全略过"));
                            taskSucceeded = shopOk;
                            break;
                        }

                        case DailyTaskKind.ClaimRewards:
                        {
                            RewardPlanStatusText.Text = L.Format($"开始领取 {rewardPlan.Count} 项奖励…");
                            taskSucceeded = rewardPlan.Count > 0;
                            foreach (var reward in rewardPlan)
                            {
                                ct.ThrowIfCancellationRequested();
                                AccountStatusText.Text = L.Format($"执行：{reward.Name}…");
                                switch (reward.Kind)
                                {
                                    case RewardTaskKind.MailReward:
                                    {
                                        var giftOk = await _giftService.CollectGiftsAsync(
                                            serial,
                                            _mainLayout,
                                            _giftLayout,
                                            ConfigService.ResolvePath(_config.GiftCollectAllTemplate),
                                            ConfigService.ResolvePath(_config.GiftCollectAllDisabledTemplate),
                                            ConfigService.ResolvePath(_config.GiftOkTemplate),
                                            ConfigService.ResolvePath(_config.GiftCloseTemplate),
                                            ct: ct);
                                        _logger!.Info(L.Format($"领取邮件奖励: {(giftOk ? L.Text("完成") : L.Text("未完成"))}"));
                                        taskSucceeded &= giftOk;
                                        if (!giftOk) failedSubtasks.Add(reward.Name);
                                        break;
                                    }

                                    case RewardTaskKind.MissionReward:
                                    {
                                        var missionOk = await _missionRewardService.ClaimAsync(
                                            serial,
                                            _mainLayout,
                                            _missionLayout,
                                            ConfigService.ResolvePath(_config.MissionClaimAllTemplate),
                                            ConfigService.ResolvePath(_config.MissionClaimAllDisabledTemplate),
                                            ConfigService.ResolvePath(_config.MissionClaimResultOkTemplate),
                                            ct: ct);
                                        _logger!.Info(L.Format($"领取每日使命奖励: {(missionOk ? L.Text("完成") : L.Text("未完成"))}"));
                                        taskSucceeded &= missionOk;
                                        if (!missionOk) failedSubtasks.Add(reward.Name);
                                        break;
                                    }

                                    default:
                                        _logger?.Warn(L.Format($"未实现的奖励类型: {reward.Kind}"));
                                        taskSucceeded = false;
                                        failedSubtasks.Add(reward.Name);
                                        break;
                                }
                            }

                            RewardPlanStatusText.Text = L.Text("领取奖励流程结束。");
                            break;
                        }

                        case DailyTaskKind.DevelopOnce:
                        {
                            var developResult = await _developService.ExecuteAsync(serial, CreateDevelopRequest(), ct);
                            // 保持每日任务语义：无可用开发正常跳过，不能用于工厂的产出计数。
                            var shortageReason = developResult.GetBlockedReason();
                            developmentResourcesInsufficient = shortageReason is not null;
                            if (developmentResourcesInsufficient)
                            {
                                if (!await RecoverDevelopmentResourcesAsync(serial, shortageReason!, message => AccountStatusText.Text = message, ct))
                                {
                                    AccountStatusText.Text = L.Format($"{shortageReason}，取消或主界面恢复未完成，每日任务已停止。");
                                    _logger!.Error(AccountStatusText.Text);
                                    return DailyQueueOutcome.Unsafe;
                                }
                                failedSubtasks.Add(L.Format($"{shortageReason}，已停止本项并继续后续任务"));
                                _logger!.Warn(L.Format($"{shortageReason}，停止开发&强化一次，继续后续每日任务"));
                            }
                            var developOk = developResult is DevelopResult.Completed or DevelopResult.Unavailable;
                            _logger!.Info(L.Format($"开发&强化一次: {(developOk ? L.Text("完成") : L.Text("未完成"))}"));
                            taskSucceeded = developOk;
                            break;
                        }

                        case DailyTaskKind.UpgradeStages:
                        {
                            AccountStatusText.Text = L.Text("执行：强化培育关卡…");
                            var upgradeResult = await _upgradeStagesService.ExecuteFromMainAsync(
                                serial,
                                _mainLayout,
                                _stageLayout,
                                _upgradeStagesLayout,
                                ct: ct);
                            if (!upgradeResult.Success)
                            {
                                AccountStatusText.Text = L.Format($"强化培育关卡失败：{upgradeResult.Message}");
                                if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
                                {
                                    AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
                                    return DailyQueueOutcome.Unsafe;
                                }
                                return DailyQueueOutcome.Interrupted;
                            }

                            _logger!.Info(L.Text("强化培育关卡完成"));
                            taskSucceeded = true;
                            break;
                        }

                        case DailyTaskKind.FierceEnemySkip:
                        {
                            var selection = task.FierceEnemySelection ?? throw new InvalidOperationException(L.Text("强敌任务缺少选择"));
                            AccountStatusText.Text = L.Format($"强敌来袭略过：{selection.Unit.Name} / {selection.Difficulty}…");
                            var result = await new FierceEnemySkipService(_automation!, _logger!, GetTextOcr(),
                                confirm => CreateStageSkipService(false, confirm)).ExecuteAsync(serial, _mainLayout,
                                    _stageLayout, selection, ct);
                            _logger!.Info(result.Message);
                            if (result.IsUnavailable)
                            {
                                AccountStatusText.Text = result.Message;
                                failedSubtasks.Add(result.Message);
                                break;
                            }
                            if (!result.Success)
                            {
                                AccountStatusText.Text = result.Message;
                                return DailyQueueOutcome.Interrupted;
                            }
                            taskSucceeded = true;
                            break;
                        }

                        case DailyTaskKind.ConsumeAp:
                        {
                            var chain = apTaskChain ?? throw new InvalidOperationException(L.Text("AP 消耗任务链未初始化"));
                            ApPlanStatusText.Text = L.Format($"开始执行 AP 消耗略过，共 {chain.Count} 个作品…");
                            AccountStatusText.Text = L.Text("执行：消耗AP略过…");
                            var apResult = await _apConsumptionService.ExecuteAsync(
                                serial,
                                chain,
                                _mainLayout,
                                _stageLayout,
                                _stagePositions,
                                _hardStages,
                                ct);
                            ApPlanStatusText.Text = apResult.Message;
                            if (apResult.IsInterrupted)
                            {
                                AccountStatusText.Text = L.Format($"AP消耗中断：{apResult.Message}");
                                if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
                                {
                                    AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
                                    return DailyQueueOutcome.Unsafe;
                                }
                                if (!dailyPlan.Any(item => item.Kind == DailyTaskKind.ClaimMissionReward))
                                {
                                    await EnsureMainScreenAsync(serial, ct);
                                }
                                return DailyQueueOutcome.Interrupted;
                            }

                            AccountStatusText.Text = apResult.Success
                                ? L.Text("AP消耗任务链完成")
                                : L.Text("AP消耗任务链完成，但存在失败作品");
                            taskSucceeded = apResult.Success;
                            break;
                        }

                        default:
                            _logger?.Warn(L.Format($"未实现的每日任务类型: {task.Kind}"));
                            break;
                    }
                    summary.Record(task.Name, taskSucceeded, string.Join("、", failedSubtasks));
                    if (!taskSucceeded && !developmentResourcesInsufficient && await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
                    {
                        AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
                        return DailyQueueOutcome.Unsafe;
                    }
                }

                return DailyQueueOutcome.Completed;
            }, async () =>
            {
                var finalTask = dailyPlan.LastOrDefault(task => task.Kind == DailyTaskKind.ClaimMissionReward);
                if (finalTask is null) return;

                var previousStatus = AccountStatusText.Text;
                AccountStatusText.Text = L.Text("收尾：领取每日使命奖励…");
                // 每次收尾重新验证主界面；失败时不进入使命页面、不执行领取点击。
                var missionOk = await EnsureMainScreenAsync(serial, ct, false)
                    && await _missionRewardService.ClaimAsync(
                        serial,
                        _mainLayout,
                        _missionLayout,
                        ConfigService.ResolvePath(_config.MissionClaimAllTemplate),
                        ConfigService.ResolvePath(_config.MissionClaimAllDisabledTemplate),
                        ConfigService.ResolvePath(_config.MissionClaimResultOkTemplate),
                        ct: ct);
                summary.Record(finalTask.Name, missionOk);
                var finalStatus = L.Format($"收尾领取每日使命奖励：{(missionOk ? L.Text("完成") : L.Text("未完成"))}");
                _logger!.Info(finalStatus);
                AccountStatusText.Text = $"{previousStatus}；{finalStatus}";
                if (!missionOk && await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
                {
                    AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
                }
            }, ct);
            if (queueOutcome != DailyQueueOutcome.Completed) return;

            ct.ThrowIfCancellationRequested();
            elapsed.Stop();
            var completionMessage = summary.GetCompletionMessage(elapsed.Elapsed);
            var completionDetails = apTaskChain is not null && completionMessage is not null
                ? $"{completionMessage}；{apTaskChain.GetExecutionSummary()}"
                : completionMessage;
            AccountStatusText.Text = L.Format($"每日任务执行完成：{completionDetails}");
            _logger!.Info(AccountStatusText.Text);
            if (endNotification is not null)
            {
                endNotification.Succeeded = completionMessage is not null && summary.FailureCount == 0;
            }
            else if (!_apStopNotification.Reported && _config.EnableWindowsNotifications && completionMessage is not null)
            {
                // 系统通知优先完整展示每日失败清单；较长的 AP 执行统计仍保留在状态和日志中。
                var lockedSummary = apTaskChain?.GetLockedSummary();
                var notificationMessage = string.IsNullOrEmpty(lockedSummary)
                    ? completionMessage
                    : $"{completionMessage}；{lockedSummary}";
                ShowQueueCompletionNotification(notificationMessage, summary.FailureCount > 0);
            }
        }
        catch (OperationCanceledException)
        {
            AccountStatusText.Text = L.Text("任务已中止");
            _logger!.Warn(L.Text("每日任务已被用户中止"));
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"每日任务执行失败: {ex.Message}"));
            // 明确区分捕获失败与业务未命中，且不再启动可能点击遮罩的恢复逻辑。
            if (ex is ScreenshotCaptureException)
            {
                AccountStatusText.Text = L.Text("截图捕获失败，每日任务已停止");
                return;
            }
            AccountStatusText.Text = L.Text("每日任务执行失败");
            if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("每日"), ct))
            {
                AccountStatusText.Text = L.Text("检测到网络错误或游戏数据更新，每日任务链已中止");
            }
        }
        finally
        {
            if (apTaskChain is not null)
            {
                var apSummary = apTaskChain.GetExecutionSummary();
                ApPlanStatusText.Text = apSummary;
                _logger?.Info(apSummary);
                if (!AccountStatusText.Text.Contains(apSummary, StringComparison.Ordinal))
                {
                    AccountStatusText.Text = $"{AccountStatusText.Text}；{apSummary}";
                }
            }

            _dailyRunning = false;
            _dailyCts?.Dispose();
            _dailyCts = null;
            DailyStartButton.Content = L.Text("任务开始");
            UpdateBusyState();
            CompleteTaskEndNotification(endNotification, AccountStatusText.Text);
        }
    }

    /// <summary>
    /// 任一自动化任务失败后等待五秒再诊断网络错误；命中时统一更新连接状态并通知用户。
    /// 用户在等待期间主动中止时不再截图，也不发送错误通知。
    /// </summary>
    private async Task<bool> AbortTaskChainIfConnectionErrorAsync(
        string serial,
        string taskChainName,
        CancellationToken ct)
    {
        if (_automation is null)
        {
            return false;
        }

        try
        {
            if (!await _automation.GlobalScreens.DetectConnectionErrorAfterFailureAsync(serial, ct))
            {
                return false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }

        ReportConnectionError(taskChainName);
        return true;
    }

    /// <summary>统一呈现已确认的网络错误并发送一次警告；不再截图或执行恢复动作。</summary>
    private string ReportConnectionError(string taskChainName)
    {
        var message = L.Format($"检测到网络错误或游戏数据更新，{taskChainName}任务链已中止");
        ConnectionStateText.Text = message;
        _logger?.Error(message);
        _activeTaskEndNotification?.Suppress();
        if (_config.EnableWindowsNotifications)
        {
            ShowNetworkErrorNotification(message);
        }

        return message;
    }

    /// <summary>发送网络错误警告；发送失败不覆盖原任务失败状态。</summary>
    private void ShowNetworkErrorNotification(string message)
    {
        try
        {
            if (!_notifications.Show(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle,
                    message,
                    L.Text("GGEA · 游戏中断"),
                    isWarning: true))
            {
                _logger?.Warn(L.Text("网络错误 Windows 通知发送失败，任务链仍已中止"));
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn(L.Format($"网络错误 Windows 通知发送失败：{ex.Message}"));
        }
    }

    /// <summary>独立任务在获得执行权并完成输入校验后登记提醒，未启动的操作不发送结束通知。</summary>
    private TaskEndNotification BeginTaskEndNotification(string taskName, bool distinguishResult = false)
        => _activeTaskEndNotification = new TaskEndNotification(taskName, distinguishResult);

    /// <summary>finally 完成状态恢复后发送真实终态；窗口退出时抑制通知，并解除本轮状态所有权。</summary>
    private void CompleteTaskEndNotification(TaskEndNotification? notification, string message)
    {
        if (notification is null) return;
        if (ReferenceEquals(_activeTaskEndNotification, notification)) _activeTaskEndNotification = null;
        notification.Send(message, _config.EnableWindowsNotifications, _windowTasks.IsStopping,
            ShowWindowsNotification);
    }

    /// <summary>所有出击与略过共用 AP 停止出口；UI 线程负责系统通知，不依赖队列正常完成。</summary>
    private void ReportApInsufficient(string message)
    {
        if (_windowTasks.IsStopping) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ReportApInsufficient(message)); return; }
        _apStopNotification.Send(message, _config.EnableWindowsNotifications, _windowTasks.IsStopping,
            _activeTaskEndNotification, ShowWindowsNotification);
    }

    /// <summary>每日队列保持原有标题，和独立任务共用不会改变业务结果的通知出口。</summary>
    private void ShowQueueCompletionNotification(string message, bool isWarning)
        => ShowWindowsNotification(message, L.Text("GGEA · 任务队列执行完毕"), isWarning);

    /// <summary>通知发送失败只记录日志，不改变已完成任务或队列的执行结果。</summary>
    private void ShowWindowsNotification(string message, string title, bool isWarning)
    {
        try
        {
            if (!_notifications.Show(new System.Windows.Interop.WindowInteropHelper(this).Handle, message,
                    title, isWarning))
            {
                _logger?.Warn(L.Text("Windows 通知发送失败，任务结果已保留在日志中"));
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn(L.Format($"Windows 通知发送失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 确保游戏处于主界面。
    /// 子任务间优先识别主界面按钮，未命中时只尝试左上角返回按钮；不处理公告。
    /// </summary>
    private Task<bool> EnsureMainScreenAsync(string serial, CancellationToken ct = default)
        => EnsureMainScreenAsync(serial, ct, allowAnnouncement: false);

    /// <summary>仅开始任务的首次主界面恢复允许公告检测；子任务和后续导航默认禁用。</summary>
    private Task<bool> EnsureMainScreenAsync(string serial, CancellationToken ct, bool allowAnnouncement)
    {
        if (_mainScreenNavigator is null)
            throw new InvalidOperationException(L.Text("主界面恢复服务尚未初始化"));
        Func<CancellationToken, Task<bool>>? closeAnnouncement = allowAnnouncement && _gameEntry is not null
            ? token => _gameEntry.TryClickAnnouncementCloseIfVisibleAsync(serial,
                ConfigService.ResolvePath(_config.LoginRewardAnnouncementCloseTemplate),
                ConfigService.ResolvePath(_config.LoginRewardAnnouncementTemplate), token)
            : null;
        return _mainScreenNavigator.RecoverAsync(serial, _config.MainScreenWaitMs,
            _config.MainScreenBackSettleDelayMs, MainScreenActivationTapX, MainScreenActivationTapY, ct, closeAnnouncement);
    }

    /// <summary>从本地 JSON 加载 AP 消耗命名配置，并恢复最后选择的方案。</summary>
    private void LoadApProfiles()
    {
        if (_apProfileService is null)
        {
            return;
        }

        _apProfiles = _apProfileService.Load(ConfigService.ResolvePath(_config.ApConsumptionProfilesFile));
        RefreshApProfileCombo(_apProfiles.LastSelectedProfile);
    }

    /// <summary>刷新方案下拉框，并在存在指定名称时自动应用。</summary>
    private void RefreshApProfileCombo(string? selectedName)
    {
        _loadingApProfile = true;
        try
        {
            ApProfileCombo.ItemsSource = _apProfiles.Profiles
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var selected = _apProfiles.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Name, selectedName, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                ApProfileCombo.SelectedItem = selected;
                ApProfileNameBox.Text = selected.Name;
                ApConsumptionPlanner.ApplyProfile(selected, _apWorkItems);
                ApPlanStatusText.Text = L.Format($"已加载配置：{selected.Name}");
            }
            else
            {
                ApProfileCombo.SelectedIndex = -1;
                ApProfileNameBox.Clear();
            }
        }
        finally
        {
            _loadingApProfile = false;
        }
    }

    /// <summary>用户从下拉框选择配置时恢复作品顺序与 Hard 勾选状态。</summary>
    private void ApProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;

        if (_loadingApProfile || ApProfileCombo.SelectedItem is not ApConsumptionProfile profile)
        {
            return;
        }

        ApConsumptionPlanner.ApplyProfile(profile, _apWorkItems);
        ApProfileNameBox.Text = profile.Name;
        _apProfiles.LastSelectedProfile = profile.Name;
        ApPlanStatusText.Text = L.Format($"已切换配置：{profile.Name}");
    }

    /// <summary>保存当前完整作品顺序及 Hard 勾选状态；同名配置直接覆盖。</summary>
    private void SaveApProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;

        if (_apProfileService is null)
        {
            return;
        }

        var name = ApProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ApPlanStatusText.Text = L.Text("请输入配置名称后再保存。");
            return;
        }

        try
        {
            var profile = ApConsumptionPlanner.CreateProfile(name, _apWorkItems);
            _apProfileService.Upsert(_apProfiles, profile);
            _apProfileService.Save(
                _apProfiles,
                ConfigService.ResolvePath(_config.ApConsumptionProfilesFile));
            RefreshApProfileCombo(profile.Name);
            ApPlanStatusText.Text = L.Format($"配置“{profile.Name}”已保存。");
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"保存 AP 消耗配置失败: {ex.Message}"));
            ApPlanStatusText.Text = L.Format($"保存失败：{ex.Message}");
        }
    }

    /// <summary>删除下拉框中指定名称的 AP 消耗方案并刷新列表。</summary>
    private void DeleteApProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;

        if (_apProfileService is null ||
            (sender as FrameworkElement)?.Tag is not ApConsumptionProfile profile)
        {
            return;
        }

        try
        {
            _apProfiles.Profiles.Remove(profile);
            if (string.Equals(
                    _apProfiles.LastSelectedProfile,
                    profile.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                _apProfiles.LastSelectedProfile = string.Empty;
            }

            _apProfileService.Save(
                _apProfiles,
                ConfigService.ResolvePath(_config.ApConsumptionProfilesFile));
            RefreshApProfileCombo(_apProfiles.LastSelectedProfile);
            ApPlanStatusText.Text = L.Format($"方案“{profile.Name}”已删除。");
            _logger?.Info(L.Format($"已删除 AP 消耗方案: {profile.Name}"));
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"删除 AP 消耗配置失败: {ex.Message}"));
            ApPlanStatusText.Text = L.Format($"删除失败：{ex.Message}");
        }
    }

    /// <summary>记录 AP 作品列表的拖拽起点与待移动作品。</summary>
    private void ApWorkList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        StartListDrag<ApConsumptionWorkItem>(
            ApWorkList,
            e,
            ref _apDragStart,
            ref _apDraggedItem,
            ref _apDragAdorner,
            ref _apDraggedContainer);
    }

    /// <summary>鼠标移动超过系统拖拽阈值后启动作品排序操作。</summary>
    private void ApWorkList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        BeginListDrag<ApConsumptionWorkItem>(
            ApWorkList,
            e,
            ref _apDragStart,
            ref _apDraggedItem,
            ref _apDragAdorner,
            ref _apDraggedContainer);
    }

    /// <summary>AP 作品列表只接受内部的移动排序。</summary>
    private void ApWorkList_DragOver(object sender, DragEventArgs e)
    {
        UpdateListDragOver<ApConsumptionWorkItem>(
            ApWorkList,
            e,
            ref _apDragAdorner,
            ref _apDropIndicator);
    }

    /// <summary>拖拽离开列表时清除插入目标高亮。</summary>
    private void ApWorkList_DragLeave(object sender, DragEventArgs e)
    {
        ApWorkList.SelectedItem = null;
        RemoveDropIndicator(ref _apDropIndicator);
    }

    /// <summary>将拖拽作品移动到鼠标所在作品的位置，集合顺序即任务执行顺序。</summary>
    private void ApWorkList_Drop(object sender, DragEventArgs e)
    {
        RemoveDropIndicator(ref _apDropIndicator);
        FinishListDrop(
            ApWorkList,
            _apWorkItems,
            e,
            item => item.Work,
            message => ApPlanStatusText.Text = message);
    }

    /// <summary>记录每日任务列表的拖拽起点与待移动任务。</summary>
    private void DailyTaskList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dailyDragMoved = false;
        // 删除按钮与勾选框属于独立操作，按下后不能启动整行拖拽。
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != DailyTaskList;
            source = source is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(source))
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase)
            { _dailyDraggedItem = null; return; }
        }
        StartListDrag<DailyTaskItem>(
            DailyTaskList,
            e,
            ref _dailyDragStart,
            ref _dailyDraggedItem,
            ref _dailyDragAdorner,
            ref _dailyDraggedContainer);
    }

    /// <summary>鼠标移动超过系统拖拽阈值后启动每日任务排序操作。</summary>
    private void DailyTaskList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        BeginListDrag<DailyTaskItem>(
            DailyTaskList,
            e,
            ref _dailyDragStart,
            ref _dailyDraggedItem,
            ref _dailyDragAdorner,
            ref _dailyDraggedContainer,
            onDragStarted: () => _dailyDragMoved = true);
    }

    /// <summary>每日任务列表只接受内部的移动排序。</summary>
    private void DailyTaskList_DragOver(object sender, DragEventArgs e)
    {
        UpdateListDragOver<DailyTaskItem>(
            DailyTaskList,
            e,
            ref _dailyDragAdorner,
            ref _dailyDropIndicator);
    }

    /// <summary>拖拽离开每日任务列表时清除插入目标高亮。</summary>
    private void DailyTaskList_DragLeave(object sender, DragEventArgs e)
    {
        DailyTaskList.SelectedItem = null;
        RemoveDropIndicator(ref _dailyDropIndicator);
    }

    /// <summary>将拖拽任务移动到鼠标所在任务的位置，集合顺序即任务执行顺序。</summary>
    private void DailyTaskList_Drop(object sender, DragEventArgs e)
    {
        RemoveDropIndicator(ref _dailyDropIndicator);
        FinishListDrop(
            DailyTaskList,
            _dailyTasks,
            e,
            item => item.Name,
            message => _logger?.Info(message));
        if (_dailyTasks.Any(task => task.Kind == DailyTaskKind.FierceEnemySkip)) SaveFierceEnemyDailyTask();
    }

    /// <summary>鼠标悬停问号时立即打开提示框。</summary>
    private void MissionHelpMark_MouseEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RewardTaskItem item)
        {
            item.IsHelpOpen = true;
        }
    }

    /// <summary>鼠标离开问号时关闭提示框。</summary>
    private void MissionHelpMark_MouseLeave(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RewardTaskItem item)
        {
            item.IsHelpOpen = false;
        }
    }

    /// <summary>点击任务名称时，带设置页的任务跳转到对应 Tab。</summary>
    private void DailyTaskName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dailyDragMoved)
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is DailyTaskItem task && task.CanOpenSettings)
        {
            OpenDailyTaskSettings(task);
            e.Handled = true;
        }
    }

    /// <summary>点击齿轮图标时，跳转到对应任务的设置 Tab。</summary>
    private void DailyTaskGearButton_Click(object sender, MouseButtonEventArgs e)
    {
        if (_dailyDragMoved)
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is DailyTaskItem task && task.CanOpenSettings)
        {
            OpenDailyTaskSettings(task);
            e.Handled = true;
        }
    }

    /// <summary>点击整个任务行时，带设置页的任务跳转到对应 Tab。</summary>
    private void DailyTaskRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dailyDragMoved || IsCheckBoxAncestor(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is DailyTaskItem task && task.CanOpenSettings)
        {
            OpenDailyTaskSettings(task);
            e.Handled = true;
        }
    }

    /// <summary>判断鼠标原始命中点是否位于勾选框内部，避免点击勾选框时触发跳转。</summary>
    private static bool IsCheckBoxAncestor(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is CheckBox)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    /// <summary>根据每日任务类型切换到对应设置页。</summary>
    private void OpenDailyTaskSettings(DailyTaskItem task)
    {
        switch (task.Kind)
        {
            case DailyTaskKind.ClaimRewards:
                RewardTab.IsSelected = true;
                _logger?.Info(L.Text("已打开 奖励 设置页"));
                break;

            case DailyTaskKind.FierceEnemySkip:
                FierceEnemyAssaultTab.IsSelected = true;
                _logger?.Info(L.Text("已打开 强敌来袭 页"));
                break;

            case DailyTaskKind.ConsumeAp:
                ApConsumptionTab.IsSelected = true;
                _logger?.Info(L.Text("已打开 AP 消耗设置页"));
                break;
        }
    }

    /// <summary>记录任意可排序列表的拖拽起点与待移动项。</summary>
    private static void StartListDrag<T>(
        ListBox listBox,
        MouseButtonEventArgs e,
        ref Point dragStart,
        ref T? draggedItem,
        ref DragPreviewAdorner? dragAdorner,
        ref ListBoxItem? draggedContainer)
        where T : class
    {
        dragStart = e.GetPosition(listBox);
        var container = ItemsControl.ContainerFromElement(
            listBox,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        draggedItem = container?.DataContext as T;
    }

    /// <summary>鼠标移动超过系统拖拽阈值后启动列表项的移动排序操作。</summary>
    private void BeginListDrag<T>(
        ListBox listBox,
        MouseEventArgs e,
        ref Point dragStart,
        ref T? draggedItem,
        ref DragPreviewAdorner? dragAdorner,
        ref ListBoxItem? draggedContainer,
        Action? onDragStarted = null)
        where T : class
    {
        if (e.LeftButton != MouseButtonState.Pressed || draggedItem is null)
        {
            return;
        }

        var current = e.GetPosition(listBox);
        if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragged = draggedItem;
        draggedItem = null;
        draggedContainer = listBox.ItemContainerGenerator.ContainerFromItem(dragged) as ListBoxItem;
        var adornerLayer = AdornerLayer.GetAdornerLayer(listBox);
        if (draggedContainer is not null && adornerLayer is not null)
        {
            draggedContainer.Opacity = 0.55;
            dragAdorner = new DragPreviewAdorner(listBox, draggedContainer);
            dragAdorner.UpdatePosition(current);
            adornerLayer.Add(dragAdorner);
        }

        try
        {
            onDragStarted?.Invoke();
            DragDrop.DoDragDrop(listBox, dragged, DragDropEffects.Move);
        }
        finally
        {
            if (dragAdorner is not null && adornerLayer is not null)
            {
                adornerLayer.Remove(dragAdorner);
            }

            if (draggedContainer is not null)
            {
                draggedContainer.Opacity = 1;
            }

            dragAdorner = null;
            draggedContainer = null;
            listBox.SelectedItem = null;
        }
    }

    /// <summary>
    /// 拖拽经过列表时，参考 AntD 拖拽排序示例：只允许同类型列表项移动，
    /// 并用虚线指示目标行上方或下方的插入位置。
    /// </summary>
    private static void UpdateListDragOver<T>(
        ListBox listBox,
        DragEventArgs e,
        ref DragPreviewAdorner? dragAdorner,
        ref DropIndicatorAdorner? dropIndicator)
        where T : class
    {
        e.Effects = e.Data.GetDataPresent(typeof(T))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        dragAdorner?.UpdatePosition(e.GetPosition(listBox));

        var targetContainer = ItemsControl.ContainerFromElement(
            listBox,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        var target = targetContainer?.DataContext as T;
        var dragged = e.Data.GetData(typeof(T)) as T;
        if (target is null || dragged is null)
        {
            RemoveDropIndicator(ref dropIndicator);
            e.Handled = true;
            return;
        }

        var dragIndex = listBox.Items.IndexOf(dragged);
        var targetIndex = listBox.Items.IndexOf(target);
        if (dragIndex < 0 || targetIndex < 0 || dragIndex == targetIndex)
        {
            RemoveDropIndicator(ref dropIndicator);
        }
        else
        {
            // 目标在拖动行上方时画顶边线，下方时画底边线，与 AntD 示例一致。
            UpdateDropIndicator(listBox, targetContainer!, targetIndex < dragIndex, ref dropIndicator);
        }

        e.Handled = true;
    }

    /// <summary>更新虚线插入位置指示线。</summary>
    private static void UpdateDropIndicator(
        ListBox listBox,
        ListBoxItem targetContainer,
        bool showTop,
        ref DropIndicatorAdorner? dropIndicator)
    {
        var adornerLayer = AdornerLayer.GetAdornerLayer(listBox);
        if (adornerLayer is null)
        {
            return;
        }

        if (dropIndicator is null)
        {
            dropIndicator = new DropIndicatorAdorner(listBox, adornerLayer);
            adornerLayer.Add(dropIndicator);
        }

        dropIndicator.UpdatePosition(targetContainer, showTop);
    }

    /// <summary>移除当前插入位置指示线。</summary>
    private static void RemoveDropIndicator(ref DropIndicatorAdorner? dropIndicator)
    {
        dropIndicator?.Remove();
        dropIndicator = null;
    }

    /// <summary>把拖拽项移动到鼠标所在项的位置，并更新对应可观察集合。</summary>
    private static void FinishListDrop<T>(
        ListBox listBox,
        ObservableCollection<T> items,
        DragEventArgs e,
        Func<T, string> getName,
        Action<string>? onMoved = null)
        where T : class
    {
        if (e.Data.GetData(typeof(T)) is not T dragged)
        {
            return;
        }

        var targetContainer = ItemsControl.ContainerFromElement(
            listBox,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        var target = targetContainer?.DataContext as T;
        var oldIndex = items.IndexOf(dragged);
        var newIndex = target is null ? items.Count - 1 : items.IndexOf(target);
        if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
        {
            items.Move(oldIndex, newIndex);
            onMoved?.Invoke(L.Format($"已调整顺序：{getName(dragged)} 移动到第 {newIndex + 1} 位。"));
        }

        listBox.SelectedItem = null;
        e.Handled = true;
    }

    /// <summary>启动及保存设置后统一刷新底部信息，区服和语言沿用设置中的显示名称。</summary>
    private void UpdateFooterText()
    {
        ApplyServerFeatureVisibility();
        var server = GameProfiles.Servers.FirstOrDefault(item => item.Id == _config.GameServer)?.Label ?? _config.GameServer;
        var language = GameProfiles.Languages.FirstOrDefault(item => item.Id == _config.GameLanguage)?.Label ?? _config.GameLanguage;
        FooterText.Text = $"v{AppVersion} · {L.Text(server)} · {language}";
    }

    /// <summary>重新加载主界面/个人基地/商店布局。</summary>
    private void ReloadLayouts()
    {
        if (_uiLayoutService is null)
        {
            return;
        }

        _mainLayout = _uiLayoutService.Load(ConfigService.ResolvePath(_config.MainUiLayout));
        _personalBaseLayout = _uiLayoutService.Load(ConfigService.ResolvePath(_config.PersonalBaseLayout));
        _shopLayout = _uiLayoutService.Load(ConfigService.ResolvePath(_config.ShopLayout));
        _giftLayout = _uiLayoutService.Load(ConfigService.ResolvePath(_config.GiftLayout));
        _developLayout = _uiLayoutService.Load(ConfigService.ResolvePath(_config.DevelopLayout));
        _stageLayout = _uiLayoutService.Load(ConfigService.ResolvePath(_config.StageLayout));
        _upgradeStagesLayout = _uiLayoutService.Load(ConfigService.ResolvePath(_config.UpgradeStagesLayout));
        _missionLayout = _uiLayoutService.Load(ConfigService.ResolvePath(_config.MissionLayout));
    }

    /// <summary>加载作品列表与画布位置，并填充任务下拉框。</summary>
    private void LoadStageWorks()
    {
        LoadAutoProgressSettings();
        if (_logger is null)
        {
            return;
        }

        var hardService = new HardStagesService(_logger);
        _hardStages = hardService.Load(ConfigService.ResolvePath(_config.HardStagesFile)).HardStages;
        RefreshAutoProgressTargets();
        _stagePositions = new StageWorkPositionsService(_logger)
            .Load(ConfigService.ResolvePath(_config.StageWorkPositionsFile));

        _apWorkItems.Clear();
        foreach (var work in _hardStages)
        {
            _apWorkItems.Add(new ApConsumptionWorkItem
            {
                WorkId = work.Id,
                Work = work.Work,
                HardCount = work.HardCount,
            });
        }

        RefreshStagePositionItems();
        ApPlanStatusText.Text = L.Format($"已加载 {_apWorkItems.Count} 个作品；拖拽可调整执行顺序。");
        _logger.Info(L.Format($"已加载 {_hardStages.Count} 个作品、{_stagePositions.Positions.Count} 个画布位置"));
    }

    /// <summary>按作品 ID 关联 hard_stages 名称，刷新位置调整列表。</summary>
    private void RefreshStagePositionItems()
    {
        var names = _hardStages.ToDictionary(work => work.Id, work => work.Work);
        _stagePositionItems.Clear();
        foreach (var position in _stagePositions.Positions.OrderBy(p => p.Id))
        {
            _stagePositionItems.Add(new StagePositionItem
            {
                Id = position.Id,
                Work = names.GetValueOrDefault(position.Id, L.Format($"作品 {position.Id}")),
                Position = position,
            });
        }

        if (StagePositionStatusText is not null)
        {
            StagePositionStatusText.Text = L.Format($"共 {_stagePositionItems.Count} 个作品位置；修改后点击“保存”写入坐标文件。");
        }
    }

    /// <summary>重新从坐标文件加载作品位置并刷新列表。</summary>
    private void StagePositionRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;

        if (_logger is null)
        {
            return;
        }

        _stagePositions = new StageWorkPositionsService(_logger)
            .Load(ConfigService.ResolvePath(_config.StageWorkPositionsFile));
        RefreshStagePositionItems();
        _logger.Info(L.Format($"已重新加载作品位置: {_stagePositions.Positions.Count} 个"));
    }

    /// <summary>将位置调整列表中的坐标保存回作品位置文件。</summary>
    private void StagePositionSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;

        if (_logger is null)
        {
            return;
        }

        try
        {
            var data = new StageWorkPositionsData();
            foreach (var item in _stagePositionItems)
            {
                var position = item.Position;
                if (item.CanvasX <= 0 || item.CanvasY <= 0)
                {
                    _logger.Warn(L.Format($"作品 {item.Id} 坐标无效，已跳过保存: ({item.CanvasX},{item.CanvasY})"));
                    continue;
                }

                position.CanvasX = item.CanvasX;
                position.CanvasY = item.CanvasY;
                data.Positions.Add(position);
            }

            new StageWorkPositionsService(_logger).Save(
                data,
                ConfigService.ResolvePath(_config.StageWorkPositionsFile));
            _logger.Info(L.Format($"已保存 {data.Positions.Count} 个作品位置"));
            StagePositionStatusText.Text = L.Format($"已保存 {data.Positions.Count} 个作品位置。");
        }
        catch (Exception ex)
        {
            _logger.Error(L.Format($"保存作品位置失败: {ex.Message}"));
            StagePositionStatusText.Text = L.Format($"保存失败: {ex.Message}");
        }
    }

    /// <summary>位置调整列表行：包装画布坐标，便于直接编辑并保存。</summary>
    private sealed class StagePositionItem
    {
        public int Id { get; init; }
        public string Work { get; init; } = string.Empty;
        public StageWorkPosition Position { get; init; } = new();

        public int CanvasX
        {
            get => Position.CanvasX;
            set => Position.CanvasX = value;
        }

        public int CanvasY
        {
            get => Position.CanvasY;
            set => Position.CanvasY = value;
        }
    }

    /// <summary>创建下载界面识别服务（下载/取消按钮在屏幕下方 30% 区域识别）。</summary>
    private DownloadDialogService CreateDownloadDialogService()
    {
        if (_automation is null || _logger is null)
        {
            throw new InvalidOperationException(L.Text("下载界面服务依赖尚未初始化"));
        }

        return new DownloadDialogService(
            _automation,
            _matcher,
            _logger,
            ConfigService.ResolvePath(_config.DownloadTemplate),
            ConfigService.ResolvePath(_config.DownloadCancelTemplate),
            Math.Clamp(_config.DefaultMatchThreshold, 0.5, 1.0));
    }

    /// <summary>创建出击后流程服务（下载界面/TAP TO NEXT 并发识别 + 战斗图标配置）。</summary>
    private SortieFlowService CreateSortieFlowService()
    {
        if (_adb is null || _automation is null || _logger is null || _downloadDialogService is null || _connectionErrorDetector is null)
        {
            throw new InvalidOperationException(L.Text("出击流程服务依赖尚未初始化"));
        }

        // 所有任务共用同一自动化会话，避免重复全局处理器的冷却状态分裂。
        return new SortieFlowService(
            _automation,
            _matcher,
            _logger,
            _downloadDialogService,
            ConfigService.ResolvePath(_config.SortieTapToNextTemplate),
            ConfigService.ResolvePath(_config.SpeedOnTemplate),
            ConfigService.ResolvePath(_config.SpeedOffTemplate),
            ConfigService.ResolvePath(_config.ContinueTemplate),
            ConfigService.ResolvePath(_config.AgainTemplate),
            ConfigService.ResolvePath(_config.SelectStageTemplate),
            ConfigService.ResolvePath(_config.AgainCloseTemplate),
            ConfigService.ResolvePath(_config.FailedTemplate),
            ConfigService.ResolvePath(_config.EmptySlotHintTemplate),
            ConfigService.ResolvePath(_config.EmptySlotSortieTemplate),
            ConfigService.ResolvePath(_config.EmptySlotCancelTemplate),
            ConfigService.ResolvePath(_config.NoApTemplate),
            ConfigService.ResolvePath(_config.PlayerLevelUpTemplate),
            ConfigService.ResolvePath(_config.RewardTemplate),
            ConfigService.ResolvePath(_config.TapToNextRewardTemplate),
            Math.Clamp(_config.DefaultMatchThreshold, 0.5, 1.0),
            ConfigService.ResolvePath(_config.SortieTerrainTemplate),
            ConfigService.ResolvePath(_config.SortieDownloadTemplate),
            new ApStopSignal(ReportApInsufficient, _logger));
    }

    /// <summary>创建使用绝对坐标与作品间相对距离的主要关卡服务。</summary>
    private StageService CreateStageService()
    {
        if (_adb is null || _automation is null || _logger is null)
        {
            throw new InvalidOperationException(L.Text("主要关卡服务依赖尚未初始化"));
        }

        var recognizer = new StageWorkRecognizer(
            _adb,
            _matcher,
            _logger,
            ConfigService.ResolvePath(_config.StageTemplateDirectory));
        var options = new StageNavigationOptions
        {
            SwipeCanvasDelta = Math.Max(1, _config.StageSwipeCanvasDelta),
            HalfSwipeLeftTapCorrection = _config.StageHalfSwipeLeftTapCorrection,
            HalfSwipeRightTapCorrection = _config.StageHalfSwipeRightTapCorrection,
            SwipeSettleMs = Math.Max(0, _config.StageSwipeSettleMs),
            WorkTapSettleMs = Math.Max(0, _config.StageWorkTapSettleMs),
            PageSettleMs = Math.Max(0, _config.StagePageSettleMs),
            ResetSearchMaxSwipes = Math.Max(0, _config.StageResetSearchMaxSwipes),
            ResetAnchorThreshold = Math.Clamp(_config.StageResetAnchorThreshold, 0.5, 1.0),
            WorkSelectTemplatePath = ConfigService.ResolvePath(_config.StageWorkSelectTemplate),
            WorkSelectClosedTemplatePath = ConfigService.ResolvePath(_config.StageWorkSelectCloseTemplate),
            WorkSelectThreshold = Math.Clamp(_config.DefaultMatchThreshold, 0.5, 1.0),
        };
        var ocr = GetTextOcr();
        var titleRecognizer = new OnnxStageTitleRecognizer(_adb, _logger, ocr);
        var nodeRecognizer = new StageNodeRecognizer(
            _adb,
            _matcher,
            _logger,
            ConfigService.ResolvePath(_config.StageNodeTemplateDirectory),
            numberRecognizer: new OnnxStageNumberRecognizer(ocr), evidence: GetRecognitionEvidence());
        _stageSkipService = CreateStageSkipService(forUpgradeStages: false);
        var stageService = new StageService(
            _automation,
            recognizer,
            _logger,
            options,
            titleRecognizer,
            nodeRecognizer,
            new WorkLogoNavigator(
                _automation,
                new WorkLogoVision(_adb, _matcher, ConfigService.ResolvePath(_config.StageTemplateDirectory),
                    ConfigService.ResolvePath("resources/templates/stage/main_stage.png"), options),
                EnsureMainScreenAsync,
                _logger,
                options, downloadDialog: CreateDownloadDialogService(), allowDownload: _config.AllowGgeaDownload));
        _apConsumptionService = new ApConsumptionService(stageService, _stageSkipService, _logger);
        return stageService;
    }

    /// <summary>强化培育与 AP 任务分别持有略过配置，避免共享实例误触发升级检测。</summary>
    private StageSkipService CreateStageSkipService(bool forUpgradeStages, Func<ScreenImage, CancellationToken, bool>? postSkipConfirmation = null)
    {
        return new StageSkipService(
            _automation ?? throw new InvalidOperationException(L.Text("自动化服务尚未初始化")),
            _logger ?? throw new InvalidOperationException(L.Text("日志服务尚未初始化")),
            new StageSkipOptions
            {
                PostSkipTimeoutMs = GameTiming.ShortRecognitionTimeoutMs,
                PostSkipConfirmation = postSkipConfirmation,
                ContinueUnitAcquisition = postSkipConfirmation is not null,
                PlayerRankUpTemplatePath = forUpgradeStages ? "" : ConfigService.ResolvePath("resources/templates/stage/skip/player_rank_up.png"),
                PlayerRankUpNextTemplatePath = forUpgradeStages ? "" : ConfigService.ResolvePath("resources/templates/stage/skip/rank_up_next.png"),
                PostSkipPageTemplatePath = forUpgradeStages ? ConfigService.ResolvePath("resources/templates/stage/upgrade_stage.png") : "",
                SkipResultTemplatePath = ConfigService.ResolvePath(_config.SkipResultTemplate),
                SkipCountLimitTemplatePath = postSkipConfirmation is null ? ConfigService.ResolvePath(_config.SkipCountLimitTemplate) : "",
                // 强化培育不消耗 AP，不能接入 AP 检测或影响后续 AP 任务的停止信号。
                ApRecoveryPromptTemplatePath = forUpgradeStages ? "" : ConfigService.ResolvePath("resources/templates/stage/skip/ap_recovery_title.png"),
                ApRecoveryCloseTemplatePath = forUpgradeStages ? "" : ConfigService.ResolvePath(_config.ApRecoveryCloseTemplate),
                SkipResultThreshold = Math.Clamp(_config.DefaultMatchThreshold, 0.5, 1.0),
                SkipCountLimitThreshold = Math.Clamp(_config.DefaultMatchThreshold, 0.5, 1.0),
            }, evidence: GetRecognitionEvidence(), apStopSignal: forUpgradeStages ? null : new ApStopSignal(ReportApInsufficient,
                _logger ?? throw new InvalidOperationException(L.Text("日志服务尚未初始化"))));
    }

    /// <summary>在覆盖层上添加矩形。</summary>
    private void AddRect(Roi roi, Brush stroke, double thickness, Brush fill, bool dash)
    {
        var rect = new Rectangle
        {
            Width = roi.Width,
            Height = roi.Height,
            Stroke = stroke,
            StrokeThickness = thickness,
            Fill = fill,
        };
        if (dash)
        {
            rect.StrokeDashArray = new DoubleCollection { 5, 3 };
        }

        Canvas.SetLeft(rect, roi.X);
        Canvas.SetTop(rect, roi.Y);
        OverlayCanvas.Children.Add(rect);
    }

    /// <summary>在覆盖层上添加小圆点标记。</summary>
    private void AddPoint(Point point, Brush brush)
    {
        const double size = 10;
        var ellipse = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = brush,
            Stroke = Brushes.White,
            StrokeThickness = 1,
        };
        Canvas.SetLeft(ellipse, point.X - size / 2);
        Canvas.SetTop(ellipse, point.Y - size / 2);
        OverlayCanvas.Children.Add(ellipse);
    }

    /// <summary>深拷贝执行配置，隔离设置窗口及调用方持有的可变集合。</summary>
    private AppConfig SnapshotConfig() => System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
        System.Text.Json.JsonSerializer.Serialize(_config))!;

    /// <summary>
    /// UI 线程上的唯一执行入口，不排队重放用户动作。持有期间禁止设备切换与服务重建，
    /// 因而业务的本地 serial、配置快照、ADB 路径和服务实例在整个 await/finally 链中保持一致。
    /// 取消只发信号，只有最外层事件完成清理后才释放租约；内部刷新不拥有释放权。
    /// </summary>
    private IDisposable? TryBeginExecution()
    {
        if (_plugins?.HasDeviceFault == true)
        {
            ConnectionStateText.Text = L.Text("插件故障，请在设置中停用并重启");
            _logger?.Warn(L.Text("插件状态未恢复，已阻止新的设备操作。"));
            return null;
        }
        return TryBeginHostOperation();
    }

    /// <summary>设置仍可在插件故障后访问；执行互斥、关窗和更新闸门保持一致。</summary>
    private IDisposable? TryBeginHostOperation()
    {
        if (_windowTasks.IsStopping || _busy || _updatePending) return null;
        var lease = _executionGate.TryEnter(() =>
        {
            _executionMonitor?.EndSession();
            UpdateBusyState();
        });
        if (lease is null) return null;
        try
        {
            _apStopNotification.Reset();
            _executionMonitor?.BeginSession();
            _config = SnapshotConfig();
            _config.IsDarkMode = ((App)Application.Current).IsDarkMode;
            UpdateBusyState();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>只在组合根绑定一次：任务、导航、识图及直接 ADB 调用共享监测，不要求每段流程接入。</summary>
    private void ConfigureExecutionMonitoring()
    {
        _executionMonitor?.EndSession();
        var detector = _connectionErrorDetector!;
        _executionMonitor = new ExecutionStallMonitor(
            (screen, ct) => Task.Run(() => detector.IsVisible(screen, ct), ct), _logger!);
        _adb!.ExecutionObserver = _executionMonitor;
        // 初始化或设置重建可能发生在已持有的租约中，新服务必须继承当前执行范围。
        if (_executionGate.IsHeld) _executionMonitor.BeginSession();
    }

    /// <summary>忙状态只消费会话所有权，内部操作结束不得解除外层会话。</summary>
    private void UpdateBusyState()
    {
        var busy = _updatePending || _executionGate.IsHeld || _windowTasks.IsStopping || _starting || _dailyRunning || _autoFarmRunning || _scoutRunning || _ballFactoryRunning || _autoProgressRunning;
        _busy = busy;
        SettingsButton.IsEnabled = !busy;
        DeviceCombo.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        ReconnectButton.IsEnabled = !busy && DeviceCombo.SelectedItem is AdbDevice { CanReconnect: true };
        ScreenshotButton.IsEnabled = !busy;
        ScoutStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || (_scoutRunning && _scoutCts?.IsCancellationRequested == false));
        BallFactoryStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || (_ballFactoryRunning && _ballFactoryCts?.IsCancellationRequested == false));
        BallFactoryGpBox.IsEnabled = !busy;
        TapButton.IsEnabled = !busy;
        SwipeButton.IsEnabled = !busy;
        SaveTemplateButton.IsEnabled = !busy;
        TestTemplateButton.IsEnabled = !busy;
        StartGameButton.IsEnabled = !_windowTasks.IsStopping && (!busy || (_starting && _startCts?.IsCancellationRequested == false));
        AutoFarmCountBox.IsEnabled = !busy;
        DailySelectAllCheckBox.IsEnabled = !busy;
        StagePositionList.IsEnabled = !busy;
        StagePositionRefreshButton.IsEnabled = !busy;
        StagePositionSaveButton.IsEnabled = !busy;
        ApWorkList.IsEnabled = !busy;
        DailyTaskList.IsEnabled = !busy;
        RewardTaskList.IsEnabled = !busy;
        ApProfileCombo.IsEnabled = !busy;
        ApProfileNameBox.IsEnabled = !busy;
        SaveApProfileButton.IsEnabled = !busy;
        // 每日任务运行中保持按钮可用，用于“中止”
        DailyStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || (_dailyRunning && _dailyCts?.IsCancellationRequested == false));
        // 自动刷本运行中保持按钮可用，用于“中止”
        AutoFarmStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || (_autoFarmRunning && _autoFarmCts?.IsCancellationRequested == false));
        AutoProgressStartButton.IsEnabled = !_windowTasks.IsStopping &&
            (!busy || (_autoProgressRunning && _autoProgressCts?.IsCancellationRequested == false));
        FierceEnemySelectionPanel.IsEnabled = !busy && FierceEnemyUnitBox.Items.Count > 0;
        FierceEnemyAssaultStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || _fierceEnemyAssaultCts?.IsCancellationRequested == false);
        MasterLeagueAutoFormationCheck.IsEnabled = !busy;
        MasterLeagueRetryDefeatedCheck.IsEnabled = !busy;
        MasterLeagueStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || _masterLeagueCts?.IsCancellationRequested == false);
        AutoProgressOptionsPanel.IsEnabled = !busy;
        AutoProgressQueueList.IsEnabled = !busy;
        AutoProgressWorkCombo.IsEnabled = !busy;
        AutoProgressTypeCombo.IsEnabled = !busy;
        AutoProgressHistoryBossCheck.IsEnabled = !_windowTasks.IsStopping && !busy;
        AutoProgressAddButton.IsEnabled = !_windowTasks.IsStopping && !busy && AutoProgressWorkCombo.SelectedItem is not null;
    }

    /// <summary>根据配置创建主界面检测器（模板路径与固定 ROI 解析为运行时参数）。</summary>
    private MainScreenNavigator CreateMainScreenNavigator()
    {
        return new MainScreenNavigator(CreateMainScreenDetector(), _adb!, _logger!, GetRecognitionEvidence());
    }

    /// <summary>略过与主界面恢复共享本地取证限频和轮转槽位，重建服务也不清空状态。</summary>
    private RecognitionEvidence GetRecognitionEvidence()
        => _recognitionEvidence ??= new RecognitionEvidence(Path.Combine(AppContext.BaseDirectory, "logs", "recognition"), _logger!);

    /// <summary>导航与推图返回确认共用主界面配置；检测器只观察，不能主动点击或恢复页面。</summary>
    private MainScreenDetector CreateMainScreenDetector()
    {
        var options = new MainScreenDetectorOptions(
            ConfigService.ResolvePath(_config.MainScreenActivatedTemplate),
            ConfigService.ResolvePath(_config.MainScreenInactiveTemplate),
            ConfigService.ResolvePath(_config.MainScreenBackTemplate),
            new Roi(
                _config.MainScreenButtonRoi.X,
                _config.MainScreenButtonRoi.Y,
                _config.MainScreenButtonRoi.Width,
                _config.MainScreenButtonRoi.Height),
            new Roi(
                _config.MainScreenBackRoi.X,
                _config.MainScreenBackRoi.Y,
                _config.MainScreenBackRoi.Width,
                _config.MainScreenBackRoi.Height),
            Math.Clamp(_config.MainScreenThreshold, 0.5, 1.0),
            Math.Max(1, _config.MainScreenConfirmationFrames),
            Math.Max(0, _config.MainScreenConfirmationIntervalMs));
        return new MainScreenDetector(_adb!, _matcher, _logger!, options, _connectionErrorDetector, _automation!.GlobalScreens);
    }

    /// <summary>根据配置创建任务失败后的网络错误检测器。</summary>
    private ConnectionErrorDetector CreateConnectionErrorDetector() =>
        new(
            _adb!,
            _matcher,
            _logger!,
            ConfigService.ResolvePath(_config.ConnectionErrorTemplate));

    /// <summary>跟随鼠标显示的列表项半透明拖拽预览。</summary>
    private sealed class DragPreviewAdorner : Adorner
    {
        private readonly VisualBrush _previewBrush;
        private readonly double _previewWidth;
        private readonly double _previewHeight;
        private Point _position;

        public DragPreviewAdorner(UIElement adornedElement, FrameworkElement previewSource)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
            _previewBrush = new VisualBrush(previewSource) { Opacity = 0.88 };
            _previewWidth = previewSource.ActualWidth;
            _previewHeight = previewSource.ActualHeight;
        }

        /// <summary>更新预览左上角附近的鼠标位置并请求重绘。</summary>
        public void UpdatePosition(Point position)
        {
            _position = position;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            var left = Math.Clamp(_position.X + 12, 0, Math.Max(0, AdornedElement.RenderSize.Width - _previewWidth));
            var top = Math.Clamp(_position.Y + 10, 0, Math.Max(0, AdornedElement.RenderSize.Height - _previewHeight));
            drawingContext.DrawRoundedRectangle(
                _previewBrush,
                new Pen((Brush)Application.Current.FindResource("Brush.Accent"), 2),
                new Rect(left, top, _previewWidth, _previewHeight),
                radiusX: 8,
                radiusY: 8);
        }
    }

    /// <summary>AntD 拖拽排序风格的虚线插入位置指示线。</summary>
    private sealed class DropIndicatorAdorner : Adorner
    {
        private readonly AdornerLayer _layer;
        private readonly ListBox _listBox;
        private Rect? _line;
        private bool _removed;

        public DropIndicatorAdorner(ListBox listBox, AdornerLayer layer)
            : base(listBox)
        {
            _listBox = listBox;
            _layer = layer;
            IsHitTestVisible = false;
        }

        /// <summary>根据目标行更新指示线；showTop 为 true 时显示在目标行上方。</summary>
        public void UpdatePosition(ListBoxItem target, bool showTop)
        {
            var origin = target.TranslatePoint(new Point(0, 0), _listBox);
            var y = showTop ? origin.Y : origin.Y + target.ActualHeight;
            _line = new Rect(
                origin.X + 3,
                y - 1,
                Math.Max(0, target.ActualWidth - 6),
                2);
            InvalidateVisual();
        }

        /// <summary>从装饰层移除指示线，重复调用安全。</summary>
        public void Remove()
        {
            if (_removed)
            {
                return;
            }

            _removed = true;
            _layer.Remove(this);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (_line is not Rect line)
            {
                return;
            }

            var pen = new Pen((Brush)Application.Current.FindResource("Brush.Accent"), 2)
            {
                DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
            };
            drawingContext.DrawLine(
                pen,
                new Point(line.Left, line.Top + line.Height / 2),
                new Point(line.Right, line.Top + line.Height / 2));
        }
    }

    /// <summary>模板下拉框条目。</summary>
    private sealed record TemplateFileItem(string Path, string Name)
    {
        public override string ToString() => Name;
    }
}

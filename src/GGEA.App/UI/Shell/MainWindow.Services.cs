using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.App.UI;

/// <summary>窗口持有的服务与布局，以及启动和设置变更共用的装配入口。</summary>
public partial class MainWindow
{
    private RecognitionEvidence? _recognitionEvidence;
    private EnvironmentDiagnostics? _environmentDiagnostics;
    private AutomationDevice? _adb;
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
    private UiLayoutService? _uiLayoutService;
    private UiLayout _mainLayout = new();
    private UiLayout _personalBaseLayout = new();
    private UiLayout _shopLayout = new();
    private UiLayout _giftLayout = new();
    private UiLayout _developLayout = new();
    private UiLayout _stageLayout = new();
    private UiLayout _upgradeStagesLayout = new();
    private UiLayout _missionLayout = new();
    private readonly TemplateMatcher _matcher = new();

    /// <summary>窗口统一拥有关卡与继续文字的 CPU OCR；重建自动化服务时仍借用同一模型。</summary>
    private OnnxTextRecognizer GetTextOcr() => _textOcr ??= new OnnxTextRecognizer(
        ConfigService.ResolvePath($"resources/ocr/{OnnxTextRecognizer.ModelFileName}"));

    /// <summary>启动和设置重建使用同一组任务服务，确保出击识别、设备通道和模板路径属于当前配置。</summary>
    private void InitializeTaskServices(GgeaLogger log)
    {
        _adb = new AutomationDevice(new AdbService(_config.AdbPath, log, _config.CommandTimeoutMs, _config.ScreenshotTimeoutMs, _environmentDiagnostics, _config.EmulatorPath, _config.PreferMuMuNativeScreenshot, _config.GamePackage));
        _deviceCompatibility = new DeviceCompatibilityService(_adb.Transport);
        _connectionErrorDetector = CreateConnectionErrorDetector();
        ConfigureExecutionMonitoring();
        _automation = new AutomationService(_adb, _matcher, log, connectionErrorDetector: _connectionErrorDetector,
                skipTemplatePath: ConfigService.ResolvePath(_config.LoginRewardSkipTemplate), textOcr: GetTextOcr());
        _automation.CapacityWarning += ReportCapacityWarning;
        _emulatorAdapter = EmulatorAdapterFactory.Create(_config.EmulatorType, _adb, log, mumuPathResolver: () => EmulatorLocator.ResolveMuMuManager(_config.AdbPath, _config.EmulatorPath));
        _emulator = new EmulatorService(_emulatorAdapter);
        _gameEntry = new GameEntryService(_automation, log);
        _mainScreenNavigator = CreateMainScreenNavigator();
        _uiLayoutService ??= new UiLayoutService(log);
        ReloadLayouts();
        _battleshipLocator = new BattleshipLocator(_adb, _matcher, log);
        _personalBaseService = new PersonalBaseService(_automation, _battleshipLocator, log);
        _shopService = new ShopService(_automation, new DiamondSafetyChecker(_adb, _matcher, log), log);
        _giftService = new GiftService(_automation, log);
        _developService = new DevelopService(_automation, log, _config.DevelopMsFixedScales);
        ApplyEmulatorCapabilities();
        _stageService = CreateStageService();
        _upgradeStagesService = new UpgradeStagesService(_automation, CreateStageSkipService(forUpgradeStages: true), log);
        _missionRewardService = new MissionRewardService(_automation, log);
        _downloadDialogService = CreateDownloadDialogService();
        _sortieFlowService = CreateSortieFlowService();
        _apProfileService = new ApConsumptionProfileService(log);
        LoadStageWorks();
        LoadApProfiles();
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
                // 当前恢复证据对应标准 HARD 的 AP/次数布局，强敌和强化培育保留各自确认策略。
                RecoverUnchangedExecutionDialog = !forUpgradeStages && postSkipConfirmation is null,
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
            }, evidence: GetRecognitionEvidence(), // AP 消耗按队列完成统一通知；强敌略过仍即时报告 AP 不足。
                apStopSignal: postSkipConfirmation is null || forUpgradeStages ? null : new ApStopSignal(ReportApInsufficient,
                _logger ?? throw new InvalidOperationException(L.Text("日志服务尚未初始化"))));
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
}

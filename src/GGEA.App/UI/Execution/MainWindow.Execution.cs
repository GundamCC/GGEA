using System.Windows;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Localization;

namespace GGEA.App.UI;

/// <summary>窗口业务的执行租约、停滞监测与控件可用状态。</summary>
public partial class MainWindow
{
    private bool _busy;
    private readonly DeviceExecutionGate _executionGate = new();
    private ExecutionStallMonitor? _executionMonitor;

    /// <summary>深拷贝执行配置，隔离设置窗口及调用方持有的可变集合。</summary>
    private AppConfig SnapshotConfig() => ConfigService.Snapshot(_config);

    /// <summary>
    /// UI 线程上的唯一执行入口，不排队重放用户动作。持有期间禁止设备切换与服务重建，
    /// 因而业务的本地 serial、配置快照、ADB 路径和服务实例在整个 await/finally 链中保持一致。
    /// 取消只发信号，只有最外层事件完成清理后才释放租约；内部刷新不拥有释放权。
    /// </summary>
    private DeviceExecutionGate.Lease? TryBeginExecution(bool logRoutineChecks = true)
    {
        if (_plugins?.HasDeviceFault == true)
        {
            ConnectionStateText.Text = L.Text("插件故障，请在设置中停用并重启");
            _logger?.Warn(L.Text("插件状态未恢复，已阻止新的设备操作。"));
            return null;
        }
        return TryBeginHostOperation(logRoutineChecks);
    }

    /// <summary>设置仍可在插件故障后访问；执行互斥、关窗和更新闸门保持一致。</summary>
    private DeviceExecutionGate.Lease? TryBeginHostOperation(bool logRoutineChecks = true)
    {
        if (_windowTasks.IsStopping || _busy) return null;
        var lease = _executionGate.TryEnter(() =>
        {
            _executionMonitor?.EndSession();
            UpdateBusyState();
        });
        if (lease is null) return null;
        try
        {
            _apStopNotification.Reset();
            _adb?.BeginOperation();
            _executionMonitor?.BeginSession(logRoutineChecks);
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
        var device = _adb!;
        var tutorial = new TutorialScreenHandler(device.CaptureNormalizedAsync, device.TapAsync, detector.IsVisible, _logger!);
        _executionMonitor = new ExecutionStallMonitor(
            (screen, ct) => Task.Run(() => detector.IsVisible(screen, ct), ct), _logger!, handleTutorial: tutorial.HandleAsync);
        _adb!.ExecutionObserver = _executionMonitor;
        // 初始化或设置重建可能发生在已持有的租约中，新服务必须继承当前执行范围。
        if (_executionGate.IsHeld) _executionMonitor.BeginSession();
    }

    /// <summary>忙状态只消费会话所有权，内部操作结束不得解除外层会话。</summary>
    private void UpdateBusyState()
    {
        var busy = _executionGate.IsHeld || _windowTasks.IsStopping || _starting || _dailyRunning || _autoFarmRunning || _scoutRunning || _ballFactoryRunning || _autoProgressRunning;
        _busy = busy;
        if (!busy) _waitingForIdle?.TrySetResult();
        SettingsButton.IsEnabled = !busy;
        DeviceCombo.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        ReconnectButton.IsEnabled = !busy && DeviceCombo.SelectedItem is AdbDevice { CanReconnect: true };
        ScreenshotView.ScreenshotButton.IsEnabled = !busy;
        ScreenshotView.MissionEntryButton.IsEnabled = !busy;
        ScoutView.ScoutStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || (_scoutRunning && _scoutCts?.IsCancellationRequested == false));
        BallFactoryView.BallFactoryStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || (_ballFactoryRunning && _ballFactoryCts?.IsCancellationRequested == false));
        BallFactoryView.BallFactoryGpBox.IsEnabled = !busy;
        ScreenshotView.TapButton.IsEnabled = !busy;
        ScreenshotView.SwipeButton.IsEnabled = !busy;
        ScreenshotView.PreciseDragButton.IsEnabled = ScreenshotView.SwipeButton.IsEnabled;
        ScreenshotView.SaveTemplateButton.IsEnabled = !busy;
        ScreenshotView.TestTemplateButton.IsEnabled = !busy;
        StartGameButton.IsEnabled = !_windowTasks.IsStopping && (!busy || (_starting && _startCts?.IsCancellationRequested == false));
        AutoFarmView.AutoFarmCountBox.IsEnabled = !busy;
        DailyView.DailySelectAllCheckBox.IsEnabled = !busy;
        StagePositionView.StagePositionList.IsEnabled = !busy;
        StagePositionView.StagePositionRefreshButton.IsEnabled = !busy;
        StagePositionView.StagePositionSaveButton.IsEnabled = !busy;
        ApConsumptionView.ApWorkList.IsEnabled = !busy;
        DailyView.DailyTaskList.IsEnabled = !busy;
        RewardView.RewardTaskList.IsEnabled = !busy;
        ApConsumptionView.ApProfileCombo.IsEnabled = !busy;
        ApConsumptionView.ApProfileNameBox.IsEnabled = !busy;
        ApConsumptionView.SaveApProfileButton.IsEnabled = !busy;
        // 每日任务运行中保持按钮可用，用于“中止”
        DailyView.DailyStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || (_dailyRunning && _dailyCts?.IsCancellationRequested == false));
        // 自动刷本运行中保持按钮可用，用于“中止”
        AutoFarmView.AutoFarmStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || (_autoFarmRunning && _autoFarmCts?.IsCancellationRequested == false));
        AutoProgressView.AutoProgressStartButton.IsEnabled = !_windowTasks.IsStopping &&
            (!busy || (_autoProgressRunning && _autoProgressCts?.IsCancellationRequested == false));
        FierceEnemyAssaultView.FierceEnemySelectionPanel.IsEnabled = !busy && FierceEnemyAssaultView.FierceEnemyUnitBox.Items.Count > 0;
        FierceEnemyAssaultView.FierceEnemyAssaultStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || _fierceEnemyAssaultCts?.IsCancellationRequested == false);
        LimitedActivity.IsEnabled = !busy && _limitedActivities.Length > 0;
        LimitedDifficulty.IsEnabled = !busy;
        foreach (var button in new[] { LimitedStart, MedalStart, EventProgress, EventDaily })
            button.IsEnabled = !_windowTasks.IsStopping && (!busy || _limitedButton == button && _limitedCts?.IsCancellationRequested == false);
        EventDailyOptions.IsEnabled = !busy && !_windowTasks.IsStopping;
        MasterLeagueView.MasterLeagueAutoFormationCheck.IsEnabled = !busy;
        MasterLeagueView.MasterLeagueRetryDefeatedCheck.IsEnabled = !busy;
        MasterLeagueView.MasterLeagueStartButton.IsEnabled = !_windowTasks.IsStopping && (!busy || _masterLeagueCts?.IsCancellationRequested == false);
        AutoProgressView.AutoProgressOptionsPanel.IsEnabled = !busy;
        AutoProgressView.AutoProgressQueueList.IsEnabled = !busy;
        AutoProgressView.AutoProgressWorkCombo.IsEnabled = !busy;
        AutoProgressView.AutoProgressTypeCombo.IsEnabled = !busy;
        AutoProgressView.AutoProgressHistoryBossCheck.IsEnabled = !_windowTasks.IsStopping && !busy;
        AutoProgressView.AutoProgressAddButton.IsEnabled = !_windowTasks.IsStopping && !busy && AutoProgressView.AutoProgressWorkCombo.SelectedItem is not null;
    }
}

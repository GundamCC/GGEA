using GGEA.App.Localization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.App.UI;

/// <summary>自动推图的界面队列、持久化编制设置与独占设备执行入口。</summary>
public partial class MainWindow
{
    // 独立保存添加顺序，复用作品配置对象，不改变 AP 消耗计划的顺序与选择。
    private readonly ObservableCollection<AutoProgressTarget> _autoProgressQueue = new();
    private bool _autoProgressUiReady;
    private bool _loadingAutoProgressOptions;
    private bool _autoProgressRunning;
    private CancellationTokenSource? _autoProgressCts;

    /// <summary>启动和设置重载时只更新界面，不把默认控件值反向覆盖到磁盘。</summary>
    private void LoadAutoProgressSettings()
    {
        _loadingAutoProgressOptions = true;
        try
        {
            var settings = (_config.AutoProgress ?? new()).Snapshot();
            _config.AutoProgress = settings;
            AutoProgressSeriesCheck.IsChecked = settings.AdjustSeriesTeam;
            AutoProgressFreeCheck.IsChecked = settings.AdjustFreeTeam;
            AutoProgressEmptyActionCombo.SelectedIndex = (int)settings.EmptySlotAction;
            AutoProgressTerrainActionCombo.SelectedIndex = (int)settings.TerrainAction;
            RefreshAutoProgressOptions(settings);
            _autoProgressUiReady = true;
        }
        finally { _loadingAutoProgressOptions = false; }
    }

    /// <summary>设置真值集中归一化；无部队权限时即时撤销自动编制，重选部队不恢复旧授权。</summary>
    private void AutoProgressOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (!_autoProgressUiReady || _loadingAutoProgressOptions || _autoProgressRunning) return;
        var settings = new AutoProgressSettings
        {
            AdjustSeriesTeam = AutoProgressSeriesCheck.IsChecked == true,
            AdjustFreeTeam = AutoProgressFreeCheck.IsChecked == true,
            EmptySlotAction = (FormationIssueAction)AutoProgressEmptyActionCombo.SelectedIndex,
            TerrainAction = (FormationIssueAction)AutoProgressTerrainActionCombo.SelectedIndex,
        };
        settings.Normalize();
        _loadingAutoProgressOptions = true;
        try { RefreshAutoProgressOptions(settings); }
        finally { _loadingAutoProgressOptions = false; }
        _config.AutoProgress = settings;
        if (_logger is null) return;
        try { new ConfigService(_logger).Save(SnapshotConfig(), ConfigService.ResolvePath(DefaultConfigFile)); }
        catch (Exception ex)
        {
            AutoProgressStatusText.Text = L.Text("设置在当前窗口已生效，但保存失败，请检查配置目录。");
            _logger.Warn(L.Format($"自动推图设置保存失败: {ex.GetType().Name}"));
        }
    }

    /// <summary>界面消费归一化设置，自动编制条目禁用状态与当前选择始终一致。</summary>
    private void RefreshAutoProgressOptions(AutoProgressSettings settings)
    {
        AutoProgressTeamsSummary.Text = (settings.AdjustSeriesTeam, settings.AdjustFreeTeam) switch
        {
            (true, true) => L.Text("系列部队、自由部队"),
            (true, false) => L.Text("系列部队"),
            (false, true) => L.Text("自由部队"),
            _ => L.Text("未选择部队"),
        };
        AutoProgressEmptyAutoItem.IsEnabled = settings.CanAutoForm;
        AutoProgressTerrainAutoItem.IsEnabled = settings.CanAutoForm;
        AutoProgressEmptyActionCombo.SelectedIndex = (int)settings.EmptySlotAction;
        AutoProgressTerrainActionCombo.SelectedIndex = (int)settings.TerrainAction;
    }

    /// <summary>推图事件加入窗口生命周期，关窗或中止会取消完整导航与战斗链。</summary>
    private async void AutoProgressStartButton_Click(object sender, RoutedEventArgs e)
        => await RunWindowTaskAsync(StartAutoProgressAsync);

    /// <summary>冻结队列和配置，使用与每日任务相同的设备互斥租约，最终清理后才释放。</summary>
    private async Task StartAutoProgressAsync()
    {
        if (_autoProgressRunning)
        {
            _autoProgressCts?.Cancel();
            AutoProgressStartButton.Content = L.Text("正在停止…");
            UpdateBusyState();
            return;
        }
        using var execution = TryBeginExecution();
        if (execution is null) return;
        if (_automation is null || _stageService is null || _sortieFlowService is null || _logger is null ||
            !TryGetDeviceSerial(out var serial))
        { AutoProgressStatusText.Text = L.Text("请先连接并选择模拟器。"); return; }
        var targets = AvailableAutoProgressTargets();
        if (targets.Length == 0)
        { AutoProgressStatusText.Text = L.Text("请先添加推图目标。"); return; }
        var settings = _config.AutoProgress.Snapshot();
        var automation = _automation;
        var stageService = _stageService;
        var sortieFlow = _sortieFlowService;
        _autoProgressRunning = true;
        _autoProgressCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var ct = _autoProgressCts.Token;
        AutoProgressStartButton.Content = L.Text("中止推图");
        AutoProgressTeamsButton.IsChecked = false;
        UpdateBusyState();
        var endNotification = BeginTaskEndNotification(L.Text("自动推图"), distinguishResult: true);
        try
        {
            void Report(string message) { AutoProgressStatusText.Text = message; _logger.Info(message); }
            if (!await EnsureDeviceCompatibilityAsync(serial, ct))
            { AutoProgressStatusText.Text = L.Text("设备画面不兼容，已停止自动推图。"); return; }
            var formation = new AutoFormationService(automation, _matcher, _logger, AutoFormationTemplates.FromConfig(_config));
            var progressTemplates = AutoProgressFlowTemplates.FromDefaults();
            var homeDetector = CreateMainScreenDetector();
            sortieFlow.ValidateAutoProgressFlow(progressTemplates);
            // 只观察当前截图的选中节点，复用既有 OCR 与几何，不在返回确认时导航或修改作品进度。
            using var returnMainNodes = new StageNodeRecognizer(_adb!, _matcher, _logger,
                ConfigService.ResolvePath(_config.StageNodeTemplateDirectory), numberRecognizer: new OnnxStageNumberRecognizer(GetTextOcr()));
            using var returnHistoryNodes = new StageNodeRecognizer(_adb!, _matcher, _logger,
                ConfigService.ResolvePath(_config.StageNodeTemplateDirectory), numberRecognizer: new OnnxStageNumberRecognizer(GetTextOcr()),
                layout: StageNodeLayout.History);
            // 三种页面共享战斗执行，返回标题按任务绑定；历史 NORMAL 终点不强制等待主线专属奖励包。
            AutoProgressBattleService CreateBattle(AutoProgressFlowTemplates templates, bool requireSeriesReward,
                Func<ScreenImage, CancellationToken, bool>? confirmStageNodes = null)
                => new(automation, formation,
                ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie_preparations.png"),
                async (kind, allowEmpty, allowTerrain, watchEntry, token) => kind == WorkStageKind.Hard && !watchEntry
                    ? await sortieFlow.ConfirmHardProgressReturnAsync(serial,
                        await sortieFlow.ExecuteAfterSortieAsync(serial,
                            allowDownload: _config.AllowGgeaDownload, allowEmptySlotSortie: allowEmpty,
                            moreRoundsRemaining: false, allowTerrainUnsuitableSortie: allowTerrain, ct: token,
                            recoverySortieTemplate: ConfigService.ResolvePath(_config.SortieTemplate), forAutoProgress: true),
                        templates.SelectStageTitle, token, confirmSelectedNode: confirmStageNodes ?? returnMainNodes.HasSelectedNode,
                        confirmPreparation: formation.IsPreparationPageAsync)
                    // 正常 HARD 仍走原结算流程；两条路径均在两分钟无进展后才启用恢复。
                    : await sortieFlow.ExecuteForProgressAsync(serial,
                    allowDownload: _config.AllowGgeaDownload, allowEmptySlotSortie: allowEmpty,
                    allowTerrainUnsuitableSortie: allowTerrain, templates: templates,
                    confirmHome: homeToken => homeDetector.IsMainScreenAsync(serial, homeToken), ct: token,
                    // BOSS 已由队列按作品 NORMAL 数量校验，沿用本轮记录，不重新导航来推断终点。
                    requireSeriesCompleteClose: requireSeriesReward && kind == WorkStageKind.Boss,
                    confirmPreparation: formation.IsPreparationPageAsync,
                    confirmSelectedNode: confirmStageNodes ?? returnMainNodes.HasSelectedNode, watchEntry: watchEntry),
                directSortieTemplate: ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie.png"),
                watchTemplate: ConfigService.ResolvePath("resources/templates/stage/sortie/watch.png"),
                // 观看与直接出击确认统一使用对话框执行素材，沿用当前语言及通用回退。
                executeTemplate: ConfigService.ResolvePath("resources/templates/dialog/execute.png"));
            var battle = CreateBattle(progressTemplates, true);
            battle.Validate(settings);
            var runner = new AutoProgressRunner(async (work, token) =>
                {
                    if (!await EnsureMainScreenAsync(serial, token))
                        throw new InvalidOperationException(L.Text("未确认主界面，停止作品导航。"));
                    return await stageService.EnterWorkForProgressAsync(serial, _mainLayout, _stageLayout,
                        _stagePositions, _hardStages, work, token);
                }, (work, token) => stageService.ReadCurrentWorkForProgressAsync(serial, work, token),
                (node, token) => battle.RunAsync(serial, settings, node.Kind, Report, token),
                (work, completed, token) => stageService.AdvanceAfterBattleAsync(serial, work, completed, token));
            var result = new AutoProgressResult(true, 0, 0, "");
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                AutoProgressResult current;
                if (target.HistoryWork is { } historyWork)
                {
                    var stageTemplates = progressTemplates with { SelectStageTitle = ConfigService.ResolvePath("resources/templates/stage/history_stage.png") };
                    var bossTemplates = progressTemplates with { SelectStageTitle = ConfigService.ResolvePath("resources/templates/stage/history_boss_title.png") };
                    sortieFlow.ValidateAutoProgressFlow(stageTemplates);
                    if (target.AttackHistoryBoss) sortieFlow.ValidateAutoProgressFlow(bossTemplates);
                    var historyBattle = CreateBattle(stageTemplates, false, returnHistoryNodes.HasSelectedNode);
                    historyBattle.Validate(settings);
                    current = await RunHistoryProgressAsync(serial, historyWork, settings, historyBattle,
                        confirmBoss => CreateBattle(bossTemplates, false, confirmBoss), Report, ct, target.AttackHistoryBoss);
                }
                else if (target.MainWork is { } mainWork)
                    current = await runner.RunAsync(new[] { mainWork }, Report, ct);
                else
                {
                    if (!await EnsureMainScreenAsync(serial, ct))
                        throw new InvalidOperationException(L.Text("未确认主界面，停止强敌导航。"));
                    current = await new FierceEnemyProgressService(automation, _logger, GetTextOcr())
                        .RunAsync(serial, _mainLayout, target.Enemy!,
                            token => battle.RunAsync(serial, settings, WorkStageKind.Unknown, Report, token,
                                preparationAlreadyOpened: true), Report, ct);
                }
                result = new(current.Completed, result.CompletedWorks + current.CompletedWorks,
                    result.CompletedStages + current.CompletedStages, current.Message);
                if (!current.Completed) break;
            }
            if (result.Completed) result = result with { Message = L.Format($"自动推图完成：{result.CompletedWorks} 个目标，本次完成 {result.CompletedStages} 场战斗。") };
            AutoProgressStatusText.Text = result.Completed ? result.Message
                : L.Format($"自动推图已停止：{result.Message} 已完成 {result.CompletedWorks} 个目标、{result.CompletedStages} 场战斗。");
            if (!result.Completed) _logger.Warn(AutoProgressStatusText.Text);
            endNotification.Succeeded = result.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            endNotification.Canceled = true;
            AutoProgressStatusText.Text = L.Text("自动推图已中止。");
            _logger.Info(AutoProgressStatusText.Text);
        }
        catch (Exception ex)
        { AutoProgressStatusText.Text = L.Format($"自动推图已停止：{ex.Message}"); _logger.Error(AutoProgressStatusText.Text); }
        finally
        {
            _autoProgressRunning = false;
            _autoProgressCts.Dispose();
            _autoProgressCts = null;
            AutoProgressStartButton.Content = L.Text("开始推图");
            UpdateBusyState();
            CompleteTaskEndNotification(endNotification, AutoProgressStatusText.Text);
        }
    }

    /// <summary>切换目录不改变已有混合队列；主线、历史与强敌使用独立目标键。</summary>
    private void AutoProgressTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_autoProgressUiReady || _busy) return;
        RefreshAutoProgressTargets();
    }

    /// <summary>统一候选显示模型，编号按类别隔离；目录失败时清空候选并给出原因。</summary>
    private void RefreshAutoProgressTargets()
    {
        if (AutoProgressWorkCombo is null) return;
        if (!FierceEnemyAvailable && AutoProgressTypeCombo.SelectedIndex == 2)
            AutoProgressTypeCombo.SelectedIndex = 0;
        try
        {
            var history = AutoProgressTypeCombo.SelectedIndex == 1;
            AutoProgressStartButton.IsEnabled = !_busy;
            AutoProgressOptionsPanel.IsEnabled = !_busy;
            AutoProgressAddButton.Content = L.Text("添加目标");
            AutoProgressHistoryBossCheck.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
            AutoProgressAddButton.ToolTip = history ? L.Text("添加历史作品至队列；攻略 BOSS 的勾选状态随目标保存，已有目标需移除后重新添加才能更改。") : null;
            var targets = history ? LoadHistoryCatalog().Works.AsEnumerable().Reverse().Select(work => new AutoProgressTarget(null, null, work)).ToArray()
                : AutoProgressTypeCombo.SelectedIndex == 2
                ? new[] { FierceEnemyUnit.Latest }.Concat(FierceEnemyUnit.Load())
                    .Select(unit => new AutoProgressTarget(null, unit)).ToArray()
                : _hardStages.Where(work => work.Id != 1).Select(work => new AutoProgressTarget(work, null)).ToArray();
            AutoProgressWorkCombo.ItemsSource = targets;
            AutoProgressWorkCombo.SelectedItem = history ? targets.FirstOrDefault() : targets.MaxBy(item => item.MainWork?.Id ?? item.Enemy!.Id);
            AutoProgressAddButton.IsEnabled = !_busy && targets.Length > 0;
            AutoProgressStatusText.Text = history ? L.Text("历史作品先推进关卡；勾选攻略 BOSS 时继续六关并复核双 100%。") : L.Text("添加目标后点击开始推图。");
        }
        catch (Exception ex)
        {
            AutoProgressWorkCombo.ItemsSource = null;
            AutoProgressAddButton.IsEnabled = false;
            AutoProgressStatusText.Text = L.Format($"无法加载推图目标：{ex.Message}");
        }
    }

    /// <summary>将选中的作品追加至队尾；同一作品只保留一项，重复添加时定位已有项。</summary>
    private void AutoProgressAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (AutoProgressWorkCombo.SelectedItem is not AutoProgressTarget work) return;
        if (!FierceEnemyAvailable && work.Enemy is not null) return;
        // 添加时冻结本目标的攻略范围，后续勾选变化不影响已有队列。
        if (work.HistoryWork is not null) work = work with { AttackHistoryBoss = AutoProgressHistoryBossCheck.IsChecked == true };
        var existing = _autoProgressQueue.FirstOrDefault(item => item.Key == work.Key);
        if (existing is null) _autoProgressQueue.Add(work);
        AutoProgressQueueList.SelectedItem = existing ?? work;
        AutoProgressQueueList.ScrollIntoView(existing ?? work);
        AutoProgressEmptyText.Visibility = Visibility.Collapsed;
    }

    /// <summary>允许纠正作品选择；最后一项移除后恢复空队列提示。</summary>
    private void AutoProgressRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is Button { Tag: AutoProgressTarget work }) _autoProgressQueue.Remove(work);
        AutoProgressEmptyText.Visibility = AvailableAutoProgressTargets().Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

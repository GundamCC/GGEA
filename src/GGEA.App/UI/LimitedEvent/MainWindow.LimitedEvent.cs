using System.Windows;
using System.Windows.Controls;
using GGEA.App.Localization;
using GGEA.App.Tasks;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.UI;

public partial class MainWindow
{
    private CancellationTokenSource? _limitedCts;

    private Button? _limitedButton;

    /// <summary>从非战斗画面识别活动入口，关闭窗口或点击中止均释放本次设备占用。</summary>
    private async void LimitedStart_Click(object sender, RoutedEventArgs e) =>
        await RunWindowTaskAsync(() => RunLimitedEventAsync((Button)sender));

    private async Task RunLimitedEventAsync(Button button)
    {
        if (_limitedCts is not null)
        {
            _limitedCts.Cancel();
            UpdateBusyState();
            return;
        }
        using var execution = TryBeginExecution();
        if (execution is null) return;
        var serial = await GetOnlineTaskSerialAsync(execution, LimitedStatus, _windowTasks.Token);
        if (serial is null || _automation is null || _logger is null) return;
        var difficulty = Enum.Parse<FierceEnemyDifficulty>((string)((ComboBoxItem)LimitedDifficulty.SelectedItem).Content);
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        _limitedCts = owner;
        _limitedButton = button;
        var original = button.Content;
        button.Content = L.Text("中止");
        LimitedStatus.Text = original?.ToString();
        UpdateBusyState();
        try
        {
            if (!await EnsureDeviceCompatibilityAsync(serial, owner.Token))
                throw new InvalidOperationException(L.Text("设备画面不兼容。"));
            var destination = button == LimitedStart ? EventStagesService.Destination.Boss :
                button == MedalStart ? EventStagesService.Destination.Supply : EventStagesService.Destination.Stages;
            await NavigateLimitedAsync(serial, SelectedActivity, destination, owner.Token);
            if (button == EventDaily)
            {
                LimitedStatus.Text = await RunEventStagesDailyAsync(serial, owner.Token);
            }
            else if (button == MedalStart)
            {
                var service = new MedalExchangeService(_automation, GetTextOcr(), _logger, serial, GetRecognitionEvidence());
                var completed = await Task.Run(() => service.RunAsync(owner.Token), owner.Token);
                LimitedStatus.Text = completed ? L.Text("批量交换按钮已置灰，自动兑换已结束。") : L.Text("奖章机体补给未解锁，已跳过。");
            }
            else
            {
                var service = new LimitedEventBossService(_automation, GetTextOcr(), _logger, serial);
                var completed = await Task.Run(() => service.RunAsync(difficulty, owner.Token), owner.Token);
                LimitedStatus.Text = completed ? L.Text("BOSS 挑战券不足，已关闭对话框。") : L.Text("BOSS 战斗未解锁，已跳过。");
            }
            _logger.Info(LimitedStatus.Text);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        { LimitedStatus.Text = L.Text("限时活动任务已中止。"); }
        catch (Exception ex)
        { LimitedStatus.Text = ex.Message; _logger.Error(ex.Message); }
        finally
        {
            _limitedCts = null;
            _limitedButton = null;
            button.Content = original;
            UpdateBusyState();
        }
    }

    private Task NavigateLimitedAsync(string serial, LimitedEventDefinition activity, EventStagesService.Destination destination,
        CancellationToken ct, bool allowAnnouncement = true, bool verifyActivity = false)
        => new EventStagesService(_automation!, serial, activity).NavigateAsync(destination, async token =>
        {
            if (!await EnsureMainScreenAsync(serial, token, allowAnnouncement))
                throw new InvalidOperationException(L.Text("未能确认主界面，任务中止"));
            var menu = await new StageMenuEntryService(_automation!, _logger!).OpenFromHomeAsync(serial, _mainLayout,
                _config.StagePageSettleMs, token);
            if (!menu.Success) throw new InvalidOperationException(menu.Message);
        }, ct, verifyActivity);

    private async Task<string> RunEventStagesDailyAsync(string serial, CancellationToken ct, bool claimRewards = true, LimitedEventDefinition? activity = null)
    {
        var title = new SelectStageTitleRecognizer(EventStagesService.PathFor("stage_title"));
        var settlement = CreateStageSkipService(false, title.Recognize, eventQuantityLayout: true);
        using var nodes = CreateEventNodeRecognizer();
        var titles = new OnnxStageTitleRecognizer(_adb!, _logger!, GetTextOcr(), eventSuffix: true);
        var navigation = _stageService!.ForEndAlignedTrack(nodes, titles);
        return await new EventStagesService(_automation!, serial, activity ?? SelectedActivity).DailyAsync(settlement, _stageLayout, navigation, ct, claimRewards);
    }

    private StageNodeRecognizer CreateEventNodeRecognizer() =>
        new(_adb!, _matcher, _logger!, ConfigService.ResolvePath(_config.StageNodeTemplateDirectory),
            numberRecognizer: new OnnxStageNumberRecognizer(GetTextOcr()), evidence: GetRecognitionEvidence(), layout: StageNodeLayout.History);

    private void QueueEventProgress_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || LimitedActivity.SelectedItem is not LimitedEventDefinition activity) return;
        AutoProgressTab.IsSelected = true;
        AutoProgressView.AutoProgressTypeCombo.SelectedItem = AutoProgressView.AutoProgressEventTypeItem;
        var target = AutoProgressView.AutoProgressWorkCombo.Items.OfType<AutoProgressTarget>()
            .FirstOrDefault(item => item.Activity?.Id == activity.Id);
        if (target is null) return;
        AutoProgressView.AutoProgressWorkCombo.SelectedItem = target;
        AddAutoProgressTarget(target);
    }

    private async Task<AutoProgressResult> RunEventProgressAsync(string serial, LimitedEventDefinition activity,
        AutoProgressSettings settings, Action<string> report, CancellationToken ct)
    {
        await NavigateLimitedAsync(serial, activity, EventStagesService.Destination.Stages, ct, verifyActivity: _limitedActivities.Length > 1);
        var stages = new EventStagesService(_automation!, serial, activity);
        if (!await stages.EnterAsync(true, ct)) return new(true, 1, 0, L.Text("事件关卡攻略度已达 100%。"));
        var work = activity.CreateWork();
        using var nodes = CreateEventNodeRecognizer();
        var reader = _stageService!.CreateProgressReader(nodes);
        var path = EventStagesService.PathFor("stage_title");
        var title = new SelectStageTitleRecognizer(path);
        var battle = CreateProgressBattle(serial, AutoProgressFlowTemplates.FromDefaults() with { SelectStageTitle = path },
            false, title.Recognize);
        battle.Validate(settings);
        var formation = new AutoFormationSession();
        async Task<StageNodeRecognition> Read(HardStageEntry target, CancellationToken token)
        {
            await stages.EnterAsync(false, token);
            return await reader.ReadCurrentWorkForProgressAsync(serial, target, token);
        }
        var runner = new AutoProgressRunner(Read, Read,
            (node, token) => battle.RunAsync(serial, settings, node.Kind, report, token,
                initialFormation: node is { Kind: WorkStageKind.Normal, Number: 1 } ? formation : null),
            (target, node, token) => reader.AdvanceAfterBattleAsync(serial, target, node, token),
            async (_, token) => !await stages.EnterAsync(true, token));
        return await runner.RunAsync([work], report, ct);
    }
}

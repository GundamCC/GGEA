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

    /// <summary>手动进入事件后才接管设备，关闭窗口或点击中止均释放本次设备占用。</summary>
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
            if (button == EventProgress)
                LimitedStatus.Text = await RunEventProgressAsync(serial, owner.Token);
            else if (button == EventDaily)
            {
                LimitedStatus.Text = await RunEventStagesDailyAsync(serial, owner.Token)
                    ? L.Text("事件每日关卡及使命领取完成。") : L.Text("AP 不足，当前略过任务结束");
            }
            else if (button == MedalStart)
            {
                var service = new MedalExchangeService(_automation, GetTextOcr(), _logger, serial);
                await Task.Run(() => service.RunAsync(owner.Token), owner.Token);
                LimitedStatus.Text = L.Text("批量交换按钮已置灰，自动兑换已结束。");
            }
            else
            {
                var service = new LimitedEventBossService(_automation, GetTextOcr(), _logger, serial);
                await Task.Run(() => service.RunAsync(difficulty, owner.Token), owner.Token);
                LimitedStatus.Text = L.Text("BOSS 挑战券不足，已关闭对话框。");
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

    private async Task<bool> RunEventStagesDailyAsync(string serial, CancellationToken ct, bool claimRewards = true)
    {
        var title = new SelectStageTitleRecognizer(EventStagesService.PathFor("stage_title"));
        var settlement = CreateStageSkipService(false, title.Recognize, eventQuantityLayout: true);
        using var nodes = CreateEventNodeRecognizer();
        var titles = new OnnxStageTitleRecognizer(_adb!, _logger!, GetTextOcr(), eventSuffix: true);
        var navigation = _stageService!.ForEndAlignedTrack(nodes, titles);
        return await new EventStagesService(_automation!, serial).DailyAsync(settlement, _stageLayout, navigation, ct, claimRewards);
    }

    private StageNodeRecognizer CreateEventNodeRecognizer() =>
        new(_adb!, _matcher, _logger!, ConfigService.ResolvePath(_config.StageNodeTemplateDirectory),
            numberRecognizer: new OnnxStageNumberRecognizer(GetTextOcr()), evidence: GetRecognitionEvidence(), layout: StageNodeLayout.History);

    private async Task<string> RunEventProgressAsync(string serial, CancellationToken ct)
    {
        var stages = new EventStagesService(_automation!, serial);
        if (!await stages.EnterAsync(true, ct)) return L.Text("事件关卡攻略度已达 100%。");
        var work = EventStagesService.CreateWork();
        using var nodes = CreateEventNodeRecognizer();
        var reader = _stageService!.CreateProgressReader(nodes);
        var path = EventStagesService.PathFor("stage_title");
        var title = new SelectStageTitleRecognizer(path);
        var battle = CreateProgressBattle(serial, AutoProgressFlowTemplates.FromDefaults() with { SelectStageTitle = path },
            false, title.Recognize);
        var settings = _config.AutoProgress.Snapshot();
        battle.Validate(settings);
        var formation = new AutoFormationSession();
        async Task<StageNodeRecognition> Read(HardStageEntry target, CancellationToken token)
        {
            await stages.EnterAsync(false, token);
            return await reader.ReadCurrentWorkForProgressAsync(serial, target, token);
        }
        void Report(string message) { LimitedStatus.Text = message; _logger!.Info(message); }
        var runner = new AutoProgressRunner(Read, Read,
            (node, token) => battle.RunAsync(serial, settings, node.Kind, Report, token,
                initialFormation: node is { Kind: WorkStageKind.Normal, Number: 1 } ? formation : null),
            (target, node, token) => reader.AdvanceAfterBattleAsync(serial, target, node, token),
            async (_, token) => !await stages.EnterAsync(true, token));
        var result = await runner.RunAsync([work], Report, ct);
        if (!result.Completed) throw new InvalidOperationException(result.Message);
        return result.Message;
    }
}

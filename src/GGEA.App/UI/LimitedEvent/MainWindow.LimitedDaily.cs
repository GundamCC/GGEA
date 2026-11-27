using System.Windows;
using System.Windows.Controls;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

public partial class MainWindow
{
    private LimitedEventDefinition[] _limitedActivities = [];
    private LimitedEventDefinition SelectedActivity => LimitedActivity.SelectedItem as LimitedEventDefinition
        ?? throw new InvalidOperationException(L.Text("当前没有可用的限时活动。"));

    private void ShowLimitedDaily(LimitedEventDailySettings choice, bool selectActivity = true)
    {
        if (selectActivity)
            LimitedActivity.SelectedItem = choice.ActivityId is int id ? _limitedActivities.FirstOrDefault(e => e.Id == id) : _limitedActivities.FirstOrDefault();
        IncludeStages.IsChecked = choice.Stages;
        IncludeMissionRewards.IsChecked = choice.ClaimRewards;
        IncludeBoss.IsChecked = choice.Boss;
        IncludeMedals.IsChecked = choice.Medals;
        LimitedDifficulty.SelectedItem = LimitedDifficulty.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Content, choice.Difficulty.ToString()));
    }

    private void RestoreLimitedDaily()
    {
        try
        {
            _limitedActivities = LimitedEventDefinition.Load();
            LimitedActivity.ItemsSource = _limitedActivities;
            LimitedActivity.SelectedItem = _limitedActivities.FirstOrDefault();
            if (_limitedActivities.Length == 0) LimitedStatus.Text = L.Text("当前没有可用的限时活动。");
        }
        catch (Exception ex) { LimitedStatus.Text = ex.Message; _logger?.Warn(ex.Message); }
        if (_config.LimitedEventDaily is not { } saved) return;
        try
        {
            var item = saved.CreateItem();
            item.PropertyChanged += DailyTask_StateChanged;
            _dailyTasks.Insert(Math.Clamp(saved.Index, 0, _dailyTasks.Count), item);
            ShowLimitedDaily(saved, selectActivity: false);
            UpdateDailySelectAllState();
        }
        catch (InvalidOperationException ex) { LimitedStatus.Text = ex.Message; }
    }

    private void AddLimitedDaily_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;
        if (LimitedActivity.SelectedItem is not LimitedEventDefinition activity) { LimitedStatus.Text = L.Text("当前没有可用的限时活动。"); return; }
        var existing = _dailyTasks.FirstOrDefault(task => task.Kind == DailyTaskKind.LimitedEvent);
        var choice = new LimitedEventDailySettings(IncludeStages.IsChecked == true, IncludeBoss.IsChecked == true,
            IncludeMedals.IsChecked == true, Enum.Parse<FierceEnemyDifficulty>((string)((ComboBoxItem)LimitedDifficulty.SelectedItem).Content),
            existing?.IsChecked ?? true, MissionRewards: IncludeMissionRewards.IsChecked == true, ActivityId: activity.Id);
        if (!choice.Stages && !choice.Boss && !choice.Medals && !choice.ClaimRewards) { LimitedStatus.Text = L.Text("请选择有效的限时活动子任务。"); return; }
        var item = choice.CreateItem();
        item.PropertyChanged += DailyTask_StateChanged;
        if (existing is null)
        {
            var ap = _dailyTasks.FirstOrDefault(t => t.Kind == DailyTaskKind.ConsumeAp);
            _dailyTasks.Insert(ap is null ? _dailyTasks.Count : _dailyTasks.IndexOf(ap), item);
        }
        else
        {
            existing.PropertyChanged -= DailyTask_StateChanged;
            _dailyTasks[_dailyTasks.IndexOf(existing)] = item;
        }
        LimitedStatus.Text = $"{item.Name}：{choice.Describe()}";
        SaveDynamicDailyTasks();
        UpdateDailySelectAllState();
        UpdateBusyState();
    }

    private async Task RunLimitedDailyTaskAsync(string serial, LimitedEventDailySettings choice, CancellationToken ct, bool allowAnnouncement)
    {
        var activity = choice.ActivityId is int id ? _limitedActivities.FirstOrDefault(e => e.Id == id) : _limitedActivities.FirstOrDefault();
        if (activity is null) throw new InvalidOperationException(L.Text("所选限时活动已不在配置中，请重新添加每日任务。"));
        var stages = new EventStagesService(_automation!, serial, activity);
        var destination = choice.Stages ? EventStagesService.Destination.Stages : choice.ClaimRewards ? EventStagesService.Destination.Story :
            choice.Boss ? EventStagesService.Destination.Boss : EventStagesService.Destination.Supply;
        await NavigateLimitedAsync(serial, activity, destination, ct, allowAnnouncement);
        if (choice.Stages)
        {
            AccountStatusText.Text = L.Text("每日10次关卡");
            LimitedStatus.Text = await RunEventStagesDailyAsync(serial, ct, choice.ClaimRewards, activity);
        }
        else if (choice.ClaimRewards)
        {
            AccountStatusText.Text = L.Text("领取限时活动使命奖励");
            await stages.ClaimFromStoryAsync(ct);
        }
        if (choice.Boss)
        {
            AccountStatusText.Text = L.Text("BOSS 战斗");
            var completed = await new LimitedEventBossService(_automation!, GetTextOcr(), _logger!, serial).RunAsync(choice.Difficulty, ct);
            if (!completed) _logger!.Info(LimitedStatus.Text = L.Text("BOSS 战斗未解锁，已跳过。"));
            else if (choice.Medals) await stages.ReturnFromBossAsync(ct);
        }
        if (choice.Medals)
        {
            AccountStatusText.Text = L.Text("奖章机体补给");
            var completed = await new MedalExchangeService(_automation!, GetTextOcr(), _logger!, serial, GetRecognitionEvidence()).RunAsync(ct);
            if (!completed) _logger!.Info(LimitedStatus.Text = L.Text("奖章机体补给未解锁，已跳过。"));
        }
    }
}

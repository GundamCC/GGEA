using System.Windows;
using System.Windows.Controls;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

public partial class MainWindow
{
    private void ShowLimitedDaily(LimitedEventDailySettings choice)
    {
        IncludeStages.IsChecked = choice.Stages;
        IncludeBoss.IsChecked = choice.Boss;
        IncludeMedals.IsChecked = choice.Medals;
        LimitedDifficulty.SelectedItem = LimitedDifficulty.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Content, choice.Difficulty.ToString()));
    }

    private void RestoreLimitedDaily()
    {
        if (_config.LimitedEventDaily is not { } saved) return;
        try
        {
            var item = saved.CreateItem();
            item.PropertyChanged += DailyTask_StateChanged;
            _dailyTasks.Insert(Math.Clamp(saved.Index, 0, _dailyTasks.Count), item);
            ShowLimitedDaily(saved);
            UpdateDailySelectAllState();
        }
        catch (InvalidOperationException ex) { LimitedStatus.Text = ex.Message; }
    }

    private void AddLimitedDaily_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _windowTasks.IsStopping) return;
        var existing = _dailyTasks.FirstOrDefault(task => task.Kind == DailyTaskKind.LimitedEvent);
        var choice = new LimitedEventDailySettings(IncludeStages.IsChecked == true, IncludeBoss.IsChecked == true,
            IncludeMedals.IsChecked == true, Enum.Parse<FierceEnemyDifficulty>((string)((ComboBoxItem)LimitedDifficulty.SelectedItem).Content),
            existing?.IsChecked ?? true);
        if (!choice.Stages && !choice.Boss && !choice.Medals) { LimitedStatus.Text = L.Text("请选择有效的限时活动子任务。"); return; }
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

    private async Task RunLimitedDailyTaskAsync(string serial, LimitedEventDailySettings choice, CancellationToken ct)
    {
        var stages = new EventStagesService(_automation!, serial);
        var menu = await new StageMenuEntryService(_automation!, _logger!).OpenFromHomeAsync(serial, _mainLayout,
            _config.StagePageSettleMs, ct);
        if (!menu.Success) throw new InvalidOperationException(menu.Message);
        await stages.OpenFromMenuAsync(ct);
        if (choice.Stages)
        {
            AccountStatusText.Text = L.Text("每日10次关卡");
            await RunEventStagesDailyAsync(serial, ct);
        }
        if (choice.Boss)
        {
            AccountStatusText.Text = L.Text("BOSS 战斗");
            await new LimitedEventBossService(_automation!, GetTextOcr(), _logger!, serial).RunAsync(choice.Difficulty, ct);
            if (choice.Medals) await stages.ReturnFromBossAsync(ct);
        }
        if (choice.Medals)
        {
            AccountStatusText.Text = L.Text("奖章机体补给");
            await new MedalExchangeService(_automation!, GetTextOcr(), _logger!, serial).RunAsync(ct);
        }
    }
}

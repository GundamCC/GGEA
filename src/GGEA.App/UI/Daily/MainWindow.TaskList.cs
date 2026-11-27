using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>每日任务的列表状态、全选、排序与设置页跳转。</summary>
public partial class MainWindow
{
    private readonly ObservableCollection<DailyTaskItem> _dailyTasks = new();
    private void ConnectDailyView()
    {
        DailyView.SettingsRequested += OpenDailyTaskSettings;
        DailyView.RemoveRequested += RemoveFierceEnemyTask;
        DailyView.DailySelectAllCheckBox.Click += DailySelectAllCheckBox_Click;
        DailyView.DailyStartButton.Click += DailyStartButton_Click;
        DailyView.Bind(_dailyTasks, message => _logger?.Info(message), () =>
        {
            if (_dailyTasks.Any(task => task.CanRemove)) SaveDynamicDailyTasks();
        });
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
            Kind = DailyTaskKind.CharacterRequests,
            Name = L.Text("承接角色要求（个人基地）"),
            IsChecked = false,
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

        var selectAll = DailyView.DailySelectAllCheckBox.IsChecked == true;
        foreach (var task in _dailyTasks.Where(task => task.IsSupported && IsDailyTaskAvailable(task)))
        {
            task.IsChecked = selectAll;
        }
    }

    /// <summary>任一任务的勾选或支持状态变化时，同步更新“全选”勾选框。</summary>
    private void DailyTask_StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DailyTaskItem.IsChecked) &&
            sender is DailyTaskItem { CanRemove: true }) SaveDynamicDailyTasks();
        if (e.PropertyName is nameof(DailyTaskItem.IsChecked) or nameof(DailyTaskItem.IsSupported))
        {
            UpdateDailySelectAllState();
        }
    }

    /// <summary>仅当所有受支持任务都已勾选时，“全选”显示为勾选状态。</summary>
    private void UpdateDailySelectAllState()
    {
        var supportedTasks = _dailyTasks.Where(task => task.IsSupported && IsDailyTaskAvailable(task)).ToList();
        DailyView.DailySelectAllCheckBox.IsChecked = supportedTasks.Count > 0 && supportedTasks.All(task => task.IsChecked);
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

            case DailyTaskKind.LimitedEvent:
                if (task.LimitedEventSelection is { } choice) ShowLimitedDaily(choice);
                LimitedEventTab.IsSelected = true;
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
}

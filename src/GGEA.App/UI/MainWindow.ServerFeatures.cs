using GGEA.App.Localization;
using System.Windows;
using System.Windows.Data;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>按区服控制尚未开放的功能；只过滤展示与执行，不删除用户已保存的队列。</summary>
public partial class MainWindow
{
    // 日服当前尚未开放强敌来袭；区服而非素材语言决定功能可用性。
    private bool FierceEnemyAvailable => _config.GameServer != "japan";

    /// <summary>每日显示、全选和执行共用同一准入规则，隐藏任务仍保留原来的勾选与顺序。</summary>
    private bool IsDailyTaskAvailable(DailyTaskItem task)
        => FierceEnemyAvailable || task.Kind != DailyTaskKind.FierceEnemySkip;

    /// <summary>冻结执行时只取当前区服可用的目标，防止隐藏的旧强敌目标继续导航。</summary>
    private AutoProgressTarget[] AvailableAutoProgressTargets()
        => _autoProgressQueue.Where(target => FierceEnemyAvailable || target.Enemy is null).ToArray();

    /// <summary>启动及保存区服设置后刷新；被隐藏的当前页和当前目录回到可用入口。</summary>
    private void ApplyServerFeatureVisibility()
    {
        var available = FierceEnemyAvailable;
        FierceEnemyAssaultTab.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        AutoProgressFierceEnemyTypeItem.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        AutoProgressFierceEnemyTypeItem.IsEnabled = available;
        if (!available && MainTabControl.SelectedItem == FierceEnemyAssaultTab)
            MainTabControl.SelectedItem = AutoProgressTab;
        if (!available && AutoProgressTypeCombo.SelectedIndex == 2)
        {
            AutoProgressTypeCombo.SelectedIndex = 0;
            RefreshAutoProgressTargets();
        }
        var dailyView = CollectionViewSource.GetDefaultView(_dailyTasks);
        dailyView.Filter = item => item is DailyTaskItem task && IsDailyTaskAvailable(task);
        dailyView.Refresh();
        var queueView = CollectionViewSource.GetDefaultView(_autoProgressQueue);
        queueView.Filter = item => item is AutoProgressTarget target && (available || target.Enemy is null);
        queueView.Refresh();
        AutoProgressEmptyText.Visibility = AvailableAutoProgressTargets().Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
            AutoProgressStatusText.Text = L.Text("主线推进至最终 HARD；历史复核双 100%。出击会消耗 AP。");
        UpdateDailySelectAllState();
    }
}

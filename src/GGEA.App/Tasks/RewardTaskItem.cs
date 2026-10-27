using System.ComponentModel;

namespace GGEA.App.Tasks;

/// <summary>奖励页中的奖励类型。</summary>
public enum RewardTaskKind
{
    /// <summary>领取邮件奖励（对应现有礼物箱流程）。</summary>
    MailReward,

    /// <summary>领取每日使命奖励。</summary>
    MissionReward,
}

/// <summary>奖励页中的单行可勾选奖励；列表顺序即领取顺序。</summary>
public sealed class RewardTaskItem : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _isHelpOpen;

    /// <summary>奖励类型，决定执行哪段领取流程。</summary>
    public RewardTaskKind Kind { get; init; }

    /// <summary>界面显示名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>是否显示问号帮助标记；使命奖励需要解释“最后执行”规则。</summary>
    public bool HasHelp => Kind == RewardTaskKind.MissionReward;

    /// <summary>是否勾选领取。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    /// <summary>问号提示框是否显示；鼠标悬停时立即打开，离开时关闭。</summary>
    public bool IsHelpOpen
    {
        get => _isHelpOpen;
        set
        {
            if (_isHelpOpen == value)
            {
                return;
            }

            _isHelpOpen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHelpOpen)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>奖励执行计划生成器：过滤未勾选奖励并严格保持列表顺序。</summary>
public static class RewardTaskPlanner
{
    /// <summary>生成独立快照，避免执行期间用户修改勾选状态影响当前任务。</summary>
    public static IReadOnlyList<RewardTaskItem> BuildExecutionPlan(
        IEnumerable<RewardTaskItem> orderedItems)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        return orderedItems.Where(item => item.IsChecked).ToList();
    }
}

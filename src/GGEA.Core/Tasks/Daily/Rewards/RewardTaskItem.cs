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
        set => TaskItemProperty.Set(this, PropertyChanged, ref _isChecked, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

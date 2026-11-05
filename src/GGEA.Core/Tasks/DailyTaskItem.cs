using GGEA.App.Localization;
using System.ComponentModel;

namespace GGEA.App.Tasks;

/// <summary>每日任务类型，对应各自独立的自动化流程。</summary>
public enum DailyTaskKind
{
    /// <summary>回收战舰（个人基地）。</summary>
    HarvestBattleship,

    /// <summary>商店购买免费礼包。</summary>
    ShopFreeGift,

    /// <summary>领取奖励（邮件奖励 / 使命奖励）。</summary>
    ClaimRewards,

    /// <summary>开发与强化一次。</summary>
    DevelopOnce,

    /// <summary>强化培育关卡（UPGRADE STAGES 四种类型）。</summary>
    UpgradeStages,

    /// <summary>领取每日使命奖励（由计划生成器追加到队列最后，不显示在列表中）。</summary>
    ClaimMissionReward,

    /// <summary>消耗 AP（按右侧 AP 作品列表顺序执行）。</summary>
    ConsumeAp,

    /// <summary>按添加时的单位与难度执行强敌来袭略过。</summary>
    FierceEnemySkip,
}

/// <summary>每日任务列表中的一行；列表顺序即执行顺序。</summary>
public sealed class DailyTaskItem : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _isSupported = true;
    private string? _unsupportedReason;

    /// <summary>任务类型，决定点击“开始”后执行哪段流程。</summary>
    public DailyTaskKind Kind { get; init; }

    /// <summary>列表中显示的任务名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>强敌任务的不可变选择；其他任务为空。</summary>
    public FierceEnemySkipSelection? FierceEnemySelection { get; init; }

    /// <summary>任务选择说明与兼容性原因共用悬浮提示。</summary>
    public string? Description => UnsupportedReason ?? (FierceEnemySelection is { } choice ? $"{choice.Unit.Name} / {choice.Difficulty}" : null);

    /// <summary>带兼容状态的界面名称；不支持时明确标记，避免用户误以为任务会执行。</summary>
    public string DisplayName => IsSupported ? Name : L.Format($"{Name}（当前模拟器不支持）");

    /// <summary>只有用户添加的强敌任务提供删除入口。</summary>
    public bool CanRemove => Kind == DailyTaskKind.FierceEnemySkip;

    /// <summary>是否显示齿轮设置入口；只有带独立设置页的任务显示。</summary>
    public bool HasSettings => Kind is DailyTaskKind.ClaimRewards or DailyTaskKind.ConsumeAp;

    /// <summary>任务行可跳转到对应页面；强敌行保留删除图标，不额外显示齿轮。</summary>
    public bool CanOpenSettings => HasSettings || Kind == DailyTaskKind.FierceEnemySkip;

    /// <summary>是否勾选执行；拖拽排序时只移动整行，不影响勾选状态。</summary>
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

    /// <summary>当前模拟器是否具备执行此任务所需的全部能力。</summary>
    public bool IsSupported
    {
        get => _isSupported;
        set
        {
            if (_isSupported == value)
            {
                return;
            }

            _isSupported = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSupported)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    /// <summary>任务不可用时显示在提示中的具体原因。</summary>
    public string? UnsupportedReason
    {
        get => _unsupportedReason;
        set
        {
            if (_unsupportedReason == value)
            {
                return;
            }

            _unsupportedReason = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnsupportedReason)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>每日任务执行计划生成器：过滤未勾选任务并严格保留拖拽后的顺序。</summary>
public static class DailyTaskPlanner
{
    /// <summary>生成独立快照，避免执行期间用户拖拽或勾选影响当前任务列表。</summary>
    public static IReadOnlyList<DailyTaskItem> BuildExecutionPlan(
        IEnumerable<DailyTaskItem> orderedItems)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        return orderedItems.Where(item => item.IsChecked && item.IsSupported).ToList();
    }

    /// <summary>把“领取奖励”任务移动到执行计划末尾，使命奖励需要最后领取。</summary>
    public static IReadOnlyList<DailyTaskItem> MoveClaimRewardsLast(
        IEnumerable<DailyTaskItem> orderedItems)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        var list = orderedItems.ToList();
        var claim = list.FirstOrDefault(item => item.Kind == DailyTaskKind.ClaimRewards);
        if (claim is not null && list.Remove(claim))
        {
            list.Add(claim);
        }

        return list;
    }

    /// <summary>把“领取每日使命奖励”追加到执行计划末尾，不移动“领取奖励”本身。</summary>
    public static IReadOnlyList<DailyTaskItem> AppendMissionRewardLast(
        IEnumerable<DailyTaskItem> orderedItems)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        var list = orderedItems.ToList();
        list.Add(new DailyTaskItem
        {
            Kind = DailyTaskKind.ClaimMissionReward,
            Name = L.Text("领取每日使命奖励"),
            IsChecked = true,
        });
        return list;
    }

    /// <summary>
    /// 构建最终每日任务执行计划。
    /// 只有同时满足“勾选了领取每日使命奖励”和“领取奖励任务被勾选”时，
    /// 才在队列末尾追加一次“领取每日使命奖励”（总计执行两次）；
    /// 未勾选“领取奖励”时不做任何自动执行，避免未勾选的任务自动运行。
    /// </summary>
    public static IReadOnlyList<DailyTaskItem> BuildDailyPlan(
        IEnumerable<DailyTaskItem> orderedItems,
        bool missionRewardChecked)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        var plan = BuildExecutionPlan(orderedItems);
        var hasRewardTask = plan.Any(item => item.Kind == DailyTaskKind.ClaimRewards);
        return missionRewardChecked && hasRewardTask
            ? AppendMissionRewardLast(plan)
            : plan;
    }
}

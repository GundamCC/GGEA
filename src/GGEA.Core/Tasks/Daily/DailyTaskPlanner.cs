using GGEA.App.Localization;

namespace GGEA.App.Tasks;

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

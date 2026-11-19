namespace GGEA.App.Tasks;

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

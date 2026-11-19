using GGEA.App.Config;

namespace GGEA.App.Tasks;

/// <summary>AP 消耗执行计划生成器，严格保留用户拖拽后的作品顺序。</summary>
public static class ApConsumptionPlanner
{
    /// <summary>过滤未勾选作品并生成独立列表，避免执行期间 UI 排序变化影响当前任务。</summary>
    public static IReadOnlyList<ApConsumptionWorkItem> BuildExecutionPlan(
        IEnumerable<ApConsumptionWorkItem> orderedItems)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        return orderedItems.Where(item => item.HasSelectedStage).ToList();
    }

    /// <summary>把当前完整作品顺序与 Hard 勾选状态转换为可持久化方案。</summary>
    public static ApConsumptionProfile CreateProfile(
        string name,
        IEnumerable<ApConsumptionWorkItem> orderedItems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(orderedItems);
        return new ApConsumptionProfile
        {
            Name = name.Trim(),
            Works = orderedItems.Select(item => new ApConsumptionProfileWork
            {
                WorkId = item.WorkId,
                HardStages = item.SelectedHardStages.ToList(),
            }).ToList(),
        };
    }

    /// <summary>
    /// 将方案应用到当前作品集合。方案内作品按保存顺序排列，游戏更新后新增作品追加在末尾。
    /// </summary>
    public static void ApplyProfile(
        ApConsumptionProfile profile,
        IList<ApConsumptionWorkItem> currentItems)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(currentItems);

        var byId = currentItems.ToDictionary(item => item.WorkId);
        foreach (var item in currentItems)
        {
            item.Hard1 = false;
            item.Hard2 = false;
            item.Hard3 = false;
            item.Hard4 = false;
            item.Hard5 = false;
        }

        var reordered = new List<ApConsumptionWorkItem>(currentItems.Count);
        var addedIds = new HashSet<int>();
        foreach (var saved in profile.Works)
        {
            if (!byId.TryGetValue(saved.WorkId, out var item) || !addedIds.Add(saved.WorkId))
            {
                continue;
            }

            var selected = saved.HardStages.ToHashSet();
            item.Hard1 = item.IsHard1Enabled && selected.Contains(1);
            item.Hard2 = item.IsHard2Enabled && selected.Contains(2);
            item.Hard3 = item.IsHard3Enabled && selected.Contains(3);
            item.Hard4 = item.IsHard4Enabled && selected.Contains(4);
            item.Hard5 = item.IsHard5Enabled && selected.Contains(5);
            reordered.Add(item);
        }

        reordered.AddRange(currentItems.Where(item => addedIds.Add(item.WorkId)));
        currentItems.Clear();
        foreach (var item in reordered)
        {
            currentItems.Add(item);
        }
    }
}

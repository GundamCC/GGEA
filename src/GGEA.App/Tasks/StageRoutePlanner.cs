using GGEA.App.Config;

namespace GGEA.App.Tasks;

/// <summary>作品关卡难度。</summary>
public enum StageDifficulty
{
    Normal,
    Hard,
}

/// <summary>单个特定关卡的略过任务。</summary>
public sealed record StageSkipTask(
    int WorkId,
    StageMapMode Mode,
    StageDifficulty Difficulty,
    int StageNumber,
    int RepeatCount);

/// <summary>一次进入作品后需要连续完成的略过任务集合。</summary>
public sealed record StageWorkVisit(
    int WorkId,
    IReadOnlyList<StageSkipTask> Tasks);

/// <summary>多作品访问路线及其预计横向移动总距离。</summary>
public sealed record StageRoutePlan(
    IReadOnlyList<StageWorkVisit> Visits,
    double TotalDistance);

/// <summary>
/// 多作品路线规划器。主要关卡画布只横向移动，因此最优路线只需比较
/// “先访问最左端”与“先访问最右端”两种顺序，不引入复杂寻路依赖。
/// </summary>
public static class StageRoutePlanner
{
    /// <summary>
    /// 在允许调整作品顺序时合并同一作品任务，并选择总横向距离较短的访问方向。
    /// 当前一次只规划同一种画布模式，挑战模式启用后可分别规划再计算切换成本。
    /// </summary>
    public static StageRoutePlan PlanOptimized(
        IReadOnlyList<StageSkipTask> tasks,
        StageWorkPositionsData positions,
        double currentWorldX)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(positions);
        if (tasks.Count == 0)
        {
            return new StageRoutePlan(Array.Empty<StageWorkVisit>(), 0);
        }

        var mode = tasks[0].Mode;
        if (tasks.Any(task => task.Mode != mode))
        {
            throw new InvalidOperationException("单次路线规划暂不允许混合普通模式与挑战模式");
        }

        ValidateTasks(tasks);
        var groups = tasks
            .GroupBy(task => task.WorkId)
            .Select(group => new
            {
                WorkId = group.Key,
                Position = positions.Find(group.Key)
                    ?? throw new InvalidOperationException($"作品缺少地图位置: id={group.Key}"),
                Tasks = (IReadOnlyList<StageSkipTask>)group.ToList(),
            })
            .OrderBy(group => group.Position.CanvasX)
            .ToList();

        var left = groups[0].Position.CanvasX;
        var right = groups[^1].Position.CanvasX;
        var span = right - left;
        var leftFirstDistance = Math.Abs(currentWorldX - left) + span;
        var rightFirstDistance = Math.Abs(currentWorldX - right) + span;
        var ordered = leftFirstDistance <= rightFirstDistance
            ? groups
            : groups.AsEnumerable().Reverse().ToList();
        var totalDistance = Math.Min(leftFirstDistance, rightFirstDistance);

        return new StageRoutePlan(
            ordered.Select(group => new StageWorkVisit(group.WorkId, group.Tasks)).ToList(),
            totalDistance);
    }

    /// <summary>
    /// 严格保持输入顺序，只合并相邻且属于同一作品的任务。
    /// 适用于任务顺序不可调整的场景。
    /// </summary>
    public static StageRoutePlan PlanStrict(
        IReadOnlyList<StageSkipTask> tasks,
        StageWorkPositionsData positions,
        double currentWorldX)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(positions);
        ValidateTasks(tasks);

        var visits = new List<StageWorkVisit>();
        foreach (var task in tasks)
        {
            if (positions.Find(task.WorkId) is null)
            {
                throw new InvalidOperationException($"作品缺少地图位置: id={task.WorkId}");
            }

            if (visits.Count > 0 && visits[^1].WorkId == task.WorkId)
            {
                visits[^1] = visits[^1] with { Tasks = visits[^1].Tasks.Append(task).ToList() };
            }
            else
            {
                visits.Add(new StageWorkVisit(task.WorkId, new[] { task }));
            }
        }

        double distance = 0;
        var current = currentWorldX;
        foreach (var visit in visits)
        {
            var next = positions.Find(visit.WorkId)!.CanvasX;
            distance += Math.Abs(next - current);
            current = next;
        }

        return new StageRoutePlan(visits, distance);
    }

    /// <summary>验证关卡编号与重复次数，防止生成无法执行的计划。</summary>
    private static void ValidateTasks(IReadOnlyList<StageSkipTask> tasks)
    {
        foreach (var task in tasks)
        {
            if (task.WorkId <= 0 || task.StageNumber <= 0 || task.RepeatCount <= 0)
            {
                throw new ArgumentException($"略过任务参数无效: work={task.WorkId}");
            }
        }
    }
}

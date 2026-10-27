using GGEA.App.Localization;
namespace GGEA.App.Tasks;

/// <summary>统计一次队列中已返回结果的任务；未执行任务不计入失败。</summary>
public sealed class TaskQueueSummary(int plannedCount)
{
    // 只保存本轮失败项的名称快照，顺序与实际执行一致，不引用可变的界面任务对象。
    private readonly List<string> _failedTasks = new();

    /// <summary>已成功的队列项数；安全略过沿用执行服务的成功判定。</summary>
    public int SuccessCount { get; private set; }

    /// <summary>失败的队列项数，组合任务的多个失败子项仍只计一次。</summary>
    public int FailureCount => _failedTasks.Count;

    /// <summary>每个队列项记录一次；组合任务可补充失败子项名称，成功子项不应传入。</summary>
    public void Record(string taskName, bool success, string? failedSubtasks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        if (success) SuccessCount++;
        else _failedTasks.Add(string.IsNullOrWhiteSpace(failedSubtasks)
            ? taskName
            : $"{taskName}（{failedSubtasks}）");
    }

    /// <summary>空队列或提前退出不生成完成通知；分钟使用累计值，避免超过一小时后归零。</summary>
    public string? GetCompletionMessage(TimeSpan elapsed)
    {
        if (plannedCount <= 0 || SuccessCount + FailureCount != plannedCount) return null;
        var duration = L.Format($"总计耗时{(long)elapsed.TotalMinutes}分{elapsed.Seconds}秒");
        if (FailureCount == 0) return L.Format($"全部任务执行成功，共{SuccessCount}项，{duration}");

        var failures = _failedTasks.Select((name, index) => $"{index + 1}. {name}");
        return L.Format($"{SuccessCount}项任务成功，{FailureCount}项任务失败，{duration}")
            + L.Format($"\n失败任务：\n{string.Join("\n", failures)}");
    }
}

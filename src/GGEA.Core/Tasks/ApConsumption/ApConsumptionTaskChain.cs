using GGEA.App.Localization;

namespace GGEA.App.Tasks;

/// <summary>
/// 单次“任务开始”创建的 AP 消耗运行链。游标记录本次每日任务内已经处理的作品，
/// 新一次任务必须创建新链，禁止跨两次“任务开始”复用进度。
/// </summary>
public sealed class ApConsumptionTaskChain
{
    private readonly IReadOnlyList<ApConsumptionWorkItem> _items;
    private readonly List<string> _failedWorks = new();
    private readonly List<string> _lockedWorks = new();

    public ApConsumptionTaskChain(string deviceSerial, IReadOnlyList<ApConsumptionWorkItem> plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);
        ArgumentNullException.ThrowIfNull(plan);

        DeviceSerial = deviceSerial;
        // 复制关卡勾选状态，避免运行期间 UI 修改配置后悄悄改变当前任务链。
        _items = plan.Select(CloneItem).ToList();
    }

    /// <summary>任务链所属设备；切换账号设备时不能复用旧进度。</summary>
    public string DeviceSerial { get; }

    /// <summary>计划中的作品总数，用于保留原始 n/总数 日志序号。</summary>
    public int Count => _items.Count;

    /// <summary>当前待执行作品的零基游标；已完成时等于作品总数。</summary>
    public int CurrentIndex { get; private set; }

    /// <summary>当前待执行作品；全部完成后为 null。</summary>
    public ApConsumptionWorkItem? CurrentItem => IsCompleted ? null : _items[CurrentIndex];

    /// <summary>是否已经处理完任务链中的全部作品。</summary>
    public bool IsCompleted => CurrentIndex >= _items.Count || ApExhaustionReason is not null;

    /// <summary>AP 耗尽时结束本轮计划，保留游标以准确统计未执行作品。</summary>
    public string? ApExhaustionReason { get; private set; }

    public void CompleteForInsufficientAp(string reason) => ApExhaustionReason = reason;

    /// <summary>本次任务链中已经确认失败的作品数。</summary>
    public int FailureCount => _failedWorks.Count;

    /// <summary>本次任务链中已经成功或安全略过的作品数。</summary>
    public int SuccessCount => CurrentIndex - FailureCount;

    /// <summary>中断保留当前游标供恢复；与已推进的局部失败分开统计，不能把未执行作品算作成功。</summary>
    public string? InterruptionReason { get; private set; }

    /// <summary>记录任务链停止原因，供界面 finally 和日志使用同一份真实结果。</summary>
    public void MarkInterrupted(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        InterruptionReason = reason;
    }

    /// <summary>显式恢复执行时清除旧中断原因，保持作品游标和已完成结果不变。</summary>
    public void ClearInterruption() => InterruptionReason = null;

    /// <summary>记录本轮已确认未解锁的作品并安全推进；同链恢复保留，新任务链不继承。</summary>
    public void MarkCurrentLocked()
    {
        if (IsCompleted)
            throw new InvalidOperationException(L.Text("AP 消耗任务链已经完成，不能再次记录未解锁作品"));
        _lockedWorks.Add(CurrentItem!.Work);
        MarkCurrentCompleted();
    }

    /// <summary>状态栏与系统通知共用未解锁说明；没有检测到时返回空串，不显示占位信息。</summary>
    public string GetLockedSummary() => _lockedWorks.Count == 0
        ? string.Empty
        : L.Format($"未解锁已跳过（{_lockedWorks.Count}）：{string.Join("、", _lockedWorks)}");

    /// <summary>当前作品完整成功或被安全略过后推进。</summary>
    public void MarkCurrentCompleted()
    {
        if (IsCompleted)
        {
            throw new InvalidOperationException(L.Text("AP 消耗任务链已经完成，不能再次推进"));
        }

        CurrentIndex++;
    }

    /// <summary>记录当前作品失败并推进到下一作品；失败原因写入日志，汇总只保留作品名。</summary>
    public void MarkCurrentFailed(string reason)
    {
        if (IsCompleted)
        {
            throw new InvalidOperationException(L.Text("AP 消耗任务链已经完成，不能再次记录失败"));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _failedWorks.Add(CurrentItem!.Work);
        CurrentIndex++;
    }

    /// <summary>生成完成、部分失败或中止时均可显示的本次任务链摘要。</summary>
    public string GetExecutionSummary()
    {
        var remaining = Count - CurrentIndex;
        var summary = L.Format($"AP任务链：成功 {SuccessCount}，失败 {FailureCount}，未执行 {remaining}");
        if (_lockedWorks.Count > 0)
            summary += $"；{GetLockedSummary()}";
        if (_failedWorks.Count > 0)
            summary += L.Format($"；失败作品：{string.Join("、", _failedWorks)}");
        if (ApExhaustionReason is not null)
            summary += $"；{ApExhaustionReason}";
        return InterruptionReason is null ? summary : L.Format($"{summary}；已中断：{InterruptionReason}");
    }

    /// <summary>复制运行需要的最小作品状态，隔离可变的 WPF 绑定对象。</summary>
    private static ApConsumptionWorkItem CloneItem(ApConsumptionWorkItem item)
    {
        var selected = item.SelectedHardStages.ToHashSet();
        return new ApConsumptionWorkItem
        {
            WorkId = item.WorkId,
            Work = item.Work,
            HardCount = item.HardCount,
            Hard1 = selected.Contains(1),
            Hard2 = selected.Contains(2),
            Hard3 = selected.Contains(3),
            Hard4 = selected.Contains(4),
            Hard5 = selected.Contains(5),
        };
    }
}

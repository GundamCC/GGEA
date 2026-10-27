using GGEA.App.Localization;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GGEA.App.Config;

namespace GGEA.App.Tasks;

/// <summary>
/// AP 消耗页中的单个作品计划项。当前阶段只记录待测试的 Hard 关卡选择，
/// 后续实现关卡略过时可直接复用所选关卡编号。
/// </summary>
public sealed class ApConsumptionWorkItem : INotifyPropertyChanged
{
    private bool _hard1;
    private bool _hard2;
    private bool _hard3;
    private bool _hard4;
    private bool _hard5;

    public int WorkId { get; init; }
    public string Work { get; init; } = string.Empty;
    public int HardCount { get; init; }

    public bool Hard1 { get => _hard1; set => SetField(ref _hard1, value); }
    public bool Hard2 { get => _hard2; set => SetField(ref _hard2, value); }
    public bool Hard3 { get => _hard3; set => SetField(ref _hard3, value); }
    public bool Hard4 { get => _hard4; set => SetField(ref _hard4, value); }
    public bool Hard5 { get => _hard5; set => SetField(ref _hard5, value); }

    public bool IsHard1Enabled => HardCount >= 1;
    public bool IsHard2Enabled => HardCount >= 2;
    public bool IsHard3Enabled => HardCount >= 3;
    public bool IsHard4Enabled => HardCount >= 4;
    public bool IsHard5Enabled => HardCount >= 5;

    /// <summary>是否至少选择了一个该作品实际存在的 Hard 关卡。</summary>
    public bool HasSelectedStage => SelectedHardStages.Count > 0;

    /// <summary>按关卡编号返回当前选择，用于日志以及后续略过实现。</summary>
    public IReadOnlyList<int> SelectedHardStages
    {
        get
        {
            var selected = new List<int>(capacity: HardCount);
            if (IsHard1Enabled && Hard1) selected.Add(1);
            if (IsHard2Enabled && Hard2) selected.Add(2);
            if (IsHard3Enabled && Hard3) selected.Add(3);
            if (IsHard4Enabled && Hard4) selected.Add(4);
            if (IsHard5Enabled && Hard5) selected.Add(5);
            return selected;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>更新勾选状态并通知 WPF 刷新配置恢复后的界面。</summary>
    private void SetField(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedStage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedHardStages)));
    }
}

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
    public bool IsCompleted => CurrentIndex >= _items.Count;

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

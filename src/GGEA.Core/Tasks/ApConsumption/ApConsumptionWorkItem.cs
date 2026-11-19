using System.ComponentModel;
using System.Runtime.CompilerServices;

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
        if (!TaskItemProperty.Set(this, PropertyChanged, ref field, value, propertyName)) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedStage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedHardStages)));
    }
}

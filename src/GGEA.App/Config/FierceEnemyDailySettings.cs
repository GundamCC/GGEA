using GGEA.App.Tasks;

namespace GGEA.App.Config;

/// <summary>只保存强敌每日任务的选择与位置，单位素材始终从受校验的分发目录解析。</summary>
public sealed class FierceEnemyDailySettings
{
    /// <summary>单位编号；0 表示执行时使用游戏当前最新单位。</summary>
    public int UnitId { get; set; }
    /// <summary>添加任务时确定的难度。</summary>
    public FierceEnemyDifficulty Difficulty { get; set; }
    /// <summary>保留用户勾选状态，未勾选任务也需要恢复。</summary>
    public bool IsChecked { get; set; } = true;
    /// <summary>在每日列表中的位置。</summary>
    public int Index { get; set; }

    /// <summary>恢复时拒绝失效编号和未知难度，避免悄悄改为其他单位执行。</summary>
    public DailyTaskItem Restore(IReadOnlyList<FierceEnemyUnit> units)
    {
        var unit = UnitId == 0 ? FierceEnemyUnit.Latest : units.FirstOrDefault(unit => unit.Id == UnitId);
        if (unit is null || !Enum.IsDefined(Difficulty))
            throw new InvalidOperationException("已保存的强敌单位或难度不可用，请重新添加任务。");
        return new DailyTaskItem { Kind = DailyTaskKind.FierceEnemySkip, Name = "强敌来袭略过",
            FierceEnemySelection = new(unit, Difficulty), IsChecked = IsChecked };
    }
}

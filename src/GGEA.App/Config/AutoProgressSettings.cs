namespace GGEA.App.Config;

/// <summary>推图出击提示的处理策略；默认自动编制，仅调整用户授权的部队。</summary>
public enum FormationIssueAction
{
    ContinueSortie,
    AutoForm,
}

/// <summary>推图专属编制权限与策略；运行时使用快照，避免界面编辑改变正在执行的授权。</summary>
public sealed class AutoProgressSettings
{
    public bool AdjustSeriesTeam { get; set; } = true;
    public bool AdjustFreeTeam { get; set; } = true;
    public FormationIssueAction EmptySlotAction { get; set; } = FormationIssueAction.AutoForm;
    public FormationIssueAction TerrainAction { get; set; } = FormationIssueAction.AutoForm;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanAutoForm => AdjustSeriesTeam || AdjustFreeTeam;

    /// <summary>撤销全部部队权限或读到未知策略时恢复为依旧出击，UI 与后台共享同一约束。</summary>
    public void Normalize()
    {
        if (!CanAutoForm || !Enum.IsDefined(EmptySlotAction)) EmptySlotAction = FormationIssueAction.ContinueSortie;
        if (!CanAutoForm || !Enum.IsDefined(TerrainAction)) TerrainAction = FormationIssueAction.ContinueSortie;
    }

    /// <summary>复制当前权限并执行归一化，不修改正在被编辑的配置对象。</summary>
    public AutoProgressSettings Snapshot()
    {
        var copy = new AutoProgressSettings
        {
            AdjustSeriesTeam = AdjustSeriesTeam,
            AdjustFreeTeam = AdjustFreeTeam,
            EmptySlotAction = EmptySlotAction,
            TerrainAction = TerrainAction,
        };
        copy.Normalize();
        return copy;
    }
}

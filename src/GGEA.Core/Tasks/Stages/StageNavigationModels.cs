namespace GGEA.App.Tasks;

/// <summary>
/// 主要关卡画布模式。挑战模式暂未启用，但从导航模型层保留独立模式，
/// 便于后续复用相同的作品绝对坐标。
/// </summary>
public enum StageMapMode
{
    Main,
    Challenge,
}

/// <summary>关卡导航当前所在的页面。</summary>
public enum StagePageState
{
    Unknown,
    Home,
    StageMenu,
    MainStageCanvas,
    WorkOverview,
    WorkStageList,
}

/// <summary>作品内关卡类型。BOSS 是最后一个 NORMAL 关卡的显式标记。</summary>
public enum WorkStageKind
{
    // 未读出关卡资料不等于游戏无法继续；不占用既有类型的持久化数值。
    Unknown = -1,
    Normal,
    Boss,
    SideStory,
    Hard,
}

/// <summary>当前选中节点的可验证状态。</summary>
public enum WorkStageNodeState
{
    Unknown,
    Locked,
    Incomplete,
    Completed,
    /// <summary>已通关但未满足全部目标（CLEAR）；推图与略过均可使用，略过仍须复核实际按钮。</summary>
    Cleared,
}

/// <summary>作品内关卡在从左到右完整序列中的位置。</summary>
public sealed record WorkStagePosition(
    WorkStageKind Kind,
    int Number,
    int OverallIndex);

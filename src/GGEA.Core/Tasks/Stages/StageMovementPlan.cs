namespace GGEA.App.Tasks;

/// <summary>
/// 数学规划得到的一次画布移动方案。
/// UsesHalfFinalSwipe 为 true 时，最后一次以标准输入距离的一半执行；
/// 用固定半屏手势避免任意比例短滑带来的不可重复位移。
/// </summary>
public sealed record StageMovementPlan(
    int Direction,
    int SwipeCount,
    int TargetScreenX,
    int CanvasMovement,
    bool UsesHalfFinalSwipe = false);

using GGEA.App.Automation;
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

/// <summary>
/// 作品关卡序列规划器。
/// NORMAL 的最后一关固定视为 BOSS；SIDE STORY（如存在）位于 BOSS 后、HARD 前。
/// 统一序号用于把识别到的当前节点与像素距离关联，禁止再假设固定五槽位。
/// </summary>
public static class WorkStageSequencePlanner
{
    /// <summary>返回作品内全部节点数。</summary>
    public static int GetTotalCount(int normalCount, int sideStories, int hardCount)
        => Math.Max(0, normalCount) + Math.Max(0, sideStories) + Math.Max(0, hardCount);

    /// <summary>根据类型和显示编号换算从左到右的一基序号。</summary>
    public static bool TryGetPosition(
        int normalCount,
        int sideStories,
        int hardCount,
        WorkStageKind kind,
        int number,
        out WorkStagePosition? position)
    {
        position = null;
        if (normalCount < 1 || sideStories < 0 || hardCount < 1 || number < 1)
        {
            return false;
        }

        switch (kind)
        {
            case WorkStageKind.Normal when number < normalCount:
                position = new WorkStagePosition(kind, number, number);
                return true;
            case WorkStageKind.Boss when number == normalCount:
                position = new WorkStagePosition(kind, number, normalCount);
                return true;
            case WorkStageKind.SideStory when number <= sideStories:
                position = new WorkStagePosition(kind, number, normalCount + number);
                return true;
            case WorkStageKind.Hard when number <= hardCount:
                position = new WorkStagePosition(kind, number, normalCount + sideStories + number);
                return true;
            default:
                return false;
        }
    }
}

/// <summary>关卡导航失败原因，调用方可据此决定重试或恢复。</summary>
public enum StageNavigationFailure
{
    None,
    UnsupportedMode,
    WorkNotConfigured,
    InitialLocationFailed,
    /// <summary>作品详情页的“选择”按钮为暗态，表示该作品尚未解锁。</summary>
    WorkStageLocked,
    TitleNotRecognized,
    SkipNotConfirmed,
    InvalidContext,
    /// <summary>从主界面进入关卡菜单后未确认标题，必须停止整段导航。</summary>
    StageMenuNotRecognized,
}

/// <summary>关卡导航结果，避免用一个无条件 true 掩盖失败阶段。</summary>
public sealed record StageNavigationResult(
    bool Success,
    StageNavigationFailure Failure,
    string Message)
{
    public static StageNavigationResult Ok(string message) =>
        new(true, StageNavigationFailure.None, message);

    public static StageNavigationResult Fail(StageNavigationFailure failure, string message) =>
        new(false, failure, message);
}

/// <summary>
/// 单设备关卡导航上下文。普通作品关卡页退出后，游戏会把该作品置于屏幕中心；
/// 但未解锁作品的详情页退出会保留原镜头，因此必须保存两种情况下的实际视口偏移。
/// </summary>
public sealed class StageNavigationContext
{
    public string? Serial { get; private set; }
    public StageMapMode Mode { get; private set; } = StageMapMode.Main;
    public StagePageState PageState { get; private set; } = StagePageState.Unknown;
    public int? CurrentWorkId { get; private set; }
    public int? CurrentHardIndex { get; private set; }
    public double? ViewOffsetX { get; private set; }
    public bool IsReliable { get; private set; }

    /// <summary>记录首次从左端绝对定位得到的视口偏移。</summary>
    public void SetLocatedCanvas(string serial, StageMapMode mode, double viewOffsetX)
    {
        Serial = serial;
        Mode = mode;
        PageState = StagePageState.MainStageCanvas;
        CurrentWorkId = null;
        CurrentHardIndex = null;
        ViewOffsetX = viewOffsetX;
        IsReliable = true;
    }

    /// <summary>记录已经进入指定作品的具体关卡页。</summary>
    public void SetWorkStageList(string serial, StageMapMode mode, int workId, int? hardIndex = null)
    {
        Serial = serial;
        Mode = mode;
        PageState = StagePageState.WorkStageList;
        CurrentWorkId = workId;
        CurrentHardIndex = hardIndex;
        ViewOffsetX = null;
        IsReliable = true;
    }

    /// <summary>记录当前作品对应的可靠画布视口；该作品可为居中状态或未解锁详情页返回后的保留状态。</summary>
    public void SetWorkCanvas(string serial, StageMapMode mode, int workId, double viewOffsetX)
    {
        Serial = serial;
        Mode = mode;
        PageState = StagePageState.MainStageCanvas;
        CurrentWorkId = workId;
        CurrentHardIndex = null;
        ViewOffsetX = viewOffsetX;
        IsReliable = true;
    }

    /// <summary>设备或页面状态发生矛盾时清空上下文。</summary>
    public void Invalidate()
    {
        PageState = StagePageState.Unknown;
        CurrentWorkId = null;
        CurrentHardIndex = null;
        ViewOffsetX = null;
        IsReliable = false;
    }
}

/// <summary>关卡导航的滑动和等待参数。</summary>
public sealed class StageNavigationOptions
{
    /// <summary>Logo 导航的观察次数、无继续提示的超时与恢复上限；每次继续提示命中会重置次数和计时。</summary>
    public int LogoNavigationMaxAttempts { get; init; } = 80;
    public int LogoNavigationTimeoutMs { get; init; } = 180000;
    public int LogoRecoveryMaxCount { get; init; } = 2;
    /// <summary>入口或滑动后无有效作品信号时的观察窗口；不能把加载帧立即当成错误页面。</summary>
    public int LogoObservationWaitTimeoutMs { get; init; } = GameTiming.RecognitionTimeoutMs;
    public int ScreenCenterX { get; init; } = 960;
    public int ViewportWidth { get; init; } = 1920;
    public int SwipeCanvasDelta { get; init; } = 1920;
    /// <summary>手指向左半屏滑动的点击 X 补偿；正数向右，负数向左。</summary>
    public int HalfSwipeLeftTapCorrection { get; init; } = 200;
    /// <summary>手指向右半屏滑动的点击 X 补偿；正数向右，负数向左。</summary>
    public int HalfSwipeRightTapCorrection { get; init; } = -200;
    public int SwipeLeftStartX { get; init; } = 1500;
    public int SwipeLeftEndX { get; init; } = 420;
    public int SwipeY { get; init; } = 540;
    public int SwipeDurationMs { get; init; } = 600;
    // 左侧靠边节点会与返回等固定控件重叠；数学上可见不代表可安全点击。
    public int WorkTapMinX { get; init; } = 180;
    // 右侧边缘的作品节点会被画面裁切，点击中心可能落在无效区域；保留同等安全边距。
    public int WorkTapRightMargin { get; init; } = 180;
    // 滑动后的短动画时间保持用户设置；超过五秒时改为观察画布停止移动。
    public int SwipeSettleMs { get; init; } = 3000;
    // 最后一次作品定位滑动完成后、点击作品节点前的稳定等待时间。
    // 该等待独立于滑动间隔，保证最终点击不会落在仍有惯性动画的画布上。
    public int WorkTapSettleMs { get; init; } = 3000;
    public int PageSettleMs { get; init; } = 2500;
    // 点击作品后、点击“选择”前，额外等待详情页入场动画完成（毫秒）。
    public int SelectTapExtraDelayMs { get; init; } = GameTiming.RecognitionIntervalMs;
    public int TitleWaitTimeoutMs { get; init; } = GameTiming.RecognitionTimeoutMs;
    public int TitlePollIntervalMs { get; init; } = GameTiming.RecognitionIntervalMs;
    /// <summary>启用左侧节点识别时，标题连续未命中多久便切换到节点识别。</summary>
    public int TitleNodeFallbackWaitMs { get; init; } = 2000;
    /// <summary>作品详情页右下角可用“选择”按钮模板的完整路径；为空时兼容固定坐标点击。</summary>
    public string WorkSelectTemplatePath { get; init; } = string.Empty;
    /// <summary>作品详情页右下角暗态“选择”按钮模板的完整路径。</summary>
    public string WorkSelectClosedTemplatePath { get; init; } = string.Empty;
    /// <summary>“选择”按钮状态模板的匹配阈值。</summary>
    public double WorkSelectThreshold { get; init; } = 0.85;
    /// <summary>等待“选择”按钮状态出现的最长时间。</summary>
    public int WorkSelectWaitTimeoutMs { get; init; } = GameTiming.ShortRecognitionTimeoutMs;
    /// <summary>“选择”按钮状态的轮询间隔。</summary>
    public int WorkSelectPollIntervalMs { get; init; } = GameTiming.RecognitionIntervalMs;
    // 点击作品节点后，详情页应与原画布出现明显变化；未变化时禁止把孤立模板命中
    // 当作“未解锁”，以免错误返回破坏后续的画布坐标上下文。
    public double WorkOverviewChangeRatio { get; init; } = 0.08;
    public int ResetSearchMaxSwipes { get; init; } = 8;
    public int ResetBoundarySwipeStartX { get; init; } = 120;
    public int ResetBoundarySwipeEndX { get; init; } = 1800;
    public double ResetAnchorThreshold { get; init; } = 0.85;
    public int MaxCalculatedSwipes { get; init; } = 16;
}

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

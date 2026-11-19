using GGEA.App.Automation;

namespace GGEA.App.Tasks;

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

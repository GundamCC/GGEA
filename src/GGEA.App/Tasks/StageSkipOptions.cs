using GGEA.App.Automation;
namespace GGEA.App.Tasks;

/// <summary>独立略过服务的运行参数。</summary>
public sealed class StageSkipOptions
{
    /// <summary>强敌获得单位展示：结果页之前允许 OCR 确认完整 TAP TO NEXT 后继续。</summary>
    public bool ContinueUnitAcquisition { get; init; }

    /// <summary>活动关卡的返回证据；借用当前帧，仍由公共循环处理玩家升级。</summary>
    public Func<GGEA.App.Vision.ScreenImage, CancellationToken, bool>? PostSkipConfirmation { get; init; }

    /// <summary>无玩家升级的强化培育流程：OK 后在左上角确认此标题，替代次数上限及升级检测。</summary>
    public string PostSkipPageTemplatePath { get; init; } = "";

    /// <summary>玩家升级固定标题及继续提示模板；不包含动态等级、AP 数字。</summary>
    public string PlayerRankUpTemplatePath { get; init; } = "";
    public string PlayerRankUpNextTemplatePath { get; init; } = "";

    /// <summary>正常关卡未出现时，延后启动低频升级检测，避免每轮都扫描稀有提示。</summary>
    public int PlayerRankUpCheckDelayMs { get; init; } = GameTiming.AnimationSettleMs;
    public int PlayerRankUpPollIntervalMs { get; init; } = GameTiming.RecognitionIntervalMs;

    /// <summary>首次识别升级标题后等待动画；等待结束必须重新截图，不能使用旧提示坐标。</summary>
    public int PlayerRankUpAnimationWaitMs { get; init; } = GameTiming.AnimationSettleMs;

    /// <summary>点击“略过”后等待确认弹窗出现的时间。</summary>
    public int SkipDialogWaitMs { get; init; } = 1500;

    /// <summary>等待执行请求、奖励动画及 SKIP RESULT 的单段超时；强敌每次奖励继续后重新计时，最多六次。</summary>
    public int SkipResultTimeoutMs { get; init; } = 60000;

    /// <summary>正常等待耗尽后，仅一次迟到结果恢复预算；不重发消费动作，奖励继续也不能续期。</summary>
    public int LateResultRecoveryTimeoutMs { get; init; } = 30000;

    /// <summary>OK 后返回关卡页的独立预算；未指定时沿用结果预算但最多 20 秒，避免延长动画等待连带扩大返回等待。</summary>
    public int? PostSkipTimeoutMs { get; init; }

    /// <summary>SKIP RESULT 标题匹配阈值。</summary>
    public double SkipResultThreshold { get; init; } = 0.85;

    /// <summary>“次数上限”暗态匹配阈值。</summary>
    public double SkipCountLimitThreshold { get; init; } = 0.85;

    /// <summary>点击结果 OK 后等待返回关卡页的时间。</summary>
    public int SettleAfterOkMs { get; init; } = 2500;

    /// <summary>点击回复AP弹窗关闭按钮后等待页面切换的时间。</summary>
    public int ApRecoverySettleMs { get; init; } = 2000;

    /// <summary>SKIP RESULT 标题模板路径；为空时使用固定等待。</summary>
    public string SkipResultTemplatePath { get; init; } = "";

    /// <summary>“次数上限”暗态模板路径；为空时跳过成功确认。</summary>
    public string SkipCountLimitTemplatePath { get; init; } = "";

    /// <summary>回复 AP 窗口标题证据（不是出击前 AP 不足询问）；必须与关闭按钮在同一帧命中，空路径禁用 AP 检测。</summary>
    public string ApRecoveryPromptTemplatePath { get; init; } = "";

    /// <summary>AP 不足时“回复AP”弹窗关闭按钮模板路径；只用于确认后的关闭定位。</summary>
    public string ApRecoveryCloseTemplatePath { get; init; } = "";
}

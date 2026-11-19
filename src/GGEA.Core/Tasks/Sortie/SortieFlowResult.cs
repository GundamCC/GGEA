namespace GGEA.App.Tasks;

/// <summary>出击后流程的处理结果。</summary>
public enum SortieFlowResult
{
    /// <summary>本轮完成：已点击“再次出击”，调用方应继续下一轮。</summary>
    RoundCompleted,

    /// <summary>全部刷取次数完成：已点击退出坐标。</summary>
    AllRoundsCompleted,

    /// <summary>检测到下载界面且不允许下载，已点击取消。</summary>
    DownloadCancelled,

    /// <summary>未在有限时间内确认开场或新战斗；下载后同时检查结算证据。</summary>
    TapToNextTimeout,

    /// <summary>战斗结束后等待“继续”按钮超时。</summary>
    ContinueTimeout,

    /// <summary>战斗结束后等待“再次出击”按钮超时。</summary>
    AgainTimeout,

    /// <summary>识别到刷本失败画面，已点击回到选择关卡。</summary>
    FailureDetected,

    /// <summary>出击阵容存在空位且未允许空位出击，已点击取消并提示玩家。</summary>
    EmptySlotCancelled,

    /// <summary>识别到空位出击提示但未识别到对话框按钮。</summary>
    EmptySlotTimeout,

    /// <summary>识别到地形适性提示且未允许强制出击，已点击取消。</summary>
    TerrainCancelled,

    /// <summary>识别到地形适性提示但未识别到对话框按钮。</summary>
    TerrainTimeout,

    /// <summary>识别到“再次出击”按钮关闭状态，AP 不足，已点击选择关卡并中止。</summary>
    NoAp,

    /// <summary>点击出击后识别到 AP 不足提示，已点击对话框取消并提示玩家。</summary>
    NoApCancelled,

    /// <summary>识别到 AP 不足提示但未识别到对话框取消按钮。</summary>
    NoApTimeout,

    /// <summary>自动推图已处理故事/结算并确认当前作品选关页，可以直接读取下一关。</summary>
    ProgressStageCompleted,

    /// <summary>自动推图未在限定时间内确认结算后的返回页面，不计为完成。</summary>
    ProgressTimeout,

    /// <summary>历史结果值，保留旧日志兼容；当前推图不再因 SKIP 未出现返回此结果。</summary>
    ProgressSkipTimeout,

    /// <summary>AUTO 切换后持续无法恢复全部自动或状态证据不足，停止本关而非无限点击/等待。</summary>
    ProgressAutoTimeout,

    /// <summary>自动推图已确认返回主界面/主要关卡；本关完成，但继续同一作品前必须重新导航。</summary>
    ProgressWorkNavigationRequired,

    /// <summary>卡顿恢复确认已在选关页；重新读取实际节点，不将本次观察计为战斗完成。</summary>
    ProgressResumeStage,

    /// <summary>卡顿恢复确认已回主界面或主要关卡；保留当前作品队列并重新导航，不计通关。</summary>
    ProgressResumeWork,

    /// <summary>卡顿恢复确认仍在准备页；交回本关编制会话重新检查后出击，不直接盲点。</summary>
    ProgressResumePreparation,

    /// <summary>连续两个两分钟检查仍无阶段进展，停止当前自动任务并保留游戏画面。</summary>
    Stalled,

    /// <summary>结算尚未确认却回到出击准备页；不计完成、不再次消费 AP，保留画面停止刷本。</summary>
    UnexpectedPreparation,

}

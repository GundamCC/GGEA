using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>历史推图的下一部分；本阶段仅给出计划，不授权直接出击。</summary>
public enum HistoryProgressStep { Unknown, Stage, Boss, Finished }

/// <summary>关卡优先于 BOSS；只用同一作品连续确认的进度生成下一步。</summary>
public static class HistoryProgressDecision
{
    public static readonly (int X, int Y) StageButton = (1250, 435);
    public static readonly (int X, int Y) BossButton = (1660, 400);

    /// <summary>锁定 BOSS 不能进入；关卡 100% 却仍锁定表示页面状态未就绪，等待重新识别。</summary>
    public static HistoryProgressStep Decide(HistoryProgressObservation? progress) => progress switch
    {
        { Stage: HistoryCompletion.Incomplete } => HistoryProgressStep.Stage,
        { Stage: HistoryCompletion.Complete, Boss: HistoryCompletion.Incomplete } => HistoryProgressStep.Boss,
        { Stage: HistoryCompletion.Complete, Boss: HistoryCompletion.Complete } => HistoryProgressStep.Finished,
        _ => HistoryProgressStep.Unknown
    };

    /// <summary>展示已确认的计划，不将识别失败或尚未执行的推图报为成功。</summary>
    public static string Describe(HistoryProgressObservation? progress) => Decide(progress) switch
    {
        HistoryProgressStep.Stage => "关卡未达 100%，下一步应先攻略关卡，达到 100% 后再检查 BOSS。",
        HistoryProgressStep.Boss => "关卡已达 100%，BOSS 未达 100%，下一步应攻略 BOSS。",
        HistoryProgressStep.Finished => "关卡与 BOSS 均为 100%，无需推图。",
        _ => "攻略度未连续确认或 BOSS 尚未解锁，暂不决定推图。"
    };
}

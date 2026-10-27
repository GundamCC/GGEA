using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;

namespace GGEA.App.Vision;

/// <summary>在共享节点算法外增加历史作品的同帧身份边界，缩小后的 Logo 也须通过全目录竞争复核。</summary>
public sealed class HistoryStageNodeRecognizer(AutomationService automation, StageNodeRecognizer nodes,
    HistoryProgressNavigator navigation) : IStageNodeRecognizer, IStageNextNodeRecognizer
{
    /// <summary>每张用于节点决策的截图先验证作品和页面，两帧编号、状态与位置一致才返回。</summary>
    public async Task<StageNodeRecognition?> RecognizeSelectedAsync(string serial, HardStageEntry work, CancellationToken ct = default)
    {
        var previous = await Read();
        StageNodeRecognition? stopEvidence = null;
        // 编号动画或背景叠色可能使单帧失败；有限采样寻找相邻两帧，不因第一帧缺号直接丢弃完成状态。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(previous is not null ? GameTiming.ConfirmationIntervalMs : GameTiming.RecognitionIntervalMs, ct);
            var next = await Read();
            if (ConfirmObservation(previous, next) is { } stable)
            {
                if (stable.Position is not null) return stable;
                stopEvidence = stable;
            }
            previous = next;
        }
        return stopEvidence;

        async Task<StageNodeRecognition?> Read()
        {
            var screen = await automation.CaptureScreenAsync(serial, ct);
            return await Task.Run(() =>
            {
                navigation.Require(screen, HistoryPageKind.StageNodes, ct);
                return nodes.RecognizeSelected(screen, work, ct);
            }, ct);
        }
    }

    /// <summary>可信编号与禁止出击状态分开确认；缺号不能把两帧已通过/锁定降级成允许重打。</summary>
    internal static StageNodeRecognition? ConfirmObservation(StageNodeRecognition? first, StageNodeRecognition? next)
    {
        if (first is null || next is null || first.State != next.State ||
            Math.Abs(first.CenterX - next.CenterX) > 8) return null;
        if (first.Position is not null && first.Position == next.Position &&
            first.NumberRegion is Roi a && next.NumberRegion is Roi b &&
            Math.Abs(a.CenterX - b.CenterX) <= 8 && Math.Abs(a.CenterY - b.CenterY) <= 12) return next;
        // 只保留禁止重复出击的证据，不能用单帧编号宣告最终关已完成。
        return Math.Abs(first.CenterY - next.CenterY) <= 12 && (next.HasPassed || next.State == WorkStageNodeState.Locked)
            ? next with { Kind = WorkStageKind.Unknown, Number = null, Position = null, NumberRegion = null }
            : null;
    }

    /// <summary>NEXT 授权仍来自当前作品的完整标记与编号，其他页面即使外观相似也不能使用。</summary>
    public StageNextNodeRecognition? RecognizeNext(ScreenImage screen, HardStageEntry work, CancellationToken ct = default)
    {
        navigation.Require(screen, HistoryPageKind.StageNodes, ct);
        return nodes.RecognizeNext(screen, work, ct);
    }
}

using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>单作品协调关卡与 BOSS，两部分的通关结果不能替代作品总览的双 100% 证据。</summary>
public sealed class HistoryWorkProgressRunner(
    Func<CancellationToken, Task<HistoryProgressObservation>> openOverview,
    Func<CancellationToken, Task<AutoProgressResult>> stages,
    Func<CancellationToken, Task<AutoProgressResult>> bosses,
    Func<CancellationToken, Task<HistoryProgressObservation>> returnOverview)
{
    /// <summary>每部分至多执行一轮；未达 100% 时说明并停止，不反复消耗 AP 补刷或跳过队列目标。</summary>
    public async Task<AutoProgressResult> RunAsync(string work, Action<string> report, CancellationToken ct, bool attackBoss = true)
    {
        var progress = await openOverview(ct);
        var completed = 0;
        ct.ThrowIfCancellationRequested();
        if (progress.Stage == HistoryCompletion.Incomplete)
        {
            report(L.Format($"历史作品 {work}：关卡未达 100%，先推进至最后 HARD。"));
            var result = await stages(ct);
            ct.ThrowIfCancellationRequested();
            completed += result.CompletedStages;
            if (!result.Completed) return new(false, 0, completed, result.Message);
            progress = await returnOverview(ct);
        }
        ct.ThrowIfCancellationRequested();
        if (progress.Stage != HistoryCompletion.Complete)
            return new(false, 0, completed, L.Text("关卡攻略度尚未确认 100%，未进入 BOSS；请检查未完成目标或识别状态。"));
        // 仅关卡目标仍要求总览 100% 证据；BOSS 未解锁或无法识别不阻塞此类目标。
        if (!attackBoss)
        {
            report(L.Format($"历史作品 {work} 已复核关卡 100%，未选择攻略 BOSS，继续下一队列目标。"));
            return new(true, 1, completed, L.Format($"{work} 已完成关卡 100% 复核（未选择攻略 BOSS）。"));
        }
        if (progress.Boss == HistoryCompletion.Incomplete)
        {
            report(L.Format($"历史作品 {work}：关卡 100%，按 NORMAL → HARD → EXPERT 推进六个 BOSS 关卡。"));
            var result = await bosses(ct);
            ct.ThrowIfCancellationRequested();
            completed += result.CompletedStages;
            if (!result.Completed) return new(false, 0, completed, result.Message);
            progress = await returnOverview(ct);
        }
        ct.ThrowIfCancellationRequested();
        if (progress.Stage != HistoryCompletion.Complete || progress.Boss != HistoryCompletion.Complete)
            return new(false, 0, completed, L.Text("作品总览未确认双 100%（可能仍锁定、未满攻略度或识别不明），停止当前作品，不切换队列。"));
        report(L.Format($"历史作品 {work} 已复核关卡与 BOSS 均为 100%，继续下一队列目标。"));
        return new(true, 1, completed, L.Format($"{work} 已完成双 100% 复核。"));
    }
}

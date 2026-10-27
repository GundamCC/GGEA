using GGEA.App.Localization;
namespace GGEA.App.Tasks;

/// <summary>工厂已确认的开发进度；计划 GP 不冒充游戏实际到账 GP。</summary>
public sealed record BallFactoryRunResult(int CompletedBatches, int DevelopedUnits, int DismantleRounds, bool Completed, string? StopReason);

/// <summary>批次编排与图像、ADB 分离；仅完成开发后推进批次，上限清理后重试原批次。</summary>
public sealed class BallFactoryRunner(
    Func<int, bool, CancellationToken, Task<DevelopResult>> developBatch,
    Func<DevelopResult, CancellationToken, Task<bool>> recoverBlocked,
    Func<CancellationToken, Task<int>> dismantleUntilEmpty)
{
    /// <summary>最后一批完成后必须再清理一次；识别异常和取消直接传播，不误报任务完成。</summary>
    public async Task<BallFactoryRunResult> RunAsync(int targetGp, Action<string> report, CancellationToken ct = default)
    {
        var batches = BallDevelopmentPlan.Batches(targetGp);
        var index = 0;
        var units = 0;
        var rounds = 0;
        var lastClearedIndex = -1;
        var locateTarget = true;
        while (index < batches.Count)
        {
            ct.ThrowIfCancellationRequested();
            report(L.Format($"开发第 {index + 1}/{batches.Count} 批，本批 {batches[index]} 台；已确认开发 {units} 台。"));
            var state = await developBatch(batches[index], locateTarget, ct);
            if (state == DevelopResult.Completed)
            {
                units += batches[index++];
                locateTarget = false;
                continue;
            }
            if (state.GetBlockedReason() is not { } reason)
                return new(index, units, rounds, false, L.Text("开发识别或确认未完成，不重复制造。"));
            if (!await recoverBlocked(state, ct)) return new(index, units, rounds, false, L.Format($"{reason}，取消或主界面恢复失败。"));
            if (state == DevelopResult.InsufficientCoins) return new(index, units, rounds, false, reason);
            // 清空后同一批仍达上限，继续解体没有进展，必须停止而不是无限消费/导航。
            if (lastClearedIndex == index) return new(index, units, rounds, false, L.Text("解体清理后同一开发批次仍达单位上限。"));
            report(L.Format($"单位已达上限，先解体清理，随后继续第 {index + 1} 批。"));
            var cleared = await dismantleUntilEmpty(ct);
            rounds += cleared;
            if (cleared == 0) return new(index, units, rounds, false, L.Text("单位已达上限，但没有推荐单位可解体，无法继续开发。"));
            lastClearedIndex = index;
            locateTarget = true;
        }
        report(L.Text("规划开发批次已完成，正在进行最后一次解体清理…"));
        rounds += await dismantleUntilEmpty(ct);
        return new(index, units, rounds, true, null);
    }
}

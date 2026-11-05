using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>当前位置对应的单关状态；1 是最底部 NORMAL，6 是最顶部 EXPERT。</summary>
public sealed record HistoryBossNode(int Order, FierceEnemyProgressState State);

/// <summary>顺序消费单关观察，遇到首个未完成目标立即攻略；不预扫描后续关卡。</summary>
public sealed class HistoryBossProgressRunner(
    Func<int, CancellationToken, Task<HistoryBossNode>> read,
    Func<int, CancellationToken, Task<AutoProgressBattleResult>> battle)
{
    /// <summary>已完成关卡不重复访问；每场战后仅复核刚才那一关，再向上一关推进。</summary>
    public async Task<AutoProgressResult> RunAsync(string work, Action<string> report, CancellationToken ct)
    {
        var completed = 0;
        for (var order = 1; order <= 6; order++)
        {
            ct.ThrowIfCancellationRequested();
            var node = await read(order, ct);
            ct.ThrowIfCancellationRequested();
            if (node.Order != order)
                return new(false, 0, completed, L.Text("BOSS 当前观察位置与目标不符，停止推进。"));
            if (node.State == FierceEnemyProgressState.Passed) continue;
            if (node.State != FierceEnemyProgressState.Available)
                return new(false, 0, completed, L.Text("下一 BOSS 关卡锁定或未知，停止推进。"));
            report(L.Format($"历史作品 {work}：推进 BOSS 第 {order}/6 关（由低到高）…"));
            var result = await battle(order, ct);
            ct.ThrowIfCancellationRequested();
            if (!result.Completed) return new(false, 0, completed, result.Message);
            completed++;
            var after = await read(order, ct);
            ct.ThrowIfCancellationRequested();
            if (after.Order != order || after.State != FierceEnemyProgressState.Passed)
                return new(false, 0, completed, L.Text("BOSS 结算后原关卡未确认 CLEAR / COMPLETE，停止重复出击。"));
        }
        return new(true, 0, completed, L.Text("六个 BOSS 关卡均已通过，返回作品页复核攻略度。"));
    }
}

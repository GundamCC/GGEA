using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>三种难度的有界推进；必须观察到 EX 通关才完成，不以战斗次数推算解锁。</summary>
public sealed class FierceEnemyProgressRunner(
    Func<CancellationToken, Task<IReadOnlyList<FierceEnemyProgressObservation>>> read,
    Func<FierceEnemyDifficulty, CancellationToken, Task<AutoProgressBattleResult>> battle)
{
    /// <summary>枚举顺序为 EXPERT、HARD、NORMAL；未知的高难度阻止向下猜测。</summary>
    public static FierceEnemyDifficulty? Select(IReadOnlyList<FierceEnemyProgressObservation> observations)
    {
        foreach (var difficulty in Enum.GetValues<FierceEnemyDifficulty>())
        {
            var node = observations.SingleOrDefault(item => item.Difficulty == difficulty);
            if (node is null || node.State == FierceEnemyProgressState.Unknown) return null;
            if (node.State == FierceEnemyProgressState.Available) return difficulty;
        }
        return null;
    }

    /// <summary>结算后要求上一难度确实变为通关；失败、锁定或未推进时停止，不能重复消耗 AP。</summary>
    public async Task<AutoProgressResult> RunAsync(string name, Action<string> report, CancellationToken ct)
    {
        FierceEnemyDifficulty? last = null;
        var completed = 0;
        for (var visit = 0; visit <= 3; visit++)
        {
            ct.ThrowIfCancellationRequested();
            var observations = await read(ct);
            ct.ThrowIfCancellationRequested();
            if (observations.Any(x => x.Difficulty == FierceEnemyDifficulty.EXPERT && x.State == FierceEnemyProgressState.Passed))
            {
                report(L.Format($"强敌 {name} 已确认 EXPERT 通关，继续下一队列目标。"));
                return new(true, 1, completed, L.Format($"{name} 已确认 EXPERT 通关。"));
            }
            if (last is { } previous && !observations.Any(x => x.Difficulty == previous && x.State == FierceEnemyProgressState.Passed))
                return new(false, 0, completed, L.Format($"{name} 的 {previous} 结算后未确认 CLEAR / COMPLETE，已停止重复出击。"));
            var next = Select(observations);
            if (next is null || visit == 3)
                return new(false, 0, completed, L.Format($"{name} 未可靠确认可推进的未通关难度，已停止。"));
            report(L.Format($"强敌来袭：{name} / {next}，复核挑战按钮后出击…"));
            var result = await battle(next.Value, ct);
            ct.ThrowIfCancellationRequested();
            if (!result.Completed) return new(false, 0, completed, result.Message);
            last = next;
            completed++;
        }
        return new(false, 0, completed, L.Text("强敌推进次数已达上限。"));
    }
}

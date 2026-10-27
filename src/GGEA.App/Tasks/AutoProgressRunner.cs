using GGEA.App.Localization;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>队列结果同时区分已完成作品和本次成功战斗数，不能把部分完成报告为全部完成。</summary>
public sealed record AutoProgressResult(bool Completed, int CompletedWorks, int CompletedStages, string Message);

/// <summary>跟随游戏当前选择推进作品队列；编号观察用于记录和终点确认，不作为出击入口的前置条件。</summary>
public sealed class AutoProgressRunner
{
    private readonly Func<HardStageEntry, CancellationToken, Task<StageNodeRecognition>> _openWork;
    private readonly Func<HardStageEntry, CancellationToken, Task<StageNodeRecognition>> _readCurrentWork;
    private readonly Func<StageNodeRecognition, CancellationToken, Task<AutoProgressBattleResult>> _runStage;
    private readonly Func<HardStageEntry, StageNodeRecognition, CancellationToken, Task<StageNodeRecognition>>? _advanceAfterBattle;
    private readonly Func<HardStageEntry, CancellationToken, Task<bool>>? _restoreWork;

    /// <summary>首次进入、原页选关复核与战斗分别注入；原页读取不能暗中回退为主界面导航。</summary>
    public AutoProgressRunner(Func<HardStageEntry, CancellationToken, Task<StageNodeRecognition>> openWork,
        Func<HardStageEntry, CancellationToken, Task<StageNodeRecognition>> readCurrentWork,
        Func<StageNodeRecognition, CancellationToken, Task<AutoProgressBattleResult>> runStage,
        Func<HardStageEntry, StageNodeRecognition, CancellationToken, Task<StageNodeRecognition>>? advanceAfterBattle = null,
        Func<HardStageEntry, CancellationToken, Task<bool>>? restoreWork = null)
    {
        _openWork = openWork; _readCurrentWork = readCurrentWork; _runStage = runStage;
        _advanceAfterBattle = advanceAfterBattle; _restoreWork = restoreWork;
    }

    /// <summary>同一作品在选关页直接读取进度；仅首次、换作品或已确认退出作品页时重新导航。</summary>
    public async Task<AutoProgressResult> RunAsync(IReadOnlyList<HardStageEntry> works,
        Action<string> report, CancellationToken ct)
    {
        if (works.Count == 0) return new(false, 0, 0, L.Text("请先添加推图作品。"));
        var completedWorks = 0;
        var completedStages = 0;
        foreach (var work in works)
        {
            var total = WorkStageSequencePlanner.GetTotalCount(work.NormalCount, work.SideStories, work.HardCount);
            if (work.NormalCount < 1 || work.HardCount < 1 || work.SideStories < 0)
                return new(false, completedWorks, completedStages, L.Format($"作品 {work.Work} 的关卡数量无效。"));
            var completedPositions = new HashSet<WorkStagePosition>();
            var finished = false;
            var needsWorkNavigation = true;
            var workBattles = 0;
            var recoveries = 0;
            // 只记录本作品最近一次成功结算的已知节点；未知编号或失败战斗不提供推进依据。
            WorkStagePosition? lastBattlePosition = null;
            for (var visit = 0; visit <= total + 3; visit++)
            {
                ct.ThrowIfCancellationRequested();
                // 历史作品恢复至总览时可直接用连续 100% 证据结束关卡部分，无须伪造最终 HARD 节点。
                // false 表示调用方已恢复节点页；主线未注入此适配，保持原来的重新导航行为。
                if (visit > 0 && needsWorkNavigation && _restoreWork is not null && await _restoreWork(work, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    completedWorks++; finished = true;
                    report(L.Format($"作品 {work.Work} 恢复后已确认关卡攻略度 100%。"));
                    break;
                }
                // 最终 HARD 已走完结算后，原页读取只复核完成状态，不能误呈现为新一轮出击。
                var confirmingCompletion = completedPositions.Any(position => position.Kind == WorkStageKind.Hard &&
                    position.Number == work.HardCount && position.OverallIndex == total);
                report(confirmingCompletion
                    ? L.Format($"复核作品 {work.Work} 的 HARD {work.HardCount} 通关状态（本关已结算，不重复出击）…")
                    : needsWorkNavigation ? L.Format($"进入作品 {work.Work}，目标 HARD {work.HardCount}…")
                    : L.Format($"在当前选关页读取作品 {work.Work} 的进度，目标 HARD {work.HardCount}…"));
                // 战斗返回的页面证据决定是否重入；识别失败不能触发每关回主界面的隐式兜底。
                var node = needsWorkNavigation ? await _openWork(work, ct) : await _readCurrentWork(work, ct);
                ct.ThrowIfCancellationRequested();
                if (_advanceAfterBattle is not null && lastBattlePosition is not null &&
                    node.Position == lastBattlePosition && node.State != WorkStageNodeState.Locked && !confirmingCompletion)
                {
                    report(L.Format($"结算后仍选中上次出击的 {node.Kind} {node.Number}，识别并选择下一关，避免重复出击…"));
                    node = await _advanceAfterBattle(work, node, ct);
                    ct.ThrowIfCancellationRequested();
                }
                if (node.State == WorkStageNodeState.Locked)
                    return new(false, completedWorks, completedStages, L.Text("当前关卡已确认锁定，已停止。"));
                var position = node.Position;
                var known = node.State is WorkStageNodeState.Incomplete or WorkStageNodeState.Completed or WorkStageNodeState.Cleared &&
                    position is not null && node.Number is int value &&
                    WorkStageSequencePlanner.TryGetPosition(work.NormalCount, work.SideStories, work.HardCount,
                        node.Kind, value, out var expected) && expected == position;
                // 上一场已确认是最终 HARD 且完成结算，编号观察丢失不要求再次出击来证明完成。
                if (!known && confirmingCompletion)
                {
                    completedWorks++;
                    finished = true;
                    report(L.Format($"作品 {work.Work} 已完成已确认的 HARD {work.HardCount} 战斗结算。"));
                    break;
                }

                if (node.HasPassed)
                {
                    // 只有已通关证据才触发历史总览完成复核；单纯缺号继续跟随游戏推进，绝不据此停止。
                    if (!known && _restoreWork is not null && await _restoreWork(work, ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        completedWorks++; finished = true;
                        report(L.Format($"作品 {work.Work} 已通过节点缺号，已用总览关卡攻略度 100% 确认完成。"));
                        break;
                    }
                    if (known && position!.OverallIndex == total && node.Kind == WorkStageKind.Hard && node.Number == work.HardCount)
                    {
                        completedWorks++;
                        finished = true;
                        report(L.Format($"作品 {work.Work} 已确认完成 HARD {node.Number}。"));
                        break;
                    }
                    return new(false, completedWorks, completedStages,
                        L.Format($"当前选中 {node.Kind} {node.Number} 已完成，但未确认可点击的 NEXT 未完成关卡；已停止，不重复刷取。"));
                }
                // 原页可能由游戏直接选中，也可能经 NEXT 点击后确认；编号不必连续，只拦截重复出击。
                if (known && completedPositions.Contains(position!))
                    return new(false, completedWorks, completedStages, L.Text("本轮关卡进度没有前进，已停止重复出击。"));
                // 编号可能持续未知，仍以作品关卡总数限制实际战斗次数，不能无限消耗 AP 或把次数当作通关证据。
                if (workBattles >= total)
                    return new(false, completedWorks, completedStages, L.Text("已达到当前作品的战斗次数上限，尚未确认作品完成，停止继续消耗 AP。"));

                // 必须先记录关卡再进入准备页；编制恢复过程中不改变当前战斗的归属。
                report(known
                    ? L.Format($"记录本轮：{work.Work} / {node.Kind} {node.Number}（{position!.OverallIndex}/{total}），准备出击。")
                    : L.Format($"{work.Work} 当前编号未识别，跟随游戏当前选择检查出击入口（不推算关卡编号）。"));
                var battle = await _runStage(known ? node : StageNodeRecognition.Unidentified, ct);
                ct.ThrowIfCancellationRequested();
                // 恢复返回导航页并不能证明上一场通关。保留队列、去重集合及计数，只重新观察当前作品。
                // 恢复次数单独有界，不能借重建单关流程获得无限次重新出击机会。
                if (battle.ResumeRequested && !battle.Completed)
                {
                    if (++recoveries > 3)
                        return new(false, completedWorks, completedStages, L.Text("当前作品连续恢复仍无法确认推进，已停止重复尝试。"));
                    needsWorkNavigation = battle.NeedsWorkNavigation;
                    report(L.Format($"恢复当前作品 {work.Work}（{recoveries}/3），保留本次已完成 {completedStages} 场记录，重新读取实际进度…"));
                    continue;
                }
                if (!battle.Completed) return new(false, completedWorks, completedStages, battle.Message);
                needsWorkNavigation = battle.NeedsWorkNavigation;
                if (known) completedPositions.Add(position!);
                lastBattlePosition = known ? position : null;
                completedStages++;
                workBattles++;
                // 普通终点的系列奖励包只表示 NORMAL 已结束，作品仍要继续自己的 HARD。
                if (known && node.Kind == WorkStageKind.Boss && node.Number == work.NormalCount)
                    report(L.Format($"作品 {work.Work} 最后一个 NORMAL 关卡已推进，继续同一作品的 HARD；确认 HARD {work.HardCount} 通关后才完成作品。"));
            }
            if (!finished) return new(false, completedWorks, completedStages, L.Text("作品推进次数超过关卡总数，已停止。"));
        }
        return new(true, completedWorks, completedStages, L.Format($"自动推图完成：{completedWorks} 个作品，本次完成 {completedStages} 场战斗。"));
    }
}

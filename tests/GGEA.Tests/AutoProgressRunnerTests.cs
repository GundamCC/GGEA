using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>队列只以重新识别到的进度判断完成，防止一场战斗后错误宣告整部作品完成。</summary>
public sealed class AutoProgressRunnerTests
{
    private static readonly HardStageEntry Work = new() { Id = 1, Work = "测试作品", NormalCount = 2, HardCount = 1 };

    /// <summary>历史关卡由游戏自动推进；战后缺号不触发总览恢复或停止，仍须真实入口与最终完成证据。</summary>
    [Fact]
    public async Task Run_HistoryUnknownAfterBattle_FollowsCurrentSelection()
    {
        var battles = new List<StageNodeRecognition>();
        var nodes = new Queue<StageNodeRecognition>([
            Node(WorkStageKind.Normal, 1), StageNodeRecognition.Unidentified,
            Node(WorkStageKind.Hard, 1, WorkStageNodeState.Completed)]);
        var runner = new AutoProgressRunner((_, _) => Task.FromResult(nodes.Dequeue()),
            (_, _) => Task.FromResult(nodes.Dequeue()), (node, _) =>
            {
                battles.Add(node);
                return Task.FromResult(new AutoProgressBattleResult(true, "settled"));
            }, restoreWork: (_, _) => throw new InvalidOperationException("缺号不能触发总览恢复"));
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(2, result.CompletedStages);
        Assert.Equal(2, battles.Count);
        Assert.Equal(StageNodeRecognition.Unidentified, battles[1]);
    }

    /// <summary>已通过状态可独立于编号存在，历史终点以总览 100% 收口；未达 100% 不能冒充完成或重打。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_HistoryPassedWithoutNumber_ConfirmsOverview(bool complete)
    {
        var passed = StageNodeRecognition.Unidentified with { State = WorkStageNodeState.Completed };
        var confirmations = 0;
        var runner = new AutoProgressRunner((_, _) => Task.FromResult(passed),
            (_, _) => Task.FromResult(passed), (_, _) => throw new InvalidOperationException("不得重打已通过关卡"),
            restoreWork: (_, _) => { confirmations++; return Task.FromResult(complete); });
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.Equal(complete, result.Completed);
        Assert.Equal(complete ? 1 : 0, result.CompletedWorks);
        Assert.Equal(0, result.CompletedStages);
        Assert.Equal(1, confirmations);
    }

    /// <summary>恢复后保留前一场计数及当前作品，按真实节点继续；恢复本身既不计通关也不切换作品。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_RecoveryPreservesQueueAndCompletedStages(bool navigate)
    {
        var nodes = new Queue<StageNodeRecognition>(new[] { Node(WorkStageKind.Normal, 1),
            Node(WorkStageKind.Boss, 2), Node(WorkStageKind.Hard, 1), Node(WorkStageKind.Hard, 1, WorkStageNodeState.Completed) });
        var opens = 0;
        var stages = 0;
        var runner = new AutoProgressRunner((_, _) => { opens++; return Task.FromResult(nodes.Dequeue()); },
            (_, _) => Task.FromResult(nodes.Dequeue()), (_, _) =>
            {
                stages++;
                return Task.FromResult(stages == 2
                    ? new AutoProgressBattleResult(false, "恢复", navigate, ResumeRequested: true)
                    : new AutoProgressBattleResult(true, "真实结算"));
            });
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(1, result.CompletedWorks);
        Assert.Equal(2, result.CompletedStages);
        Assert.Equal(navigate ? 2 : 1, opens);
        Assert.Equal(3, stages);
        Assert.Empty(nodes);
    }

    /// <summary>同一作品一直请求恢复不能无限重新出击；已经完成的关卡也不能借恢复清除去重记录。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_RecoveryPreservesRetryAndDuplicateBounds(bool duplicate)
    {
        var calls = 0;
        var reads = 0;
        var runner = new AutoProgressRunner((_, _) => Task.FromResult(Node(WorkStageKind.Normal, 1)),
            (_, _) => { reads++; return Task.FromResult(Node(duplicate && reads == 1 ? WorkStageKind.Boss : WorkStageKind.Normal, duplicate && reads == 1 ? 2 : 1)); },
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new AutoProgressBattleResult(duplicate && calls == 1, "恢复",
                    ResumeRequested: !(duplicate && calls == 1)));
            });
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.False(result.Completed);
        Assert.Equal(duplicate ? 1 : 0, result.CompletedStages);
        Assert.Equal(duplicate ? 2 : 4, calls);
        Assert.Contains(duplicate ? "重复出击" : "恢复", result.Message);
    }

    /// <summary>最后一个普通关卡返回奖励包后仍留在原作品，三个 HARD 都推进并确认终点后才切换作品。</summary>
    [Theory]
    [InlineData(20, true)]
    [InlineData(23, false)]
    public async Task Run_LastNormalRewardReturn_KeepsWorkUntilFinalHardConfirmed(int normalCount, bool returnedOutsideWork)
    {
        var work = new HardStageEntry { Id = 20, Work = "普通关卡终点作品", NormalCount = normalCount, HardCount = 3 };
        var next = new HardStageEntry { Id = 21, Work = "下一作品", NormalCount = 1, HardCount = 1 };
        StageNodeRecognition Recognized(HardStageEntry target, WorkStageKind kind, int number,
            WorkStageNodeState state = WorkStageNodeState.Incomplete)
        {
            Assert.True(WorkStageSequencePlanner.TryGetPosition(target.NormalCount, target.SideStories,
                target.HardCount, kind, number, out var position));
            return new(400, 800, kind, number, state, position, "测试识别");
        }
        var nodes = new Queue<StageNodeRecognition>(new[] {
            Recognized(work, WorkStageKind.Boss, normalCount),
            Recognized(work, WorkStageKind.Hard, 1), Recognized(work, WorkStageKind.Hard, 2),
            Recognized(work, WorkStageKind.Hard, 3),
            Recognized(work, WorkStageKind.Hard, 3, WorkStageNodeState.Completed),
            Recognized(next, WorkStageKind.Hard, 1, WorkStageNodeState.Completed) });
        var visits = new List<int>();
        var reads = new List<int>();
        var battles = new List<(WorkStageKind Kind, int? Number)>();
        var reports = new List<string>();
        var runner = new AutoProgressRunner((target, _) => {
            visits.Add(target.Id);
            return Task.FromResult(nodes.Dequeue());
        }, (target, _) => {
            reads.Add(target.Id);
            return Task.FromResult(nodes.Dequeue());
        }, (node, _) => {
            battles.Add((node.Kind, node.Number));
            return Task.FromResult(new AutoProgressBattleResult(true, "已关闭普通关卡奖励包或返回选关页",
                NeedsWorkNavigation: node.Kind == WorkStageKind.Boss && returnedOutsideWork));
        });

        var result = await runner.RunAsync(new[] { work, next }, reports.Add, default);

        Assert.True(result.Completed);
        Assert.Equal(2, result.CompletedWorks);
        Assert.Equal(4, result.CompletedStages);
        Assert.Equal(returnedOutsideWork ? new[] { 20, 20, 21 } : new[] { 20, 21 }, visits);
        Assert.Equal(Enumerable.Repeat(20, returnedOutsideWork ? 3 : 4), reads);
        Assert.Equal(new (WorkStageKind, int?)[] {
            (WorkStageKind.Boss, normalCount), (WorkStageKind.Hard, 1),
            (WorkStageKind.Hard, 2), (WorkStageKind.Hard, 3) }, battles);
        Assert.True(reports.FindIndex(message => message.Contains("继续同一作品的 HARD")) <
            reports.FindIndex(message => message.Contains("已确认完成 HARD 3")));
        Assert.Contains(reports, message => message.Contains("继续同一作品的 HARD"));
        Assert.Empty(nodes);
    }

    /// <summary>连续普通、BOSS、HARD 均原地读取；最终 HARD 在原页复核后才完成作品，入口只调用一次。</summary>
    [Fact]
    public async Task Run_ConfirmsFinalHardAfterBattle()
    {
        var nodes = new Queue<StageNodeRecognition>(new[] {
            Node(WorkStageKind.Normal, 1), Node(WorkStageKind.Boss, 2),
            Node(WorkStageKind.Hard, 1), Node(WorkStageKind.Hard, 1, WorkStageNodeState.Completed) });
        var calls = 0;
        var opens = 0;
        var reads = 0;
        var reports = new List<string>();
        var runner = new AutoProgressRunner((_, _) => { opens++; return Task.FromResult(nodes.Dequeue()); },
            (_, _) => { reads++; return Task.FromResult(nodes.Dequeue()); },
            (_, _) => { calls++; return Task.FromResult(new AutoProgressBattleResult(true, "完成")); });
        var result = await runner.RunAsync(new[] { Work }, reports.Add, default);
        Assert.True(result.Completed);
        Assert.Equal(1, result.CompletedWorks);
        Assert.Equal(3, result.CompletedStages);
        Assert.Equal(3, calls);
        Assert.Equal(1, opens);
        Assert.Equal(3, reads);
        Assert.Contains(reports, message => message.Contains("通关状态（本关已结算，不重复出击）"));
        Assert.Empty(nodes);
    }

    /// <summary>游戏选中的关卡允许跳号或回到较小编号，不以与上一轮的差值决定能否出击。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_NonConsecutiveSelection_UsesCurrentNode(bool lowerNumber)
    {
        var sequence = new List<StageNodeRecognition>();
        if (lowerNumber) sequence.Add(Node(WorkStageKind.Boss, 2));
        sequence.Add(Node(WorkStageKind.Normal, 1));
        sequence.Add(Node(WorkStageKind.Hard, 1));
        var expected = sequence.Select(node => node.Position).ToArray();
        sequence.Add(Node(WorkStageKind.Hard, 1, WorkStageNodeState.Completed));
        var nodes = new Queue<StageNodeRecognition>(sequence);
        var battles = new List<WorkStagePosition?>();
        var runner = new AutoProgressRunner((_, _) => Task.FromResult(nodes.Dequeue()),
            (_, _) => Task.FromResult(nodes.Dequeue()),
            (node, _) => { battles.Add(node.Position); return Task.FromResult(new AutoProgressBattleResult(true, "完成")); });
        var result = await runner.RunAsync(new[] { Work }, _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(expected, battles);
        Assert.Empty(nodes);
    }

    /// <summary>确认相同未完成节点、已完成非终点或锁定状态时不得再次战斗。</summary>
    [Theory]
    [InlineData(WorkStageNodeState.Incomplete, 1)]
    [InlineData(WorkStageNodeState.Completed, 0)]
    [InlineData(WorkStageNodeState.Locked, 0)]
    public async Task Run_UnconfirmedProgress_Stops(WorkStageNodeState state, int expectedBattles)
    {
        var calls = 0;
        var runner = new AutoProgressRunner((_, _) => Task.FromResult(Node(WorkStageKind.Normal, 1, state)),
            (_, _) => Task.FromResult(Node(WorkStageKind.Normal, 1, state)),
            (_, _) => { calls++; return Task.FromResult(new AutoProgressBattleResult(true, "完成")); });
        var result = await runner.RunAsync(new[] { Work }, _ => { }, default);
        Assert.False(result.Completed);
        Assert.Equal(expectedBattles, calls);
        Assert.Equal(0, result.CompletedWorks);
    }

    /// <summary>最终 HARD 确认完成后才导航下一个作品；新作品从自己的识别序号重新推进。</summary>
    [Fact]
    public async Task Run_FinalHardComplete_OpensNextWorkInQueueOrder()
    {
        var nextWork = new HardStageEntry { Id = 2, Work = "下一作品", NormalCount = 2, HardCount = 1 };
        var visits = new List<int>();
        var reads = new List<int>();
        var kinds = new List<WorkStageKind>();
        var nodes = new Queue<StageNodeRecognition>(new[] {
            Node(WorkStageKind.Hard, 1), Node(WorkStageKind.Hard, 1, WorkStageNodeState.Completed),
            Node(WorkStageKind.Normal, 1), Node(WorkStageKind.Boss, 2),
            Node(WorkStageKind.Hard, 1), Node(WorkStageKind.Hard, 1, WorkStageNodeState.Completed) });
        var runner = new AutoProgressRunner((work, _) =>
            {
                visits.Add(work.Id);
                return Task.FromResult(nodes.Dequeue());
            }, (work, _) =>
            {
                reads.Add(work.Id);
                return Task.FromResult(nodes.Dequeue());
            }, (node, _) =>
            {
                kinds.Add(node.Kind);
                return Task.FromResult(new AutoProgressBattleResult(true, "完成"));
            });
        var result = await runner.RunAsync(new[] { Work, nextWork }, _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(2, result.CompletedWorks);
        Assert.Equal(4, result.CompletedStages);
        Assert.Equal(new[] { 1, 2 }, visits);
        Assert.Equal(new[] { 1, 2, 2, 2 }, reads);
        Assert.Equal(new[] { WorkStageKind.Hard, WorkStageKind.Normal, WorkStageKind.Boss, WorkStageKind.Hard }, kinds);
    }

    /// <summary>本关结算后取消必须先于原页续接或重入，不能再发送下一轮导航。</summary>
    [Fact]
    public async Task Run_CancelAfterSettlement_DoesNotReadOrReopenWork()
    {
        using var cancellation = new CancellationTokenSource();
        var opens = 0;
        var runner = new AutoProgressRunner((_, _) =>
            {
                opens++;
                return Task.FromResult(Node(WorkStageKind.Normal, 1));
            }, (_, _) => throw new InvalidOperationException("取消后不得继续读取"), (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(new AutoProgressBattleResult(true, "结算完成"));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync([Work], _ => { }, cancellation.Token));
        Assert.Equal(1, opens);
    }

    /// <summary>关卡观察缺失不终止原页接续，实际执行未知节点但不按上一关加一；终点仍须真实确认。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_UnidentifiedStageContinuesWithoutInventingNumber(bool unknownAtEntry)
    {
        var nodes = new Queue<StageNodeRecognition>(unknownAtEntry
            ? [StageNodeRecognition.Unidentified, Node(WorkStageKind.Hard, 1, WorkStageNodeState.Completed)]
            : [Node(WorkStageKind.Normal, 1), StageNodeRecognition.Unidentified,
                Node(WorkStageKind.Hard, 1, WorkStageNodeState.Completed)]);
        var battles = new List<StageNodeRecognition>();
        var opens = 0;
        var runner = new AutoProgressRunner((_, _) => { opens++; return Task.FromResult(nodes.Dequeue()); },
            (_, _) => Task.FromResult(nodes.Dequeue()), (node, _) =>
            { battles.Add(node); return Task.FromResult(new AutoProgressBattleResult(true, "实际结算完成")); });
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(1, result.CompletedWorks);
        Assert.Equal(unknownAtEntry ? 1 : 2, result.CompletedStages);
        Assert.Equal(StageNodeRecognition.Unidentified, battles[^1]);
        Assert.Equal(1, opens);
        Assert.Empty(nodes);
    }

    /// <summary>未知编号的真实出击失败仍停止；仅有未知战斗次数也不能宣布作品完成或无限刷取。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_UnidentifiedStagePreservesFailureAndBound(bool battleFails)
    {
        var battles = 0;
        var runner = new AutoProgressRunner((_, _) => Task.FromResult(StageNodeRecognition.Unidentified),
            (_, _) => Task.FromResult(StageNodeRecognition.Unidentified), (_, _) =>
            { battles++; return Task.FromResult(new AutoProgressBattleResult(!battleFails, "出击入口不可用")); });
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.False(result.Completed);
        Assert.Equal(0, result.CompletedWorks);
        Assert.Equal(battleFails ? 1 : 3, battles);
        Assert.Equal(battleFails ? 0 : 3, result.CompletedStages);
        Assert.Contains(battleFails ? "出击入口不可用" : "战斗次数上限", result.Message);
    }

    /// <summary>已确认最终 HARD 且本场完成结算后，下一帧编号未知不能诱发重复出击或报告失败。</summary>
    [Fact]
    public async Task Run_FinalHardSettledDoesNotRequireAnotherReadableNumber()
    {
        var battles = 0;
        var runner = new AutoProgressRunner((_, _) => Task.FromResult(Node(WorkStageKind.Hard, 1)),
            (_, _) => Task.FromResult(StageNodeRecognition.Unidentified), (_, _) =>
            { battles++; return Task.FromResult(new AutoProgressBattleResult(true, "实际结算完成")); });
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(1, result.CompletedWorks);
        Assert.Equal(1, result.CompletedStages);
        Assert.Equal(1, battles);
    }

    /// <summary>只有成功结算后再次读到同一编号才要求手动推进；不依赖 CLEAR 或 COMPLETE 字样，也不重复战斗。</summary>
    [Theory]
    [InlineData(WorkStageNodeState.Cleared)]
    [InlineData(WorkStageNodeState.Incomplete)]
    public async Task Run_RepeatedSettledStage_RequestsAdvanceBeforeNextBattle(WorkStageNodeState returnedState)
    {
        var reads = 0; var advances = 0;
        var battles = new List<WorkStagePosition>();
        var runner = new AutoProgressRunner((_, _) => Task.FromResult(Node(WorkStageKind.Normal, 1)),
            (_, _) => Task.FromResult(++reads == 1 ? Node(WorkStageKind.Normal, 1, returnedState)
                : Node(WorkStageKind.Hard, 1, WorkStageNodeState.Cleared)),
            (node, _) => { battles.Add(node.Position!); return Task.FromResult(new AutoProgressBattleResult(true, "结算完成")); },
            (_, node, _) => { advances++; Assert.Equal(1, node.Number); return Task.FromResult(Node(WorkStageKind.Boss, 2)); });
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(1, advances);
        Assert.Equal(new[] { Node(WorkStageKind.Normal, 1).Position, Node(WorkStageKind.Boss, 2).Position }, battles);
        Assert.Equal(2, result.CompletedStages);
    }

    /// <summary>推进没有改变实际编号时仍停止，不能把调用推进动作本身视为成功。</summary>
    [Fact]
    public async Task Run_AdvanceDidNotChangeStage_DoesNotRepeatBattle()
    {
        var battles = 0;
        var runner = new AutoProgressRunner((_, _) => Task.FromResult(Node(WorkStageKind.Normal, 1)),
            (_, _) => Task.FromResult(Node(WorkStageKind.Normal, 1)),
            (_, _) => { battles++; return Task.FromResult(new AutoProgressBattleResult(true, "结算完成")); },
            (_, node, _) => Task.FromResult(node));
        var result = await runner.RunAsync([Work], _ => { }, default);
        Assert.False(result.Completed);
        Assert.Equal(1, battles);
        Assert.Contains("重复出击", result.Message);
    }

    /// <summary>生成与作品配置一致的节点位置，模拟识别器的可信返回值。</summary>
    private static StageNodeRecognition Node(WorkStageKind kind, int number, WorkStageNodeState state = WorkStageNodeState.Incomplete)
    {
        Assert.True(WorkStageSequencePlanner.TryGetPosition(Work.NormalCount, Work.SideStories, Work.HardCount, kind, number, out var position));
        return new(400, 800, kind, number, state, position, "测试识别");
    }
}

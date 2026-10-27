using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>验证局部推进的操作次数与安全边界：遇到可用关立即出击，战后只复核当前关。</summary>
public sealed class HistoryBossLocalProgressTests
{
    /// <summary>第二关攻略前不读取任何高难度；前一关完成后不再回读。</summary>
    [Fact]
    public async Task Runner_AttacksFirstIncompleteBeforeReadingHigherStages()
    {
        var calls = new List<string>(); var passed = new HashSet<int> { 1 };
        var runner = new HistoryBossProgressRunner((order, _) =>
        {
            calls.Add($"read{order}");
            return Task.FromResult(new HistoryBossNode(order, passed.Contains(order)
                ? FierceEnemyProgressState.Passed : FierceEnemyProgressState.Available));
        }, (order, _) =>
        {
            calls.Add($"battle{order}"); passed.Add(order);
            return Task.FromResult(new AutoProgressBattleResult(true, "完成"));
        });
        var result = await runner.RunAsync("测试", _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(5, result.CompletedStages);
        Assert.Equal(new[] { "read1", "read2", "battle2", "read2", "read3", "battle3", "read3",
            "read4", "battle4", "read4", "read5", "battle5", "read5", "read6", "battle6", "read6" }, calls);
    }

    /// <summary>从底部起步，每对两关与战后确认均不滚动，只有跨难度时上移。</summary>
    [Fact]
    public async Task Locator_StaysInViewportUntilNextDifficulty()
    {
        var group = 0; var reads = 0; var moves = new List<bool>();
        var locator = new HistoryBossLocator(_ => { reads++; return Task.FromResult(Pair(group)); },
            (movement, _) => { moves.Add(movement < 0); group += movement < 0 ? -1 : 1; return Task.CompletedTask; });
        foreach (var order in Enumerable.Range(1, 6))
        {
            await locator.FindAsync(order, default);
            await locator.FindAsync(order, default); // 战后仍位于本难度对。
        }
        Assert.Equal(new[] { false, false }, moves);
        Assert.Equal(14, reads); // 十二次目标读取加两次发现需要跨视口。
    }

    /// <summary>游戏若在结算后重置到顶部，可按当前难度向下找回原关，不会把另一关当成战后完成。</summary>
    [Fact]
    public async Task Locator_RecoversFromGameResetWithoutFullInventory()
    {
        var group = 2; var moves = new List<bool>();
        var locator = new HistoryBossLocator(_ => Task.FromResult(Pair(group)),
            (movement, _) => { moves.Add(movement < 0); group += movement < 0 ? -1 : 1; return Task.CompletedTask; });
        var card = await locator.FindAsync(2, default);
        Assert.Equal(FierceEnemyDifficulty.NORMAL, card.Difficulty);
        Assert.Equal(220, card.Label.Y);
        Assert.Equal(new[] { true, true }, moves);
    }

    /// <summary>目标难度对已出现时，按目标高度调整一次；不能受旁边高难度影响反向搜索。</summary>
    [Theory]
    [InlineData(3, 569, 917, -255)]
    [InlineData(4, 170, 518, 55)]
    public async Task Locator_ClippedTargetUsesLabelHeight(int order, int upperY, int lowerY, int expectedMovement)
    {
        var offset = 0; var moves = new List<int>(); var reads = 0;
        var locator = new HistoryBossLocator(_ =>
        {
            reads++;
            return Task.FromResult<IReadOnlyList<HistoryBossCard>>([
                new(FierceEnemyDifficulty.EXPERT, FierceEnemyProgressState.Locked, new(1045, 221, 100, 25), null),
                new(FierceEnemyDifficulty.HARD, FierceEnemyProgressState.Unknown, new(1045, upperY + offset, 100, 25), null),
                new(FierceEnemyDifficulty.HARD, offset == 0 ? FierceEnemyProgressState.Unknown : FierceEnemyProgressState.Available,
                    new(1045, lowerY + offset, 100, 25), null)]);
        }, (movement, _) => { moves.Add(movement); offset += movement; return Task.CompletedTask; });
        var target = await locator.FindAsync(order, default);
        Assert.True(target.IsFullyVisible);
        Assert.Equal(new[] { expectedMovement }, moves);
        Assert.Equal(2, reads);
    }

    /// <summary>只见一个底部目标标签时先居中，再用新帧的两标签定位，不按另一个难度来回滚动。</summary>
    [Fact]
    public async Task Locator_SingleBottomLabelCentersBeforeChoosingSlot()
    {
        var moves = new List<int>();
        var locator = new HistoryBossLocator(_ => Task.FromResult<IReadOnlyList<HistoryBossCard>>(moves.Count == 0
            ? [new(FierceEnemyDifficulty.HARD, FierceEnemyProgressState.Unknown, new(1045, 920, 100, 25), null)]
            : [new(FierceEnemyDifficulty.HARD, FierceEnemyProgressState.Unknown, new(1045, 192, 100, 25), null),
                new(FierceEnemyDifficulty.HARD, FierceEnemyProgressState.Available, new(1045, 540, 100, 25), null)]),
            (movement, _) => { moves.Add(movement); return Task.CompletedTask; });
        var target = await locator.FindAsync(3, default);
        Assert.Equal(540, target.Label.Y);
        Assert.Equal(new[] { -380 }, moves);
    }

    /// <summary>锁定、未知及观察序号错位均不能触发挑战。</summary>
    [Theory]
    [InlineData(FierceEnemyProgressState.Locked, 1)]
    [InlineData(FierceEnemyProgressState.Unknown, 1)]
    [InlineData(FierceEnemyProgressState.Available, 2)]
    public async Task Runner_InvalidNextStageStops(FierceEnemyProgressState state, int observed)
    {
        var runner = new HistoryBossProgressRunner((_, _) => Task.FromResult(new HistoryBossNode(observed, state)),
            (_, _) => throw new InvalidOperationException("不得挑战"));
        Assert.False((await runner.RunAsync("测试", _ => { }, default)).Completed);
    }

    /// <summary>位置尚未确认时取消必须阻止下一次滚动。</summary>
    [Fact]
    public async Task Locator_CancellationAfterReadDoesNotScroll()
    {
        using var cancellation = new CancellationTokenSource();
        var locator = new HistoryBossLocator(_ => { cancellation.Cancel(); return Task.FromResult(Pair(2)); },
            (_, _) => throw new InvalidOperationException("不得滚动"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locator.FindAsync(1, cancellation.Token));
    }

    /// <summary>可控难度对代表两个独立位置，不依赖文字标题或全表清单。</summary>
    private static IReadOnlyList<HistoryBossCard> Pair(int rank)
    {
        var difficulty = rank switch { 0 => FierceEnemyDifficulty.NORMAL, 1 => FierceEnemyDifficulty.HARD,
            2 => FierceEnemyDifficulty.EXPERT, _ => throw new ArgumentOutOfRangeException(nameof(rank)) };
        return new[] { new HistoryBossCard(difficulty, FierceEnemyProgressState.Available, new(1045, 220, 100, 25), null),
            new HistoryBossCard(difficulty, FierceEnemyProgressState.Available, new(1045, 568, 100, 25), null) };
    }
}

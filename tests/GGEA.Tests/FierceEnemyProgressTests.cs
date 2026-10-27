using System.IO;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>用户截图回放与推进策略回归，保护锁定、已通关及不重复消耗 AP 的边界。</summary>
public sealed class FierceEnemyProgressTests
{
    /// <summary>原像素的锁定 HARD 不可点，亮态 NORMAL 是唯一可推进目标。</summary>
    [Fact]
    public void LockedHardAndAvailableNormal_RecognizedFromUserFrame()
    {
        var nodes = Recognize("fierce-progress-locked.png");
        Assert.Equal(FierceEnemyProgressState.Locked, nodes.Single(x => x.Difficulty == FierceEnemyDifficulty.HARD).State);
        var normal = nodes.Single(x => x.Difficulty == FierceEnemyDifficulty.NORMAL);
        Assert.Equal(FierceEnemyProgressState.Available, normal.State);
        Assert.NotNull(normal.Challenge);
    }

    /// <summary>EXPERT 的 CLEAR 与 HARD 的 COMPLETE 都表示通关，不会因为星数不足再次挑战。</summary>
    [Fact]
    public void ClearAndComplete_AreBothPassed()
    {
        var nodes = Recognize("fierce-progress-cleared.png");
        Assert.Equal(FierceEnemyProgressState.Passed, nodes.Single(x => x.Difficulty == FierceEnemyDifficulty.EXPERT).State);
        Assert.Equal(FierceEnemyProgressState.Passed, nodes.Single(x => x.Difficulty == FierceEnemyDifficulty.HARD).State);
        Assert.All(nodes, node => Assert.Null(node.Challenge));
    }

    /// <summary>逐轮观察解锁证据后才推进，三个难度各执行一次，并以 EX 标记结束。</summary>
    [Fact]
    public async Task FreshEnemy_ProgressesNormalHardExpert()
    {
        var done = new HashSet<FierceEnemyDifficulty>();
        var battles = new List<FierceEnemyDifficulty>();
        var runner = new FierceEnemyProgressRunner(_ => Task.FromResult<IReadOnlyList<FierceEnemyProgressObservation>>(
            Enum.GetValues<FierceEnemyDifficulty>().Select(d => Observe(d, done.Contains(d)
                ? FierceEnemyProgressState.Passed : d == FierceEnemyDifficulty.NORMAL || done.Contains(d + 1)
                    ? FierceEnemyProgressState.Available : FierceEnemyProgressState.Locked)).ToArray()),
            (d, _) => { battles.Add(d); done.Add(d); return Task.FromResult(new AutoProgressBattleResult(true, "结算完成")); });
        var result = await runner.RunAsync("测试强敌", _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(3, result.CompletedStages);
        Assert.Equal(new[] { FierceEnemyDifficulty.NORMAL, FierceEnemyDifficulty.HARD, FierceEnemyDifficulty.EXPERT }, battles);
    }

    /// <summary>已有 EX 通关标记时零战斗结束；未知高难度不可跳过后猜测 NORMAL。</summary>
    [Fact]
    public async Task PassedExpert_NoBattle_UnknownHigherBlocksSelection()
    {
        var runner = new FierceEnemyProgressRunner(_ => Task.FromResult<IReadOnlyList<FierceEnemyProgressObservation>>(
            new[] { Observe(FierceEnemyDifficulty.EXPERT, FierceEnemyProgressState.Passed) }),
            (_, _) => throw new InvalidOperationException("已通关不得再战斗"));
        var result = await runner.RunAsync("已完成", _ => { }, default);
        Assert.True(result.Completed);
        Assert.Equal(0, result.CompletedStages);
        Assert.Null(FierceEnemyProgressRunner.Select(new[] { Observe(FierceEnemyDifficulty.NORMAL, FierceEnemyProgressState.Available) }));
        Assert.Equal(FierceEnemyDifficulty.EXPERT, FierceEnemyProgressRunner.Select(
            Enum.GetValues<FierceEnemyDifficulty>().Select(d => Observe(d, FierceEnemyProgressState.Available)).ToArray()));
    }

    /// <summary>战斗返回而标记未改变时只战斗一次；取消也不能继续扫描或点击。</summary>
    [Fact]
    public async Task UnchangedProgress_StopsWithoutRepeating_AndHonorsCancellation()
    {
        var count = 0;
        var runner = new FierceEnemyProgressRunner(_ => Task.FromResult<IReadOnlyList<FierceEnemyProgressObservation>>(
            Enum.GetValues<FierceEnemyDifficulty>().Select(d => Observe(d,
                d == FierceEnemyDifficulty.NORMAL ? FierceEnemyProgressState.Available : FierceEnemyProgressState.Locked)).ToArray()),
            (_, _) => { count++; return Task.FromResult(new AutoProgressBattleResult(true, "结算完成")); });
        var result = await runner.RunAsync("未推进", _ => { }, default);
        Assert.False(result.Completed);
        Assert.Equal(1, count);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("取消", _ => { }, cts.Token));
        Assert.Equal(1, count);
    }

    /// <summary>将用户提供的右侧裁剪以原始比例放回标准画布，空白部分不冒充实机截图。</summary>
    private static IReadOnlyList<FierceEnemyProgressObservation> Recognize(string name)
    {
        var panel = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation", name)));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        for (var y = 0; y < panel.Height; y++)
            Buffer.BlockCopy(panel.Bgra, y * panel.Width * 4, pixels, ((168 + y) * 1920 + 966) * 4, panel.Width * 4);
        return new FierceEnemyProgressRecognizer(TapToNextOcrSamples.SharedOcr)
            .Read(ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080)), default);
    }

    /// <summary>策略测试只注入观察状态，不伪造按钮位置或生产识别结果。</summary>
    private static FierceEnemyProgressObservation Observe(FierceEnemyDifficulty difficulty, FierceEnemyProgressState state)
        => new(difficulty, state, null);
}

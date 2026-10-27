using GGEA.App.Tasks;

namespace GGEA.Tests;

/// <summary>批次推进、上限重试、最后清理和失败传播不依赖模拟器时序。</summary>
public sealed class BallFactoryRunnerTests
{
    /// <summary>第 17 批达上限后仍重试第 17 批，完成余数批次后再做最终清理。</summary>
    [Fact]
    public async Task Limit_RetriesSameBatch_AndFinallyCleans()
    {
        var quantities = new List<int>();
        var navigations = new List<bool>();
        var recovered = 0;
        var cleaned = 0;
        var runner = new BallFactoryRunner((quantity, locateTarget, _) =>
        {
            quantities.Add(quantity);
            navigations.Add(locateTarget);
            return Task.FromResult(quantities.Count == 17 ? DevelopResult.UnitLimitReached : DevelopResult.Completed);
        }, (reason, _) => { Assert.Equal(DevelopResult.UnitLimitReached, reason); recovered++; return Task.FromResult(true); },
            _ => { cleaned++; return Task.FromResult(3); });
        var result = await runner.RunAsync(11000, _ => { });
        Assert.True(result.Completed);
        Assert.Equal(550, result.DevelopedUnits);
        Assert.Equal(18, result.CompletedBatches);
        Assert.Equal(19, quantities.Count);
        Assert.All(quantities.Take(18), x => Assert.Equal(32, x));
        Assert.Equal(6, quantities[^1]);
        Assert.Equal(1, recovered);
        Assert.Equal(2, cleaned);
        Assert.Equal(6, result.DismantleRounds);
        Assert.True(navigations[0]);
        Assert.All(navigations.Skip(1).Take(16), Assert.False);
        Assert.True(navigations[17]); // 第 17 批解体后重试时才重新进入开发。
        Assert.False(navigations[18]);
    }

    /// <summary>金币不足、未知失败或恢复失败都不进入解体。</summary>
    [Theory]
    [InlineData(DevelopResult.InsufficientCoins, true, 1)]
    [InlineData(DevelopResult.UnitLimitReached, false, 1)]
    [InlineData(DevelopResult.Failed, true, 0)]
    public async Task Failure_DoesNotDismantle(DevelopResult state, bool recovered, int expectedRecoveries)
    {
        var recoveries = 0;
        var runner = new BallFactoryRunner((_, _, _) => Task.FromResult(state),
            (_, _) => { recoveries++; return Task.FromResult(recovered); },
            _ => throw new Exception("Must not dismantle"));
        var result = await runner.RunAsync(20, _ => { });
        Assert.False(result.Completed);
        Assert.Equal(0, result.DevelopedUnits);
        Assert.Equal(expectedRecoveries, recoveries);
    }

    /// <summary>无单位可清理或清理后仍满仓必须停止，不能无限重试原批次。</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task NoProgress_Stops(int rounds, int expectedAttempts)
    {
        var attempts = 0;
        var cleans = 0;
        var runner = new BallFactoryRunner((_, _, _) => { attempts++; return Task.FromResult(DevelopResult.UnitLimitReached); },
            (_, _) => Task.FromResult(true), _ => { cleans++; return Task.FromResult(rounds); });
        Assert.False((await runner.RunAsync(20, _ => { })).Completed);
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal(1, cleans);
    }

    /// <summary>最终清理失败不报告完成；取消时不得开始任何批次。</summary>
    [Fact]
    public async Task FinalCleanupFailureAndCancellation_Propagate()
    {
        var runner = new BallFactoryRunner((_, _, _) => Task.FromResult(DevelopResult.Completed),
            (_, _) => throw new Exception("Unexpected recovery"), _ => throw new InvalidOperationException("empty not confirmed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(20, _ => { }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(20, _ => { }, new CancellationToken(true)));
    }
}

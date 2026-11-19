using GGEA.App.Tasks;

namespace GGEA.Tests;

public sealed class AutoFarmRunnerTests
{
    [Fact]
    public async Task ThreeRounds_StartOnceAndExitOnlyOnLastSettlement()
    {
        var calls = new List<string>();
        var result = await AutoFarmRunner.RunAsync(3,
            _ => { calls.Add("start"); return Task.FromResult(true); },
            (more, _) => { calls.Add($"first:{more}"); return Task.FromResult(SortieFlowResult.RoundCompleted); },
            (more, _) => { calls.Add($"next:{more}"); return Task.FromResult(more ? SortieFlowResult.RoundCompleted : SortieFlowResult.AllRoundsCompleted); },
            _ => { }, default);
        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "start", "first:True", "next:True", "next:False" }, calls);
    }

    [Theory]
    [InlineData(false, SortieFlowResult.Stalled, true)]
    [InlineData(true, SortieFlowResult.Stalled, false)]
    [InlineData(true, SortieFlowResult.UnexpectedPreparation, true)]
    public async Task UnconfirmedRound_StopsWithoutStartingAnother(bool started, SortieFlowResult outcome, bool diagnose)
    {
        int rounds = 0;
        var result = await AutoFarmRunner.RunAsync(2, _ => Task.FromResult(started),
            (_, _) => { rounds++; return Task.FromResult(outcome); },
            (_, _) => throw new InvalidOperationException(), _ => { }, default);
        Assert.False(result.Succeeded);
        Assert.Equal(diagnose, result.CheckConnection);
        Assert.Equal(started ? 1 : 0, rounds);
    }

    [Fact]
    public async Task CancelAfterFirstSettlement_DoesNotEnterNextRound()
    {
        using var source = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutoFarmRunner.RunAsync(2,
            _ => Task.FromResult(true), (_, _) => { source.Cancel(); return Task.FromResult(SortieFlowResult.RoundCompleted); },
            (_, _) => throw new InvalidOperationException(), _ => { }, source.Token));
    }
}

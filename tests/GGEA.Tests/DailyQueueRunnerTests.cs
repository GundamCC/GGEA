using GGEA.App.Tasks;

namespace GGEA.Tests;

/// <summary>验证普通队列提前结束仍收尾，且不突破取消、异常和安全停止边界。</summary>
public class DailyQueueRunnerTests
{
    /// <summary>模拟 AP 不足提前返回与正常结束，队尾领取均只在普通任务结束后运行一次。</summary>
    [Theory]
    [InlineData(DailyQueueOutcome.Completed)]
    [InlineData(DailyQueueOutcome.Interrupted)]
    [InlineData(DailyQueueOutcome.Unsafe)]
    public async Task RunAsync_FinalizesAccordingToOutcome(DailyQueueOutcome outcome)
    {
        var calls = new List<string>();
        var result = await DailyQueueRunner.RunAsync(async () =>
        {
            calls.Add("普通任务");
            await Task.Yield();
            calls.Add("普通任务结束");
            return outcome;
        }, () =>
        {
            calls.Add("队尾使命奖励");
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(outcome, result);
        Assert.Equal(outcome == DailyQueueOutcome.Unsafe
            ? new[] { "普通任务", "普通任务结束" }
            : new[] { "普通任务", "普通任务结束", "队尾使命奖励" }, calls);
    }

    /// <summary>普通任务返回时刚好收到用户取消，也不能再启动领取。</summary>
    [Fact]
    public async Task RunAsync_CancelledBeforeFinalization_DoesNotClaim()
    {
        using var cts = new CancellationTokenSource();
        var claimed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DailyQueueRunner.RunAsync(() =>
        {
            cts.Cancel();
            return Task.FromResult(DailyQueueOutcome.Interrupted);
        }, () =>
        {
            claimed = true;
            return Task.CompletedTask;
        }, cts.Token));
        Assert.False(claimed);
    }

    /// <summary>设备或截图异常原样传播，不能用后续领取掩盖失败。</summary>
    [Fact]
    public async Task RunAsync_Exception_DoesNotClaim()
    {
        var error = new InvalidOperationException("设备操作失败");
        var claimed = false;
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => DailyQueueRunner.RunAsync(
            () => Task.FromException<DailyQueueOutcome>(error),
            () =>
            {
                claimed = true;
                return Task.CompletedTask;
            }, CancellationToken.None));
        Assert.Same(error, actual);
        Assert.False(claimed);
    }
}

using GGEA.App.UI;

namespace GGEA.Tests;

/// <summary>验证关窗先取消、等待完整 finally、超时保持停止且禁止重启。</summary>
public sealed class WindowTaskLifetimeTests
{
    /// <summary>正在清理的旧任务未完成前，不得将窗口标为可关闭或启动后续动作。</summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Stop_WaitsForCleanup_RejectsNewWork()
    {
        var lifetime = new WindowTaskLifetime();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = lifetime.RunAsync(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, lifetime.Token); }
            catch (OperationCanceledException) { canceled.SetResult(); }
            finally { await cleanup.Task; }
        });
        var stopping = lifetime.StopAsync(TimeSpan.FromMilliseconds(100));
        await canceled.Task;
        Assert.False(await stopping);
        Assert.False(task.IsCompleted);
        Assert.True(lifetime.IsStopping);
        var newAction = false;
        await lifetime.RunAsync(() => { newAction = true; return Task.CompletedTask; });
        Assert.False(newAction);
        cleanup.SetResult();
        await task;
        Assert.True(await lifetime.StopAsync(TimeSpan.FromSeconds(1)));
    }

    /// <summary>任务失败也必须移出活动集合，避免退出永久等待。</summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Failure_RemovesTask()
    {
        var lifetime = new WindowTaskLifetime();
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.RunAsync(() => throw new InvalidOperationException()));
        Assert.True(await lifetime.StopAsync(TimeSpan.FromSeconds(1)));
    }
}

using GGEA.App.UI;

namespace GGEA.Tests;

/// <summary>验证互斥门的所有权必须跨越取消与异步收尾，且旧租约不能释放新会话。</summary>
public sealed class DeviceExecutionGateTests
{
    /// <summary>取消发生后，只有完整清理退出才能开放下一次设备操作。</summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Cancellation_KeepsOwnershipUntilCleanupCompletes()
    {
        var gate = new DeviceExecutionGate();
        using var cancellation = new CancellationTokenSource();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        async Task Run()
        {
            using var lease = gate.TryEnter(() => releases++);
            Assert.NotNull(lease);
            try { await Task.Delay(Timeout.Infinite, cancellation.Token); }
            catch (OperationCanceledException) { stopping.SetResult(); }
            finally { await cleanup.Task; }
        }
        var running = Run();
        Assert.True(gate.IsHeld);
        cancellation.Cancel();
        await stopping.Task;
        Assert.Null(gate.TryEnter(() => { }));
        Assert.Equal(0, releases);
        cleanup.SetResult();
        await running;
        Assert.False(gate.IsHeld);
        Assert.Equal(1, releases);
        using var next = gate.TryEnter(() => { });
        Assert.NotNull(next);
    }

    /// <summary>异常路径必须释放，重复释放旧租约不得改变新任务的执行权。</summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public void Failure_AndRepeatedDispose_PreserveOwnership()
    {
        var gate = new DeviceExecutionGate();
        IDisposable? oldLease = null;
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var lease = gate.TryEnter(() => { });
            oldLease = lease;
            throw new InvalidOperationException();
        }));
        using var next = gate.TryEnter(() => { });
        Assert.NotNull(next);
        oldLease!.Dispose();
        Assert.True(gate.IsHeld);
        Assert.Null(gate.TryEnter(() => { }));
    }
}

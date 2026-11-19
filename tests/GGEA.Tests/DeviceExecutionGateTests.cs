using GGEA.App.UI;

namespace GGEA.Tests;

/// <summary>验证互斥门的所有权必须跨越取消与异步收尾，且旧租约不能释放新会话。</summary>
public sealed class DeviceExecutionGateTests
{
    [Fact]
    public void CompetingWindows_KeepDeviceUntilOwnerFinishes_AndAllowOtherDevices()
    {
        var id = Guid.NewGuid();
        var serial = "emu-" + id;
        var alias = "tcp-" + id;
        using var owner = new DeviceExecutionGate().TryEnter(() => { })!;
        using var second = new DeviceExecutionGate().TryEnter(() => { })!;
        using var third = new DeviceExecutionGate().TryEnter(() => { })!;
        Assert.True(owner.TryClaimDevice(serial, id));
        Assert.False(second.TryClaimDevice(serial, Guid.Empty));
        Assert.False(second.TryClaimDevice(alias, id));
        // 别名冲突时不得遗留部分取得的地址锁。
        Assert.True(third.TryClaimDevice(alias, Guid.NewGuid()));
        owner.Dispose();
        Assert.True(second.TryClaimDevice(serial, id));
    }

    [Fact]
    public async Task ConcurrentStarts_OnlyOneOwner_EvenWithoutBootId()
    {
        var serial = "emu-" + Guid.NewGuid();
        using var first = new DeviceExecutionGate().TryEnter(() => { })!;
        using var second = new DeviceExecutionGate().TryEnter(() => { })!;
        var results = await Task.WhenAll(Task.Run(() => first.TryClaimDevice(serial, Guid.Empty)),
            Task.Run(() => second.TryClaimDevice(serial, Guid.Empty)));
        Assert.Single(results, acquired => acquired);
    }

    /// <summary>取消发生后，只有完整清理退出才能开放下一次设备操作。</summary>
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Cancellation_KeepsOwnershipUntilCleanupCompletes()
    {
        var gate = new DeviceExecutionGate();
        var serial = "emu-" + Guid.NewGuid();
        using var nextWindow = new DeviceExecutionGate().TryEnter(() => { })!;
        using var cancellation = new CancellationTokenSource();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        async Task Run()
        {
            using var lease = gate.TryEnter(() => releases++);
            Assert.NotNull(lease);
            Assert.True(lease.TryClaimDevice(serial, Guid.Empty));
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
        Assert.False(nextWindow.TryClaimDevice(serial, Guid.Empty));
        cleanup.SetResult();
        await running;
        Assert.False(gate.IsHeld);
        Assert.Equal(1, releases);
        Assert.True(nextWindow.TryClaimDevice(serial, Guid.Empty));
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

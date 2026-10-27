namespace GGEA.App.UI;

/// <summary>由 UI 线程持有的设备执行互斥门；拒绝重入，租约释放前不接受下一项操作。</summary>
internal sealed class DeviceExecutionGate
{
    public bool IsHeld { get; private set; }

    /// <summary>同步取得所有权，不等待旧任务，以免停止后自动执行过时的点击。</summary>
    public IDisposable? TryEnter(Action released)
    {
        if (IsHeld) return null;
        IsHeld = true;
        return new Lease(this, released);
    }

    /// <summary>只有取得执行权的调用者可释放；重复 Dispose 不影响后续会话。</summary>
    private sealed class Lease(DeviceExecutionGate owner, Action released) : IDisposable
    {
        private bool _disposed;

        /// <summary>业务 finally 完成后解除所有权并通知界面刷新。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.IsHeld = false;
            released();
        }
    }
}

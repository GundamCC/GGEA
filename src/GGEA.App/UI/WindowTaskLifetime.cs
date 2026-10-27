namespace GGEA.App.UI;

/// <summary>由 UI 线程持有的窗口任务集合；关闭后禁止登记新任务，收尾完成前保留所有权。</summary>
internal sealed class WindowTaskLifetime
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _tasks = new();
    public CancellationToken Token => _shutdown.Token;
    public bool IsStopping { get; private set; }

    /// <summary>先登记完成信号再调用业务，覆盖同步完成与事件重入。</summary>
    public async Task RunAsync(Func<Task> action)
    {
        if (IsStopping) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.Add(completion.Task);
        try { await action(); }
        finally
        {
            completion.SetResult();
            _tasks.Remove(completion.Task);
        }
    }

    /// <summary>有限等待；超时保留停止状态，调用方不得释放仍被业务使用的资源。</summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        IsStopping = true;
        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(_tasks.ToArray()).WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException) { return false; }
    }
}

namespace GGEA.App.Automation;

/// <summary>仅让连续两次一致的观察授权动作；未命中或位置变化会重新开始确认。</summary>
internal static class StableObservation
{
    internal static async Task<T?> WaitAsync<T>(Func<Task<T?>> read, Func<T,T,bool> agrees,
        int timeoutMs, CancellationToken ct) where T : class
    {
        T? previous = null;
        long deadline = Environment.TickCount64 + timeoutMs;
        do
        {
            ct.ThrowIfCancellationRequested();
            var current = await read();
            if (previous is not null && current is not null && agrees(previous, current)) return current;
            previous = current;
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        } while (Environment.TickCount64 < deadline);
        return null;
    }
}

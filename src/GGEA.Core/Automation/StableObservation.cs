namespace GGEA.App.Automation;

/// <summary>只让连续一致的新帧授权动作，默认需要两帧；未命中或位置变化会重新开始确认。</summary>
internal static class StableObservation
{
    internal static async Task<T?> WaitAsync<T>(Func<Task<T?>> read, Func<T,T,bool> agrees,
        int timeoutMs, CancellationToken ct, int requiredFrames = 2) where T : class
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredFrames, 2);
        T? first = null;
        int streak = 0;
        long deadline = Environment.TickCount64 + timeoutMs;
        do
        {
            ct.ThrowIfCancellationRequested();
            var current = await read();
            if (first is not null && current is not null && agrees(first, current))
            {
                if (++streak == requiredFrames) return current;
            }
            else
            {
                first = current;
                streak = current is null ? 0 : 1;
            }
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        } while (Environment.TickCount64 < deadline);
        return null;
    }
}

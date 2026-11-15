namespace GGEA.App.Automation;

/// <summary>页面身份缺失时只等待和重读，不执行输入；调用方只能使用最后一次观察的证据。</summary>
internal static class PageObservation
{
    internal static async Task<(T Frame, bool Confirmed)> ConfirmAsync<T>(T first, Func<Task<T>> capture,
        Func<T, bool, Task<bool>> confirm, int intervalMs, CancellationToken ct)
    {
        var frame = first;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var confirmed = await confirm(frame, attempt > 0);
            ct.ThrowIfCancellationRequested();
            if (confirmed || attempt == 2) return (frame, confirmed);
            await Task.Delay(intervalMs, ct);
            frame = await capture();
        }
    }

    /// <summary>只重做无输入的观察过程。第二帧缺失时整对作废，已确认错误身份或设备异常直接上抛。</summary>
    internal static async Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        async Task<(T Value, MissingPageEvidenceException? Error)> Observe()
        {
            ct.ThrowIfCancellationRequested();
            try { return (await read(), null); }
            catch (MissingPageEvidenceException ex) { return (default!, ex); }
        }
        var check = await ConfirmAsync(await Observe(), Observe, (sample, _) => Task.FromResult(sample.Error is null),
            GameTiming.ConfirmationIntervalMs, ct);
        if (check.Frame.Error is { } error) throw error;
        return check.Frame.Value;
    }
}

internal sealed class MissingPageEvidenceException(string message) : InvalidOperationException(message);

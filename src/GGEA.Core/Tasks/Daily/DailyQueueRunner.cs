namespace GGEA.App.Tasks;

/// <summary>区分普通提前结束与禁止继续操作设备的安全停止。</summary>
public enum DailyQueueOutcome
{
    /// <summary>普通队列已完整执行。</summary>
    Completed,
    /// <summary>普通队列提前结束，仍需尝试收尾。</summary>
    Interrupted,
    /// <summary>网络错误或未关闭的危险弹窗等要求停止所有后续操作。</summary>
    Unsafe,
}

/// <summary>将队尾领取与普通任务的提前返回隔离，保留取消和异常的停止语义。</summary>
public static class DailyQueueRunner
{
    /// <summary>正常结束或业务中断后收尾一次；不在取消、异常或安全停止后继续操作设备。</summary>
    public static async Task<DailyQueueOutcome> RunAsync(
        Func<Task<DailyQueueOutcome>> runQueue,
        Func<Task> finalizeQueue,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var outcome = await runQueue();
        ct.ThrowIfCancellationRequested();
        if (outcome != DailyQueueOutcome.Unsafe)
        {
            await finalizeQueue();
        }
        return outcome;
    }
}

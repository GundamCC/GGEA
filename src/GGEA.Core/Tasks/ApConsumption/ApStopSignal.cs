using GGEA.App.Logging;

namespace GGEA.App.Tasks;

/// <summary>业务确认 AP 不足并停止后发出的信号；通知失败不能影响取消弹窗及原有失败返回。</summary>
public sealed class ApStopSignal(Action<string> report, IGgeaLog log)
{
    /// <summary>只由已确认停止的流程调用，不从日志文本猜测 AP 状态。</summary>
    public void Report(string message)
    {
        try { report(message); }
        catch (Exception ex) { log.Warn($"AP 不足停止提醒失败，原停止流程继续：{ex.GetType().Name}"); }
    }
}

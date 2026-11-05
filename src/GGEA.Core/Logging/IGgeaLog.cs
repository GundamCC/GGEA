namespace GGEA.App.Logging;

/// <summary>
/// 日志接口，界面与各服务只依赖该接口。
/// </summary>
public interface IGgeaLog
{
    /// <summary>产生新日志时触发，界面通过该事件实时展示。</summary>
    event EventHandler<LogEntry>? Logged;

    void Debug(string message);
    /// <summary>正常进度、未命中后继续等待或预期内跳过，不应被呈现为执行错误。</summary>
    void Info(string message);
    /// <summary>未确认的识别结果、安全拦截、可恢复异常或配置回退；不代表任务链已停止。</summary>
    void Warn(string message);
    /// <summary>仅用于已确认导致当前操作、任务或任务流程无法继续的失败；底层单帧观察不应提前报错。</summary>
    void Error(string message);
}

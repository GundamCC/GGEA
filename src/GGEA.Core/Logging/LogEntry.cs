namespace GGEA.App.Logging;

/// <summary>
/// 一条日志记录，同时用于文件输出与界面展示。
/// </summary>
public sealed record LogEntry(DateTime Timestamp, string Level, string Message)
{
    public override string ToString() => $"[{Timestamp:HH:mm:ss}] [{Level}] {Message}";
}

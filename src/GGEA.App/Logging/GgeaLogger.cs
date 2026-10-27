using System.IO;

namespace GGEA.App.Logging;

/// <summary>
/// 默认日志实现：写入当日文件，开发版按进程隔离，同时向界面广播事件。
/// </summary>
public sealed class GgeaLogger : IGgeaLog, IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public event EventHandler<LogEntry>? Logged;

    /// <summary>创建日志文件；开发版多进程不能共同持有同一个写入流。</summary>
    public GgeaLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
#if DEBUG
        var filePath = Path.Combine(logDirectory, $"ggea-{DateTime.Now:yyyyMMdd}-{Environment.ProcessId}.log");
#else
        var filePath = Path.Combine(logDirectory, $"ggea-{DateTime.Now:yyyyMMdd}.log");
#endif
        _writer = new StreamWriter(filePath, append: true) { AutoFlush = true };
    }

    public void Debug(string message) => Write("DEBUG", message);
    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (_lock)
        {
            _writer?.WriteLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {entry.Message}");
        }

        // 界面订阅事件，事件回调内自行切回 UI 线程
        Logged?.Invoke(this, entry);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

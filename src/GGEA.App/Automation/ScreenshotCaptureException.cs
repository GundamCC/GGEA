namespace GGEA.App.Automation;

/// <summary>截图或解码连续失败；交由任务层报告失败，不能解释为目标不存在。</summary>
public sealed class ScreenshotCaptureException : Exception
{
    /// <summary>保留底层错误，方便区分设备捕获失败与图片解码失败。</summary>
    public ScreenshotCaptureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

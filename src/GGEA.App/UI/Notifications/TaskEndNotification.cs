using GGEA.App.Localization;
namespace GGEA.App.UI;

/// <summary>持有一次独立任务的结束提醒状态；网络错误已提醒时抑制重复通知，不参与业务结果判定。</summary>
internal sealed class TaskEndNotification(string taskName, bool distinguishResult = false)
{
    private bool _handled;

    /// <summary>仅由调用方在业务明确完成后设置；中止、资源不足和异常默认使用警告提醒。</summary>
    public bool Succeeded { get; set; }

    /// <summary>调用方确认主动中止时标记；中止提示不能冒充刷本失败或成功。</summary>
    public bool Canceled { get; set; }

    /// <summary>已有更具体的网络错误提醒时，不再发送第二条任务结束提醒。</summary>
    public void Suppress() => _handled = true;

    /// <summary>任务收尾后最多发送一次；关闭通知或关闭窗口时也消费本轮状态，避免后续补发。</summary>
    public void Send(string message, bool enabled, bool isStopping, Action<string, string, bool> show)
    {
        if (_handled) return;
        _handled = true;
        if (!enabled || isStopping) return;
        var result = Canceled ? L.Text("已中止") : Succeeded ? L.Text("完成") : L.Text("失败");
        var title = distinguishResult ? $"GGEA · {taskName}{result}" : L.Format($"GGEA · {taskName}任务结束");
        show(message, title, Canceled || !Succeeded);
    }
}

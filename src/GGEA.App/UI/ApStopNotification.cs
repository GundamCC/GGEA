using GGEA.App.Localization;
namespace GGEA.App.UI;

/// <summary>一次执行会话内只发送一条 AP 不足通知，独立于任务是否走到正常收尾。</summary>
internal sealed class ApStopNotification
{
    public bool Reported { get; private set; }

    /// <summary>新执行取得互斥租约后重置，子任务之间不重置。</summary>
    public void Reset() => Reported = false;

    /// <summary>尊重通知开关与关窗状态；具体 AP 提醒取代本轮普通结束通知。</summary>
    public void Send(string message, bool enabled, bool stopping, TaskEndNotification? task,
        Action<string, string, bool> show)
    {
        task?.Suppress();
        if (Reported) return;
        Reported = true;
        if (enabled && !stopping) show(message, L.Text("GGEA · AP 不足"), true);
    }
}

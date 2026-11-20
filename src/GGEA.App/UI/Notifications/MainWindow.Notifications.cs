using GGEA.App.Localization;

namespace GGEA.App.UI;

/// <summary>任务完成与中断时的状态提示及 Windows 通知。</summary>
public partial class MainWindow
{
    private readonly WindowsNotificationService _notifications = new();

    // 设备执行权保证同一时刻只有一个独立任务；网络错误提醒与 finally 共用本轮通知状态。
    private TaskEndNotification? _activeTaskEndNotification;
    private readonly ApStopNotification _apStopNotification = new();

    /// <summary>
    /// 任一自动化任务失败后等待五秒再诊断网络错误；命中时统一更新连接状态并通知用户。
    /// 用户在等待期间主动中止时不再截图，也不发送错误通知。
    /// </summary>
    private async Task<bool> AbortTaskChainIfConnectionErrorAsync(
        string serial,
        string taskChainName,
        CancellationToken ct)
    {
        if (_automation is null)
        {
            return false;
        }

        try
        {
            if (!await _automation.GlobalScreens.DetectConnectionErrorAfterFailureAsync(serial, ct))
            {
                return false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }

        ReportConnectionError(taskChainName);
        return true;
    }

    /// <summary>统一呈现已确认的网络错误并发送一次警告；不再截图或执行恢复动作。</summary>
    private string ReportConnectionError(string taskChainName)
    {
        var message = L.Format($"检测到网络错误或游戏数据更新，{taskChainName}任务链已中止");
        ConnectionStateText.Text = message;
        _logger?.Error(message);
        _activeTaskEndNotification?.Suppress();
        if (_config.EnableWindowsNotifications)
        {
            ShowNetworkErrorNotification(message);
        }

        return message;
    }

    /// <summary>发送网络错误警告；发送失败不覆盖原任务失败状态。</summary>
    private void ShowNetworkErrorNotification(string message)
    {
        try
        {
            if (!_notifications.Show(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle,
                    message,
                    L.Text("GGEA · 游戏中断"),
                    isWarning: true))
            {
                _logger?.Warn(L.Text("网络错误 Windows 通知发送失败，任务链仍已中止"));
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn(L.Format($"网络错误 Windows 通知发送失败：{ex.Message}"));
        }
    }

    /// <summary>独立任务在获得执行权并完成输入校验后登记提醒，未启动的操作不发送结束通知。</summary>
    private TaskEndNotification BeginTaskEndNotification(string taskName, bool distinguishResult = false)
        => _activeTaskEndNotification = new TaskEndNotification(taskName, distinguishResult);

    /// <summary>finally 完成状态恢复后发送真实终态；窗口退出时抑制通知，并解除本轮状态所有权。</summary>
    private void CompleteTaskEndNotification(TaskEndNotification? notification, string message)
    {
        if (notification is null) return;
        if (ReferenceEquals(_activeTaskEndNotification, notification)) _activeTaskEndNotification = null;
        notification.Send(message, _config.EnableWindowsNotifications, _windowTasks.IsStopping,
            ShowWindowsNotification);
    }

    private void ReportCapacityWarning(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ReportCapacityWarning(message)); return; }
        ConnectionStateText.Text = message;
        _activeTaskEndNotification?.Suppress();
        if (_config.EnableWindowsNotifications && !_windowTasks.IsStopping)
            ShowWindowsNotification(message, L.Text("GGEA · 游戏中断"), true);
    }

    /// <summary>所有出击与略过共用 AP 停止出口；UI 线程负责系统通知，不依赖队列正常完成。</summary>
    private void ReportApInsufficient(string message)
    {
        if (_windowTasks.IsStopping) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ReportApInsufficient(message)); return; }
        _apStopNotification.Send(message, _config.EnableWindowsNotifications, _windowTasks.IsStopping,
            _activeTaskEndNotification, ShowWindowsNotification);
    }

    /// <summary>每日队列保持原有标题，和独立任务共用不会改变业务结果的通知出口。</summary>
    private void ShowQueueCompletionNotification(string message, bool isWarning)
        => ShowWindowsNotification(message, L.Text("GGEA · 任务队列执行完毕"), isWarning);

    /// <summary>通知发送失败只记录日志，不改变已完成任务或队列的执行结果。</summary>
    private void ShowWindowsNotification(string message, string title, bool isWarning)
    {
        try
        {
            if (!_notifications.Show(new System.Windows.Interop.WindowInteropHelper(this).Handle, message,
                    title, isWarning))
            {
                _logger?.Warn(L.Text("Windows 通知发送失败，任务结果已保留在日志中"));
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn(L.Format($"Windows 通知发送失败：{ex.Message}"));
        }
    }
}

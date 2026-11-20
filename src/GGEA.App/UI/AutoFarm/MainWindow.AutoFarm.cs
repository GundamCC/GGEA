using System.Windows;
using System.Windows.Input;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>自动刷本参数校验、启动与任务状态显示。</summary>
public partial class MainWindow
{
    private void ConnectAutoFarmView()
    {
        AutoFarmView.AutoFarmCountBox.PreviewTextInput += AutoFarmCountBox_PreviewTextInput;
        DataObject.AddPastingHandler(AutoFarmView.AutoFarmCountBox, AutoFarmCountBox_Pasting);
        AutoFarmView.AutoFarmStartButton.Click += AutoFarmStartButton_Click;
    }

    private bool _autoFarmRunning;
    private CancellationTokenSource? _autoFarmCts;

    /// <summary>开始或中止刷本，窗口等待执行器完成后再恢复控件。</summary>
    private async void AutoFarmStartButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => AutoFarmStartButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task AutoFarmStartButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        // 运行中：点击按钮 = 中止自动刷本
        if (_autoFarmRunning)
        {
            _logger?.Info(L.Text("用户请求中止自动刷本…"));
            AutoFarmView.AutoFarmStartButton.Content = L.Text("正在停止…");
            AutoFarmView.AutoFarmStartButton.IsEnabled = false;
            _autoFarmCts?.Cancel();
            return;
        }

        using var execution = TryBeginExecution(logRoutineChecks: false);
        if (execution is null) return;

        if (_automation is null || _matcher is null || _logger is null || _sortieFlowService is null)
        {
            _logger?.Warn(L.Text("自动刷本依赖尚未初始化"));
            return;
        }

        var serial = await GetOnlineTaskSerialAsync(execution, AutoFarmView.AutoFarmStatusText, _windowTasks.Token);
        if (serial is null) return;

        _autoFarmRunning = true;
        _autoFarmCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var ct = _autoFarmCts.Token;
        AutoFarmView.AutoFarmStartButton.Content = L.Text("中止");
        UpdateBusyState();
        var endNotification = BeginTaskEndNotification(L.Text("自动刷本"), distinguishResult: true);
        try
        {
            if (!await EnsureDeviceCompatibilityAsync(serial, ct))
            {
                AutoFarmView.AutoFarmStatusText.Text = L.Text("当前设备画面不兼容，已停止自动刷取。");
                return;
            }

            // 读取刷取次数（输入框，仅数字，范围 1~999；非法或为空按默认 10 处理）
            var rounds = int.TryParse(AutoFarmView.AutoFarmCountBox.Text?.Trim(), out var count)
                ? Math.Clamp(count, 1, 999)
                : 10;
            var options = new AutoFarmOptions(rounds, ConfigService.ResolvePath(_config.SortieTemplate),
                _config.AllowGgeaDownload, AutoFarmView.EmptySlotSortieCheckBox.IsChecked == true,
                AutoFarmView.TerrainUnsuitableSortieCheckBox.IsChecked == true);
            var result = await _sortieFlowService.RunFarmAsync(serial, options,
                message => AutoFarmView.AutoFarmStatusText.Text = message, ct);
            if (result.CheckConnection) await ReportAutoFarmFailureAsync(serial, result.Message, ct);
            else AutoFarmView.AutoFarmStatusText.Text = result.Message;
            endNotification.Succeeded = result.Succeeded;
        }
        catch (OperationCanceledException)
        {
            AutoFarmView.AutoFarmStatusText.Text = L.Text("自动刷本已中止。");
            endNotification.Canceled = true;
            _logger?.Warn(L.Text("自动刷本已被用户中止"));
        }
        catch (Exception ex)
        {
            _logger.Error(L.Format($"自动刷本执行失败: {ex.Message}"));
            if (ex is ConnectionErrorException)
            {
                AutoFarmView.AutoFarmStatusText.Text = ReportConnectionError(L.Text("自动刷本"));
                return;
            }
            // 捕获失败不能进入页面恢复流程；没有有效画面时立即停止控制动作。
            if (ex is ScreenshotCaptureException)
            {
                AutoFarmView.AutoFarmStatusText.Text = L.Text("截图捕获失败，自动刷本已停止。");
                return;
            }
            await ReportAutoFarmFailureAsync(serial, L.Text("执行失败，请查看日志。"), ct);
        }
        finally
        {
            _autoFarmRunning = false;
            _autoFarmCts?.Dispose();
            _autoFarmCts = null;
            AutoFarmView.AutoFarmStartButton.Content = L.Text("开始");
            UpdateBusyState();
            CompleteTaskEndNotification(endNotification, AutoFarmView.AutoFarmStatusText.Text);
        }
    }

    /// <summary>显示刷本失败原因；若稳定画面确认网络错误，则用网络错误覆盖并终止当前链。</summary>
    private async Task ReportAutoFarmFailureAsync(
        string serial,
        string failureMessage,
        CancellationToken ct)
    {
        AutoFarmView.AutoFarmStatusText.Text = failureMessage;
        if (await AbortTaskChainIfConnectionErrorAsync(serial, L.Text("自动刷本"), ct))
        {
            AutoFarmView.AutoFarmStatusText.Text = L.Text("检测到网络错误或游戏数据更新，自动刷本任务链已中止。");
        }
    }

    /// <summary>刷取次数输入框：仅允许输入数字。</summary>
    private void AutoFarmCountBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    /// <summary>刷取次数输入框：禁止粘贴包含非数字的内容。</summary>
    private void AutoFarmCountBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)) &&
            (e.DataObject.GetData(typeof(string)) as string)?.All(char.IsDigit) == true)
        {
            return;
        }

        e.CancelCommand();
    }
}

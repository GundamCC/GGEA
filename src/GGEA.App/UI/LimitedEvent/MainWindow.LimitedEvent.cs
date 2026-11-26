using System.Windows;
using System.Windows.Controls;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

public partial class MainWindow
{
    private CancellationTokenSource? _limitedCts;

    /// <summary>手动进入事件后才接管设备，关闭窗口或点击中止均释放本次设备占用。</summary>
    private async void LimitedStart_Click(object sender, RoutedEventArgs e) =>
        await RunWindowTaskAsync(RunLimitedEventAsync);

    private async Task RunLimitedEventAsync()
    {
        if (_limitedCts is not null)
        {
            _limitedCts.Cancel();
            UpdateBusyState();
            return;
        }
        using var execution = TryBeginExecution();
        if (execution is null) return;
        var serial = await GetOnlineTaskSerialAsync(execution, LimitedStatus, _windowTasks.Token);
        if (serial is null || _automation is null || _logger is null) return;
        var difficulty = Enum.Parse<FierceEnemyDifficulty>((string)((ComboBoxItem)LimitedDifficulty.SelectedItem).Content);
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        _limitedCts = owner;
        LimitedStart.Content = L.Text("中止略过");
        LimitedStatus.Text = L.Text("正在略过限时活动 BOSS…");
        UpdateBusyState();
        try
        {
            if (!await EnsureDeviceCompatibilityAsync(serial, owner.Token))
                throw new InvalidOperationException(L.Text("设备画面不兼容，已停止略过。"));
            var service = new LimitedEventBossService(_automation, GetTextOcr(), _logger, serial);
            await Task.Run(() => service.RunAsync(difficulty, owner.Token), owner.Token);
            LimitedStatus.Text = L.Text("BOSS 挑战券不足，已关闭对话框。");
            _logger.Info(LimitedStatus.Text);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        { LimitedStatus.Text = L.Text("限时活动略过已中止。"); }
        catch (Exception ex)
        { LimitedStatus.Text = ex.Message; _logger.Error(ex.Message); }
        finally
        {
            _limitedCts = null;
            LimitedStart.Content = L.Text("开始 BOSS 略过");
            UpdateBusyState();
        }
    }
}

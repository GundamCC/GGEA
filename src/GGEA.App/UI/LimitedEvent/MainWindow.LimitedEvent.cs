using System.Windows;
using System.Windows.Controls;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

public partial class MainWindow
{
    private CancellationTokenSource? _limitedCts;

    private bool _exchangingMedals;

    /// <summary>手动进入事件后才接管设备，关闭窗口或点击中止均释放本次设备占用。</summary>
    private async void LimitedStart_Click(object sender, RoutedEventArgs e) =>
        await RunWindowTaskAsync(() => RunLimitedEventAsync(sender == MedalStart));

    private async Task RunLimitedEventAsync(bool medals)
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
        _exchangingMedals = medals;
        var button = medals ? MedalStart : LimitedStart;
        button.Content = medals ? L.Text("中止兑换") : L.Text("中止略过");
        LimitedStatus.Text = medals ? L.Text("正在兑换奖章机体补给…") : L.Text("正在略过限时活动 BOSS…");
        UpdateBusyState();
        try
        {
            if (!await EnsureDeviceCompatibilityAsync(serial, owner.Token))
                throw new InvalidOperationException(medals ? L.Text("设备画面不兼容，已停止兑换。") : L.Text("设备画面不兼容，已停止略过。"));
            if (medals)
            {
                var service = new MedalExchangeService(_automation, GetTextOcr(), _logger, serial);
                await Task.Run(() => service.RunAsync(owner.Token), owner.Token);
                LimitedStatus.Text = L.Text("奖章不足，自动兑换已结束。");
            }
            else
            {
                var service = new LimitedEventBossService(_automation, GetTextOcr(), _logger, serial);
                await Task.Run(() => service.RunAsync(difficulty, owner.Token), owner.Token);
                LimitedStatus.Text = L.Text("BOSS 挑战券不足，已关闭对话框。");
            }
            _logger.Info(LimitedStatus.Text);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        { LimitedStatus.Text = medals ? L.Text("奖章兑换已中止。") : L.Text("限时活动略过已中止。"); }
        catch (Exception ex)
        { LimitedStatus.Text = ex.Message; _logger.Error(ex.Message); }
        finally
        {
            _limitedCts = null;
            button.Content = medals ? L.Text("开始奖章自动兑换") : L.Text("开始 BOSS 略过");
            UpdateBusyState();
        }
    }
}

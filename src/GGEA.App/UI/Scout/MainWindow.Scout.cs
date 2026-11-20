using System.Windows;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>自动招募的启动、中止与结果显示。</summary>
public partial class MainWindow
{
    private void ConnectScoutView()
    {
        ScoutView.ScoutStartButton.Click += ScoutStartButton_Click;
    }

    /// <summary>招募循环独占设备；其按钮运行中只负责中止，不创建第二个循环。</summary>
    private bool _scoutRunning;
    private CancellationTokenSource? _scoutCts;

    /// <summary>招募操作纳入窗口任务生命周期，关闭时取消并等待收尾。</summary>
    private async void ScoutStartButton_Click(object sender, RoutedEventArgs e)
        => await RunWindowTaskAsync(StartScoutAsync);

    /// <summary>持有设备执行权运行招募循环；再次点击中止，并在退出后统一释放状态。</summary>
    private async Task StartScoutAsync()
    {
        if (_scoutRunning)
        {
            ScoutView.ScoutStartButton.Content = L.Text("正在停止…");
            _scoutCts?.Cancel();
            UpdateBusyState();
            return;
        }
        using var execution = TryBeginExecution();
        if (execution is null) return;
        if (_automation is null)
        {
            ScoutView.ScoutStatusText.Text = L.Text("请先连接并选择模拟器。");
            return;
        }
        var serial = await GetOnlineTaskSerialAsync(execution, ScoutView.ScoutStatusText, _windowTasks.Token);
        if (serial is null) return;
        _scoutRunning = true;
        _scoutCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var ct = _scoutCts.Token;
        var completed = 0;
        var insufficientResources = false;
        ScoutView.ScoutStartButton.Content = L.Text("中止招募");
        ScoutView.ScoutStatusText.Text = L.Text("正在检查招募模板…");
        UpdateBusyState();
        var endNotification = BeginTaskEndNotification(L.Text("自动招募"));
        try
        {
            var templates = new ScoutTemplates(
                ConfigService.ResolvePath(ScoutService.TitleTemplate),
                ConfigService.ResolvePath(ScoutService.ButtonTemplate),
                ConfigService.ResolvePath(ScoutService.ExecuteHintTemplate),
                ConfigService.ResolvePath(ScoutService.ExecuteTemplate),
                ConfigService.ResolvePath(ScoutService.NextTemplate),
                ConfigService.ResolvePath(ScoutService.NoCharactersTemplate),
                ConfigService.ResolvePath(ScoutService.ClosedButtonTemplate));
            var result = await new ScoutService(_automation).RunAsync(serial, templates, _developLayout, (count, message) =>
            {
                completed = count;
                ScoutView.ScoutStatusText.Text = L.Format($"已完成 {count} 轮 · {message}");
                _logger?.Info(message);
            }, _config.DevelopTitleThreshold, ct);
            insufficientResources = result == ScoutResult.InsufficientResources;
            if (insufficientResources)
            {
                // 恢复期间保留设备执行权和取消令牌，禁止其他任务插入或恢复后重新招募。
                var recovered = await EnsureMainScreenAsync(serial, ct);
                ScoutView.ScoutStatusText.Text = L.Format($"招募资源不足，自动招募已中止，已完成 {completed} 轮；")
                    + (recovered ? L.Text("已返回主界面。") : L.Text("未能确认主界面，请检查游戏画面。"));
                if (recovered) _logger?.Info(ScoutView.ScoutStatusText.Text);
                else _logger?.Warn(ScoutView.ScoutStatusText.Text);
            }
            else
            {
                ScoutView.ScoutStatusText.Text = L.Format($"自动招募完成，已完成 {completed} 轮，没有可招募的角色。");
                endNotification.Succeeded = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ScoutView.ScoutStatusText.Text = insufficientResources
                ? L.Format($"招募资源不足，自动招募已中止，已完成 {completed} 轮；主界面恢复已取消。")
                : L.Format($"自动招募已中止，已完成 {completed} 轮。");
            _logger?.Info(ScoutView.ScoutStatusText.Text);
        }
        catch (Exception ex)
        {
            ScoutView.ScoutStatusText.Text = insufficientResources
                ? L.Format($"招募资源不足，自动招募已中止，已完成 {completed} 轮；主界面恢复失败：{ex.Message}")
                : L.Format($"自动招募已停止，已完成 {completed} 轮：{ex.Message}");
            _logger?.Error(ScoutView.ScoutStatusText.Text);
        }
        finally
        {
            _scoutRunning = false;
            _scoutCts.Dispose();
            _scoutCts = null;
            ScoutView.ScoutStartButton.Content = L.Text("开始招募");
            UpdateBusyState();
            CompleteTaskEndNotification(endNotification, ScoutView.ScoutStatusText.Text);
        }
    }
}

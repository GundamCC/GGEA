using System.Windows;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>大师联盟页共用窗口任务生命周期和设备互斥；任务选项仅属于本次窗口会话。</summary>
public partial class MainWindow
{
    private CancellationTokenSource? _masterLeagueCts;

    /// <summary>注册到窗口任务集合，关闭窗口会取消并等待清理。</summary>
    private async void MasterLeagueStartButton_Click(object sender, RoutedEventArgs e)
        => await RunWindowTaskAsync(StartMasterLeagueAsync);

    /// <summary>启动前快照选项，执行中按钮用于取消，租约释放前禁止切换设备及修改选项。</summary>
    private async Task StartMasterLeagueAsync()
    {
        if (_masterLeagueCts is not null)
        {
            _masterLeagueCts.Cancel();
            MasterLeagueStartButton.Content = L.Text("正在停止…");
            UpdateBusyState();
            return;
        }
        using var execution = TryBeginExecution();
        if (execution is null) return;
        if (_automation is null || _logger is null || !TryGetDeviceSerial(out var serial))
        { MasterLeagueStatusText.Text = L.Text("请先连接并选择模拟器。"); return; }
        if (_config.GameLanguage != "zh-CN")
        { MasterLeagueStatusText.Text = L.Text("大师联盟当前仅支持简体中文游戏界面。"); return; }
        var autoFormation = MasterLeagueAutoFormationCheck.IsChecked == true;
        var retryDefeated = MasterLeagueRetryDefeatedCheck.IsChecked == true;
        _masterLeagueCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var ct = _masterLeagueCts.Token;
        MasterLeagueStartButton.Content = L.Text("中止");
        MasterLeagueStatusText.Text = L.Text("正在进入大师联盟…");
        UpdateBusyState();
        try
        {
            var service = new MasterLeagueService(_automation, _logger, _config);
            var vision = new MasterLeagueVision(GetTextOcr());
            var resultRecognizer = new MasterLeagueResultRecognizer(GetTextOcr());
            _logger.Info(retryDefeated
                ? "已启用刷新挑战失败的对手：五人均已挑战时，仅刷新明确败北的对手。"
                : "未选择刷新挑战失败的对手，不自动刷新对手。");
            // 在任何导航或消费动作前检查模型、布局和全部素材，避免运行中才发现依赖缺失。
            await Task.Run(() => { service.Validate(autoFormation); vision.Validate(); resultRecognizer.Validate(); }, ct);
            // 导航尚未发出挑战或变更，失败时可以重新确认入口；进入任务后由会话持有消费状态。
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var screen = await _automation.CaptureScreenAsync(serial, ct, retryOnFailure: false);
                    if (!autoFormation && await Task.Run(() => vision.ReadOpponents(screen, ct), ct) is not null) break;
                    if (await EnsureDeviceCompatibilityAsync(serial, ct) && await EnsureMainScreenAsync(serial, ct))
                    {
                        var entered = await service.OpenFromHomeAsync(serial, _mainLayout, autoFormation, ct);
                        if (entered.Success) break;
                    }
                }
                catch (Exception ex) when (MasterLeagueService.IsRecoverableDeviceError(ex, ct))
                { _logger.Info("大师联盟入口暂不可用，等待原设备或网络恢复后重新确认。"); }
                MasterLeagueStatusText.Text = "正在等待设备或入口恢复，任务仍在运行，可中止。";
                await Task.Delay(5000, ct);
            }
            MasterLeagueStatusText.Text = L.Text("正在挑战大师联盟…");
            var progress = new Progress<string>(message =>
            {
                if (_masterLeagueCts is not null && !ct.IsCancellationRequested) MasterLeagueStatusText.Text = message;
            });
            var homeDetector = CreateMainScreenDetector();
            var result = await service.RunChallengesAsync(serial, vision, resultRecognizer, retryDefeated, ct, progress,
                async token =>
                {
                    // 回到首页后只恢复导航，不再次自动编制；没有首页证据时不发出返回/中心点击。
                    if (!await homeDetector.IsMainScreenAsync(serial, token)) return false;
                    return (await service.OpenFromHomeAsync(serial, _mainLayout, false, token)).Success;
                });
            MasterLeagueStatusText.Text = L.Text(result.Message);
            _logger.Info(result.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { MasterLeagueStatusText.Text = L.Text("大师联盟流程已中止。"); _logger.Info(MasterLeagueStatusText.Text); }
        catch (Exception ex)
        { MasterLeagueStatusText.Text = L.Format($"大师联盟流程已停止：{ex.Message}"); _logger.Error(MasterLeagueStatusText.Text); }
        finally
        {
            _masterLeagueCts.Dispose();
            _masterLeagueCts = null;
            MasterLeagueStartButton.Content = L.Text("开始大师联盟");
            UpdateBusyState();
        }
    }
}

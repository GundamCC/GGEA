using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Tasks;

namespace GGEA.App.UI;

/// <summary>铁球工厂与每日开发所需的参数，以及工厂任务的执行入口。</summary>
public partial class MainWindow
{
    private void ConnectBallFactoryView()
    {
        BallFactoryView.BallFactoryGpBox.PreviewTextInput += BallFactoryGpBox_PreviewTextInput;
        BallFactoryView.BallFactoryGpBox.PreviewKeyDown += BallFactoryGpBox_PreviewKeyDown;
        DataObject.AddPastingHandler(BallFactoryView.BallFactoryGpBox, BallFactoryGpBox_Pasting);
        BallFactoryView.BallFactoryGpBox.TextChanged += BallFactoryGpBox_TextChanged;
        BallFactoryView.BallFactoryGpBox.LostFocus += BallFactoryGpBox_LostFocus;
        BallFactoryView.BallFactoryStartButton.Click += BallFactoryStartButton_Click;
    }

    /// <summary>工厂定位流程持有设备执行权，取消后等待整个流程收尾再释放。</summary>
    private bool _ballFactoryRunning;
    private CancellationTokenSource? _ballFactoryCts;

    /// <summary>每日任务与工厂共用开发参数来源，调用方只替换目标素材，不复制识别配置。</summary>
    private DevelopRequest CreateDevelopRequest() => new()
    {
        MainLayout = _mainLayout,
        DevelopLayout = _developLayout,
        RoadmapFirstTemplatePath = ConfigService.ResolvePath(_config.RoadmapFirstTemplate),
        MsTemplatePath = ConfigService.ResolvePath(_config.DevelopMsTemplate),
        MsLargeTemplatePath = string.IsNullOrWhiteSpace(_config.DevelopMsLargeTemplate) ? "" : ConfigService.ResolvePath(_config.DevelopMsLargeTemplate),
        DevelopAllTemplatePath = ConfigService.ResolvePath(_config.DevelopAllTemplate),
        DevelopAllDisabledTemplatePath = ConfigService.ResolvePath(_config.DevelopAllDisabledTemplate),
        ExecuteDevelopTemplatePath = ConfigService.ResolvePath(_config.DevelopExecuteTemplate),
        ExecuteConfirmTemplatePath = ConfigService.ResolvePath(_config.DevelopExecuteConfirmTemplate),
        ShowcaseTapToNextTemplatePath = ConfigService.ResolvePath(_config.DevelopShowcaseTapTemplate),
        BackButtonTemplatePath = ConfigService.ResolvePath(_config.DevelopBackTemplate),
        DevelopTitleTemplatePath = ConfigService.ResolvePath(_config.DevelopTitleTemplate),
        Threshold = _config.DevelopMsThreshold,
        TitleThreshold = _config.DevelopTitleThreshold,
        TitleWaitMs = _config.DevelopTitleWaitMs,
        RoadmapDisplaySettingsTitleTemplatePath = ConfigService.ResolvePath(_config.DevelopRoadmapDisplaySettingsTitleTemplate),
    };

    /// <summary>每日开发与铁球工厂共用退出顺序，正常主界面导航仍由现有识别服务负责。</summary>
    private Task<bool> RecoverDevelopmentResourcesAsync(string serial, string reason, Action<string> report, CancellationToken ct)
        => new DevelopmentResourceRecovery(_automation!, (device, token) => EnsureMainScreenAsync(device, token))
            .RecoverAsync(serial, message => { report(message); _logger!.Info(message); }, ct, reason: reason);

    /// <summary>工厂入口纳入窗口生命周期；关窗时取消并等待收尾。</summary>
    private async void BallFactoryStartButton_Click(object sender, RoutedEventArgs e)
        => await RunWindowTaskAsync(StartBallFactoryAsync);

    /// <summary>从非战斗页面开始完整铁球工厂循环；共享导航、开发与解体服务，统一响应中止。</summary>
    private async Task StartBallFactoryAsync()
    {
        if (_ballFactoryRunning)
        {
            BallFactoryView.BallFactoryStartButton.Content = L.Text("正在停止…");
            _ballFactoryCts?.Cancel();
            UpdateBusyState();
            return;
        }
        using var execution = TryBeginExecution();
        if (execution is null) return;
        CommitBallFactoryGp();
        if (_automation is null || _logger is null)
        {
            BallFactoryView.BallFactoryStatusText.Text = L.Text("请先连接并选择模拟器。");
            return;
        }
        var serial = await GetOnlineTaskSerialAsync(execution, BallFactoryView.BallFactoryStatusText, _windowTasks.Token);
        if (serial is null) return;
        var targetGp = _ballFactoryTargetGp;
        _ballFactoryRunning = true;
        _ballFactoryCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var ct = _ballFactoryCts.Token;
        BallFactoryView.BallFactoryStartButton.Content = L.Text("中止制造");
        BallFactoryView.BallFactoryStatusText.Text = L.Text("正在检查设备画面…");
        UpdateBusyState();
        var endNotification = BeginTaskEndNotification(L.Text("铁球工厂"));
        try
        {
            if (!await EnsureDeviceCompatibilityAsync(serial, ct))
            {
                BallFactoryView.BallFactoryStatusText.Text = L.Text("设备画面不兼容，已停止，请检查设备状态。");
                return;
            }
            // 铁球固定使用原生立绘两档识别，不继承每日任务自定义截图的多尺度开关。
            IDevelopService develop = new DevelopService(_automation, _logger, fixedUnitScales: true);
            var factory = new BallFactoryService(develop,
                (device, token) => EnsureMainScreenAsync(device, token, allowAnnouncement: true));
            DismantleService.ValidateTemplates();
            void Report(string message) { BallFactoryView.BallFactoryStatusText.Text = message; _logger.Info(message); }
            var request = CreateDevelopRequest();
            var runner = new BallFactoryRunner(
                (quantity, locateTarget, token) => factory.DevelopBatchAsync(_automation, serial, quantity, request, Report, token, locateTarget),
                (state, token) => RecoverDevelopmentResourcesAsync(serial, state.GetBlockedReason()!, Report, token),
                async token =>
                {
                    // 清理既用于单位上限，也用于全部开发结束；均需正常恢复主界面后才进入。
                    if (!await EnsureMainScreenAsync(serial, token, allowAnnouncement: true))
                        throw new InvalidOperationException(L.Text("解体前未能恢复主界面。"));
                    if (!await new DismantleEntryService(_automation).OpenFromHomeAsync(serial, token))
                        throw new InvalidOperationException(L.Text("未能进入解体页面。"));
                    var rounds = await new DismantleService(_automation).RunUntilEmptyAsync(serial, Report, token);
                    if (!await EnsureMainScreenAsync(serial, token, allowAnnouncement: true))
                        throw new InvalidOperationException(L.Text("解体结束后未能恢复主界面，停止后续开发。"));
                    return rounds;
                });
            var outcome = await runner.RunAsync(targetGp, Report, ct);
            BallFactoryView.BallFactoryStatusText.Text = outcome.Completed
                ? L.Format($"铁球工厂完成：已开发 {outcome.DevelopedUnits} 台，共 {outcome.CompletedBatches} 批；解体 {outcome.DismantleRounds} 轮，已确认无推荐单位并返回主界面。目标 {targetGp} GP，实际到账以游戏为准。")
                : L.Format($"铁球工厂已停止：{outcome.StopReason} 已完成 {outcome.CompletedBatches} 批、开发 {outcome.DevelopedUnits} 台、解体 {outcome.DismantleRounds} 轮。");
            if (outcome.Completed) _logger.Info(BallFactoryView.BallFactoryStatusText.Text);
            else _logger.Warn(BallFactoryView.BallFactoryStatusText.Text);
            endNotification.Succeeded = outcome.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            BallFactoryView.BallFactoryStatusText.Text = L.Text("铁球工厂已中止。");
            _logger.Info(BallFactoryView.BallFactoryStatusText.Text);
        }
        catch (Exception ex)
        {
            BallFactoryView.BallFactoryStatusText.Text = L.Format($"铁球工厂已停止：{ex.Message}");
            _logger.Error(BallFactoryView.BallFactoryStatusText.Text);
        }
        finally
        {
            _ballFactoryRunning = false;
            _ballFactoryCts.Dispose();
            _ballFactoryCts = null;
            BallFactoryView.BallFactoryStartButton.Content = L.Text("开始制造");
            UpdateBusyState();
            CompleteTaskEndNotification(endNotification, BallFactoryView.BallFactoryStatusText.Text);
        }
    }

    // 仅保存本窗口最后确认的输入；定位阶段只记录目标，不据此声称已获得 GP。
    private int _ballFactoryTargetGp = 30000;

    /// <summary>GP 只接受十进制整数；编辑期间允许清空或暂时低于下限，以便逐位输入。</summary>
    private void BallFactoryGpBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => c is >= '0' and <= '9');

    /// <summary>空格不经过 TextInput；Enter 提交当前值，其他编辑快捷键保持原行为。</summary>
    private void BallFactoryGpBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;
        if (e.Key == Key.Enter)
        {
            CommitBallFactoryGp();
            e.Handled = true;
        }
    }

    /// <summary>粘贴与键入采用相同字符规则，拒绝小数、负数与混合文本。</summary>
    private void BallFactoryGpBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is not string text ||
            text.Length == 0 || !text.All(c => c is >= '0' and <= '9'))
            e.CancelCommand();
    }

    /// <summary>输入变化时立即提示无效范围。</summary>
    private void BallFactoryGpBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var valid = TryReadBallFactoryGp(out _);
        BallFactoryView.BallFactoryGpHint.Text = valid ? "" : L.Text("请输入 20–100000 的整数；离开输入框时将恢复上次有效值。");
        BallFactoryView.BallFactoryGpHint.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        BallFactoryView.BallFactoryGpHint.SetResourceReference(TextBlock.ForegroundProperty, valid ? "Brush.TextDim" : "Brush.Warn");
    }

    /// <summary>统一校验范围与溢出，不将非法文本变为可执行的目标。</summary>
    private bool TryReadBallFactoryGp(out int value) =>
        int.TryParse(BallFactoryView.BallFactoryGpBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
        value is >= 20 and <= 100000;

    /// <summary>离开输入框时完成编辑，禁止越界值作为页面的已确认目标。</summary>
    private void BallFactoryGpBox_LostFocus(object sender, RoutedEventArgs e) => CommitBallFactoryGp();

    /// <summary>有效值取最近的 20 的倍数，等距时向上；先校验范围，空白、越界或溢出恢复最后确认值。</summary>
    private void CommitBallFactoryGp()
    {
        // 两端均为 20 的倍数，范围内舍入不会越界；使用整数运算避免浮点舍入差异。
        if (TryReadBallFactoryGp(out var value)) _ballFactoryTargetGp = (value + 10) / 20 * 20;
        BallFactoryView.BallFactoryGpBox.Text = _ballFactoryTargetGp.ToString(CultureInfo.InvariantCulture);
    }
}

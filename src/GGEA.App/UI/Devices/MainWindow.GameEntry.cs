using System.IO;
using System.Windows;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.UI;

/// <summary>模拟器及游戏启动、登录弹窗检测与主界面恢复。</summary>
public partial class MainWindow
{
    /// <summary>主界面确认后固定点击主画面入口的坐标，用于收敛底部导航的焦点状态。</summary>
    private const int MainScreenActivationTapX = 165;
    private const int MainScreenActivationTapY = 1032;
    private bool _starting;
    private CancellationTokenSource? _startCts;

    /// <summary>一键启动：启动模拟器 → 等待设备上线 → 打开游戏。</summary>
    private async void StartGameButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWindowTaskAsync(() => StartGameButton_ClickCoreAsync(sender, e));
    }

    /// <summary>可等待的事件业务，确保关闭窗口包含其异常处理和 finally 收尾。</summary>
    private async Task StartGameButton_ClickCoreAsync(object sender, RoutedEventArgs e)
    {
        if (_starting)
        {
            _logger?.Info(L.Text("用户请求中止启动…"));
            StartGameButton.Content = L.Text("正在停止…");
            StartGameButton.IsEnabled = false;
            _startCts?.Cancel();
            return;
        }

        using var execution = TryBeginExecution();
        if (execution is null) return;

        if (_adb is null || string.IsNullOrWhiteSpace(_config.GamePackage))
        {
            _logger?.Warn(L.Text("请先在设置中配置游戏包名"));
            return;
        }

        // 曾经选过的目标必须重新核验，不能因设备关闭而进入默认实例启动分支。
        if (DeviceCombo.SelectedItem is AdbDevice selectedDevice)
        {
            _starting = true;
            _startCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
            var ct = _startCts.Token;
            StartGameButton.Content = L.Text("中止");
            UpdateBusyState();
            ConnectionStateText.Text = L.Format($"正在打开游戏: {selectedDevice.Serial}");
            try
            {
                if (await GetOnlineTaskSerialAsync(execution, ConnectionStateText, ct) is null) return;
                await SyncDeviceServicesAsync(selectedDevice.Serial, ct: ct);
                await _adb.LaunchAppAsync(selectedDevice.Serial, _config.GamePackage, ct);
                _preferredDeviceSerial = selectedDevice.Serial;
                await RefreshDevicesAsync(ct);
                await OpenGameAndEnterAsync(selectedDevice.Serial, ct);
            }
            catch (OperationCanceledException)
            {
                ConnectionStateText.Text = L.Text("已中止启动");
                _logger?.Warn(L.Text("启动游戏已中止"));
            }
            catch (Exception ex)
            {
                _logger?.Error(L.Format($"打开游戏失败: {ex.Message}"));
                ConnectionStateText.Text = L.Format($"打开游戏失败: {ex.Message}");
            }
            finally
            {
                _starting = false;
                _startCts?.Dispose();
                _startCts = null;
                StartGameButton.Content = L.Text("启动并打开游戏");
                UpdateBusyState();
            }

            return;
        }

        if (_emulator is null)
        {
            _logger?.Warn(L.Text("未配置模拟器启动服务"));
            return;
        }

        _starting = true;
        _startCts = CancellationTokenSource.CreateLinkedTokenSource(_windowTasks.Token);
        var launchCt = _startCts.Token;
        StartGameButton.Content = L.Text("中止");
        UpdateBusyState();
        ConnectionStateText.Text = L.Text("正在启动模拟器…");
        try
        {
            // 没有选择不等于允许使用默认实例，尤其是原目标刚离线的情况。
            if (!string.IsNullOrEmpty(_preferredDeviceSerial) ||
                (await _adb.ListDevicesAsync(launchCt)).Any(device => device.IsAvailable))
                throw new AdbException(L.Text("请先在设备列表中明确选择目标；原目标离线时不会自动启动或操作其他实例"));
            var options = new EmulatorLaunchOptions
            {
                EmulatorType = _config.EmulatorType,
                EmulatorPath = _config.EmulatorPath,
                EmulatorArgs = _config.EmulatorArgs,
                GamePackage = _config.GamePackage,
                DeviceWaitTimeoutMs = _config.DeviceWaitTimeoutMs,
                BootCompletedTimeoutMs = _config.BootCompletedTimeoutMs,
                BeforeGameLaunchAsync = async (serial, ct) =>
                {
                    var busy = await ClaimTaskDeviceAsync(execution, serial, Guid.Empty, ct);
                    if (busy is not null) throw new AdbException(busy);
                    await SyncDeviceServicesAsync(serial, ct: ct, newlyStarted: true);
                },
            };

            var result = await _emulator.StartAndOpenGameAsync(options, launchCt);
            ConnectionStateText.Text = result.Success
                ? L.Format($"已打开游戏: {_config.GamePackage}")
                : L.Format($"启动失败: {result.Message}");
            _logger?.Info(L.Format($"一键启动完成: {result.Message}"));

            if (result.Success)
            {
                _preferredDeviceSerial = result.Serial;
                Guid.TryParse(await _adb.GetBootIdAsync(result.Serial!, launchCt), out _preferredDeviceBootId);
            }

            await RefreshDevicesAsync(launchCt);
            if (result.Success) await OpenGameAndEnterAsync(result.Serial!, launchCt);
        }
        catch (OperationCanceledException)
        {
            ConnectionStateText.Text = L.Text("已中止启动");
            _logger?.Warn(L.Text("启动游戏已中止"));
        }
        catch (Exception ex)
        {
            _logger?.Error(L.Format($"一键启动失败: {ex.Message}"));
            ConnectionStateText.Text = L.Format($"启动失败: {ex.Message}");
        }
        finally
        {
            _starting = false;
            _startCts?.Dispose();
            _startCts = null;
            StartGameButton.Content = L.Text("启动并打开游戏");
            UpdateBusyState();
        }
    }

    /// <summary>打开游戏并执行入口流程（tap to start → 确认主界面），支持取消。</summary>
    private async Task OpenGameAndEnterAsync(string serial, CancellationToken ct)
    {
        if (_gameEntry is null)
        {
            return;
        }

        // 两个启动入口都先打开游戏并等待显示建立，再检查真实游戏画面；原生截图不能用于启动前检查。
        await Task.Delay(Math.Max(0, _config.SplashDelayMs), ct);
        if (!await EnsureDeviceCompatibilityAsync(serial, ct))
        {
            return;
        }

        var templatePath = ConfigService.ResolvePath(_config.TapToStartTemplate);
        var wait = new WaitOptions
        {
            TimeoutMs = _config.TapToStartTimeoutMs,
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = _config.DefaultMatchThreshold,
        };
        var alreadyHome = _mainScreenNavigator is not null && await _mainScreenNavigator.IsMainScreenAsync(serial, ct);
        var entered = alreadyHome || await _gameEntry.EnterGameAsync(
            serial: serial,
            tapToStartTemplatePath: templatePath,
            splashDelayMs: 0, // 启动画面等待已在兼容性截图前完成，避免重复等待。
            networkSettleDelayMs: 1500,
            waitOptions: wait,
            tapToNextTemplatePath: ConfigService.ResolvePath(_config.TapToNextTemplate),
            loginRewardWaitMs: _config.LoginRewardWaitMs,
            tapToNextLoginRewardTemplatePath: ConfigService.ResolvePath(_config.TapToNextLoginRewardTemplate),
            loginRewardSkipTemplatePath: ConfigService.ResolvePath(_config.LoginRewardSkipTemplate),
            loginRewardFallbackDelayMs: _config.LoginRewardFallbackDelayMs,
            loginRewardAnnouncementTemplatePath: ConfigService.ResolvePath(_config.LoginRewardAnnouncementTemplate),
            loginRewardAnnouncementCloseTemplatePath: ConfigService.ResolvePath(_config.LoginRewardAnnouncementCloseTemplate),
            loginRewardAnnouncementWaitMs: _config.LoginRewardAnnouncementWaitMs,
            loginRewardPageWaitMs: _config.LoginRewardPageWaitMs,
            ct: ct);

        if (await IsHumanVerificationVisibleAsync(serial, ct))
        {
            ConnectionStateText.Text = L.Text("检测到人机验证，请检查网络状况");
            _logger?.Warn(L.Text("检测到人机验证，请检查网络状况"));
            return;
        }

        if (await IsDataSyncConfirmVisibleAsync(serial, ct))
        {
            ConnectionStateText.Text = L.Text("检测到确认解除数据同步，请检查当前状况");
            _logger?.Warn(L.Text("检测到确认解除数据同步，请检查当前状况"));
            return;
        }

        if (await IsDownloadPromptVisibleAsync(serial, ct, confirmButton: alreadyHome))
        {
            ConnectionStateText.Text = L.Text("检测到下载确认，请检查当前状况");
            _logger?.Warn(L.Text("检测到下载确认，请检查当前状况"));
            return;
        }

        if (alreadyHome)
        {
            _logger?.Info(L.Text("已进入主界面"));
            ConnectionStateText.Text = L.Text("已进入主界面");
            return;
        }

        if (entered && _mainScreenNavigator is not null)
        {
            var (isMain, humanVerification, dataSyncConfirm, downloadPrompt) =
                await WaitForMainScreenOrVerificationAsync(serial, ct);
            if (humanVerification)
            {
                ConnectionStateText.Text = L.Text("检测到人机验证，请检查网络状况");
                return;
            }

            if (dataSyncConfirm)
            {
                ConnectionStateText.Text = L.Text("检测到确认解除数据同步，请检查当前状况");
                return;
            }

            if (downloadPrompt)
            {
                ConnectionStateText.Text = L.Text("检测到下载确认，请检查当前状况");
                return;
            }

            ConnectionStateText.Text = isMain
                ? L.Text("已进入主界面")
                : L.Text("已进入游戏，但未确认主界面");
        }
        else
        {
            ConnectionStateText.Text = entered
                ? L.Text("已进入游戏")
                : L.Text("已打开游戏，但未完成 tap to start");
        }
    }

    /// <summary>等待主界面，同时轮询人机验证；命中验证时立即中止。</summary>
    private async Task<(bool IsMain, bool HumanVerification, bool DataSyncConfirm, bool DownloadPrompt)> WaitForMainScreenOrVerificationAsync(
        string serial,
        CancellationToken ct)
    {
        if (_mainScreenNavigator is null)
        {
            return (false, false, false, false);
        }

        var humanVerification = false;
        var dataSyncConfirm = false;
        var downloadPrompt = false;
        // 特殊状态由 UI 呈现；等待预算、连续帧与超时诊断归统一流程服务所有。
        async Task<bool> StopWhenAsync(CancellationToken token)
        {
            if (await IsHumanVerificationVisibleAsync(serial, token))
            {
                humanVerification = true;
                _logger?.Warn(L.Text("检测到人机验证，请检查网络状况"));
                return true;
            }
            if (await IsDataSyncConfirmVisibleAsync(serial, token))
            {
                dataSyncConfirm = true;
                _logger?.Warn(L.Text("检测到确认解除数据同步，请检查当前状况"));
                return true;
            }
            if (await IsDownloadPromptVisibleAsync(serial, token))
            {
                downloadPrompt = true;
                _logger?.Warn(L.Text("检测到下载确认，请检查当前状况"));
                return true;
            }
            return false;
        }
        var isMain = await _mainScreenNavigator.WaitForMainScreenAsync(serial,
            _config.MainScreenWaitMs, ct: ct, stopWhen: StopWhenAsync);
        return (isMain, humanVerification, dataSyncConfirm, downloadPrompt);
    }

    private Task<bool> IsHumanVerificationVisibleAsync(string serial, CancellationToken ct)
        => IsEntryDialogVisibleAsync(serial, _config.HumanVerificationTemplate, ct);

    private Task<bool> IsDataSyncConfirmVisibleAsync(string serial, CancellationToken ct)
        => IsEntryDialogVisibleAsync(serial, _config.DataSyncConfirmTemplate, ct);

    private Task<bool> IsDownloadPromptVisibleAsync(string serial, CancellationToken ct, bool confirmButton = false)
        => IsEntryDialogVisibleAsync(serial, _config.DownloadPromptTemplate, ct, confirmButton);

    /// <summary>亮色横幅与浅色弹窗的平均灰度相似；同帧复核具体文字笔画后才能报告入口受阻。</summary>
    private async Task<bool> IsEntryDialogVisibleAsync(string serial, string path, CancellationToken ct, bool confirmButton = false)
    {
        if (_automation is null || _matcher is null) return false;
        var screen = await _automation.CaptureScreenAsync(serial, ct);
        return await Task.Run(() =>
        {
            var title = TemplateFallback.Any(ConfigService.ResolvePath(path), candidate =>
            {
                var template = ImageCodec.DecodePng(File.ReadAllBytes(candidate));
                var match = _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
                    template.Width, template.Height, .80, ct: ct);
                return match is not null && new AlphaMaskedTemplateMatcher(template).Match(screen, match.Region, .80, ct) is not null;
            }, ct);
            return title && (!confirmButton || _downloadDialogService?.RecognizeForNavigation(screen).Download is not null);
        }, ct);
    }

    /// <summary>
    /// 确保游戏处于主界面。
    /// 子任务间优先识别主界面按钮，未命中时只尝试左上角返回按钮；不处理公告。
    /// </summary>
    private Task<bool> EnsureMainScreenAsync(string serial, CancellationToken ct = default)
        => EnsureMainScreenAsync(serial, ct, allowAnnouncement: false);

    /// <summary>仅开始任务的首次主界面恢复允许公告检测；子任务和后续导航默认禁用。</summary>
    private Task<bool> EnsureMainScreenAsync(string serial, CancellationToken ct, bool allowAnnouncement)
    {
        if (_mainScreenNavigator is null)
            throw new InvalidOperationException(L.Text("主界面恢复服务尚未初始化"));
        Func<CancellationToken, Task<bool>>? closeAnnouncement = allowAnnouncement && _gameEntry is not null
            ? token => _gameEntry.TryClickAnnouncementCloseIfVisibleAsync(serial,
                ConfigService.ResolvePath(_config.LoginRewardAnnouncementCloseTemplate),
                ConfigService.ResolvePath(_config.LoginRewardAnnouncementTemplate), token)
            : null;
        return _mainScreenNavigator.RecoverAsync(serial, _config.MainScreenWaitMs,
            _config.MainScreenBackSettleDelayMs, MainScreenActivationTapX, MainScreenActivationTapY, ct, closeAnnouncement);
    }
}

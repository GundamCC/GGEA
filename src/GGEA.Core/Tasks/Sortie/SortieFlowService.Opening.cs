using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    /// <summary>下载后确认实际页面：开场文字、已进入战斗或已进入结算，不依赖必然出现的开场提示。</summary>
    private async Task<SortieFlowResult> WaitTapToNextAndConfigureAsync(
        string serial,
        int timeoutMs,
        int tapToNextPollIntervalMs,
        int iconClickIntervalMs,
        int battleIconDelayMs,
        int tapToNextClickDelayMs,
        int continueStartDelayMs,
        int continueClickDelayMs,
        int continuePollIntervalMs,
        int continueWaitTimeoutMs,
        int continueRepeatIntervalMs,
        bool moreRoundsRemaining,
        int againStartDelayMs,
        int againPollIntervalMs,
        int againWaitTimeoutMs,
        CancellationToken ct, FarmRecoverySession recovery)
    {
        var startedAt = Environment.TickCount64;
        var deadline = startedAt + Math.Max(0, timeoutMs);
        var capacity = new DownloadCapacityWatch(_automation, _log);
        capacity.Start();
        var nextProgressAt = startedAt;
        TemplateMatchResult? tapToNext = null;
        var battleReady = false;
        var settlementReady = false;
        var openingFrames = 0;
        _log.Info("已点击下载，等待下载/加载完成，检查开场 TAP TO NEXT、战斗图标及结算页面…");
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var screen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (await HandleTerminalStateAsync(serial, screen, ct) is { } terminal) return terminal;

            await capacity.CheckAsync(serial, screen, ct);
            // 专属开场目标由本流程点击；全局恢复不能抢先吃掉文字，绕过 AUTO/速度配置。
            // 首张下载后截图也参与识别，避免丢掉短暂出现的开场帧。
            tapToNext = await MatchTapToNextAsync(screen, ct, recovery.ForAutoProgress);
            if (tapToNext is not null) { recovery.Watchdog.Observe(SortiePage.TapToNext); break; }
            settlementReady = IsSettlementPage(screen);
            if (settlementReady)
            {
                _log.Info("下载后已识别到结算页面，接续本轮结算，不再等待开场 TAP TO NEXT。");
                break;
            }
            var controls = HasBattleControls(screen);
            if (controls || openingFrames > 0) openingFrames++;
            battleReady = controls && (recovery.ForAutoProgress || openingFrames >= OpeningObservationFrames);
            if (battleReady)
            {
                recovery.Watchdog.Observe(SortiePage.Battle);
                _log.Info("下载后已识别到战斗图标，配置战斗后继续等待本轮结算。");
                break;
            }
            var now = Environment.TickCount64;
            if (now >= nextProgressAt)
            {
                _log.Info($"下载/加载后尚未确认开场、战斗或结算页面，继续等待（已等待 {(now - startedAt) / 1000}s，上限 {timeoutMs / 1000}s）。");
                nextProgressAt = now + 15000;
            }
            var delay = Math.Max(1, tapToNextPollIntervalMs);
            if (!recovery.ForAutoProgress && (now - startedAt < GameTiming.AnimationSettleMs ||
                openingFrames is > 0 and < OpeningObservationFrames)) delay = Math.Min(delay, GameTiming.ConfirmationIntervalMs);
            await Task.Delay(delay, ct);
        }
        ct.ThrowIfCancellationRequested();
        if (tapToNext is null && !battleReady && !settlementReady)
        {
            _log.Warn("下载后等待开场、战斗或结算页面超时，已停止本轮出击流程；请检查下载进度或当前游戏画面。");
            return SortieFlowResult.TapToNextTimeout;
        }

        if (!settlementReady)
        {
            var configured = await ConfigureAndAdvanceOpeningAsync(serial, tapToNext, iconClickIntervalMs,
                battleIconDelayMs, tapToNextClickDelayMs, ct);
            if (configured.Terminal is { } configurationTerminal) return configurationTerminal;
            settlementReady = configured.SettlementReady;
        }

        return await CompleteRoundAsync(
            serial,
            moreRoundsRemaining,
            settlementReady ? 0 : continueStartDelayMs,
            continueClickDelayMs,
            continuePollIntervalMs,
            continueWaitTimeoutMs,
            continueRepeatIntervalMs,
            againStartDelayMs,
            againPollIntervalMs,
            againWaitTimeoutMs,
            ct, recovery: recovery);
    }

    /// <summary>开场提示可能闪烁，先保留 OCR 命中位置。点击提示会触发剧情，必须先处理仍可见的 AUTO 和倍速。</summary>
    private async Task<(bool SettlementReady, SortieFlowResult? Terminal)> ConfigureAndAdvanceOpeningAsync(
        string serial, TemplateMatchResult? opening, int iconClickIntervalMs, int battleIconDelayMs,
        int clickDelayMs, CancellationToken ct)
    {
        var configured = await ConfigureBattleIconsAsync(serial, iconClickIntervalMs, battleIconDelayMs, ct,
            stopAtSettlement: true);
        if (configured.Terminal is { } terminal) return (false, terminal);
        if (IsSettlementPage(configured.Screen)) return (true, null);
        if (opening is not null)
        {
            if (clickDelayMs > 0) await Task.Delay(clickDelayMs, ct);
            await _automation.TapAsync(serial, opening.CenterX, opening.CenterY, ct);
            _log.Info($"已点击 TAP TO NEXT: ({opening.CenterX},{opening.CenterY})");
        }
        return (false, null);
    }

    /// <summary>
    /// 配置战斗界面右上角图标：AUTO 切换到“全部自动”（关闭→全部自动→敌军自动→关闭，按需点击），
    /// 二倍速若为关闭状态则点击开启。模板仅用于识别状态，点击位置使用固定坐标
    /// （AUTO 在二倍速按钮左侧），避免 AUTO 模板误匹配到二倍速图标。
    /// </summary>
    private async Task<(ScreenImage Screen, SortieFlowResult? Terminal)> ConfigureBattleIconsAsync(
        string serial,
        int iconClickIntervalMs,
        int battleIconDelayMs,
        CancellationToken ct,
        bool stopAtSettlement = false)
    {
        // 进入按钮配置阶段后等待界面稳定（默认 2 秒），再开始识别右上角战斗图标。
        await Task.Delay(Math.Max(1, battleIconDelayMs), ct);

        var screen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
        if (await HandleTerminalStateAsync(serial, screen, ct) is { } terminal) return (screen, terminal);
        if (stopAtSettlement && IsSettlementPage(screen)) return (screen, null);

        // 统一使用字形与颜色证据，每次点击后重新截图；未确认蓝色时不能声称已开启自动。
        var configuredAuto = await EnsureAllAutoAsync(serial, screen, BattleAutoRecognizer(), iconClickIntervalMs, ct);
        if (configuredAuto.Terminal is { } autoTerminal) return (configuredAuto.Screen, autoTerminal);
        if (IsSettlementPage(configuredAuto.Screen)) return (configuredAuto.Screen, null);
        // AUTO 已操作时复用刚取得的确认帧；未操作时按原间隔取新帧，避免额外等待使证据变旧。
        if (!configuredAuto.Acted) await Task.Delay(Math.Max(1, iconClickIntervalMs), ct);
        // 2. 二倍速：关闭状态点击开启。
        var speedScreen = configuredAuto.Acted ? configuredAuto.Screen
            : ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
        if (await HandleTerminalStateAsync(serial, speedScreen, ct) is { } speedTerminal) return (speedScreen, speedTerminal);
        if (stopAtSettlement && IsSettlementPage(speedScreen)) return (speedScreen, null);
        var speed = RecognizeSpeed(speedScreen, ct);
        if (speed?.State == SpeedButtonState.Off)
        {
            await _automation.TapAsync(serial, SpeedButtonCenter.X, SpeedButtonCenter.Y, ct);
            _speedVerificationSerial = serial;
            _log.Info("颜色确认二倍速关闭，已点击，等待后续画面确认开启。");
        }
        else if (speed?.State == SpeedButtonState.On)
            _log.Info("颜色确认二倍速已开启，无需操作");
        else if (speed is not null)
            _log.Warn("二倍速字形存在但颜色证据不足，暂不切换速度");
        else
            _log.Info("当前画面未识别到二倍速双箭头，暂不切换速度");

        // 返回最新截图尺寸，供未命中 TAP TO NEXT 的分支精确点击画面中央。
        return (speedScreen, null);
    }

    /// <summary>同时确认 AUTO 与速度的字形、颜色；首轮要配置 AUTO，因此也接受已识别的关闭态。</summary>
    private bool HasBattleControls(ScreenImage screen, bool allowClosedAuto = false)
    {
        var auto = BattleAutoRecognizer().Recognize(screen, AutoToggleRoi)?.State;
        return (auto is AutoButtonState.All or AutoButtonState.Enemy || allowClosedAuto && auto == AutoButtonState.Close)
            && RecognizeSpeed(screen)?.State is SpeedButtonState.On or SpeedButtonState.Off;
    }

    /// <summary>按需缓存真实速度字形；所有战斗入口共用颜色状态判定。</summary>
    private SpeedButtonMatch? RecognizeSpeed(ScreenImage screen, CancellationToken ct = default)
        => (_speedRecognizer ??= new SpeedButtonRecognizer(_speedOnTemplatePath, _speedOffTemplatePath))
            .Recognize(screen, SpeedButtonRoi(screen), ct);

    /// <summary>二倍速图标识别区域：右侧 300 像素、顶部 115 像素。</summary>
    private static Roi SpeedButtonRoi(ScreenImage screen) =>
        new(
            screen.Width - 300,
            0,
            300,
            115);
}

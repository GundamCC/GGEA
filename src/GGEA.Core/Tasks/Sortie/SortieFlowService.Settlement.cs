using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    /// <summary>收尾一轮刷取：处理成绩/奖励继续及有限补点，确认结算完成页后按剩余次数处理。</summary>
    private async Task<SortieFlowResult> CompleteRoundAsync(
        string serial,
        bool moreRoundsRemaining,
        int continueStartDelayMs,
        int continueClickDelayMs,
        int continuePollIntervalMs,
        int continueWaitTimeoutMs,
        int continueRepeatIntervalMs,
        int againStartDelayMs,
        int againPollIntervalMs,
        int againWaitTimeoutMs,
        CancellationToken ct, FarmRecoverySession recovery)
    {
        var continueResult = await ClickContinueUntilGoneAsync(
            serial,
            continueStartDelayMs,
            continueClickDelayMs,
            continuePollIntervalMs,
            continueWaitTimeoutMs,
            continueRepeatIntervalMs,
            ct, recovery: recovery);
        if (continueResult is { } terminal) return terminal;

        return await WaitAndHandleAgainAsync(
            serial,
            moreRoundsRemaining,
            continueClickDelayMs,
            againStartDelayMs,
            againPollIntervalMs,
            againWaitTimeoutMs,
            ct, recovery: recovery);
    }

    /// <summary>
    /// 战斗结束后识别“继续”按钮：图标配置完成后开始观察，每 5 秒重试一次；
    /// 每帧先检查失败与 AP 不足，命中立即终止，不能被结算按钮覆盖；
    /// 点击后最多重试一次，且每次都重新确认当前页面；结算完成页的“再次出击 + 选择关卡”
    /// 双模板优先于继续识别，避免将右下角“选择关卡”误点为“继续”。
    /// </summary>
    /// <returns>null 表示可以进入下一结算阶段；非空时原样向调用方传递终止原因。</returns>
    private async Task<SortieFlowResult?> ClickContinueUntilGoneAsync(
        string serial,
        int continueStartDelayMs,
        int continueClickDelayMs,
        int continuePollIntervalMs,
        int continueWaitTimeoutMs,
        int continueRepeatIntervalMs,
        CancellationToken ct, FarmRecoverySession recovery)
    {
        // 保留五秒以内的手动动画时间；长战斗直接观察结算，不先强制等待。
        if (continueStartDelayMs <= 5000) await Task.Delay(Math.Max(1, continueStartDelayMs), ct);

        var screen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
        if (await HandleTerminalStateAsync(serial, screen, ct) is { } initialTerminal) return initialTerminal;
        var waitBudget = TimeSpan.FromMilliseconds(Math.Max(0, continueWaitTimeoutMs));
        var lastBattleProgress = recovery.Clock.GetTimestamp();
        ScreenImage? previousAutoBattle = null;

        while (recovery.Clock.GetElapsedTime(lastBattleProgress) < waitBudget)
        {
            ct.ThrowIfCancellationRequested();

            var fresh = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (await HandleTerminalStateAsync(serial, fresh, ct) is { } terminal) return terminal;

            if (await HandleSettlementContinuationAsync(serial, fresh, ct, recovery.ForAutoProgress,
                sortieOpening: !recovery.ForAutoProgress)) continue;

            var match = Match(fresh, _continueTemplatePath, ContinueButtonRoi(fresh), ContinueTemplateThreshold);
            // “选择关卡”与“继续”模板存在相似度；终局页双按钮命中时，只有选择关卡模板
            // 明显优于继续模板，才把它当成终局页，避免中间继续页的背景误匹配抢先结束流程。
            var resultPage = TryRecognizeResultPageBeforeContinue(fresh, match, out var again, out var selectStage, recovery.ForAutoProgress);
            if (resultPage)
            {
                _log.Info(
                    $"识别到结算完成页（再次出击 {again!.Confidence:P1}，选择关卡 {selectStage!.Confidence:P1}），不点击继续按钮");
                return null;
            }

            if (match is null && StopForUnexpectedFarmPreparation(fresh, recovery, ct))
                return SortieFlowResult.UnexpectedPreparation;

            if (match is not null && IsCurrentContinueCandidate(match, selectStage))
            {
                // 必须先识别到继续按钮：记录识别位置与置信度，等待界面稳定（1.5 秒）后再点击
                _log.Info(
                    $"识别到继续按钮: ({match.CenterX},{match.CenterY})，置信度 {match.Confidence:P1}，" +
                    $"{continueClickDelayMs}ms 后点击");
                await Task.Delay(Math.Max(1, continueClickDelayMs), ct);
                await _automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
                _log.Info($"已点击继续按钮: ({match.CenterX},{match.CenterY})，置信度 {match.Confidence:P1}");
                recovery.Watchdog.Observe(SortiePage.Continue);
                return await ClickContinueOnceMoreAsync(
                    serial,
                    screen,
                    match,
                    continueClickDelayMs,
                    continueRepeatIntervalMs,
                    continueWaitTimeoutMs,
                    ct, recovery: recovery);
            }

            // 等待首次结算也需要两分钟 AUTO 健康检查，蓝色继续等；未知动画不授权切换。
            var recoveryResult = await RecoverFarmIfStalledAsync(serial, fresh, recovery, FarmRecoveryStage.Battle, ct);
            if (recoveryResult.Terminal is { } recoveryTerminal) return recoveryTerminal;
            if (recoveryResult.ObservedAutoBattle)
            {
                // 仅在两次低频检查均确认全部自动、速度控件且场景确实变化时续等。静止 HUD 不能无限续期。
                if (previousAutoBattle is not null && ExecutionStallMonitor.HasSceneChange(previousAutoBattle, fresh))
                    lastBattleProgress = recovery.Clock.GetTimestamp();
                previousAutoBattle = fresh;
            }
            if (recoveryResult.Acted) continue;
            await Task.Delay(Math.Max(1, continuePollIntervalMs), ct);
        }

        _log.Warn($"等待继续按钮超时（{continueWaitTimeoutMs}ms）");
        return SortieFlowResult.ContinueTimeout;
    }

    /// <summary>
    /// 点击继续按钮后持续复查（网络缓慢时第二次继续按钮可能延迟出现）：
    /// 识别到第二个继续按钮则点击当前识别位置一次；若直接出现结算完成页则提前进入下一阶段。
    /// </summary>
    /// <returns>null 表示可以进入下一结算阶段；非空时原样向调用方传递终止原因。</returns>
    private async Task<SortieFlowResult?> ClickContinueOnceMoreAsync(
        string serial,
        ScreenImage screen,
        TemplateMatchResult match,
        int continueClickDelayMs,
        int continueRepeatIntervalMs,
        int continueWaitTimeoutMs,
        CancellationToken ct, FarmRecoverySession recovery)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, continueWaitTimeoutMs);
        // 必须先识别到奖励图标，之后才会处理（点击）第二个继续按钮
        var rewardSeen = false;
        // 第一个继续按钮已经点击过；网络延迟时最多补点一次，绝不允许无限沿用旧坐标点击。
        var firstContinueRetryCount = 0;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(Math.Max(1, continueRepeatIntervalMs), ct);

            var fresh = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (await HandleTerminalStateAsync(serial, fresh, ct) is { } terminal) return terminal;
            var recovered = await RecoverFarmIfStalledAsync(serial, fresh, recovery, FarmRecoveryStage.Reward, ct);
            if (recovered.Terminal is { } recoveryTerminal) return recoveryTerminal;
            if (recovered.Acted) { await Task.Delay(Math.Max(1, continueRepeatIntervalMs), ct); continue; }
            if (recovered.RetryContinue) firstContinueRetryCount = 0;

            if (await HandleSettlementContinuationAsync(serial, fresh, ct, recovery.ForAutoProgress)) continue;

            // 页面标题可能被奖励横幅遮挡；根据当前按钮和双按钮置信度判定页面，不能依赖标题单点。
            var currentContinue = Match(fresh, _continueTemplatePath, ContinueButtonRoi(fresh), ContinueTemplateThreshold);
            var resultPage = TryRecognizeResultPageBeforeContinue(fresh, currentContinue, out var again, out var selectStage, recovery.ForAutoProgress);
            if (resultPage)
            {
                _log.Info(
                    $"识别到结算完成页（再次出击 {again!.Confidence:P1}，选择关卡 {selectStage!.Confidence:P1}），不点击继续按钮");
                return null;
            }

            // 识别奖励/第二个继续按钮的同时检查玩家升级提示：命中则点击画面关闭弹窗，等待后继续轮询
            var levelUp = Match(fresh, _playerLevelUpTemplatePath, PlayerLevelUpRoi(fresh));
            if (levelUp is not null)
            {
                await _automation.TapAsync(serial, levelUp.CenterX, levelUp.CenterY, ct);
                _log.Info($"识别到玩家升级，已点击画面关闭: ({levelUp.CenterX},{levelUp.CenterY})");
                await Task.Delay(Math.Max(1, PlayerLevelUpDismissDelayMs), ct);
                continue;
            }

            // 奖励图标：识别到后标记，之后才允许处理右下角的第二个继续按钮
            var reward = Match(fresh, _rewardTemplatePath, RewardRoi(fresh));
            if (reward is not null)
            {
                if (!rewardSeen)
                {
                    recovery.Watchdog.Observe(SortiePage.Reward);
                    rewardSeen = true;
                    _log.Info("识别到奖励图标，开始等待第二个继续按钮");
                }
            }

            // 已知结算页面无需额外扫描准备页；只有缺失正常页面证据时才执行双按钮排查。
            if (currentContinue is null && reward is null && StopForUnexpectedFarmPreparation(fresh, recovery, ct))
                return SortieFlowResult.UnexpectedPreparation;

            if (!rewardSeen)
            {
                // 仅在当前帧仍高置信度命中“继续”时补点一次；不能使用上一次的坐标盲点，
                // 否则转场后的“选择关卡”会被反复点击。
                if (currentContinue is not null && IsCurrentContinueCandidate(currentContinue, selectStage) && firstContinueRetryCount == 0)
                {
                    firstContinueRetryCount++;
                    _log.Info("尚未识别到奖励图标，当前帧仍为继续页，补点一次继续按钮…");
                    await _automation.TapAsync(serial, currentContinue.CenterX, currentContinue.CenterY, ct);
                    await Task.Delay(Math.Max(1, ContinueRetryDelayMs), ct);
                }
                else if (firstContinueRetryCount > 0)
                {
                    _log.Info("尚未识别到奖励图标，已完成一次继续补点，继续等待页面转场…");
                }
                else
                {
                    _log.Info("尚未识别到奖励图标，当前帧未确认继续页，继续等待页面转场…");
                }
                continue;
            }

            if (currentContinue is not null && IsCurrentContinueCandidate(currentContinue, selectStage))
            {
                // 奖励图标已识别且第二个继续按钮出现：等待界面稳定（默认 1 秒）再点击
                await Task.Delay(Math.Max(1, continueClickDelayMs), ct);
                await _automation.TapAsync(serial, currentContinue.CenterX, currentContinue.CenterY, ct);
                _log.Info(
                    $"识别到第二个继续按钮（置信度 {currentContinue.Confidence:P1}），点击当前识别位置: " +
                    $"({currentContinue.CenterX},{currentContinue.CenterY})");
                return null;
            }

            _log.Info("继续按钮暂未再次出现，继续等待…");
        }

        _log.Warn("等待第二个继续按钮超时");
        return SortieFlowResult.ContinueTimeout;
    }

    /// <summary>
    /// 第二次继续点击后等待结算完成；复核右侧选择关卡与继续的歧义，再按剩余次数再次出击或退出。
    /// 若继续点击未生效，最多重新确认后补点一次；持续未转场不能计数或无限点击。
    /// </summary>
    private async Task<SortieFlowResult> WaitAndHandleAgainAsync(
        string serial,
        bool moreRoundsRemaining,
        int continueClickDelayMs,
        int againStartDelayMs,
        int againPollIntervalMs,
        int againWaitTimeoutMs,
        CancellationToken ct, FarmRecoverySession recovery)
    {
        // 第二次点击继续按钮后等待 2 秒，再开始识别再次出击按钮
        await Task.Delay(Math.Max(1, againStartDelayMs), ct);

        var screen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
        if (await HandleTerminalStateAsync(serial, screen, ct) is { } initialTerminal) return initialTerminal;
        var roi = AgainButtonRoi(screen);
        var deadline = Environment.TickCount64 + Math.Max(0, againWaitTimeoutMs);
        var continueRetried = false;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var fresh = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (await HandleTerminalStateAsync(serial, fresh, ct) is { } terminal) return terminal;
            var recovered = await RecoverFarmIfStalledAsync(serial, fresh, recovery, FarmRecoveryStage.Result, ct);
            if (recovered.Terminal is { } recoveryTerminal) return recoveryTerminal;
            if (recovered.Acted) { await Task.Delay(Math.Max(1, againPollIntervalMs), ct); continue; }
            if (recovered.RetryContinue) continueRetried = false;

            if (await HandleSettlementContinuationAsync(serial, fresh, ct, recovery.ForAutoProgress)) continue;

            // 与再次出击识别并行：识别玩家升级，命中则点击画面关闭弹窗后继续
            var levelUp = Match(fresh, _playerLevelUpTemplatePath, PlayerLevelUpRoi(screen));
            if (levelUp is not null)
            {
                await _automation.TapAsync(serial, levelUp.CenterX, levelUp.CenterY, ct);
                _log.Info($"识别到玩家升级，已点击画面关闭: ({levelUp.CenterX},{levelUp.CenterY})");
                await Task.Delay(Math.Max(1, PlayerLevelUpDismissDelayMs), ct);
                continue;
            }

            var currentContinue = Match(fresh, _continueTemplatePath, ContinueButtonRoi(fresh), ContinueTemplateThreshold);
            var resultPage = TryRecognizeResultPageBeforeContinue(fresh, currentContinue, out var match, out var selectStage, recovery.ForAutoProgress);
            if (!resultPage && currentContinue is null && StopForUnexpectedFarmPreparation(fresh, recovery, ct))
                return SortieFlowResult.UnexpectedPreparation;
            if (!resultPage && currentContinue is not null && IsCurrentContinueCandidate(currentContinue, selectStage))
            {
                if (!continueRetried)
                {
                    continueRetried = true;
                    await Task.Delay(Math.Max(1, continueClickDelayMs), ct);
                    // 等待后重新取帧，不能用旧坐标补点已经变成选择关卡的按钮。
                    var retryScreen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
                    if (await HandleTerminalStateAsync(serial, retryScreen, ct) is { } retryTerminal) return retryTerminal;
                    if (await HandleSettlementContinuationAsync(serial, retryScreen, ct, recovery.ForAutoProgress)) continue;
                    // 升级弹窗可能在补点等待中出现，交回外层优先关闭，不能越过弹窗点底部按钮。
                    if (Match(retryScreen, _playerLevelUpTemplatePath, PlayerLevelUpRoi(retryScreen)) is not null) continue;
                    var retryContinue = Match(retryScreen, _continueTemplatePath, ContinueButtonRoi(retryScreen), ContinueTemplateThreshold);
                    if (retryContinue is null && StopForUnexpectedFarmPreparation(retryScreen, recovery, ct))
                        return SortieFlowResult.UnexpectedPreparation;
                    var retryResultPage = TryRecognizeResultPageBeforeContinue(retryScreen, retryContinue, out _, out var retrySelectStage, recovery.ForAutoProgress);
                    if (!retryResultPage && IsCurrentContinueCandidate(retryContinue, retrySelectStage))
                    {
                        await _automation.TapAsync(serial, retryContinue!.CenterX, retryContinue.CenterY, ct);
                        _log.Info($"等待再次出击时仍为继续页，已重新识别并补点一次继续: ({retryContinue.CenterX},{retryContinue.CenterY})");
                    }
                }
                await Task.Delay(Math.Max(1, againPollIntervalMs), ct);
                continue;
            }

            // 旧配置没有选择关卡模板时保留单按钮兼容，但继续证据始终优先，不能误报本轮完成。
            if (string.IsNullOrWhiteSpace(_selectStageTemplatePath))
                match = Match(fresh, _againTemplatePath, roi, StageSortieTemplateThreshold);

            if (match is not null && (resultPage || string.IsNullOrWhiteSpace(_selectStageTemplatePath)))
            {
                _log.Info(
                    $"识别到再次出击按钮: ({match.CenterX},{match.CenterY})，置信度 {match.Confidence:P1}，本轮刷取完成");
                if (moreRoundsRemaining)
                {
                    await _automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
                    _log.Info($"已点击再次出击，开始下一轮: ({match.CenterX},{match.CenterY})");
                    return SortieFlowResult.RoundCompleted;
                }

                var target = recovery.ForAutoProgress && selectStage is not null
                    ? (X: selectStage.CenterX, Y: selectStage.CenterY) : BackToStageSelectButtonCenter;
                await _automation.TapAsync(serial, target.X, target.Y, ct);
                _log.Info($"全部刷取次数完成，已点击回到选择关卡: ({target.X},{target.Y})");
                return SortieFlowResult.AllRoundsCompleted;
            }

            await Task.Delay(Math.Max(1, againPollIntervalMs), ct);
        }

        _log.Warn($"等待再次出击按钮超时（{againWaitTimeoutMs}ms）");
        return SortieFlowResult.AgainTimeout;
    }
}

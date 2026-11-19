using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>
/// 出击后流程服务：处理点击出击按钮之后的界面流转。
/// 下载界面与 TAP TO NEXT 可能先后出现，同一帧内同时识别、先到先处理；
/// 进入战斗后把 AUTO 图标切换到“全部自动”，并把二倍速开启。
/// </summary>
public sealed partial class SortieFlowService
{
    // 战斗 HUD 会透过开场遮罩显示。首次看到控件后至少再观察五张新帧，覆盖文字的亮暗变化。
    private const int OpeningObservationFrames = 6;
    /// <summary>AUTO 按钮固定点击位置（当前设备 1920x1080 实测，位于二倍速按钮左侧）。</summary>
    private static readonly (int X, int Y) AutoButtonCenter = (1508, 50);

    /// <summary>二倍速按钮固定点击位置（当前设备 1920x1080 实测）。</summary>
    private static readonly (int X, int Y) SpeedButtonCenter = (1712, 50);

    /// <summary>回到选择关卡按钮固定点击位置（当前设备 1920x1080 实测；全部完成退出与失败处理共用）。</summary>
    private static readonly (int X, int Y) BackToStageSelectButtonCenter = (1670, 980);

    /// <summary>识别到玩家升级提示后点击画面，等待弹窗关闭的时长。</summary>
    private const int PlayerLevelUpDismissDelayMs = GameTiming.RecognitionIntervalMs;

    /// <summary>无进展时重试点击第一个继续按钮后的等待时长。</summary>
    private const int ContinueRetryDelayMs = 1500;

    /// <summary>再次出击关闭状态模板的匹配置信度阈值（高于普通阈值，避免误判 AP 不足）。</summary>
    private const double AgainCloseMatchThreshold = 0.95;

    /// <summary>出击流程普通模板的识别阈值；继续文字及 TAP TO NEXT 使用独立阈值。</summary>
    private const double StageSortieTemplateThreshold = 0.90;

    /// <summary>继续按钮在局部 ROI 内允许轻微像素差异；以置信度差消解与选择关卡按钮的相似命中。</summary>
    private const double ContinueTemplateThreshold = 0.90;
    /// <summary>同位置的按钮证据相差至少三个百分点才用于否决另一状态；相近分数仍结合双按钮布局。</summary>
    private const double ResultPageContinueConfidenceMargin = 0.03;

    private readonly AutomationService _automation;
    private readonly TemplateMatcher _matcher;
    private readonly TemplateImageCache _templateImages = new();
    private readonly IGgeaLog _log;
    private readonly DownloadDialogService _downloadDialogService;
    // 同一流程实例缓存字形；待复查标记仅记录已发送点击的设备，不授权额外点击。
    private SpeedButtonRecognizer? _speedRecognizer;
    private string? _speedVerificationSerial;
    private readonly string _speedOnTemplatePath;
    private readonly string _speedOffTemplatePath;
    private readonly string _continueTemplatePath;
    private readonly string _againTemplatePath;
    private readonly string _selectStageTemplatePath;
    private readonly string _againCloseTemplatePath;
    private readonly string _failedTemplatePath;
    private readonly string _emptySlotHintTemplatePath;
    private readonly string _emptySlotSortieTemplatePath;
    private readonly string _emptySlotCancelTemplatePath;
    private readonly string _noApTemplatePath;
    private readonly string _playerLevelUpTemplatePath;
    private readonly string _rewardTemplatePath;
    private readonly double _threshold;
    private readonly string _terrainTemplatePath;
    private readonly string _sortieDownloadTemplatePath;
    private readonly ApStopSignal? _apStopSignal;

    public SortieFlowService(
        AutomationService automation,
        TemplateMatcher matcher,
        IGgeaLog log,
        DownloadDialogService downloadDialogService,
        string tapToNextTemplatePath,
        string speedOnTemplatePath,
        string speedOffTemplatePath,
        string continueTemplatePath,
        string againTemplatePath,
        string selectStageTemplatePath,
        string againCloseTemplatePath,
        string failedTemplatePath,
        string emptySlotHintTemplatePath,
        string emptySlotSortieTemplatePath,
        string emptySlotCancelTemplatePath,
        string noApTemplatePath,
        string playerLevelUpTemplatePath,
        string rewardTemplatePath,
        string tapToNextRewardTemplatePath,
        double threshold,
        string terrainTemplatePath = "",
        string sortieDownloadTemplatePath = "", ApStopSignal? apStopSignal = null)
    {
        _automation = automation;
        _matcher = matcher;
        _log = log;
        _downloadDialogService = downloadDialogService;
        _speedOnTemplatePath = speedOnTemplatePath;
        _speedOffTemplatePath = speedOffTemplatePath;
        _continueTemplatePath = continueTemplatePath;
        _againTemplatePath = againTemplatePath;
        _selectStageTemplatePath = selectStageTemplatePath;
        _againCloseTemplatePath = againCloseTemplatePath;
        _failedTemplatePath = failedTemplatePath;
        _emptySlotHintTemplatePath = emptySlotHintTemplatePath;
        _emptySlotSortieTemplatePath = emptySlotSortieTemplatePath;
        _emptySlotCancelTemplatePath = emptySlotCancelTemplatePath;
        _noApTemplatePath = noApTemplatePath;
        _playerLevelUpTemplatePath = playerLevelUpTemplatePath;
        _rewardTemplatePath = rewardTemplatePath;
        _threshold = threshold;
        _terrainTemplatePath = terrainTemplatePath;
        _sortieDownloadTemplatePath = sortieDownloadTemplatePath;
        _apStopSignal = apStopSignal;
    }

    /// <summary>
    /// 点击出击后优先处理终止状态和编制、下载提示。刷本首轮可从开场、战斗或结算接续；
    /// HARD 推图沿用自己的开场配置及回退顺序。
    /// </summary>
    public async Task<SortieFlowResult> ExecuteAfterSortieAsync(
        string serial,
        bool allowDownload,
        bool allowEmptySlotSortie,
        bool moreRoundsRemaining,
        bool allowTerrainUnsuitableSortie = true,
        int downloadDetectTimeoutMs = 30000,
        int tapToNextWaitTimeoutMs = 600000,
        int tapToNextPollIntervalMs = 5000,
        int pollIntervalMs = GameTiming.RecognitionIntervalMs,
        int iconClickIntervalMs = GameTiming.RecognitionIntervalMs,
        int battleIconDelayMs = 2000,
        int tapToNextClickDelayMs = GameTiming.RecognitionIntervalMs,
        int continueStartDelayMs = 30000,
        int continueClickDelayMs = 1500,
        int continuePollIntervalMs = 5000,
        int continueWaitTimeoutMs = 900000,
        int continueRepeatIntervalMs = 2500,
        int againStartDelayMs = 2000,
        int againPollIntervalMs = 5000,
        int againWaitTimeoutMs = 600000,
        CancellationToken ct = default, TimeProvider? recoveryTimeProvider = null, string? recoverySortieTemplate = null,
        bool forAutoProgress = false)
    {
        var recovery = new FarmRecoverySession(allowDownload, allowEmptySlotSortie, allowTerrainUnsuitableSortie,
            recoverySortieTemplate, recoveryTimeProvider, forAutoProgress);
        if (!forAutoProgress) _log.Info("已点击首轮出击，开始检查编制、下载、开场、战斗和结算页面。");
        var startedAt = Environment.TickCount64;
        var deadline = startedAt + Math.Max(0, downloadDetectTimeoutMs);
        var openingFrames = 0;
        Task<SortieFlowResult> CompleteRound() => CompleteRoundAsync(serial, moreRoundsRemaining,
            continueStartDelayMs, continueClickDelayMs, continuePollIntervalMs, continueWaitTimeoutMs,
            continueRepeatIntervalMs, againStartDelayMs, againPollIntervalMs, againWaitTimeoutMs, ct, recovery);
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            // 同一帧先检查失败/AP 不足，再识别地形、空位、下载与 TAP TO NEXT。
            // 下载按钮仅在下载提示命中后匹配，避免其它页面的相似按钮触发下载流程。
            var screen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (await HandleTerminalStateAsync(serial, screen, ct) is { } initialTerminal) return initialTerminal;
            var delay = Math.Max(1, pollIntervalMs);
            if (!forAutoProgress && Environment.TickCount64 - startedAt < GameTiming.AnimationSettleMs)
                delay = Math.Min(delay, GameTiming.ConfirmationIntervalMs);

            var terrainHint = MatchIfConfigured(screen, _terrainTemplatePath, TerrainHintRoi(screen), StageSortieTemplateThreshold);
            var emptySlotHint = MatchEmptySlotDialog(screen);
            var downloadHint = MatchIfConfigured(screen, _sortieDownloadTemplatePath, DownloadHintRoi(screen), StageSortieTemplateThreshold);

            // 地形适性提示优先：按自动刷本勾选项点击出击或取消。
            if (terrainHint is not null)
            {
                var handled = await HandleTerrainDialogAsync(serial, screen, allowTerrainUnsuitableSortie, ct);
                if (handled == TerrainHandlingResult.Cancelled)
                {
                    return SortieFlowResult.TerrainCancelled;
                }

                if (handled == TerrainHandlingResult.Timeout)
                {
                    return SortieFlowResult.TerrainTimeout;
                }

                openingFrames = 0;
                await Task.Delay(delay, ct);
                continue;
            }

            // 空位出击提示：按设置点击对话框的出击或取消。
            if (emptySlotHint is not null)
            {
                var handled = await HandleEmptySlotDialogAsync(serial, screen, allowEmptySlotSortie, ct);
                if (handled == EmptySlotHandlingResult.Cancelled)
                {
                    return SortieFlowResult.EmptySlotCancelled;
                }

                if (handled == EmptySlotHandlingResult.Timeout)
                {
                    return SortieFlowResult.EmptySlotTimeout;
                }

                // 已点击空位出击确认：继续本循环识别后续提示。
                openingFrames = 0;
                await Task.Delay(delay, ct);
                continue;
            }

            // 下载提示命中后才识别下载/取消按钮，并按设置处理。
            if (downloadHint is not null)
            {
                var dialog = _downloadDialogService.Recognize(screen);
                if (!allowDownload)
                {
                    if (dialog.Cancel is not null)
                    {
                        await _automation.TapAsync(serial, dialog.Cancel.CenterX, dialog.Cancel.CenterY, ct);
                        _log.Warn("检测到下载界面，未允许下载，已点击取消");
                        return SortieFlowResult.DownloadCancelled;
                    }

                    // 不允许下载但取消按钮尚未出现：继续等待
                }
                else if (dialog.Download is not null)
                {
                    await _automation.TapAsync(serial, dialog.Download.CenterX, dialog.Download.CenterY, ct);
                    _log.Info($"检测到下载界面，已点击下载按钮: ({dialog.Download.CenterX},{dialog.Download.CenterY})");
                    return await WaitTapToNextAndConfigureAsync(
                        serial,
                        tapToNextWaitTimeoutMs,
                        tapToNextPollIntervalMs,
                        iconClickIntervalMs,
                        battleIconDelayMs,
                        tapToNextClickDelayMs,
                        continueStartDelayMs,
                        continueClickDelayMs,
                        continuePollIntervalMs,
                        continueWaitTimeoutMs,
                        continueRepeatIntervalMs,
                        moreRoundsRemaining,
                        againStartDelayMs,
                        againPollIntervalMs,
                        againWaitTimeoutMs,
                        ct, recovery: recovery);
                }

                // 下载提示仍在但按钮尚未渲染完成：保持在下载处理阶段，不能越过提示去点击其它目标。
                openingFrames = 0;
                await Task.Delay(delay, ct);
                continue;
            }

            var tapToNext = await MatchTapToNextAsync(screen, ct, recovery.ForAutoProgress);
            if (!forAutoProgress)
            {
                if (tapToNext is not null)
                {
                    recovery.Watchdog.Observe(SortiePage.TapToNext);
                }
                else if (IsSettlementPage(screen))
                {
                    _log.Info("首轮出击后已识别到结算页面，开始处理本轮结算。");
                    return await CompleteRound();
                }
                else
                {
                    var controls = HasBattleControls(screen, allowClosedAuto: true);
                    if (controls || openingFrames > 0) openingFrames++;
                    if (openingFrames < OpeningObservationFrames || !controls)
                    {
                        await Task.Delay(openingFrames is > 0 and < OpeningObservationFrames
                            ? Math.Min(delay, GameTiming.ConfirmationIntervalMs) : delay, ct);
                        continue;
                    }
                    recovery.Watchdog.Observe(SortiePage.Battle);
                    _log.Info("首轮出击后已确认 AUTO 和速度控件，开始配置战斗并等待结算。");
                }
                var configured = await ConfigureAndAdvanceOpeningAsync(serial, tapToNext, iconClickIntervalMs,
                    battleIconDelayMs, 0, ct);
                if (configured.Terminal is { } farmTerminal) return farmTerminal;
                return await CompleteRound();
            }

            // TAP TO NEXT 先出现：先完成右上角按钮配置，再点击提示开始战斗。
            if (tapToNext is not null)
            {
                recovery.Watchdog.Observe(SortiePage.TapToNext);
                var configured = await ConfigureBattleIconsAsync(serial, iconClickIntervalMs, battleIconDelayMs, ct);
                if (configured.Terminal is { } configurationTerminal) return configurationTerminal;
                // 按钮配置完成后等待画面稳定（默认 0.8 秒）再点击，避免点击无效。
                await Task.Delay(Math.Max(1, tapToNextClickDelayMs), ct);
                await _automation.TapAsync(serial, tapToNext.CenterX, tapToNext.CenterY, ct);
                _log.Info($"已点击 TAP TO NEXT: ({tapToNext.CenterX},{tapToNext.CenterY})");
                return await CompleteRound();
            }

            await Task.Delay(Math.Max(1, pollIntervalMs), ct);
        }

        if (!forAutoProgress)
        {
            _log.Warn($"首轮出击后 {downloadDetectTimeoutMs}ms 内未确认开场、战斗或结算，停止刷本并保留当前画面。");
            return SortieFlowResult.TapToNextTimeout;
        }
        _log.Warn(
            $"点击出击后 {downloadDetectTimeoutMs}ms 内未识别到地形、空位、AP 不足、下载提示或 TAP TO NEXT，" +
            "开始配置战斗图标，并点击画面中央进入战斗");
        var (battleScreen, terminal) = await ConfigureBattleIconsAsync(serial, iconClickIntervalMs, battleIconDelayMs, ct);
        if (terminal is { } fallbackTerminal) return fallbackTerminal;
        var centerX = battleScreen.Width / 2;
        var centerY = battleScreen.Height / 2;
        await _automation.TapAsync(serial, centerX, centerY, ct);
        _log.Info($"未识别到 TAP TO NEXT，已点击画面中央开始战斗: ({centerX},{centerY})");

        // 中央点击与 TAP TO NEXT 点击具有相同语义：从此处统一执行正常战斗收尾流程。
        return await CompleteRound();
    }

    /// <summary>
    /// 点击“再次出击”后开启新一轮：识别开场 TAP TO NEXT，或同帧确认已进入新战斗后接续收尾。
    /// 上一轮结算页面可能仍在过渡帧中，不能仅凭继续/奖励/终局信号完成新一轮入口确认。
    /// </summary>
    public async Task<SortieFlowResult> ExecuteNextRoundAsync(
        string serial,
        bool moreRoundsRemaining,
        int tapToNextWaitTimeoutMs = 600000,
        int tapToNextPollIntervalMs = 5000,
        int tapToNextClickDelayMs = 0,
        int continueStartDelayMs = 30000,
        int continueClickDelayMs = 1500,
        int continuePollIntervalMs = 5000,
        int continueWaitTimeoutMs = 900000,
        int continueRepeatIntervalMs = 2500,
        int againStartDelayMs = 2000,
        int againPollIntervalMs = 5000,
        int againWaitTimeoutMs = 600000,
        CancellationToken ct = default, TimeProvider? recoveryTimeProvider = null,
        bool allowDownload = true, bool allowEmptySlotSortie = false, bool allowTerrainUnsuitableSortie = false,
        string? recoverySortieTemplate = null)
    {
        var recovery = new FarmRecoverySession(allowDownload, allowEmptySlotSortie, allowTerrainUnsuitableSortie,
            recoverySortieTemplate, recoveryTimeProvider);
        // 第一帧也参与入口识别，避免短暂开场提示被丢掉。只有明确战斗控件能替代开场文字，
        // 旧结算信号、单独速度按钮或全局继续遮罩都不能授权新一轮计数。
        _log.Info("再次出击后开启新一轮，等待关卡开场 TAP TO NEXT 或战斗图标");
        var startedAt = Environment.TickCount64;
        var deadline = startedAt + Math.Max(0, tapToNextWaitTimeoutMs);
        var fastPollUntil = startedAt + GameTiming.AnimationSettleMs;
        var pollInterval = Math.Max(1, tapToNextPollIntervalMs);
        var fastPollInterval = Math.Min(pollInterval, GameTiming.ConfirmationIntervalMs);
        var nextProgressAt = startedAt + 15000;
        TemplateMatchResult? tapToNext = null;
        var openingFrames = 0;
        var battleReady = false;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var screen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (await HandleTerminalStateAsync(serial, screen, ct) is { } terminal) return terminal;
            tapToNext = await MatchTapToNextAsync(screen, ct, recovery.ForAutoProgress);
            if (tapToNext is not null) { recovery.Watchdog.Observe(SortiePage.TapToNext); break; }
            if (openingFrames > 0) openingFrames++;
            if (openingFrames > 0 && (IsSettlementPage(screen) ||
                openingFrames >= OpeningObservationFrames && HasBattleControls(screen)))
            {
                battleReady = true;
                recovery.Watchdog.Observe(SortiePage.Battle);
                _log.Info("再次出击后已识别到战斗图标，开场观察结束，开始处理本轮结算。");
                break;
            }

            var recovered = await RecoverFarmIfStalledAsync(serial, screen, recovery, FarmRecoveryStage.NextBattle, ct);
            if (recovered.Terminal is { } recoveryTerminal) return recoveryTerminal;
            // 恢复操作改变了画面，只有未经操作的同帧战斗控件能触发下一帧复查。
            if (recovered.Acted) openingFrames = 0;
            else if (openingFrames == 0 && HasBattleControls(screen)) openingFrames = 1;
            var now = Environment.TickCount64;
            if (now >= nextProgressAt)
            {
                _log.Info($"再次出击后尚未确认新开场或战斗，继续等待（已等待 {(now - startedAt) / 1000}s，上限 {tapToNextWaitTimeoutMs / 1000}s）。");
                nextProgressAt = now + 15000;
            }
            var delay = openingFrames is > 0 and < OpeningObservationFrames || now < fastPollUntil
                ? fastPollInterval : pollInterval;
            await Task.Delay((int)Math.Clamp(deadline - now, 0, delay), ct);
        }
        ct.ThrowIfCancellationRequested();
        if (tapToNext is null && !battleReady)
        {
            _log.Warn("再次出击后等待 TAP TO NEXT 或战斗图标超时，已停止本轮出击流程；请检查游戏画面及再次出击是否成功");
            return SortieFlowResult.TapToNextTimeout;
        }

        if (tapToNext is not null)
        {
            if (tapToNextClickDelayMs > 0) await Task.Delay(tapToNextClickDelayMs, ct);
            await _automation.TapAsync(serial, tapToNext.CenterX, tapToNext.CenterY, ct);
            _log.Info($"已点击 TAP TO NEXT: ({tapToNext.CenterX},{tapToNext.CenterY})");
        }

        // 2. 新一轮开场确认完成后等待战斗，再进入本轮收尾识别。
        return await CompleteRoundAsync(
            serial,
            moreRoundsRemaining,
            continueStartDelayMs,
            continueClickDelayMs,
            continuePollIntervalMs,
            continueWaitTimeoutMs,
            continueRepeatIntervalMs,
            againStartDelayMs,
            againPollIntervalMs,
            againWaitTimeoutMs,
            ct, recovery: recovery);
    }
}

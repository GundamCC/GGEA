using System.IO;
using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>出击后流程的处理结果。</summary>
public enum SortieFlowResult
{
    /// <summary>本轮完成：已点击“再次出击”，调用方应继续下一轮。</summary>
    RoundCompleted,

    /// <summary>全部刷取次数完成：已点击退出坐标。</summary>
    AllRoundsCompleted,

    /// <summary>检测到下载界面且不允许下载，已点击取消。</summary>
    DownloadCancelled,

    /// <summary>未在有限时间内确认开场或新战斗；下载后同时检查结算证据。</summary>
    TapToNextTimeout,

    /// <summary>战斗结束后等待“继续”按钮超时。</summary>
    ContinueTimeout,

    /// <summary>战斗结束后等待“再次出击”按钮超时。</summary>
    AgainTimeout,

    /// <summary>识别到刷本失败画面，已点击回到选择关卡。</summary>
    FailureDetected,

    /// <summary>出击阵容存在空位且未允许空位出击，已点击取消并提示玩家。</summary>
    EmptySlotCancelled,

    /// <summary>识别到空位出击提示但未识别到对话框按钮。</summary>
    EmptySlotTimeout,

    /// <summary>识别到地形适性提示且未允许强制出击，已点击取消。</summary>
    TerrainCancelled,

    /// <summary>识别到地形适性提示但未识别到对话框按钮。</summary>
    TerrainTimeout,

    /// <summary>识别到“再次出击”按钮关闭状态，AP 不足，已点击选择关卡并中止。</summary>
    NoAp,

    /// <summary>点击出击后识别到 AP 不足提示，已点击对话框取消并提示玩家。</summary>
    NoApCancelled,

    /// <summary>识别到 AP 不足提示但未识别到对话框取消按钮。</summary>
    NoApTimeout,

    /// <summary>自动推图已处理故事/结算并确认当前作品选关页，可以直接读取下一关。</summary>
    ProgressStageCompleted,

    /// <summary>自动推图未在限定时间内确认结算后的返回页面，不计为完成。</summary>
    ProgressTimeout,

    /// <summary>历史结果值，保留旧日志兼容；当前推图不再因 SKIP 未出现返回此结果。</summary>
    ProgressSkipTimeout,

    /// <summary>AUTO 切换后持续无法恢复全部自动或状态证据不足，停止本关而非无限点击/等待。</summary>
    ProgressAutoTimeout,

    /// <summary>自动推图已确认返回主界面/主要关卡；本关完成，但继续同一作品前必须重新导航。</summary>
    ProgressWorkNavigationRequired,

    /// <summary>卡顿恢复确认已在选关页；重新读取实际节点，不将本次观察计为战斗完成。</summary>
    ProgressResumeStage,

    /// <summary>卡顿恢复确认已回主界面或主要关卡；保留当前作品队列并重新导航，不计通关。</summary>
    ProgressResumeWork,

    /// <summary>卡顿恢复确认仍在准备页；交回本关编制会话重新检查后出击，不直接盲点。</summary>
    ProgressResumePreparation,

    /// <summary>连续两个两分钟检查仍无阶段进展，停止当前自动任务并保留游戏画面。</summary>
    Stalled,

    /// <summary>结算尚未确认却回到出击准备页；不计完成、不再次消费 AP，保留画面停止刷本。</summary>
    UnexpectedPreparation,

}

/// <summary>
/// 出击后流程服务：处理点击出击按钮之后的界面流转。
/// 下载界面与 TAP TO NEXT 可能先后出现，同一帧内同时识别、先到先处理；
/// 进入战斗后把 AUTO 图标切换到“全部自动”，并把二倍速开启。
/// </summary>
public sealed partial class SortieFlowService
{
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
    /// 点击出击按钮后执行：30 秒内依次识别地形、空位、AP 不足、下载与 TAP TO NEXT；
    /// 命中 TAP TO NEXT 时先配置战斗图标再点击提示；若前置提示均未出现，
    /// 则配置战斗图标并点击画面中央开始战斗，之后统一进入继续按钮识别流程。
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
        var deadline = Environment.TickCount64 + Math.Max(0, downloadDetectTimeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            // 同一帧先检查失败/AP 不足，再识别地形、空位、下载与 TAP TO NEXT。
            // 下载按钮仅在下载提示命中后匹配，避免其它页面的相似按钮触发下载流程。
            var screen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (await HandleTerminalStateAsync(serial, screen, ct) is { } initialTerminal) return initialTerminal;

            var terrainHint = MatchIfConfigured(screen, _terrainTemplatePath, TerrainHintRoi(screen), StageSortieTemplateThreshold);
            var emptySlotHint = MatchEmptySlotDialog(screen);
            var downloadHint = MatchIfConfigured(screen, _sortieDownloadTemplatePath, DownloadHintRoi(screen), StageSortieTemplateThreshold);
            var tapToNext = await MatchTapToNextAsync(screen, ct, recovery.ForAutoProgress);

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

                await Task.Delay(Math.Max(1, pollIntervalMs), ct);
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
                await Task.Delay(Math.Max(1, pollIntervalMs), ct);
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
                await Task.Delay(Math.Max(1, pollIntervalMs), ct);
                continue;
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

            await Task.Delay(Math.Max(1, pollIntervalMs), ct);
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
        return await CompleteRoundAsync(
            serial,
            moreRoundsRemaining,
            continueStartDelayMs: continueStartDelayMs,
            continueClickDelayMs: continueClickDelayMs,
            continuePollIntervalMs: continuePollIntervalMs,
            continueWaitTimeoutMs: continueWaitTimeoutMs,
            continueRepeatIntervalMs: continueRepeatIntervalMs,
            againStartDelayMs: againStartDelayMs,
            againPollIntervalMs: againPollIntervalMs,
            againWaitTimeoutMs: againWaitTimeoutMs,
            ct: ct, recovery: recovery);
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
        int tapToNextClickDelayMs = GameTiming.RecognitionIntervalMs,
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
        var nextProgressAt = startedAt + 15000;
        TemplateMatchResult? tapToNext = null;
        var battleReady = false;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var screen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (await HandleTerminalStateAsync(serial, screen, ct) is { } terminal) return terminal;
            var recovered = await RecoverFarmIfStalledAsync(serial, screen, recovery, FarmRecoveryStage.NextBattle, ct);
            if (recovered.Terminal is { } recoveryTerminal) return recoveryTerminal;
            if (recovered.Acted) { await Task.Delay(Math.Max(1, tapToNextPollIntervalMs), ct); continue; }

            tapToNext = await MatchTapToNextAsync(screen, ct, recovery.ForAutoProgress);
            if (tapToNext is not null) { recovery.Watchdog.Observe(SortiePage.TapToNext); break; }
            battleReady = HasActiveBattleControls(screen);
            if (battleReady)
            {
                recovery.Watchdog.Observe(SortiePage.Battle);
                _log.Info("再次出击后已识别到战斗图标，开场提示已略过，继续等待本轮结算。");
                break;
            }
            var now = Environment.TickCount64;
            if (now >= nextProgressAt)
            {
                _log.Info($"再次出击后尚未确认新开场或战斗，继续等待（已等待 {(now - startedAt) / 1000}s，上限 {tapToNextWaitTimeoutMs / 1000}s）。");
                nextProgressAt = now + 15000;
            }
            await Task.Delay(Math.Max(1, tapToNextPollIntervalMs), ct);
        }
        ct.ThrowIfCancellationRequested();
        if (tapToNext is null && !battleReady)
        {
            _log.Warn("再次出击后等待 TAP TO NEXT 或战斗图标超时，已停止本轮出击流程；请检查游戏画面及再次出击是否成功");
            return SortieFlowResult.TapToNextTimeout;
        }

        if (tapToNext is not null)
        {
            await Task.Delay(Math.Max(1, tapToNextClickDelayMs), ct);
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
        var nextProgressAt = startedAt;
        TemplateMatchResult? tapToNext = null;
        var battleReady = false;
        var settlementReady = false;
        _log.Info("已点击下载，等待下载/加载完成，检查开场 TAP TO NEXT、战斗图标及结算页面…");
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var screen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (await HandleTerminalStateAsync(serial, screen, ct) is { } terminal) return terminal;

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
            battleReady = HasActiveBattleControls(screen);
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
            await Task.Delay(Math.Max(1, tapToNextPollIntervalMs), ct);
        }
        ct.ThrowIfCancellationRequested();
        if (tapToNext is null && !battleReady && !settlementReady)
        {
            _log.Warn("下载后等待开场、战斗或结算页面超时，已停止本轮出击流程；请检查下载进度或当前游戏画面。");
            return SortieFlowResult.TapToNextTimeout;
        }

        if (!settlementReady)
        {
            var configured = await ConfigureBattleIconsAsync(serial, iconClickIntervalMs, battleIconDelayMs, ct,
                stopAtSettlement: true);
            if (configured.Terminal is { } configurationTerminal) return configurationTerminal;
            settlementReady = IsSettlementPage(configured.Screen);
            if (tapToNext is not null && !settlementReady)
            {
                await Task.Delay(Math.Max(1, tapToNextClickDelayMs), ct);
                await _automation.TapAsync(serial, tapToNext.CenterX, tapToNext.CenterY, ct);
                _log.Info($"已点击 TAP TO NEXT: ({tapToNext.CenterX},{tapToNext.CenterY})");
            }
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

    /// <summary>所有轮次/结算阶段共用的终止检查；错误优先于任何推进操作，禁用按钮独立识别。</summary>
    private async Task<SortieFlowResult?> HandleTerminalStateAsync(string serial, ScreenImage screen, CancellationToken ct)
    {
        if (Match(screen, _failedTemplatePath, FailureRoi(screen), StageSortieTemplateThreshold) is not null)
        {
            await _automation.TapAsync(serial, BackToStageSelectButtonCenter.X, BackToStageSelectButtonCenter.Y, ct);
            _log.Warn("识别到刷本失败，已点击回到选择关卡，请调整部队配置");
            return SortieFlowResult.FailureDetected;
        }

        if (Match(screen, _noApTemplatePath, NoApHintRoi(screen), StageSortieTemplateThreshold) is not null)
        {
            _apStopSignal?.Report("AP 不足，当前出击流程已停止，请补充 AP 后重新开始。");
            var handled = await HandleNoApDialogAsync(serial, screen, ct);
            return handled == NoApHandlingResult.Cancelled ? SortieFlowResult.NoApCancelled : SortieFlowResult.NoApTimeout;
        }

        // 灰色“再次出击”只有在结算页布局已确认、且继续按钮已消失后才表示 AP 不足。
        // 中间页背景可能误中灰色模板，不能仅凭一个按钮模板终止任务。
        var againClose = Match(screen, _againCloseTemplatePath, AgainButtonRoi(screen), AgainCloseMatchThreshold);
        if (againClose is not null)
        {
            var continueButton = Match(screen, _continueTemplatePath, ContinueButtonRoi(screen), ContinueTemplateThreshold);
            var selectStage = MatchIfConfigured(
                screen,
                _selectStageTemplatePath,
                SelectStageButtonRoi(screen),
                StageSortieTemplateThreshold);
            if (!IsCurrentContinueCandidate(continueButton, selectStage) && selectStage is not null)
            {
                _apStopSignal?.Report("AP 不足，无法再次出击，当前流程已停止。");
                await _automation.TapAsync(serial, BackToStageSelectButtonCenter.X, BackToStageSelectButtonCenter.Y, ct);
                _log.Warn(
                    $"识别到结算页的再次出击按钮为关闭状态（{againClose.Confidence:P1}，选择关卡 {selectStage.Confidence:P1}），" +
                    "判断为 AP 不足，已点击选择关卡并中止刷本");
                return SortieFlowResult.NoAp;
            }
        }

        if (_speedVerificationSerial == serial)
        {
            var state = RecognizeSpeed(screen, ct)?.State;
            if (state == SpeedButtonState.On)
                _log.Info("二倍速点击后复查：颜色确认已开启。");
            else if (state == SpeedButtonState.Off)
                _log.Warn("二倍速点击后复查：仍为关闭，尚未确认开启。");
            else
                _log.Info("二倍速点击后复查：按钮消失或颜色不明确，尚未确认开启。");
            _speedVerificationSerial = null;
        }
        return null;
    }

    /// <summary>空位出击对话框的处理结果。</summary>
    private enum EmptySlotHandlingResult
    {
        /// <summary>已点击空位出击确认，继续等待下载/TAP TO NEXT。</summary>
        Continue,

        /// <summary>未允许空位出击，已点击取消。</summary>
        Cancelled,

        /// <summary>未识别到对话框按钮。</summary>
        Timeout,
    }

    /// <summary>地形适性对话框的处理结果。</summary>
    private enum TerrainHandlingResult
    {
        /// <summary>已点击出击确认，继续等待后续提示。</summary>
        Continue,

        /// <summary>未允许强制出击，已点击取消。</summary>
        Cancelled,

        /// <summary>未识别到对话框按钮。</summary>
        Timeout,
    }

    /// <summary>AP 不足对话框的处理结果。</summary>
    private enum NoApHandlingResult
    {
        /// <summary>已点击取消，中止本轮刷本。</summary>
        Cancelled,

        /// <summary>未识别到对话框取消按钮。</summary>
        Timeout,
    }

    /// <summary>
    /// 处理空位出击对话框：优先在提示帧上识别按钮；按钮未出现时短暂轮询。
    /// 允许空位出击则点击“出击”确认并继续；否则点击“取消”并提示玩家。
    /// </summary>
    private async Task<EmptySlotHandlingResult> HandleEmptySlotDialogAsync(
        string serial,
        ScreenImage screen,
        bool allowEmptySlotSortie,
        CancellationToken ct)
    {
        var roi = CenterBottomDialogRoi(screen);
        var buttonPath = allowEmptySlotSortie
            ? _emptySlotSortieTemplatePath
            : _emptySlotCancelTemplatePath;

        // 提示与按钮通常在同一帧出现，先直接匹配当前帧
        var button = Match(screen, buttonPath, roi);
        if (button is null)
        {
            // 按钮可能稍后出现，短暂轮询（最长 15 秒）
            var wait = new WaitOptions
            {
                TimeoutMs = GameTiming.ShortRecognitionTimeoutMs,
                PollIntervalMs = GameTiming.RecognitionIntervalMs,
                Threshold = _threshold,
                Roi = roi,
            };
            button = await _automation.WaitForImageAsync(serial, buttonPath, wait, ct);
        }

        if (button is null)
        {
            _log.Warn("未识别到空位出击对话框按钮");
            return EmptySlotHandlingResult.Timeout;
        }

        await _automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
        if (allowEmptySlotSortie)
        {
            _log.Info($"已点击空位出击确认: ({button.CenterX},{button.CenterY})，继续等待下载/TAP TO NEXT");
            return EmptySlotHandlingResult.Continue;
        }

        _log.Warn("出击阵容存在空位且未允许空位出击，已点击取消");
        return EmptySlotHandlingResult.Cancelled;
    }

    /// <summary>
    /// 处理地形适性对话框：按钮位置与空位、下载对话框一致。
    /// 勾选强制出击时点击“出击”继续；否则点击“取消”并终止本次自动刷本。
    /// </summary>
    private async Task<TerrainHandlingResult> HandleTerrainDialogAsync(
        string serial,
        ScreenImage screen,
        bool allowTerrainUnsuitableSortie,
        CancellationToken ct)
    {
        var roi = CenterBottomDialogRoi(screen);
        var buttonPath = allowTerrainUnsuitableSortie
            ? _emptySlotSortieTemplatePath
            : _emptySlotCancelTemplatePath;

        // 提示和按钮通常同帧出现；若按钮晚于提示渲染，最多等待 15 秒。
        var button = Match(screen, buttonPath, roi);
        if (button is null)
        {
            var wait = new WaitOptions
            {
                TimeoutMs = GameTiming.ShortRecognitionTimeoutMs,
                PollIntervalMs = GameTiming.RecognitionIntervalMs,
                Threshold = _threshold,
                Roi = roi,
            };
            button = await _automation.WaitForImageAsync(serial, buttonPath, wait, ct);
        }

        if (button is null)
        {
            _log.Warn("未识别到地形适性对话框按钮");
            return TerrainHandlingResult.Timeout;
        }

        await _automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
        if (allowTerrainUnsuitableSortie)
        {
            _log.Info($"检测到地形适性提示，已点击出击确认: ({button.CenterX},{button.CenterY})，继续等待后续提示");
            return TerrainHandlingResult.Continue;
        }

        _log.Warn("检测到地形适性提示且未允许强制出击，已点击取消");
        return TerrainHandlingResult.Cancelled;
    }

    /// <summary>
    /// 处理 AP 不足对话框：在提示帧上识别对话框取消按钮（templates/dialog/cancel.png，
    /// 与空位出击对话框共用），识别到后点击并提示玩家。
    /// </summary>
    private async Task<NoApHandlingResult> HandleNoApDialogAsync(
        string serial,
        ScreenImage screen,
        CancellationToken ct)
    {
        var roi = CenterBottomDialogRoi(screen);

        // 提示与按钮通常在同一帧出现，先直接匹配当前帧
        var cancel = Match(screen, _emptySlotCancelTemplatePath, roi);
        if (cancel is null)
        {
            // 按钮可能稍后出现，短暂轮询（最长 15 秒）
            var wait = new WaitOptions
            {
                TimeoutMs = GameTiming.ShortRecognitionTimeoutMs,
                PollIntervalMs = GameTiming.RecognitionIntervalMs,
                Threshold = _threshold,
                Roi = roi,
            };
            cancel = await _automation.WaitForImageAsync(serial, _emptySlotCancelTemplatePath, wait, ct);
        }

        if (cancel is null)
        {
            _log.Warn("未识别到 AP 不足对话框取消按钮");
            return NoApHandlingResult.Timeout;
        }

        await _automation.TapAsync(serial, cancel.CenterX, cancel.CenterY, ct);
        _log.Warn($"识别到 AP 不足提示，已点击取消: ({cancel.CenterX},{cancel.CenterY})");
        return NoApHandlingResult.Cancelled;
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
        var deadline = Environment.TickCount64 + Math.Max(0, continueWaitTimeoutMs);

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var fresh = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (await HandleTerminalStateAsync(serial, fresh, ct) is { } terminal) return terminal;

            if (await HandleSettlementContinuationAsync(serial, fresh, ct, recovery.ForAutoProgress)) continue;

            var match = Match(fresh, _continueTemplatePath, ContinueButtonRoi(fresh), ContinueTemplateThreshold);
            // “选择关卡”与“继续”模板存在相似度；终局页双按钮命中时，只有选择关卡模板
            // 明显优于继续模板，才把它当成终局页，避免中间继续页的背景误匹配抢先结束流程。
            var resultPage = TryRecognizeResultPageBeforeContinue(fresh, match, out var again, out var selectStage);
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
            var resultPage = TryRecognizeResultPageBeforeContinue(fresh, currentContinue, out var again, out var selectStage);
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
            var resultPage = TryRecognizeResultPageBeforeContinue(fresh, currentContinue, out var match, out var selectStage);
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
                    var retryResultPage = TryRecognizeResultPageBeforeContinue(retryScreen, retryContinue, out _, out var retrySelectStage);
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

                await _automation.TapAsync(
                    serial,
                    BackToStageSelectButtonCenter.X,
                    BackToStageSelectButtonCenter.Y,
                    ct);
                _log.Info(
                    $"全部刷取次数完成，已点击回到选择关卡: ({BackToStageSelectButtonCenter.X},{BackToStageSelectButtonCenter.Y})");
                return SortieFlowResult.AllRoundsCompleted;
            }

            await Task.Delay(Math.Max(1, againPollIntervalMs), ct);
        }

        _log.Warn($"等待再次出击按钮超时（{againWaitTimeoutMs}ms）");
        return SortieFlowResult.AgainTimeout;
    }

    /// <summary>
    /// 确认战斗结算完成页：左侧“再次出击”与右侧“选择关卡”必须在同一帧同时命中。
    /// 单独按钮不足以表示页面状态，双模板可排除继续按钮与选择关卡按钮外框相似造成的误判。
    /// </summary>
    private bool TryRecognizeResultPage(
        ScreenImage screen,
        out TemplateMatchResult? again,
        out TemplateMatchResult? selectStage)
    {
        // 兼容旧配置：未提供新模板时不做终局页识别，但也不能把空路径交给模板加载器。
        if (string.IsNullOrWhiteSpace(_selectStageTemplatePath))
        {
            again = null;
            selectStage = null;
            return false;
        }

        again = Match(screen, _againTemplatePath, AgainButtonRoi(screen), StageSortieTemplateThreshold);
        selectStage = Match(screen, _selectStageTemplatePath, SelectStageButtonRoi(screen), StageSortieTemplateThreshold);
        return again is not null && selectStage is not null;
    }

    /// <summary>使用同帧双按钮确认结算页，避免继续模板因按钮外框相似覆盖结算状态。</summary>
    private bool TryRecognizeResultPageBeforeContinue(
        ScreenImage screen,
        TemplateMatchResult? continueMatch,
        out TemplateMatchResult? again,
        out TemplateMatchResult? selectStage)
    {
        if (!TryRecognizeResultPage(screen, out again, out selectStage)) return false;
        // 左侧相似图案不能把明确的右侧“继续”变成终局；同分时仍保留双按钮布局证据。
        // 只比较同一右侧按钮，避免其他位置的继续候选否决真实的选择关卡按钮。
        var sameButton = continueMatch is not null &&
            Math.Abs(continueMatch.CenterX - selectStage!.CenterX) <= selectStage.Width / 4 &&
            Math.Abs(continueMatch.CenterY - selectStage.CenterY) <= selectStage.Height / 4;
        return !sameButton || continueMatch!.Confidence - selectStage!.Confidence < ResultPageContinueConfidenceMargin;
    }

    /// <summary>选择关卡模板明显强于继续模板时，避免把终局页按钮当作继续按钮点击。</summary>
    private static bool IsCurrentContinueCandidate(
        TemplateMatchResult? continueMatch,
        TemplateMatchResult? selectStageMatch)
        => continueMatch is not null
            && (selectStageMatch is null
                || selectStageMatch.Confidence - continueMatch.Confidence < ResultPageContinueConfidenceMargin);

    /// <summary>下载已发生在本轮出击后；只有明确的继续按钮或双按钮终局证据才允许转入结算。</summary>
    private bool IsSettlementPage(ScreenImage screen)
    {
        var currentContinue = Match(screen, _continueTemplatePath, ContinueButtonRoi(screen), ContinueTemplateThreshold);
        var resultPage = TryRecognizeResultPageBeforeContinue(screen, currentContinue, out _, out var selectStage);
        return resultPage || currentContinue is not null && IsCurrentContinueCandidate(currentContinue, selectStage);
    }

    /// <summary>彩色 AUTO 与速度字形、颜色同帧确认才确认已在战斗；背景色或单独速度相似块不足以推进。</summary>
    private bool HasActiveBattleControls(ScreenImage screen)
        => BattleAutoRecognizer().Recognize(screen, AutoToggleRoi)?.State is AutoButtonState.All or AutoButtonState.Enemy &&
            RecognizeSpeed(screen)?.State is SpeedButtonState.On or SpeedButtonState.Off;

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

    /// <summary>“继续”保留水平偏移空间，纵向限定底部 175 像素，排除上方同框选关按钮。</summary>
    private static Roi ContinueButtonRoi(ScreenImage screen) =>
        new(
            Math.Max(0, screen.Width - 768),
            Math.Max(0, screen.Height - 175),
            Math.Min(768, screen.Width),
            Math.Min(175, screen.Height));

    /// <summary>“选择关卡”仍限制在右下按钮位，避免与扩大后的继续区域交叉误判。</summary>
    private static Roi SelectStageButtonRoi(ScreenImage screen) =>
        new(
            screen.Width - 450,
            screen.Height - 175,
            450,
            175);

    /// <summary>“再次出击”按钮识别区域：右侧 435~825 像素、底部 175 像素。</summary>
    private static Roi AgainButtonRoi(ScreenImage screen) =>
        new(
            screen.Width - 825,
            screen.Height - 175,
            390,
            175);

    /// <summary>刷本失败提示识别区域：屏幕顶部 150 像素。</summary>
    private static Roi FailureRoi(ScreenImage screen) =>
        new(0, 0, screen.Width, 150);

    /// <summary>空位提示共用 600×500 区域，完整容纳日文两行说明，不截断出击确认句。</summary>
    internal static Roi EmptySlotHintRoi(ScreenImage screen) =>
        new(
            (screen.Width - 600) / 2,
            0,
            600,
            500);

    /// <summary>地形提示横向覆盖对话框，容纳日文完整两行说明；纵向范围不变，排除下方可变机体列表。</summary>
    private static Roi TerrainHintRoi(ScreenImage screen) =>
        new(
            (screen.Width - 1040) / 2,
            0,
            1040,
            420);

    /// <summary>
    /// 下载提示横向覆盖整个对话框；日文完整说明句宽于旧 666 像素区域。
    /// 保留原纵向范围和阈值，包含说明文字但不依赖下方可变下载大小。
    /// </summary>
    internal static Roi DownloadHintRoi(ScreenImage screen) =>
        new(
            (screen.Width - Math.Min(1080, screen.Width)) / 2,
            Math.Min(60, Math.Max(0, screen.Height - 420)),
            Math.Min(1080, screen.Width),
            420);

    /// <summary>AP 不足提示识别区域：画面正中上方 400×550。</summary>
    private static Roi NoApHintRoi(ScreenImage screen) =>
        new(
            (screen.Width - 400) / 2,
            0,
            400,
            550);

    /// <summary>玩家升级提示识别区域：画面正中上方 640×315。</summary>
    private static Roi PlayerLevelUpRoi(ScreenImage screen) =>
        new(
            (screen.Width - 640) / 2,
            0,
            640,
            315);

    /// <summary>奖励图标识别区域：画面顶部中间 230×120。</summary>
    private static Roi RewardRoi(ScreenImage screen) =>
        new(
            (screen.Width - 230) / 2,
            0,
            230,
            120);

    /// <summary>对话框按钮识别区域：画面正中下方 825×300（下载、空位出击与 AP 不足对话框共用）。</summary>
    private static Roi CenterBottomDialogRoi(ScreenImage screen) =>
        new(
            (screen.Width - 825) / 2,
            screen.Height - 300,
            825,
            300);

    /// <summary>结算等待复用会话级全局继续策略；终止检查后调用，遮罩命中或冷却中均丢弃旧帧。</summary>
    private async Task<bool> HandleSettlementContinuationAsync(string serial, ScreenImage screen, CancellationToken ct,
        bool forAutoProgress = false)
        => await _automation.GlobalScreens.HandleContinuationAsync(serial,
            ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height), ct, allowSkip: false,
            tapRegion: forAutoProgress ? RecognitionRegion.ProgressTapToNext : RecognitionRegion.BottomButtons)
            != GlobalScreenResult.None;

    /// <summary>首次、再次与下载后入口共用底部 OCR；只读匹配不接管取消、安全检查或开场确认。</summary>
    private Task<TemplateMatchResult?> MatchTapToNextAsync(ScreenImage screen, CancellationToken ct, bool forAutoProgress = false)
        => Task.Run(() => _automation.TapToNext.Recognize(screen, ct,
            forAutoProgress ? RecognitionRegion.ProgressTapToNext : RecognitionRegion.BottomButtons), ct);

    /// <summary>提示可能与剧情灰色背景相似；同帧确认左右按钮后才进入空位处理，避免跨帧借用其他弹窗。</summary>
    internal TemplateMatchResult? MatchEmptySlotDialog(ScreenImage screen)
    {
        var hint = Match(screen, _emptySlotHintTemplatePath, EmptySlotHintRoi(screen));
        if (hint is null) return null;
        var roi = CenterBottomDialogRoi(screen);
        var cancel = Match(screen, _emptySlotCancelTemplatePath, roi);
        // 下载与出击共用蓝色外框；同时复核中央出击文字，不能由外框为另一种确认授权。
        var sortie = _progressButtons.Match(screen, _emptySlotSortieTemplatePath, roi, _threshold, default);
        return cancel is not null && sortie is not null && cancel.CenterX < screen.Width / 2 &&
            sortie.CenterX > screen.Width / 2 ? hint : null;
    }

    /// <summary>在指定区域匹配模板；模板加载失败返回 null。</summary>
    private TemplateMatchResult? Match(ScreenImage screen, string templatePath, Roi roi)
        => Match(screen, templatePath, roi, _threshold);

    /// <summary>匹配可选模板；旧配置缺少新增模板时安全跳过，避免反复记录空路径加载错误。</summary>
    private TemplateMatchResult? MatchIfConfigured(ScreenImage screen, string templatePath, Roi roi, double threshold)
        => string.IsNullOrWhiteSpace(templatePath) ? null : Match(screen, templatePath, roi, threshold);

    /// <summary>在指定区域按指定阈值匹配模板；模板加载失败返回 null。</summary>
    private TemplateMatchResult? Match(ScreenImage screen, string templatePath, Roi roi, double threshold)
    {
        try
        {
            return TemplateFallback.Match(templatePath, candidate =>
            {
                var template = _templateImages.Load(candidate);
                return _matcher.Match(
                    screen.Bgra,
                    screen.Width,
                    screen.Height,
                    template.Bgra,
                    template.Width,
                    template.Height,
                    threshold,
                    roi);
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"出击流程模板不可用，本次无法判断对应目标: {templatePath}: {ex.Message}");
            return null;
        }
    }
}

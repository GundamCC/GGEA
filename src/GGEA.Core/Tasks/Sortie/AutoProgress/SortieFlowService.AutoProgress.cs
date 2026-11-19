using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>自动推图的单关卡轮询；所有动作共用截图与取消链，不启动并发点击任务。</summary>
public sealed partial class SortieFlowService
{
    /// <summary>
    /// 从点击出击开始持续检查 AUTO、MENU→SKIP、继续/故事与选关标题。
    /// 复用刷本的安全和弹窗处理；系列完成提示关闭后也可确认主界面或主要关卡。
    /// 固定总时限不因重复点击重置，取消会中断整个轮询；每次动作后重新截图。
    /// </summary>
    public Task<SortieFlowResult> ExecuteForProgressAsync(
        string serial, bool allowDownload, bool allowEmptySlotSortie, bool allowTerrainUnsuitableSortie,
        AutoProgressFlowTemplates templates, int timeoutMs = 1800000, int pollIntervalMs = GameTiming.RecognitionIntervalMs,
        int actionDelayMs = 1500, int skipWaitTimeoutMs = GameTiming.ShortRecognitionTimeoutMs,
        Func<CancellationToken, Task<bool>>? confirmHome = null, CancellationToken ct = default,
        bool requireSeriesCompleteClose = false,
        Func<ScreenImage, CancellationToken, Task<bool>>? confirmPreparation = null,
        TimeProvider? recoveryTimeProvider = null,
        Func<ScreenImage, CancellationToken, bool>? confirmSelectedNode = null, bool watchEntry = false)
        // WPF 调用方保留自己的续接上下文，识别和原生计算在后台执行，不能占用界面线程。
        => Task.Run(() => ExecuteForProgressCoreAsync(serial, allowDownload, allowEmptySlotSortie,
            allowTerrainUnsuitableSortie, templates, timeoutMs, pollIntervalMs, actionDelayMs,
            skipWaitTimeoutMs, confirmHome, ct, requireSeriesCompleteClose, confirmPreparation, recoveryTimeProvider, confirmSelectedNode, watchEntry), ct);

    /// <summary>单关识别及点击仍串行运行；后台执行只改变计算位置，不新增并发点击或延长安全等待。</summary>
    private async Task<SortieFlowResult> ExecuteForProgressCoreAsync(
        string serial, bool allowDownload, bool allowEmptySlotSortie, bool allowTerrainUnsuitableSortie,
        AutoProgressFlowTemplates templates, int timeoutMs, int pollIntervalMs, int actionDelayMs,
        int skipWaitTimeoutMs, Func<CancellationToken, Task<bool>>? confirmHome, CancellationToken ct,
        bool requireSeriesCompleteClose, Func<ScreenImage, CancellationToken, Task<bool>>? confirmPreparation,
        TimeProvider? recoveryTimeProvider, Func<ScreenImage, CancellationToken, bool>? confirmSelectedNode, bool watchEntry)
    {
        ValidateAutoProgressFlow(templates);
        if (watchEntry) _progressButtons.Validate(WatchExecuteTemplate, WatchSkipExecuteRoi);
        var autoRecognizer = new AutoButtonRecognizer(_matcher, templates.AutoAll, templates.AutoEnemy, templates.AutoClose);
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        var waitingForStageReturn = false;
        // 结束证据与临时等待分开保存；恢复重开按钮检测不能遗忘本关已经观察到的结束流程。
        var returnEvidence = false;
        // 战后故事 SKIP 或观看关卡 SKIP 后的执行确认授权返回；普通开场和战斗中的 SKIP 不提供结束证据。
        // 仅允许后续真实返回页作为完成证据，不把 SKIP 点击本身当作结算。
        var endingStoryStarted = false;
        var skippedEndingStory = false;
        // 每次略过后继续观察 OK，直到本关结束；没有弹窗时仍按原入口确认，不强制要求奖励出现。
        var skipObserved = false;
        var okClickAttempts = 0;
        var okClickLimit = 3;
        var seriesCloseAttempts = 0;
        var seriesCloseRetryLimit = 3;
        var seriesCloseWaitReported = false;
        var downloadAccepted = false;
        // 连续可见的 MENU 只尝试打开一次；SKIP 未出现只能结束局部观察，不能结束关卡或阻塞其他识别。
        var menuAttempted = false;
        long? skipDeadline = null;
        var watch = new WatchStoryPlayback(this, serial, recoveryTimeProvider ?? TimeProvider.System,
            actionDelayMs, pollIntervalMs, skipWaitTimeoutMs);
        using var progressVision = new ProgressTemplateMatcher();
        var stageTitle = new SelectStageTitleRecognizer(templates.SelectStageTitle);
        var watchdog = new SortieStallWatchdog(recoveryTimeProvider);
        var controls = new ProgressBattleControls(this, serial, watchdog, actionDelayMs, pollIntervalMs);

        // 新模板仅在本次关卡内缓存；普通刷本匹配、阈值和等待路径不受此入口影响。
        TemplateMatchResult? Find(ScreenImage screen, string path, Roi roi)
            => progressVision.Match(screen, path, roi, StageSortieTemplateThreshold, ct);

        async Task Click(TemplateMatchResult match, string label, SortiePage page)
        {
            // 记录识别到的阶段变化；相同按钮反复点击不会假装任务仍在前进。
            watchdog.Observe(page);
            await _automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
            _log.Info($"自动推图识别到{label}，点击 ({match.CenterX},{match.CenterY})");
            await Task.Delay(Math.Max(1, actionDelayMs), ct);
        }

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var screen = await CaptureProgressScreenAsync(serial, ct);
            // 共享截图保留网络错误保护；失败/AP 不足优先于故事、菜单及自动按钮。
            if (await HandleTerminalStateAsync(serial, screen, ct) is { } terminal) return terminal;

            var progressOk = skipObserved ? FindProgressOk(screen, ct) : null;
            // 跳过与执行必须使用不同截图；等待期间保留失败、AP、网络与取消保护，不重复点跳过。
            if (progressOk is null && watch.AwaitingConfirmation)
            {
                if (await watch.ConfirmSkipAsync(screen, Click, ct))
                    skippedEndingStory = true;
                continue;
            }

            // 只读观察用于阻止准备页误交接；实际恢复仍排在下载、地形和空位弹窗处理之后。
            var preparationVisible = (returnEvidence || skippedEndingStory) && confirmPreparation is not null &&
                await confirmPreparation(screen, ct);

            if (watchdog.TryInspect())
            {
                _log.Info("自动推图连续 2 分钟无阶段进展，重新检测当前画面以恢复运行。");
                var observed = await InspectProgressRecoveryAsync(screen, templates, progressVision,
                    autoRecognizer, returnEvidence, confirmHome, confirmPreparation, ct, confirmSelectedNode);
                watchdog.ObserveDuringInspection(observed.Page);
                if (observed.Page == SortiePage.Battle && !observed.HealthyBattle &&
                    watchdog.Attempts < SortieStallWatchdog.MaximumAttempts)
                {
                    // 恢复入口直接复查到蓝色，不依赖后续旧阶段状态是否允许走到常规 AUTO 分支。
                    var configured = await EnsureAllAutoAsync(serial, screen, autoRecognizer, actionDelayMs, ct);
                    if (configured.Terminal is { } autoTerminal) return autoTerminal;
                    if (configured.Confirmed) watchdog.ConfirmProgress();
                    if (configured.Acted)
                    {
                        controls.AutoToggleAttempts = configured.Confirmed ? 0 : 3;
                        continue; // 点击后丢弃本轮旧截图，不能继续点旧 MENU 或结算位置。
                    }
                }
                if (observed.HealthyBattle)
                {
                    // 战斗长于两分钟是正常情况；不退出战斗、不重复出击，也不重置整关总时限。
                    watchdog.ConfirmProgress();
                    _log.Info("恢复检测确认战斗仍为全部自动，保持当前战斗并继续等待结算。");
                }
                else if (watchdog.Attempts >= SortieStallWatchdog.MaximumAttempts)
                {
                    _log.Warn("自动推图连续两次 2 分钟检查仍无正常流程进展，已停止当前任务并保留画面，不再点击或计为完成。");
                    return SortieFlowResult.Stalled;
                }
                else
                    _log.Info(observed.Page == SortiePage.Unknown
                        ? "恢复检测仍为未知画面，本次不点击；下一个 2 分钟检查仍无进展将停止任务。"
                        : $"恢复检测识别到{RecoveryPageName(observed.Page)}，按当前画面接续流程。");

                if (observed.Page is SortiePage.Stage or SortiePage.MainStage or SortiePage.Home or SortiePage.Preparation)
                {
                    // 最后 NORMAL 的奖励包不能被恢复跳过；准备页则仍可返回原编制会话重试未生效的出击。
                    if (observed.Page != SortiePage.Preparation && requireSeriesCompleteClose && seriesCloseAttempts == 0)
                        _log.Info("恢复仍须等待并关闭最后 NORMAL 的系列奖励包，不能直接略过此步骤。");
                    else
                    {
                        await Task.Delay(Math.Max(1, pollIntervalMs), ct);
                        var fresh = await CaptureProgressScreenAsync(serial, ct);
                        if (await HandleTerminalStateAsync(serial, fresh, ct) is { } recoveryTerminal) return recoveryTerminal;
                        var confirmed = await InspectProgressRecoveryAsync(fresh, templates, progressVision,
                            autoRecognizer, returnEvidence, confirmHome, confirmPreparation, ct, confirmSelectedNode);
                        ct.ThrowIfCancellationRequested();
                        if (confirmed.Page == observed.Page)
                        {
                            // 恢复时碰巧已出现正常返回证据，仍按原门禁完成；缺少结束证据才仅请求重新同步。
                            if (confirmed.Page == SortiePage.Stage && (returnEvidence || skippedEndingStory))
                                return SortieFlowResult.ProgressStageCompleted;
                            if ((confirmed.Page == SortiePage.MainStage && (seriesCloseAttempts > 0 || skippedEndingStory)) ||
                                (confirmed.Page == SortiePage.Home && seriesCloseAttempts > 0))
                                return SortieFlowResult.ProgressWorkNavigationRequired;
                            _log.Info($"恢复已复核{RecoveryPageName(confirmed.Page)}，保留当前作品进度继续；本次恢复不计为通关。");
                            return confirmed.Page switch
                            {
                                SortiePage.Stage => SortieFlowResult.ProgressResumeStage,
                                SortiePage.Preparation => SortieFlowResult.ProgressResumePreparation,
                                _ => SortieFlowResult.ProgressResumeWork,
                            };
                        }
                        _log.Info("恢复复核时页面已变化，继续观察新画面，不使用旧截图点击。");
                        continue;
                    }
                }
                // 仅清除与当前真实画面冲突的短期等待；已略过剧情、奖励包、编制和总时限记录全部保留。
                if (observed.Page is SortiePage.Story or SortiePage.Continue or SortiePage.Result or SortiePage.LevelUp)
                    waitingForStageReturn = false;
                if (observed.Page == SortiePage.Menu) { menuAttempted = false; skipDeadline = null; }
                if (observed.Page == SortiePage.Download) downloadAccepted = false;
                if (observed.Page == SortiePage.Reward) seriesCloseRetryLimit++;
                if (observed.Page == SortiePage.Battle) controls.AutoToggleAttempts = 0;
                if (observed.Page == SortiePage.StoryOk)
                {
                    // 巡查可恢复当前会话未记录 SKIP 的奖励弹窗；点击仍交给下方共享的安全顺序。
                    progressOk = FindProgressOk(screen, ct);
                    okClickLimit++;
                }
            }

            // 游戏可跳过故事/选择关卡按钮直接展示系列奖励包；同帧专属标题与关闭按钮授权该返回分支。
            var close = _progressClose.Match(screen, templates.SeriesCompleteClose,
                RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, screen.Width, screen.Height), StageSortieTemplateThreshold, ct);
            if (close is not null && (returnEvidence ||
                Find(screen, templates.SeriesCompleteTitle, SeriesCompleteTitleRoi) is not null))
            {
                watchdog.Observe(SortiePage.Reward);
                if (seriesCloseAttempts >= seriesCloseRetryLimit)
                {
                    await Task.Delay(Math.Max(1, pollIntervalMs), ct);
                    continue; // 快速重试无效后等两分钟重新确认，不能无间隔地反复点击。
                }
                waitingForStageReturn = true;
                returnEvidence = true;
                seriesCloseAttempts++;
                await Click(close, "系列完成奖励包关闭按钮", SortiePage.Reward);
                continue; // 点击前的截图不能证明弹窗已关闭，必须重新取帧。
            }
            // 队列已在出击前确认 BOSS 是本作品最后一个 NORMAL。结束标识或短暂选关页
            // 不能绕过随后必定出现的奖励包；必须在本关内关闭，再交回作品导航。
            var canConfirmReturn = !preparationVisible && (!requireSeriesCompleteClose || seriesCloseAttempts > 0);
            if (requireSeriesCompleteClose && returnEvidence && seriesCloseAttempts == 0 && !seriesCloseWaitReported)
            {
                _log.Info("最后一个 NORMAL 已进入结束流程，等待识别并关闭系列完成奖励包，再确认返回页面。");
                seriesCloseWaitReported = true;
            }
            var terrain = MatchIfConfigured(screen, _terrainTemplatePath, TerrainHintRoi(screen), StageSortieTemplateThreshold);
            if (terrain is not null)
            {
                watchdog.Observe(SortiePage.Terrain);
                var result = await HandleTerrainDialogAsync(serial, screen, allowTerrainUnsuitableSortie, ct);
                if (result == TerrainHandlingResult.Cancelled) return SortieFlowResult.TerrainCancelled;
                if (result == TerrainHandlingResult.Timeout) return SortieFlowResult.TerrainTimeout;
                await Task.Delay(Math.Max(1, actionDelayMs), ct);
                continue;
            }
            if (MatchEmptySlotDialog(screen) is not null)
            {
                watchdog.Observe(SortiePage.EmptySlots);
                var result = await HandleEmptySlotDialogAsync(serial, screen, allowEmptySlotSortie, ct);
                if (result == EmptySlotHandlingResult.Cancelled) return SortieFlowResult.EmptySlotCancelled;
                if (result == EmptySlotHandlingResult.Timeout) return SortieFlowResult.EmptySlotTimeout;
                await Task.Delay(Math.Max(1, actionDelayMs), ct);
                continue;
            }
            if (MatchIfConfigured(screen, _sortieDownloadTemplatePath, DownloadHintRoi(screen), StageSortieTemplateThreshold) is not null)
            {
                watchdog.Observe(SortiePage.Download);
                var dialog = _downloadDialogService.Recognize(screen);
                if (!allowDownload && dialog.Cancel is not null)
                {
                    await Click(dialog.Cancel, "下载取消", SortiePage.Download);
                    return SortieFlowResult.DownloadCancelled;
                }
                if (allowDownload && !downloadAccepted && dialog.Download is not null)
                {
                    await Click(dialog.Download, "下载按钮", SortiePage.Download);
                    downloadAccepted = true;
                }
                await Task.Delay(Math.Max(1, pollIntervalMs), ct);
                continue;
            }
            downloadAccepted = false;

            // OK 弹窗先于背后的选关入口与十五秒中央点击；点击本身不算完成，必须重拍确认返回。
            if (progressOk is not null)
            {
                watchdog.Observe(SortiePage.StoryOk);
                if (okClickAttempts >= okClickLimit)
                {
                    await Task.Delay(Math.Max(1, pollIntervalMs), ct);
                    continue;
                }
                await Click(progressOk, "略过后底部 OK 按钮", SortiePage.StoryOk);
                okClickAttempts++;
                skipObserved = true;
                returnEvidence = true;
                waitingForStageReturn = true;
                watch.ResetCycle();
                continue;
            }

            if (canConfirmReturn && (returnEvidence || skippedEndingStory))
            {
                // SKIP 后只用本帧的导航标题确认；主界面额外截图复核仍仅用于已关闭奖励包的出口，
                // 避免开场 SKIP 后每一帧战斗都重复调用主界面检测并拖慢持续识别。
                if (((seriesCloseAttempts > 0 || skippedEndingStory) &&
                     Find(screen, templates.MainStageTitle, WorkLogoVision.MainStageTitleRoi) is not null) ||
                    (seriesCloseAttempts > 0 && confirmHome is not null && await confirmHome(ct)))
                {
                    ct.ThrowIfCancellationRequested();
                    _log.Info(seriesCloseAttempts > 0
                        ? "已关闭系列完成提示并确认返回主界面或主要关卡，本关推进完成，继续作品导航。"
                        : "剧情略过后已确认返回主要关卡，本关推进完成，继续作品导航。");
                    return SortieFlowResult.ProgressWorkNavigationRequired;
                }
            }

            if (canConfirmReturn && (returnEvidence || skippedEndingStory) && IsProgressStageReady(screen, ct, confirmSelectedNode))
            {
                _log.Info(stageTitle.Recognize(screen, ct)
                    ? "自动推图已确认返回选择关卡页面，本关推进完成。"
                    : "选关标题暂未识别，已通过实际入口或选中关卡确认返回，本关推进完成。");
                return SortieFlowResult.ProgressStageCompleted;
            }

            // 战后若已进入准备页，立即用新帧复核并交回原编制会话，不再误报选关完成或等待两分钟。
            if (preparationVisible)
            {
                await Task.Delay(Math.Clamp(pollIntervalMs, 1, GameTiming.ConfirmationIntervalMs), ct);
                var fresh = await CaptureProgressScreenAsync(serial, ct);
                if (await HandleTerminalStateAsync(serial, fresh, ct) is { } preparationTerminal) return preparationTerminal;
                if (await confirmPreparation!(fresh, ct))
                {
                    _log.Info("已连续确认出击准备页，交回部队检查；不读取选关节点，也不据此计为通关。");
                    return SortieFlowResult.ProgressResumePreparation;
                }
                continue;
            }

            if (!waitingForStageReturn)
            {
                // 已进入结算时优先处理继续/故事；MENU 残帧或误命中不能遮蔽真实成绩页。
                var levelUp = Match(screen, _playerLevelUpTemplatePath, PlayerLevelUpRoi(screen));
                if (levelUp is not null) { await Click(levelUp, "玩家升级提示", SortiePage.LevelUp); continue; }

                // 故事与继续同区域且外框相似，以同帧较高分决定文字，不能让继续抢占故事。
                var story = _progressButtons.Match(screen, templates.Story, ContinueButtonRoi(screen), StageSortieTemplateThreshold, ct);
                var next = _progressButtons.Match(screen, _continueTemplatePath, ContinueButtonRoi(screen), ContinueTemplateThreshold, ct);
                var continuation = story is not null && (next is null || story.Confidence >= next.Confidence) ? story : next;
                if (TryRecognizeResultPageBeforeContinue(screen, continuation, out _, out var selectStage))
                {
                    await Click(selectStage!, "结算页选择关卡", SortiePage.Result);
                    waitingForStageReturn = true;
                    returnEvidence = true;
                    skipDeadline = null;
                    menuAttempted = false;
                    continue;
                }
                if (continuation is not null)
                {
                    var isStory = ReferenceEquals(continuation, story);
                    await Click(continuation, isStory ? "故事按钮" : "继续按钮",
                        isStory ? SortiePage.Story : SortiePage.Continue);
                    if (isStory) { waitingForStageReturn = true; returnEvidence = true; endingStoryStarted = true; }
                    skipDeadline = null;
                    menuAttempted = false;
                    continue;
                }
            }

            // 观看剧情没有战斗控制；与返回入口、结算按钮在同一串行轮询中检查，避免定时器并发点击。
            // 返回识别优先于中央点击；略过未能返回时继续下一轮，中央点击本身不刷新卡顿进展。
            if (watchEntry)
            {
                if (await watch.AdvanceAsync(screen, progressVision, templates.Skip, Click, ct))
                {
                    skipObserved = true;
                    okClickAttempts = 0;
                    okClickLimit = 3;
                }
                continue;
            }

            // SKIP 独立识别且优先于 MENU，兼容菜单已经打开或上一次点击后继续运行的画面。
            var skip = Find(screen, templates.Skip, ProgressSkipRoi);
            if (skip is not null)
            {
                await Click(skip, "SKIP，略过当前剧情", SortiePage.Skip);
                skipObserved = true;
                okClickAttempts = 0;
                okClickLimit = 3;
                skippedEndingStory = endingStoryStarted;
                skipDeadline = null;
                menuAttempted = false;
                continue;
            }
            var menu = Find(screen, templates.Menu, ProgressMenuRoi);
            // MENU 消失后才允许下一次打开；局部等待不会屏蔽同帧 AUTO、TAP TO NEXT 或结算检测。
            if (menu is null)
            {
                if (skipDeadline is not null)
                    _log.Info("自动推图 MENU 与 SKIP 均未出现，继续检测剧情推进后的画面。");
                skipDeadline = null;
                menuAttempted = false;
            }
            else if (skipDeadline is not null && Environment.TickCount64 >= skipDeadline.Value)
            {
                skipDeadline = null;
                _log.Info("自动推图点击 MENU 后未识别到 SKIP，结束本次菜单等待，继续检测战斗、剧情及结算画面。");
            }
            if (menu is not null && !menuAttempted)
            {
                await Click(menu, "MENU", SortiePage.Menu);
                menuAttempted = true;
                skipDeadline = Environment.TickCount64 + Math.Max(0, skipWaitTimeoutMs);
                continue;
            }

            // 字形确认按钮存在，颜色只来自按钮内部；外框背景或青色亮光不能把全部自动误判成关闭。
            var auto = autoRecognizer.Recognize(screen, AutoToggleRoi, ct);
            if (await controls.TryConfigureAsync(screen, auto, menu is not null, ct)) continue;

            // 略过优先于 TAP TO NEXT；故事后的剧情仍可能有开场提示，继续使用真实文字识别。
            var tap = await MatchTapToNextAsync(screen, ct, forAutoProgress: true);
            if (tap is not null)
            {
                await Click(tap, "TAP TO NEXT", SortiePage.TapToNext);
                skipDeadline = null;
                menuAttempted = false;
                continue;
            }
            if (auto is not null) watchdog.Observe(SortiePage.Battle);
            await Task.Delay(Math.Max(1, pollIntervalMs), ct);
        }
        _log.Warn(requireSeriesCompleteClose && seriesCloseAttempts == 0
            ? "最后一个 NORMAL 尚未识别并关闭系列完成奖励包，等待超时，停止当前队列，不计为关卡完成。"
            : "自动推图等待本关完成并确认返回页面超时，停止当前队列，不计为关卡完成。");
        return SortieFlowResult.ProgressTimeout;
    }
}

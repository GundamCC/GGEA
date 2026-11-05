using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class MasterLeagueService
{
    /// <summary>阶段属于一次运行，断连或识别失败不重建阶段，也不重置消费动作记录。</summary>
    private enum LeaguePhase { Reading, Selecting, Preparing, Fighting, Returning, OpeningRefresh, Refreshing }

    /// <summary>只保存本次运行发出的意图；游戏积分和可用次数仍以新截图为准。</summary>
    private sealed class LeagueSession
    {
        public LeaguePhase Phase;
        public int Target = -1;
        public int ContinuePages;
        public int Idle;
        public int Battles;
        public bool StageBoostInfoDismissSent;
        public bool SkipSent;
        public bool DownloadSent;
        public bool ReturnSent;
        public bool SelectionAcknowledged;
        public MasterLeagueOpponentPage? Previous;
        public HashSet<int> Attempted { get; } = [];
        public string? LastStatus;
    }

    /// <summary>持续观察与恢复；无界的是等待，不是消费点击。只有明确完成或无授权可执行操作才结束。</summary>
    public async Task<MasterLeagueResult> RunChallengesAsync(string serial, IMasterLeagueVision vision,
        MasterLeagueResultRecognizer resultRecognizer, bool retryDefeated, CancellationToken ct = default,
        IProgress<string>? progress = null, Func<CancellationToken, Task<bool>>? recoverKnownHome = null)
    {
        vision.Validate(); resultRecognizer.Validate(); _automation.TapToNext.Validate();
        var state = new LeagueSession();
        // 按单调时间检查停滞，不能依赖截图次数；重复识别和同页补点不重置期限。
        var lastProgress = _time.GetTimestamp();
        var lastInspection = lastProgress;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var wait = state.Idle < 30 ? 1000 : state.Idle < 90 ? 5000 : 15000;
            var reason = "暂未确认当前页面，持续观察并等待恢复…";
            try
            {
                // 本会话拥有重试与日志等级，单次失败只产生 WARN，不沿用底层“整任务停止”的重试结论。
                var observation = state.Phase is LeaguePhase.Preparing or LeaguePhase.Fighting
                    ? await CaptureBattleObservationAsync(serial, vision, state.Phase == LeaguePhase.Preparing, ct, retryOnFailure: false)
                    : null;
                var screen = observation?.Screen ?? await _automation.CaptureScreenAsync(serial, ct, retryOnFailure: false);
                if (screen.Width != 1920 || screen.Height != 1080)
                {
                    state.Idle++;
                    state.Previous = null;
                    reason = "画面尺寸不兼容，等待恢复为 1920×1080；可随时中止。";
                }
                else
                {
                    var now = _time.GetTimestamp();
                    var inspectRecovery = _time.GetElapsedTime(lastInspection, now) >= TimeSpan.FromMinutes(2);
                    if (inspectRecovery)
                    {
                        lastInspection = now;
                        var stalledSeconds = (int)_time.GetElapsedTime(lastProgress, now).TotalSeconds;
                        var inspection = $"大师联盟已 {stalledSeconds} 秒无阶段进展，开始两分钟恢复检查（阶段：{state.Phase}）；重新检查弹层、结算、继续提示与导航页。";
                        _log.Warn(inspection);
                        progress?.Report(inspection);
                    }
                    var previousPhase = state.Phase;
                    var previousBattles = state.Battles;
                    var outcome = await AdvanceSessionAsync(serial, screen, vision, state, retryDefeated, ct, recoverKnownHome, inspectRecovery, observation);
                    if (state.Phase != previousPhase || state.Battles != previousBattles)
                        lastProgress = lastInspection = _time.GetTimestamp();
                    if (inspectRecovery)
                    {
                        var inspectionResult = outcome.Acted || outcome.End is not null
                            ? $"大师联盟恢复检查已处理当前页面：{outcome.Status}{outcome.End?.Message}"
                            : $"大师联盟恢复检查未发现可安全执行的后续动作：{outcome.Status} 保留阶段与消费记录，继续观察；两分钟后再次检查。";
                        if (outcome.Acted || outcome.End is not null) _log.Info(inspectionResult);
                        else _log.Warn(inspectionResult);
                        progress?.Report(inspectionResult);
                    }
                    if (outcome.End is not null) return outcome.End;
                    reason = outcome.Status;
                    if (outcome.Acted)
                    {
                        state.Idle = 0;
                        state.Previous = null;
                        // 进入准备或发送挑战/下载后尽早观察；下一动作仍依赖新帧证据，不靠等待到时授权。
                        wait = state.Phase is LeaguePhase.Preparing or LeaguePhase.Fighting ? 1000 : GameTiming.RecognitionIntervalMs;
                    }
                    else state.Idle++;
                }
            }
            catch (Exception ex) when (IsRecoverableDeviceError(ex, ct))
            {
                // 截图和输入异常都保留阶段。输入可能已经送达，绝不因异常直接重发挑战/刷新确认。
                state.Previous = null;
                state.Idle++;
                wait = state.Idle < 10 ? 5000 : 15000;
                reason = ex is ConnectionErrorException
                    ? "检测到网络或数据更新提示，等待提示解除后恢复；不自动确认未知弹窗。"
                    : "设备连接或截图暂不可用，等待原设备恢复；保留当前阶段和已发送动作。";
                if (state.LastStatus != reason)
                    _log.Warn($"大师联盟等待恢复（{ex.GetType().Name}）：{ex.Message}");
            }
            if (state.LastStatus != reason)
            {
                state.LastStatus = reason;
                _log.Info(reason);
                progress?.Report(reason);
            }
            await _delay(wait, ct);
        }
    }

    /// <summary>只捕获可恢复的设备/截图问题；资源损坏、代码错误和用户取消不能被吞掉。</summary>
    internal static bool IsRecoverableDeviceError(Exception ex, CancellationToken ct)
        => !ct.IsCancellationRequested && ex is AdbException or ScreenshotCaptureException or ConnectionErrorException or TimeoutException;

    /// <summary>每次只根据当前帧推进一个动作，状态在输入前提交，避免输入超时后的重复消费。</summary>
    private async Task<(MasterLeagueResult? End, bool Acted, string Status)> AdvanceSessionAsync(string serial,
        ScreenImage screen, IMasterLeagueVision vision, LeagueSession state, bool retryDefeated, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? recoverKnownHome, bool inspectRecovery, BattleObservation? observation)
    {
        if (state.Phase is LeaguePhase.Reading or LeaguePhase.Selecting or LeaguePhase.Returning ||
            inspectRecovery && state.Phase is not (LeaguePhase.Refreshing or LeaguePhase.OpeningRefresh))
        {
            var info = await Task.Run(() => vision.FindStageBoostInfo(screen, ct), ct);
            if (info is not null)
            {
                state.Previous = null;
                if (state.StageBoostInfoDismissSent)
                    return (null, false, "已点击关闭关卡情报与增幅信息，等待弹层消失…");
                // 输入可能已送达，即使 ADB 回执丢失也不重复点击，以免落到弹层背后的对手。
                state.StageBoostInfoDismissSent = true;
                await _automation.TapAsync(serial, info.CenterX, info.CenterY, ct);
                return (null, true, "已识别关卡情报与增幅信息，点击关闭并等待重新确认对手列表…");
            }
            state.StageBoostInfoDismissSent = false;
        }
        var battle = observation?.Battle ?? await Task.Run(() => vision.ReadBattle(screen, ct), ct);
        if (battle.ResultVisible)
        {
            state.Phase = LeaguePhase.Returning;
            if (battle.ReturnButton is not null && (!state.ReturnSent || state.Idle >= 5))
            {
                state.ReturnSent = true;
                await _automation.TapAsync(serial, battle.ReturnButton.CenterX, battle.ReturnButton.CenterY, ct);
                return (null, true, "已确认结算返回，持续等待晋级、报酬或对手页…");
            }
            return (null, false, "已识别结算，等待返回按钮就绪…");
        }

        // 晋级动效可能晚于旧列表及选敌点击；正常入口覆盖选敌/准备阶段，恢复检查也不被旧战斗阶段挡住。
        if (state.Phase is LeaguePhase.Returning or LeaguePhase.Reading or LeaguePhase.Selecting or LeaguePhase.Preparing ||
            inspectRecovery && state.Phase is not (LeaguePhase.Refreshing or LeaguePhase.OpeningRefresh))
        {
            // 挑战模式解锁提示位于 Y≈856，需使用底部 300 像素的继续专用区域。
            var next = await Task.Run(() => _automation.TapToNext.Recognize(screen, ct, RecognitionRegion.ProgressTapToNext), ct);
            if (next is not null)
            {
                // 旧列表可能先出现，再出现晋级动效；即使已进入读列表阶段，也重新接管晋级返回。
                state.Phase = LeaguePhase.Returning;
                if (state.ContinuePages >= 3)
                    return (null, false, "已处理三页继续提示，等待画面变化或人工确认；任务仍保留，可中止。");
                state.ContinuePages++;
                await _automation.TapAsync(serial, next.CenterX, next.CenterY, ct);
                return (null, true, $"已处理第 {state.ContinuePages}/3 页继续提示，持续观察后续页面…");
            }
        }

        if (state.Phase is LeaguePhase.Preparing or LeaguePhase.Fighting)
        {
            var download = observation is not null ? observation.DownloadHint : await _automation.MatchImageAsync(screen, _downloadHintPath,
                new WaitOptions { Roi = SortieFlowService.DownloadHintRoi(screen), Threshold = .90, AllowGlobalContinuation = false }, ct);
            if (download is not null)
            {
                var buttons = await Task.Run(() => _downloadDialog.Recognize(screen), ct);
                if (!_allowDownload)
                {
                    if (buttons.Cancel is not null)
                        await _automation.TapAsync(serial, buttons.Cancel.CenterX, buttons.Cancel.CenterY, ct);
                    return (new(false, "未允许下载游戏数据，大师联盟已暂停，请开启下载许可后重新运行。"), false, "");
                }
                if (!state.DownloadSent && buttons.Download is not null)
                {
                    state.DownloadSent = true;
                    await _automation.TapAsync(serial, buttons.Download.CenterX, buttons.Download.CenterY, ct);
                    return (null, true, "已确认下载，持续等待下载完成及战斗结算…");
                }
                return (null, false, "下载尚未完成，持续等待；不重复确认下载。");
            }
        }

        if (state.Phase == LeaguePhase.Fighting && battle.Skip is not null && !state.SkipSent)
        {
            // 输入前再次同帧检查结算与 SKIP；转场时直接交给下一轮，不沿用旧坐标。
            var freshObservation = await CaptureBattleObservationAsync(serial, vision, false, ct, retryOnFailure: false);
            var fresh = freshObservation.Screen;
            if (fresh.Width != 1920 || fresh.Height != 1080)
                return (null, false, "画面尺寸发生变化，等待恢复，不点击旧 SKIP 位置。");
            battle = freshObservation.Battle;
            if (!battle.ResultVisible && freshObservation.DownloadHint is null && battle.Skip is not null)
            {
                state.SkipSent = true;
                await _automation.TapAsync(serial, battle.Skip.CenterX, battle.Skip.CenterY, ct);
                return (null, true, "已点击 SKIP，持续等待结算…");
            }
            return (null, false, "战斗画面正在变化，重新确认结算…");
        }

        if (state.Phase == LeaguePhase.Preparing)
        {
            var remaining = observation is not null ? observation.ChallengeRemaining
                : await Task.Run(() => vision.ReadChallengeRemaining(screen, ct), ct);
            if (remaining == 0) return (new(false, "当前对手挑战次数已耗尽，任务结束。"), false, "");
            if (remaining > 0)
            {
                state.Phase = LeaguePhase.Fighting;
                state.Attempted.Add(state.Target);
                state.SkipSent = state.DownloadSent = state.ReturnSent = false;
                state.ContinuePages = 0;
                await _automation.TapAsync(serial, 1675, 1000, ct);
                return (null, true, "已发送挑战，等待下载、战斗或结算；不会重复发送本次挑战。");
            }
        }

        if (state.Phase == LeaguePhase.OpeningRefresh)
        {
            var dialog = await Task.Run(() => vision.ReadRefreshDialog(screen, ct), ct);
            if (dialog is not null)
            {
                if (dialog.Remaining <= 0) return (new(false, "对手变更次数已耗尽，任务结束。"), false, "");
                state.Phase = LeaguePhase.Refreshing;
                await _automation.TapAsync(serial, dialog.ConfirmButton.CenterX, dialog.ConfirmButton.CenterY, ct);
                return (null, true, "已确认刷新，持续等待目标对手积分清零…");
            }
        }

        var page = await Task.Run(() => vision.ReadOpponents(screen, ct), ct);
        if (page is not null && MasterLeagueProgress.IsConsistent(page))
        {
            var stable = state.Previous is not null && SameSessionDecision(state.Previous, page, state, retryDefeated);
            state.Previous = page;
            if (!stable) return (null, false, page.Opponents.All(o => o.Victory)
                ? "已识别五名对手全胜，正在连续复核…" : "正在确认对手列表…");
            if (state.Phase == LeaguePhase.Fighting)
            {
                if (state.Target >= 0 && page.Opponents[state.Target] is { Victory: false, Defeat: false } opponent &&
                    !(opponent.Points > 0))
                    return (null, false, "挑战结果尚未同步，保留本次操作并等待；不重复挑战。");
                state.Phase = LeaguePhase.Returning;
            }
            if (state.Phase == LeaguePhase.Returning)
            {
                // 晋级继续之后，游戏会重置整组对手；据新列表开启下一轮，不再读取右侧任何分数。
                if (state.ContinuePages > 0 && page.Opponents.All(o => o.Unchallenged))
                {
                    state.Attempted.Clear();
                    state.Target = -1;
                    _log.Info("晋级继续后已确认新的零分对手列表，开始下一轮挑战。");
                }
                state.Phase = LeaguePhase.Reading;
                state.Battles++;
                _log.Info($"大师联盟第 {state.Battles} 场已返回，重新读取对手状态。");
            }
            if (state.Phase == LeaguePhase.Refreshing)
            {
                if (page.Opponents[state.Target].Points != 0)
                    return (null, false, "刷新结果尚未确认，持续等待目标积分清零；不重复消耗变更次数。");
                state.Attempted.Remove(state.Target);
                state.Phase = LeaguePhase.Selecting;
                state.SelectionAcknowledged = false;
                await _automation.TapAsync(serial, 143 + 286 * state.Target, 400, ct);
                state.SelectionAcknowledged = true;
                return (null, true, "已确认刷新清零，重新选择该对手…");
            }
            if (state.Phase is LeaguePhase.Selecting or LeaguePhase.Preparing)
            {
                if (page.Opponents[state.Target].Points != 0) state.Phase = LeaguePhase.Reading;
                else if (state.Phase == LeaguePhase.Selecting && !state.SelectionAcknowledged)
                {
                    // 选择列不消费次数；回执不明时在新对手页重选，不能直接点击可能对应旧目标的对战。
                    await _automation.TapAsync(serial, 143 + 286 * state.Target, 400, ct);
                    state.SelectionAcknowledged = true;
                    return (null, true, "已重新确认目标选择，等待页面稳定…");
                }
                else if (state.Phase == LeaguePhase.Selecting || state.Idle >= 5)
                {
                    var button = vision.FindBattleButton(screen, ct);
                    if (button is not null)
                    {
                        state.Phase = LeaguePhase.Preparing;
                        await _automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
                        return (null, true, "已进入对战准备，持续等待挑战次数与页面就绪…");
                    }
                }
            }
            if (state.Phase == LeaguePhase.OpeningRefresh && state.Idle >= 5)
            {
                var refresh = vision.FindRefresh(screen, state.Target, ct);
                if (refresh is not null)
                {
                    await _automation.TapAsync(serial, refresh.CenterX, refresh.CenterY, ct);
                    return (null, true, "重新打开已确认败北对手的变更弹窗…");
                }
            }
            if (state.Phase == LeaguePhase.Reading)
            {
                var choice = MasterLeagueProgress.Choose(page, retryDefeated);
                if (choice.StopReason is not null)
                {
                    if (page.Opponents.Any(o => o.Points is null && !o.Victory && !o.Defeat) ||
                        retryDefeated && page.Opponents.Any(o => o.Defeat) && page.RefreshRemaining is null)
                        return (null, false, "暂未找到可确认的下一目标，持续读取未知状态；任务保持运行。");
                    // 结算后的旧列表可能先于晋级动效短暂出现；没有下一目标时多留一个转场观察窗口。
                    if (state.ReturnSent && state.Idle < 10)
                        return (null, false, page.Opponents.All(o => o.Victory)
                            ? "已连续确认五名对手全胜，观察晋级转场后完成…"
                            : "当前对手已处理完，继续观察可能延迟出现的晋级提示…");
                    return (new(true, choice.StopReason), false, "");
                }
                if (!choice.Refresh && state.Attempted.Contains(choice.Index))
                {
                    var nextIndex = MasterLeagueProgress.ChallengeOrder
                        .Where(i => page.Opponents[i].Unchallenged && !state.Attempted.Contains(i))
                        .Select(i => (int?)i).FirstOrDefault();
                    if (nextIndex is null)
                        return (null, false, "已发出的挑战尚未同步，等待页面更新；不会再次消耗该目标的次数。");
                    choice = (nextIndex.Value, false, null);
                }
                state.Target = choice.Index;
                if (choice.Refresh)
                {
                    var refresh = vision.FindRefresh(screen, choice.Index, ct);
                    if (refresh is null) return (null, false, "败北对手刷新按钮尚未识别，继续重试…");
                    state.Phase = LeaguePhase.OpeningRefresh;
                    await _automation.TapAsync(serial, refresh.CenterX, refresh.CenterY, ct);
                    return (null, true, "已打开败北对手的变更弹窗，等待确认…");
                }
                state.Phase = LeaguePhase.Selecting;
                state.SelectionAcknowledged = false;
                await _automation.TapAsync(serial, 143 + 286 * choice.Index, 400, ct);
                state.SelectionAcknowledged = true;
                return (null, true, $"已选择第 {choice.Index + 1} 位未挑战对手…");
            }
        }
        else state.Previous = null;

        // 只在未处于消费结果等待时修复已知导航页；未知画面不发送中心点击。
        if (state.Phase is not (LeaguePhase.Refreshing or LeaguePhase.OpeningRefresh) &&
            (inspectRecovery || state.Idle == 5 || state.Idle > 5 && state.Idle % 15 == 0))
        {
            if (await MatchTitleAsync(screen, ct) is not null)
            {
                var select = await _automation.MatchImageAsync(screen, ConfigService.ResolvePath(SelectOpponentTemplate), OpponentOptions, ct);
                if (select is not null)
                {
                    state.Phase = LeaguePhase.Reading;
                    await _automation.TapAsync(serial, select.CenterX, select.CenterY, ct);
                    return (null, true, "已恢复到联盟首页，重新进入对手列表…");
                }
            }
            var stage = await _automation.MatchImageAsync(screen, ConfigService.ResolvePath(StageMenuEntryService.TitleTemplate),
                new WaitOptions { Region = RecognitionRegion.PageTitle, Threshold = .85, AllowGlobalContinuation = false }, ct);
            if (stage is not null)
            {
                state.Phase = LeaguePhase.Reading;
                await _automation.TapAsync(serial, 1668, 400, ct);
                return (null, true, "已确认关卡菜单，重新进入大师联盟…");
            }
            if (recoverKnownHome is not null && await recoverKnownHome(ct))
            {
                state.Phase = LeaguePhase.Reading;
                return (null, true, "已从确认的主界面恢复联盟入口，保留本次已发送挑战记录…");
            }
        }
        return (null, false, state.Phase switch
        {
            LeaguePhase.Fighting => "持续等待战斗或结算；超时后降低检查频率，不重发挑战。",
            LeaguePhase.Returning => "持续等待晋级或对手信息恢复；暂时识别失败不会结束任务。",
            LeaguePhase.Refreshing => "持续等待刷新结果；确认状态未知时不重复消费。",
            LeaguePhase.Preparing => "持续等待对战准备页面与挑战次数；不会把未知次数当作零。",
            _ => "页面暂未就绪，持续重试并恢复已识别的导航页；可随时中止。"
        });
    }

    /// <summary>连续确认的是下一步决策，不要求无关列、总分末位或变更次数在两帧中完全相等。</summary>
    private static bool SameSessionDecision(MasterLeagueOpponentPage previous, MasterLeagueOpponentPage current,
        LeagueSession state, bool retryDefeated)
    {
        if (state.Target >= 0 && state.Phase is LeaguePhase.Selecting or LeaguePhase.Preparing or LeaguePhase.Refreshing)
            return (previous.Opponents[state.Target].Points == 0) == (current.Opponents[state.Target].Points == 0);
        if (state.Phase == LeaguePhase.Fighting && state.Target >= 0)
            return previous.Opponents[state.Target].Victory == current.Opponents[state.Target].Victory &&
                previous.Opponents[state.Target].Defeat == current.Opponents[state.Target].Defeat &&
                (previous.Opponents[state.Target].Points > 0) == (current.Opponents[state.Target].Points > 0);
        if (state.Phase == LeaguePhase.Returning)
            return previous.Opponents.All(o => o.Unchallenged) == current.Opponents.All(o => o.Unchallenged);
        return MasterLeagueProgress.Choose(previous, retryDefeated) == MasterLeagueProgress.Choose(current, retryDefeated);
    }
}

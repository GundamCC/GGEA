using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class StageSkipService
{
    /// <summary>外部已经执行的略过没有原弹窗证据，只等待结果，不授权补点消费。</summary>
    public Task<StageSkipResult> CompleteExecutedSkipAsync(
        string serial, UiLayout stageLayout, CancellationToken ct = default)
        => CompleteExecutedSkipCoreAsync(serial, stageLayout, null, ct);

    /// <summary>每次略过独立持有补点预算；观察到离开原弹窗后永久撤销补点资格。</summary>
    private async Task<StageSkipResult> CompleteExecutedSkipCoreAsync(
        string serial, UiLayout stageLayout, SkipExecutionDialog? originalDialog, CancellationToken ct)
    {
        var skipCancel = stageLayout.Require("skipDialogCancel", "关卡布局缺少 skipDialogCancel 锚点");
        var resultOk = stageLayout.Require("skipResultOk", "关卡布局缺少 skipResultOk 锚点");
        var resultTitle = string.IsNullOrWhiteSpace(_options.SkipResultTemplatePath)
            ? null
            : stageLayout.Require("skipResultTitle", "关卡布局缺少 skipResultTitle 锚点");
        // 诊断取证与识别共用底部区域定义，不保留已退役的窄文字锚点。
        var bottom = RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, stageLayout.ScreenWidth, stageLayout.ScreenHeight);
        var apRecoveryClose = !DetectApRecovery
            ? null
            : new UiAnchor { Name = "apRecoveryClose", X = bottom.X, Y = bottom.Y, Width = bottom.Width, Height = bottom.Height };

        var acquisitionContinueCount = 0;
        if (string.IsNullOrWhiteSpace(_options.SkipResultTemplatePath) &&
            !DetectApRecovery)
        {
            await GameTiming.DelayIfPositiveAsync(_options.SkipResultTimeoutMs, ct);
        }
        else
        {
            // 单位展示可连续出现，使用确认采样频率，避免通用五秒间隔耗尽整段奖励预算。
            var resultPollMs = _options.ContinueUnitAcquisition
                ? GameTiming.ConfirmationIntervalMs : GameTiming.RecognitionIntervalMs;
            var resultReady = false;
            var recoveringLateResult = false;
            var executionRetried = false;
            var executionDialogDeparted = false;
            var deadline = Environment.TickCount64 + Math.Max(0, _options.SkipResultTimeoutMs);
            var nextProgressAt = Environment.TickCount64 + 15000;
            ScreenImage? lastScreen = null;
            _log.Info($"等待 SKIP RESULT…（最长 {_options.SkipResultTimeoutMs / 1000.0:F1} 秒）");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Environment.TickCount64 >= deadline)
                {
                    // 保留略过上下文处理迟到结果；只有结果标题成立才允许 OK，不能盲目返回主页。
                    if (recoveringLateResult || resultTitle is null || _options.LateResultRecoveryTimeoutMs <= 0)
                        break;
                    recoveringLateResult = true;
                    deadline = Environment.TickCount64 + _options.LateResultRecoveryTimeoutMs;
                    _log.Warn($"略过正常等待超时，开始迟到结果恢复（最多 {_options.LateResultRecoveryTimeoutMs}ms）；按当前画面复核恢复动作。");
                }

                // 同帧比较结果与 AP 弹窗，避免每轮两次 ADB 截图把短暂结果帧只交给 AP 检测而丢掉。
                // 公共截图入口仍先校验规格和网络错误；取消后不能发送结果 OK。
                var screen = await _automation.CaptureScreenAsync(serial, ct);
                lastScreen = screen;

                if (resultTitle is not null)
                {
                    var resultWait = new WaitOptions
                    {
                        Threshold = _options.SkipResultThreshold,
                        Roi = resultTitle.Roi,
                    };
                    var resultMatch = await _automation.MatchImageAsync(
                        screen,
                        _options.SkipResultTemplatePath,
                        resultWait,
                        ct);
                    if (resultMatch is not null)
                    {
                        if (recoveringLateResult) _log.Info("已确认迟到的 SKIP RESULT，继续结果收尾与返回校验。");
                        resultReady = true;
                        break;
                    }
                }

                if (apRecoveryClose is not null && await _automation.MatchImageAsync(
                    screen, _options.ApRecoveryPromptTemplatePath,
                    new WaitOptions { Threshold = 0.90, Roi = new Roi((screen.Width - 400) / 2, 0, 400, 550) }, ct) is not null)
                {
                    // 略过直接打开回复方式选择页，不出现出击前的 AP 不足询问；专属标题成立后才能查找关闭按钮。
                    var recoveryWait = new WaitOptions
                    {
                        Threshold = _options.SkipResultThreshold,
                        // 同帧底部识别通用关闭模板，不依赖旧 AP 文字裁剪区域。
                        Roi = RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, screen.Width, screen.Height),
                    };
                    var recoveryMatch = await _automation.MatchImageAsync(
                        screen,
                        _options.ApRecoveryCloseTemplatePath,
                        recoveryWait,
                        ct);
                    if (recoveryMatch is not null)
                    {
                        _apStopSignal?.Report("AP 不足，当前略过流程已停止，请补充 AP 后重新开始。");
                        _log.Warn("AP 不足，关闭回复AP弹窗");
                        await _automation.TapAsync(
                            serial,
                            recoveryMatch.CenterX,
                            recoveryMatch.CenterY,
                            ct);
                        await GameTiming.DelayIfPositiveAsync(_options.ApRecoverySettleMs, ct);

                        _log.Info("取消略过窗口");
                        await _automation.TapAsync(
                            serial,
                            skipCancel.CenterX,
                            skipCancel.CenterY,
                            ct);
                        await GameTiming.DelayIfPositiveAsync(_options.SkipDialogWaitMs, ct);

                        return StageSkipResult.ApInsufficient("AP 不足，当前略过任务结束");
                    }
                }

                // 获得单位展示可能先于 SKIP RESULT；结果和 AP 检查优先，不以继续提示代替成功证据。
                if (_options.ContinueUnitAcquisition && acquisitionContinueCount < 6)
                {
                    var next = await Task.Run(() => _automation.TapToNext.Recognize(screen, ct), ct);
                    ct.ThrowIfCancellationRequested();
                    if (next is not null)
                    {
                        executionDialogDeparted = true;
                        _log.Info("略过获得单位展示：已确认 TAP TO NEXT，点击后继续等待 SKIP RESULT。");
                        await _automation.TapAsync(serial, next.CenterX, next.CenterY, ct);
                        // 一次略过最多获得六台机体；每次确认并点击后重新给予完整动画预算。
                        // 以点击次数限制续期，静止提示或丢失点击也不会导致无限等待。
                        acquisitionContinueCount++;
                        if (!recoveringLateResult)
                            deadline = Environment.TickCount64 + Math.Max(0, _options.SkipResultTimeoutMs);
                        _log.Info($"奖励继续 {acquisitionContinueCount}/6：继续等待下一台机体或 SKIP RESULT，剩余预算 {Math.Max(0, deadline - Environment.TickCount64)}ms。");
                        await DelayWithinBudgetAsync(deadline, resultPollMs, ct);
                        continue;
                    }
                }

                if (originalDialog is not null && !executionDialogDeparted)
                {
                    var dialog = await Task.Run(() => _executionDialog!.Recognize(screen, ct), ct);
                    if (dialog is null || !await Task.Run(() => _executionDialog!.IsSameSelection(originalDialog, dialog, ct), ct))
                        executionDialogDeparted = true;
                    else if (recoveringLateResult && !executionRetried)
                    {
                        var fresh = await ConfirmExecutionDialogAsync(serial, originalDialog, ct);
                        if (fresh is null)
                        {
                            // 复核时已离开原弹窗，下一轮优先识别结果/AP；旧坐标失效。
                            executionDialogDeparted = true;
                            continue;
                        }
                        executionRetried = true;
                        _log.Info("略过恢复已连续确认原执行弹窗、次数与 AP 未变，补点执行一次并重新等待结果。");
                        await _automation.TapAsync(serial, fresh.Execute.CenterX, fresh.Execute.CenterY, ct);
                        // 补点后的请求拥有一次完整结果等待，但不能再次补点或续开迟到恢复。
                        deadline = Environment.TickCount64 + Math.Max(0, _options.SkipResultTimeoutMs);
                        await DelayWithinBudgetAsync(deadline, Math.Min(resultPollMs, GameTiming.ConfirmationIntervalMs), ct);
                        continue;
                    }
                }

                if (Environment.TickCount64 >= nextProgressAt)
                {
                    _log.Info("略过执行后尚未确认 SKIP RESULT 或 AP 弹窗，继续等待加载/奖励动画…");
                    nextProgressAt = Environment.TickCount64 + 15000;
                }
                await DelayWithinBudgetAsync(deadline, resultPollMs, ct);
            }

            if (!resultReady)
            {
                ct.ThrowIfCancellationRequested();
                if (_evidence is not null && lastScreen is not null)
                    await SaveResultTimeoutEvidenceAsync(serial, lastScreen, resultTitle, apRecoveryClose, ct);
                ct.ThrowIfCancellationRequested();
                if (originalDialog is not null && !executionDialogDeparted)
                {
                    var unchanged = await ConfirmExecutionDialogAsync(serial, originalDialog, ct);
                    if (unchanged is not null)
                    {
                        _log.Warn("略过执行弹窗仍未退出，已复核原选择并点击取消，停止当前略过；不计为成功。");
                        await _automation.TapAsync(serial, unchanged.Cancel.CenterX, unchanged.Cancel.CenterY, ct);
                        await GameTiming.DelayIfPositiveAsync(_options.SkipDialogWaitMs, ct);
                        return StageSkipResult.Fail(L.Text(executionRetried
                            ? "略过执行补点一次后仍未生效，已发送取消并停止当前任务。"
                            : "略过执行未生效，已发送取消并停止当前任务。"));
                    }
                }
                return StageSkipResult.Fail($"等待 SKIP RESULT 超时（正常段 {_options.SkipResultTimeoutMs}ms，迟到恢复 {Math.Max(0, _options.LateResultRecoveryTimeoutMs)}ms，已点击奖励继续 {acquisitionContinueCount}/6 次），未确认略过结果页");
            }
        }

        ct.ThrowIfCancellationRequested();
        _log.Info($"点击结果 OK: ({resultOk.CenterX},{resultOk.CenterY})");
        await _automation.TapAsync(serial, resultOk.CenterX, resultOk.CenterY, ct);
        await GameTiming.DelayIfPositiveAsync(_options.SettleAfterOkMs, ct);

        if (!await ConfirmPostSkipAsync(serial, stageLayout, acquisitionContinueCount, ct))
            return StageSkipResult.Fail(string.IsNullOrWhiteSpace(_options.PostSkipPageTemplatePath)
                ? "未确认升级提示已关闭并返回目标关卡，已停止后续操作"
                : "未确认返回强化培育关卡，已停止后续操作");

        return StageSkipResult.Ok("略过执行完成");
    }

    /// <summary>结果等待真正超时后保存最后观察帧和本阶段模板，避免只得到后续主界面恢复的无关候选。</summary>
    private async Task SaveResultTimeoutEvidenceAsync(string serial, ScreenImage screen,
        UiAnchor? resultTitle, UiAnchor? apRecoveryClose, CancellationToken ct)
    {
        try
        {
            var candidates = await Task.Run(() =>
            {
                var matcher = new TemplateMatcher();
                var observations = new List<RecognitionCandidate>();
                foreach (var (anchor, path) in new[] { (resultTitle, _options.SkipResultTemplatePath),
                             (apRecoveryClose, _options.ApRecoveryCloseTemplatePath) })
                {
                    if (anchor is null) continue;
                    var template = LoadPostSkipTemplate(path).Primary;
                    var best = matcher.MatchCandidate(screen.Bgra, screen.Width, screen.Height, template.Bgra,
                        template.Width, template.Height, _options.SkipResultThreshold, anchor.Roi, ct);
                    observations.Add(new(Path.GetFileName(path), ImageCodec.EncodePng(template.Bgra, template.Width, template.Height),
                        template.Width, template.Height, anchor.Roi, _options.SkipResultThreshold, best));
                }
                return observations.ToArray();
            }, ct);
            ct.ThrowIfCancellationRequested();
            await _evidence!.SaveAsync("skip-result-timeout", serial,
                ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height), screen, candidates);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 取证是诊断副作用；素材或编码异常不能替换已经确认的业务超时，取消仍须向上传递。
            _log.Warn($"略过结果超时取证失败: {ex.GetType().Name}；原识别结果保持不变");
        }
    }
}

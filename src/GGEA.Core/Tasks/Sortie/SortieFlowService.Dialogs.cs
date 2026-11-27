using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    /// <summary>每次出击独立确认；点击后的残留弹窗只等待消失，避免重复消耗次数。</summary>
    internal sealed class EventAttemptDialog(string? hint)
    {
        internal string? Hint = hint;
        internal readonly StableStageEntry Button = new();
        internal bool Submitted;
        internal bool Visible;
    }

    internal async Task<bool> HandleEventAttemptAsync(string serial, ScreenImage screen, EventAttemptDialog dialog, CancellationToken ct)
    {
        if (dialog.Hint is null) return false;
        dialog.Visible = Match(screen, dialog.Hint, new Roi(740, 400, 450, 95), StageSortieTemplateThreshold) is not null;
        if (!dialog.Visible)
        {
            dialog.Button.Observe(null, "sortie");
            if (dialog.Submitted) dialog.Hint = null;
            return false;
        }
        if (!dialog.Submitted)
        {
            var button = _progressButtons.MatchBlueLabel(screen, _emptySlotSortieTemplatePath,
                new Roi(945, 765, 415, 170), StageSortieTemplateThreshold, ct);
            if (dialog.Button.Observe(button, "sortie", 2) is { } confirmed)
            {
                await _automation.TapAsync(serial, confirmed.CenterX, confirmed.CenterY, ct);
                dialog.Submitted = true;
                _log.Info("事件推图：已确认消耗 1 次可出击次数，点击出击，等待后续画面。");
            }
        }
        await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        return true;
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
}

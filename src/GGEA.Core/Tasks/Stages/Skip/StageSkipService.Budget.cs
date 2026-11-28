using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class StageSkipService
{
    internal Task<StageSkipResult> SkipDailyHardAsync(string serial, UiLayout layout, CancellationToken ct) =>
        SkipWithinBudgetAsync(serial, layout, new StageSkipBudget(_automation.GlobalScreens.TextOcr, _automation.Resources), int.MaxValue, true, ct);

    /// <summary>标准关卡从实际余额和单价计算批次上限。关卡页、弹窗调数及结算各自核对新帧。</summary>
    internal async Task<StageSkipResult> SkipWithinBudgetAsync(string serial, UiLayout layout, StageSkipBudget budget,
        int needed, bool hard, CancellationToken ct, CaptureRequestBudget? capture = null)
    {
        if (string.IsNullOrWhiteSpace(_options.SkipResultTemplatePath))
            return StageSkipResult.Fail(L.Text("未配置略过结果识别素材，不能用固定等待确认执行结果。"));
        if (needed < 1) throw new ArgumentOutOfRangeException(nameof(needed));
        if ((hard || capture is not null) && !await IsSkipEnabledAsync(serial, layout, ct))
            return StageSkipResult.Fail("略过不可用（次数上限）", isUnavailable: true);
        var before = await StableObservation.WaitAsync(async () => budget.ReadStage(
            await _automation.CaptureScreenAsync(serial, ct), hard, ct, checkNormalLimit: capture is not null), (a, b) => a == b,
            GameTiming.RecognitionTimeoutMs, ct);
        if (before is null) return StageSkipResult.Fail(L.Text("无法确认关卡体力、单次消耗或可用次数。"));
        int count = before.CountFor(needed);
        if (capture is not null) count = Math.Min(count, capture.RemainingAp / before.Cost);
        // 普通关没有可读限次时只执行一回，并以弹窗与奖励数量复核，避免超出实际可用次数。
        if (capture is not null && before.Remaining == int.MaxValue) count = Math.Min(count, 1);
        if (before.Remaining == 0) return StageSkipResult.Fail("略过不可用（次数上限）", isUnavailable: true);
        if (count == 0) return StageSkipResult.ApInsufficient(L.Text("剩余 AP 不足一次略过。"));

        var skip = layout.Require("stageSkip", "关卡布局缺少 stageSkip");
        await _automation.TapAsync(serial, skip.CenterX, skip.CenterY, ct);
        var dialogReader = _executionDialog ?? new SkipExecutionDialogRecognizer();
        Task<SkipExecutionDialog?> Observe(int? expected) => StableObservation.WaitAsync(async () =>
        {
            var screen = await _automation.CaptureScreenAsync(serial, ct);
            var dialog = dialogReader.Recognize(screen, ct);
            var value = dialog is null ? null : budget.Selected(screen, ct);
            return value is > 0 && (expected is null || value == expected) ? dialog : null;
        }, (a, b) => dialogReader.IsSameSelection(a, b, ct), GameTiming.RecognitionTimeoutMs, ct);

        var current = await Observe(null);
        if (current is null) return StageSkipResult.Fail(L.Text("未能确认略过次数和执行弹窗。"));
        var selected = budget.Selected(current.Screen, ct)!.Value;
        // 普通关卡可能记住很大的次数；回到 1 后逐次增加，每次都读回期望值。
        if (selected > count && selected - count > count)
        {
            await _automation.TapAsync(serial, 630, 477, ct);
            current = await Observe(1);
            selected = 1;
        }
        while (current is not null && selected != count)
        {
            var increase = selected < count;
            await _automation.TapAsync(serial, increase ? 1157 : 759, 477, ct);
            selected += increase ? 1 : -1;
            current = await Observe(selected);
        }
        if (current is null || budget.OriginalAp(current.Screen, ct) is not int ap ||
            ap < before.Ap || ap - before.Ap > 1 ||
            budget.ProjectedAp(current.Screen, ct) != ap - (long)count * before.Cost || ap < (long)count * before.Cost)
            return StageSkipResult.Fail(L.Text("略过次数或 AP 预览未通过核对。"));

        _log.Info(L.Format($"本批略过 {count} 次，当前 AP {ap}，每次消耗 {before.Cost}。"));
        // 夺取委托的 AP 预算只在最终确认后扣除，结果丢失也不重发执行。
        capture?.Reserve(count * before.Cost);
        await _automation.TapAsync(serial, current.Execute.CenterX, current.Execute.CenterY, ct);
        // 未耗尽时按钮仍亮。确认扣减后的次数，读取结算后的实际体力以涵盖升级回复。
        int? remainingAp = null;
        bool Returned(ScreenImage f, CancellationToken token)
        {
            var after = budget.ReadStage(f, hard, token, checkNormalLimit: capture is not null);
            if (after is not null)
            {
                if (after.Cost != before.Cost) return false;
                if (before.Remaining == int.MaxValue || after.Remaining == before.Remaining - count)
                {
                    remainingAp = after.Ap;
                    return true;
                }
                // 有明确的余次读数时必须与本批扣减相符。
                if (after.Remaining != int.MaxValue) return false;
            }
            if (before.Remaining != count || !budget.OnStage(f, token) ||
                string.IsNullOrEmpty(_options.SkipCountLimitTemplatePath)) return false;
            return LoadPostSkipTemplate(_options.SkipCountLimitTemplatePath).Match(new TemplateMatcher(), f,
                _options.SkipCountLimitThreshold, layout.Require("skipCountLimit", "关卡布局缺少 skipCountLimit").Roi, token) is not null;
        }
        var result = await CompleteExecutedSkipCoreAsync(serial, layout, _executionDialog is null || capture is not null ? null : current, ct, Returned,
            capture is null ? null : (frame, token) => capture.ReadRewards(frame, count, _automation.GlobalScreens.TextOcr, token));
        return result.Success ? result with { ExecutedCount = count, RemainingAp = remainingAp } : result;
    }
}

using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class StageService
{
    /// <summary>
    /// 选关重试仍失败时，从当前作品关卡页点击左上角返回主要关卡画布，
    /// 并保留当前作品居中的可靠上下文，供 AP 任务链继续下一作品。
    /// </summary>
    public async Task<StageNavigationResult> ReturnCurrentWorkToCanvasAsync(
        string serial,
        UiLayout stageLayout,
        StageWorkPositionsData positions,
        int workId,
        CancellationToken ct = default)
    {
        var work = positions.Find(workId);
        if (work is null ||
            !Context.IsReliable ||
            Context.Serial != serial ||
            Context.PageState != StagePageState.WorkStageList ||
            Context.CurrentWorkId != workId)
        {
            return StageNavigationResult.Fail(
                StageNavigationFailure.InvalidContext,
                "缺少可靠的当前作品关卡上下文，不能安全返回主要关卡画布");
        }

        await ReturnWorkToCanvasAsync(
            serial,
            stageLayout,
            work,
            Context.Mode,
            workId,
            preservedViewOffsetX: null,
            ct);
        return StageNavigationResult.Ok("已返回主要关卡画布");
    }

    /// <summary>执行规划好的标准横向滑动。</summary>
    private async Task ExecuteMovementAsync(
        string serial,
        StageMovementPlan movement,
        CancellationToken ct)
    {
        for (var i = 0; i < movement.SwipeCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var screenDragDistance = _options.SwipeLeftStartX - _options.SwipeLeftEndX;
            var isHalfFinalSwipe = i == movement.SwipeCount - 1 && movement.UsesHalfFinalSwipe;
            // 只允许固定半屏手势，不执行任意比例的短滑，确保输入动作可按日志稳定复现。
            var screenDelta = isHalfFinalSwipe
                ? Math.Max(1, screenDragDistance / 2)
                : screenDragDistance;
            var durationMs = isHalfFinalSwipe
                ? Math.Max(50, _options.SwipeDurationMs / 2)
                : _options.SwipeDurationMs;

            if (isHalfFinalSwipe)
            {
                var correction = movement.Direction > 0 ? _options.HalfSwipeLeftTapCorrection : _options.HalfSwipeRightTapCorrection;
                _log.Info($"执行末次半屏滑动: 屏幕拖动 {screenDelta}px，" +
                    $"手指{(movement.Direction > 0 ? "左" : "右")}滑，点击 X 补偿 {correction:+0;-0;0}px（已纳入视口规划）");
            }

            if (movement.Direction > 0)
            {
                // 视口向右：手指向左滑。
                var endX = _options.SwipeLeftStartX - screenDelta;
                await _automation.SwipeAsync(
                    serial,
                    _options.SwipeLeftStartX,
                    _options.SwipeY,
                    endX,
                    _options.SwipeY,
                    durationMs,
                    ct);
            }
            else
            {
                // 视口向左：手指向右滑。
                var endX = _options.SwipeLeftEndX + screenDelta;
                await _automation.SwipeAsync(
                    serial,
                    _options.SwipeLeftStartX - screenDragDistance,
                    _options.SwipeY,
                    endX,
                    _options.SwipeY,
                    durationMs,
                    ct);
            }

            // 最后一次滑动由作品节点点击前的专用稳定等待处理；
            // 长等待通过新截图确认停稳，不再以倒计时结束作为动画完成证据。
            if (i < movement.SwipeCount - 1)
            {
                await WaitForCanvasAsync(serial, _options.SwipeSettleMs, ct);
            }
        }

    }

    /// <summary>最后一次作品定位滑动后等待画布稳定；无滑动时不额外延迟。</summary>
    private Task WaitForWorkTapAfterMovementAsync(string serial, StageMovementPlan movement, CancellationToken ct)
        => movement.SwipeCount > 0
            ? WaitForCanvasAsync(serial, _options.WorkTapSettleMs, ct)
            : Task.CompletedTask;

    /// <summary>
    /// 逐次向左移动并查找初代高达锚点；识别到后执行一次大幅拖动，确保画布到达边界。
    /// </summary>
    private async Task<bool> ResetToLeftmostAsync(string serial, CancellationToken ct)
    {
        var fullCanvasRoi = new Roi(0, 80, 1920, 800);
        for (var swipeCount = 0; swipeCount <= _options.ResetSearchMaxSwipes; swipeCount++)
        {
            ct.ThrowIfCancellationRequested();
            var anchor = await _recognizer.FindAsync(
                serial,
                LeftBoundaryAnchorTemplate,
                fullCanvasRoi,
                _options.ResetAnchorThreshold,
                ct);
            if (anchor is not null)
            {
                _log.Info(
                    $"识别到左端锚点: confidence={anchor.Confidence:F3}, " +
                    $"center=({anchor.CenterX},{anchor.CenterY})，执行一次左端大幅拖动");
                await SwipeCanvasToLeftBoundaryAsync(serial, ct);

                return true;
            }

            if (swipeCount == _options.ResetSearchMaxSwipes)
            {
                break;
            }

            _log.Info($"未识别到左端锚点，继续向左拖动: {swipeCount + 1}/{_options.ResetSearchMaxSwipes}");
            await SwipeCanvasTowardLeftAsync(serial, ct);
        }

        return false;
    }

    /// <summary>手指向右滑动一次，使主要关卡画布朝最左端移动并等待稳定。</summary>
    private async Task SwipeCanvasTowardLeftAsync(string serial, CancellationToken ct)
    {
        await _automation.SwipeAsync(
            serial,
            _options.SwipeLeftEndX,
            _options.SwipeY,
            _options.SwipeLeftStartX,
            _options.SwipeY,
            _options.SwipeDurationMs,
            ct);
        await WaitForCanvasAsync(serial, _options.SwipeSettleMs, ct);
    }

    /// <summary>识别到初代高达后执行一次接近全屏宽度的拖动，使画布触碰最左边界。</summary>
    private async Task SwipeCanvasToLeftBoundaryAsync(string serial, CancellationToken ct)
    {
        await _automation.SwipeAsync(
            serial,
            _options.ResetBoundarySwipeStartX,
            _options.SwipeY,
            _options.ResetBoundarySwipeEndX,
            _options.SwipeY,
            _options.SwipeDurationMs,
            ct);
        await WaitForCanvasAsync(serial, _options.SwipeSettleMs, ct);
    }

    /// <summary>
    /// 已确认位于作品详情或关卡页时返回主要关卡画布。普通关卡页会让作品居中，
    /// 未解锁详情页则维持进入详情前的镜头；调用方必须传入对应的真实状态。
    /// </summary>
    private async Task ReturnWorkToCanvasAsync(
        string serial,
        UiLayout stageLayout,
        StageWorkPosition work,
        StageMapMode mode,
        int workId,
        double? preservedViewOffsetX,
        CancellationToken ct)
    {
        var stageBack = stageLayout.Require("stageBack", "关卡布局缺少 stageBack 锚点");
        await _automation.TapAsync(serial, stageBack.CenterX, stageBack.CenterY, ct);
        await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);

        var viewOffset = preservedViewOffsetX ?? Math.Max(0, work.CanvasX - _options.ScreenCenterX);
        if (preservedViewOffsetX is not null)
        {
            _log.Info($"未解锁作品返回画布，保留选择前视口偏移: {viewOffset:F0}px");
        }

        Context.SetWorkCanvas(serial, mode, workId, viewOffset);
    }

    /// <summary>保留短动画设置；较长滑动等待改为观察画布，后续流程仍须核对页面与目标。</summary>
    private Task WaitForCanvasAsync(string serial, int milliseconds, CancellationToken ct)
        => milliseconds <= 5000 ? GameTiming.DelayIfPositiveAsync(milliseconds, ct)
            : _automation.WaitForCanvasStillAsync(serial, new Roi(0, 120, 1920, 760), ct);
}

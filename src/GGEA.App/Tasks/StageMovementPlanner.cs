namespace GGEA.App.Tasks;

/// <summary>
/// 作品间数学移动规划器：根据当前视口偏移和目标绝对 X 坐标，计算滑动次数与点击位置。
/// 规划时会处理画布左边界，避免最后一次滑动被边界截短后仍按完整距离累计。
/// 目标已在屏幕内时直接点击不滑动；优先使用整屏滑动。
/// 只有整屏滑动跳过安全点击范围时，才使用一次固定半屏手势作为可重复的兜底。
/// </summary>
public static class StageMovementPlanner
{
    /// <summary>
    /// 从当前视口移动到目标作品。
    /// Direction：1 表示视口向右（手指向左），-1 表示视口向左（手指向右）。
    /// </summary>
    public static StageMovementPlan PlanFromViewport(
        double currentViewOffsetX,
        double targetWorldX,
        StageNavigationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SwipeCanvasDelta <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "标准滑动画布距离必须大于 0");
        }

        // 方向由目标相对当前屏幕中心的位置决定；视口偏移不得越过画布左边界 0。
        var currentCenterWorldX = currentViewOffsetX + options.ScreenCenterX;
        var direction = Math.Sign(targetWorldX - currentCenterWorldX);
        // 节点靠近任一屏幕边缘时都可能被固定控件覆盖或被裁切；只有安全区域内才能直接点击。
        var workTapMaxX = Math.Max(
            options.WorkTapMinX,
            options.ViewportWidth - Math.Max(0, options.WorkTapRightMargin));
        StageMovementPlan? bestFull = null;
        StageMovementPlan? bestHalf = null;

        void Consider(int fullSwipes, bool useHalfFinalSwipe)
        {
            var fullOffset = Math.Max(
                0,
                currentViewOffsetX + direction * fullSwipes * options.SwipeCanvasDelta);
            // 两个方向的参数均为点击 X 的有符号补偿，不能按手势方向再次翻转其符号。
            // 先校正视口再应用边界与安全区，保证点击点和未解锁返回后的上下文使用同一坐标系。
            // 补偿过大时将预计位移限制为 0，避免把半屏手势错误规划成反向移动。
            var correction = direction > 0 ? options.HalfSwipeLeftTapCorrection : options.HalfSwipeRightTapCorrection;
            var halfCanvasDelta = Math.Max(0, options.SwipeCanvasDelta / 2d - direction * (double)correction);
            var finalViewOffset = useHalfFinalSwipe
                ? Math.Max(0, fullOffset + direction * halfCanvasDelta)
                : fullOffset;
            var screenX = (int)Math.Round(targetWorldX - finalViewOffset);
            if (screenX < options.WorkTapMinX || screenX > workTapMaxX)
            {
                return;
            }

            var actualMovement = (int)Math.Round(finalViewOffset - currentViewOffsetX);
            var swipeCount = fullSwipes + (useHalfFinalSwipe ? 1 : 0);
            var candidate = new StageMovementPlan(
                swipeCount == 0 ? 0 : direction,
                swipeCount,
                screenX,
                actualMovement,
                useHalfFinalSwipe);
            var currentBest = useHalfFinalSwipe ? bestHalf : bestFull;
            if (currentBest is null ||
                Math.Abs(candidate.TargetScreenX - options.ScreenCenterX) <
                Math.Abs(currentBest.TargetScreenX - options.ScreenCenterX))
            {
                if (useHalfFinalSwipe)
                {
                    bestHalf = candidate;
                }
                else
                {
                    bestFull = candidate;
                }
            }
        }

        // 目标已在两侧安全点击范围内时不再滑动，避免任何滑动误差。
        var currentScreenX = (int)Math.Round(targetWorldX - currentViewOffsetX);
        if (currentScreenX >= options.WorkTapMinX && currentScreenX <= workTapMaxX)
        {
            return new StageMovementPlan(0, 0, currentScreenX, 0);
        }

        for (var fullSwipes = 0; fullSwipes <= options.MaxCalculatedSwipes; fullSwipes++)
        {
            Consider(fullSwipes, useHalfFinalSwipe: false);
            if (fullSwipes < options.MaxCalculatedSwipes)
            {
                Consider(fullSwipes, useHalfFinalSwipe: true);
            }
        }

        // 整屏路径已由当前标定验证，优先采用；半屏只解决整屏跨过安全区的间隙。
        return bestFull ?? bestHalf ?? throw new InvalidOperationException(
            $"无法在 {options.MaxCalculatedSwipes} 次标准滑动内把目标移入屏幕可见范围");
    }
}

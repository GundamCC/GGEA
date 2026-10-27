using GGEA.App.Tasks;

namespace GGEA.Tests;

/// <summary>作品绝对坐标移动规划测试。</summary>
public class StageMovementPlannerTests
{
    /// <summary>左滑补偿可关闭或调整，点击点与最终视口必须始终对应同一个世界坐标。</summary>
    [Theory]
    [InlineData(0, 1059, 925)]
    [InlineData(200, 1259, 725)]
    [InlineData(350, 1409, 575)]
    [InlineData(-200, 859, 1125)]
    public void Plan_LeftHalfSwipe_AppliesConfiguredCorrection(int correction, int screenX, int movement)
    {
        var plan = StageMovementPlanner.PlanFromViewport(2578, 4562, new StageNavigationOptions
        {
            SwipeCanvasDelta = 1850,
            HalfSwipeLeftTapCorrection = correction,
        });

        Assert.True(plan.UsesHalfFinalSwipe);
        Assert.Equal(1, plan.Direction);
        Assert.Equal(screenX, plan.TargetScreenX);
        Assert.Equal(movement, plan.CanvasMovement);
        Assert.Equal(4562, 2578 + plan.CanvasMovement + plan.TargetScreenX);
    }

    /// <summary>修改左滑参数不能改变右滑半屏原有的负向 200px 补偿。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(350)]
    public void Plan_RightHalfSwipe_KeepsExistingCorrection(int leftCorrection)
    {
        var plan = StageMovementPlanner.PlanFromViewport(6423, 6537, new StageNavigationOptions
        {
            SwipeCanvasDelta = 1770,
            HalfSwipeLeftTapCorrection = leftCorrection,
        });

        Assert.True(plan.UsesHalfFinalSwipe);
        Assert.Equal(-1, plan.Direction);
        Assert.Equal(799, plan.TargetScreenX);
        Assert.Equal(-685, plan.CanvasMovement);
    }

    /// <summary>右滑参数的正负号直接控制点击方向，与左滑参数独立，并同步更新视口。</summary>
    [Theory]
    [InlineData(-200, 799, -685)]
    [InlineData(0, 999, -885)]
    [InlineData(200, 1199, -1085)]
    public void Plan_RightHalfSwipe_AppliesSignedCorrection(int correction, int screenX, int movement)
    {
        var plan = StageMovementPlanner.PlanFromViewport(6423, 6537, new StageNavigationOptions
        {
            SwipeCanvasDelta = 1770,
            HalfSwipeLeftTapCorrection = -350,
            HalfSwipeRightTapCorrection = correction,
        });

        Assert.True(plan.UsesHalfFinalSwipe);
        Assert.Equal(-1, plan.Direction);
        Assert.Equal(screenX, plan.TargetScreenX);
        Assert.Equal(movement, plan.CanvasMovement);
        Assert.Equal(6537, 6423 + plan.CanvasMovement + plan.TargetScreenX);
    }

    /// <summary>补偿后超出右侧安全区的方案应被拒绝，不能把点击点强行夹到屏幕边缘。</summary>
    [Fact]
    public void Plan_CorrectionOutsideSafeRange_RejectsUnsafeHalfSwipe()
    {
        Assert.Throws<InvalidOperationException>(() => StageMovementPlanner.PlanFromViewport(
            2578, 4562, new StageNavigationOptions
            {
                SwipeCanvasDelta = 1850,
                HalfSwipeLeftTapCorrection = 800,
                MaxCalculatedSwipes = 1,
            }));
    }

    private static readonly StageNavigationOptions Options = new()
    {
        ScreenCenterX = 960,
        ViewportWidth = 1920,
        SwipeCanvasDelta = 1500,
    };

    [Fact]
    public void Plan_TargetAtCenter_DoesNotSwipe()
    {
        var plan = StageMovementPlanner.PlanFromViewport(40, 1000, Options);

        Assert.Equal(0, plan.Direction);
        Assert.Equal(0, plan.SwipeCount);
        Assert.Equal(960, plan.TargetScreenX);
    }

    [Fact]
    public void Plan_TargetToRight_UsesStandardSwipesAndRemainderClick()
    {
        var plan = StageMovementPlanner.PlanFromViewport(40, 4200, Options);

        Assert.Equal(1, plan.Direction);
        Assert.Equal(2, plan.SwipeCount);
        Assert.Equal(1160, plan.TargetScreenX);
        Assert.Equal(3000, plan.CanvasMovement);
    }

    [Fact]
    public void Plan_TargetToLeft_ReversesSwipeDirection()
    {
        var plan = StageMovementPlanner.PlanFromViewport(3240, 1000, Options);

        Assert.Equal(-1, plan.Direction);
        Assert.Equal(3, plan.SwipeCount);
        Assert.Equal(1000, plan.TargetScreenX);
        Assert.Equal(-3240, plan.CanvasMovement);
    }

    /// <summary>00 未解锁返回后切换哈萨维时，应按 1920px 位移计算点击位置。</summary>
    [Fact]
    public void Plan_00ToHathaway_UsesCalibratedFullSwipe()
    {
        var calibratedOptions = new StageNavigationOptions
        {
            ScreenCenterX = 960,
            ViewportWidth = 1920,
            SwipeCanvasDelta = 1920,
        };

        var plan = StageMovementPlanner.PlanFromViewport(
            currentViewOffsetX: 4268,
            targetWorldX: 6492,
            calibratedOptions);

        Assert.Equal(1, plan.Direction);
        Assert.Equal(1, plan.SwipeCount);
        Assert.Equal(304, plan.TargetScreenX);
        Assert.Equal(1920, plan.CanvasMovement);
    }

    /// <summary>实机标定后，逆袭的夏亚应只滑动两次并在屏幕右侧安全区域点击。</summary>
    [Fact]
    public void Plan_CharsCounterAttack_WithCalibratedDelta_UsesTwoSwipes()
    {
        var calibratedOptions = new StageNavigationOptions
        {
            ScreenCenterX = 960,
            ViewportWidth = 1920,
            SwipeCanvasDelta = 1770,
        };

        var plan = StageMovementPlanner.PlanFromViewport(0, 5034, calibratedOptions);

        Assert.Equal(1, plan.Direction);
        Assert.Equal(2, plan.SwipeCount);
        Assert.Equal(1494, plan.TargetScreenX);
        Assert.Equal(3540, plan.CanvasMovement);
    }

    /// <summary>从 F91 返回初代高达时，最后一次反向滑动会触碰左边界，点击点必须按边界重算。</summary>
    [Fact]
    public void Plan_F91ToFirstGundam_ClampsAtLeftBoundary()
    {
        var calibratedOptions = new StageNavigationOptions
        {
            ScreenCenterX = 960,
            ViewportWidth = 1920,
            SwipeCanvasDelta = 1770,
        };

        var plan = StageMovementPlanner.PlanFromViewport(
            currentViewOffsetX: 7383 - 960,
            targetWorldX: 734,
            calibratedOptions);

        Assert.Equal(-1, plan.Direction);
        Assert.Equal(4, plan.SwipeCount);
        Assert.Equal(734, plan.TargetScreenX);
        Assert.Equal(-(7383 - 960), plan.CanvasMovement);
    }

    /// <summary>初代高达受左边界限制无法居中，向右移动时应从偏移 0 而非作品中心开始。</summary>
    [Fact]
    public void Plan_FirstGundamToCharsCounterAttack_StartsAtLeftBoundary()
    {
        var calibratedOptions = new StageNavigationOptions
        {
            ScreenCenterX = 960,
            ViewportWidth = 1920,
            SwipeCanvasDelta = 1770,
        };

        var plan = StageMovementPlanner.PlanFromViewport(0, 5034, calibratedOptions);

        Assert.Equal(2, plan.SwipeCount);
        Assert.Equal(1494, plan.TargetScreenX);
    }

    /// <summary>F91 切到相邻的哈萨维时，目标虽可见但位于左缘，应先回退画布避开固定控件。</summary>
    [Fact]
    public void Plan_F91ToHathaway_LeftEdgeTargetRepositionsBeforeClick()
    {
        var calibratedOptions = new StageNavigationOptions
        {
            ScreenCenterX = 960,
            ViewportWidth = 1920,
            SwipeCanvasDelta = 1770,
        };

        var plan = StageMovementPlanner.PlanFromViewport(
            currentViewOffsetX: 7383 - 960,
            targetWorldX: 6537,
            calibratedOptions);

        Assert.Equal(-1, plan.Direction);
        Assert.Equal(1, plan.SwipeCount);
        Assert.Equal(799, plan.TargetScreenX);
        Assert.True(plan.UsesHalfFinalSwipe);
    }

    /// <summary>左缘可见节点会压到固定控件区，必须回退一次画布而不是直接点击。</summary>
    [Fact]
    public void Plan_LeftEdgeVisibleTarget_RepositionsBeforeClick()
    {
        var calibratedOptions = new StageNavigationOptions
        {
            ScreenCenterX = 960,
            ViewportWidth = 1920,
            SwipeCanvasDelta = 1770,
            WorkTapMinX = 180,
        };

        // 复现从 ZZ(2565) 返回画布后切换至 SEED(1688)：目标原本落在 X=83。
        var plan = StageMovementPlanner.PlanFromViewport(
            currentViewOffsetX: 2565 - 960,
            targetWorldX: 1688,
            calibratedOptions);

        Assert.Equal(-1, plan.Direction);
        Assert.Equal(1, plan.SwipeCount);
        Assert.Equal(1688, plan.TargetScreenX);
    }

    /// <summary>右缘作品虽在画面内但已被裁切，必须重新定位，禁止点击 1888px 的边缘坐标。</summary>
    [Fact]
    public void Plan_TargetAtRightEdge_UsesHalfSwipeBeforeTapping()
    {
        var calibratedOptions = new StageNavigationOptions
        {
            ScreenCenterX = 960,
            ViewportWidth = 1920,
            SwipeCanvasDelta = 1770,
        };

        // 复现作品节点位于屏幕 X=1888、Y=525 的情况；右缘可见不代表节点完整可点击。
        var plan = StageMovementPlanner.PlanFromViewport(
            currentViewOffsetX: 5500,
            targetWorldX: 7388,
            calibratedOptions);

        Assert.Equal(1, plan.SwipeCount);
        Assert.Equal(1203, plan.TargetScreenX);
        Assert.Equal(685, plan.CanvasMovement);
        Assert.True(plan.UsesHalfFinalSwipe);
    }

    /// <summary>
    /// 整屏滑动会让目标从右缘外跨到左侧保护区时，应以固定半屏手势兜底，
    /// 不能再使用任意比例的短滑。
    /// </summary>
    [Fact]
    public void Plan_StandardSwipeSkipsSafeRange_UsesHalfFinalSwipe()
    {
        var calibratedOptions = new StageNavigationOptions
        {
            ScreenCenterX = 960,
            ViewportWidth = 1920,
            SwipeCanvasDelta = 1850,
            WorkTapMinX = 180,
        };

        // 复现 SEED DESTINY 未解锁返回（视口 2578）后切换 00(4562) 的失败路径。
        var plan = StageMovementPlanner.PlanFromViewport(2578, 4562, calibratedOptions);

        Assert.Equal(1, plan.Direction);
        Assert.Equal(1, plan.SwipeCount);
        Assert.Equal(1259, plan.TargetScreenX);
        Assert.Equal(725, plan.CanvasMovement);
        Assert.True(plan.UsesHalfFinalSwipe);
    }
}

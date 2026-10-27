using GGEA.App.Tasks;

namespace GGEA.Tests;

/// <summary>关卡视口五槽位映射测试。</summary>
public class StageSlotPlannerTests
{
    /// <summary>F91 当前 HARD1 时，视口显示最后五关，但 HARD2/3 尚未解锁不应执行。</summary>
    [Fact]
    public void F91_CurrentHard1_ShowsLastFiveButSkipsLaterStages()
    {
        Assert.Equal(4, StageSlotPlanner.GetViewportStart(
            normalCount: 5,
            hardCount: 3,
            currentHardIndex: 1));

        Assert.True(StageSlotPlanner.TryGetSlotIndex(
            normalCount: 5,
            hardCount: 3,
            currentHardIndex: 1,
            targetHardIndex: 3,
            out var slotIndex));
        Assert.Equal(5, slotIndex);

        Assert.True(StageSlotPlanner.ShouldExecute(
            normalCount: 5,
            currentHardIndex: 1,
            targetHardIndex: 1));
        Assert.False(StageSlotPlanner.ShouldExecute(
            normalCount: 5,
            currentHardIndex: 1,
            targetHardIndex: 2));
    }

    /// <summary>F91 当前 HARD3 时，最后五关为普通4/5和HARD1/2/3，HARD1 可执行。</summary>
    [Fact]
    public void F91_CurrentHard3_MapsHard1ToSlot3()
    {
        Assert.True(StageSlotPlanner.TryGetSlotIndex(
            normalCount: 5,
            hardCount: 3,
            currentHardIndex: 3,
            targetHardIndex: 1,
            out var slotIndex));
        Assert.Equal(3, slotIndex);

        Assert.True(StageSlotPlanner.ShouldExecute(
            normalCount: 5,
            currentHardIndex: 3,
            targetHardIndex: 1));
    }

    /// <summary>00 高达当前 HARD5 时，视口被右端钳制，五个槽位正好是 HARD1~HARD5。</summary>
    [Fact]
    public void Gundam00_CurrentHard5_ShowsHard1ToHard5()
    {
        Assert.Equal(25, StageSlotPlanner.GetViewportStart(
            normalCount: 24,
            hardCount: 5,
            currentHardIndex: 5));

        Assert.True(StageSlotPlanner.TryGetSlotIndex(
            normalCount: 24,
            hardCount: 5,
            currentHardIndex: 5,
            targetHardIndex: 1,
            out var firstSlot));
        Assert.Equal(1, firstSlot);

        Assert.True(StageSlotPlanner.TryGetSlotIndex(
            normalCount: 24,
            hardCount: 5,
            currentHardIndex: 5,
            targetHardIndex: 5,
            out var lastSlot));
        Assert.Equal(5, lastSlot);
    }

    /// <summary>中间位置时当前关居中，视口从 currentIndex-2 开始。</summary>
    [Fact]
    public void MiddlePosition_CurrentStageCentered()
    {
        // normalCount=2、hardCount=6 时，当前 HARD2 的整体序号为 4。
        Assert.Equal(2, StageSlotPlanner.GetViewportStart(
            normalCount: 2,
            hardCount: 6,
            currentHardIndex: 2));

        Assert.True(StageSlotPlanner.TryGetSlotIndex(
            normalCount: 2,
            hardCount: 6,
            currentHardIndex: 2,
            targetHardIndex: 2,
            out var slotIndex));
        Assert.Equal(3, slotIndex);
    }

    /// <summary>SIDE STORY 应插入 BOSS 与 HARD 之间，避免 HARD 槽位整体左移。</summary>
    [Fact]
    public void SideStories_ShiftHardStagesAfterBoss()
    {
        Assert.True(WorkStageSequencePlanner.TryGetPosition(
            normalCount: 23,
            sideStories: 6,
            hardCount: 5,
            WorkStageKind.Hard,
            number: 1,
            out var hard));
        Assert.Equal(30, hard!.OverallIndex);

        Assert.True(WorkStageSequencePlanner.TryGetPosition(
            normalCount: 23,
            sideStories: 6,
            hardCount: 5,
            WorkStageKind.SideStory,
            number: 6,
            out var sideStory));
        Assert.Equal(29, sideStory!.OverallIndex);

        Assert.Equal(30, StageSlotPlanner.GetOverallIndex(
            normalCount: 23,
            hardIndex: 1,
            sideStories: 6));
    }
}

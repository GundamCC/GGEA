using GGEA.App.Config;
using GGEA.App.Tasks;

namespace GGEA.Tests;

/// <summary>多作品略过任务路线规划测试。</summary>
public class StageRoutePlannerTests
{
    [Fact]
    public void PlanOptimized_GroupsSameWorkAndChoosesNearestEnd()
    {
        var positions = CreatePositions((1, 1000), (2, 4000), (3, 7000));
        var tasks = new[]
        {
            HardTask(1, stage: 3, repeat: 2),
            HardTask(3, stage: 1, repeat: 3),
            HardTask(1, stage: 2, repeat: 1),
            HardTask(2, stage: 2, repeat: 1),
        };

        var plan = StageRoutePlanner.PlanOptimized(tasks, positions, currentWorldX: 6500);

        Assert.Equal(new[] { 3, 2, 1 }, plan.Visits.Select(visit => visit.WorkId));
        Assert.Equal(2, plan.Visits.Single(visit => visit.WorkId == 1).Tasks.Count);
        Assert.Equal(6500, plan.TotalDistance);
    }

    [Fact]
    public void PlanStrict_PreservesOrderAndOnlyMergesAdjacentTasks()
    {
        var positions = CreatePositions((1, 1000), (2, 4000));
        var tasks = new[]
        {
            HardTask(1, stage: 3, repeat: 2),
            HardTask(1, stage: 2, repeat: 1),
            HardTask(2, stage: 2, repeat: 1),
            HardTask(1, stage: 1, repeat: 1),
        };

        var plan = StageRoutePlanner.PlanStrict(tasks, positions, currentWorldX: 1000);

        Assert.Equal(new[] { 1, 2, 1 }, plan.Visits.Select(visit => visit.WorkId));
        Assert.Equal(2, plan.Visits[0].Tasks.Count);
        Assert.Equal(6000, plan.TotalDistance);
    }

    private static StageSkipTask HardTask(int workId, int stage, int repeat)
        => new(workId, StageMapMode.Main, StageDifficulty.Hard, stage, repeat);

    private static StageWorkPositionsData CreatePositions(params (int Id, int X)[] values)
    {
        var data = new StageWorkPositionsData();
        foreach (var value in values)
        {
            data.Positions.Add(new StageWorkPosition
            {
                Id = value.Id,
                CanvasX = value.X,
                CanvasY = 500,
                Verified = true,
            });
        }

        return data;
    }
}

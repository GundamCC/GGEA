using System.IO;
using GGEA.App.Config;
using GGEA.App.Tasks;

namespace GGEA.Tests;

/// <summary>AP 消耗作品计划项的 Hard 关卡选择测试。</summary>
public class ApConsumptionWorkItemTests
{
    /// <summary>未解锁记录在中断恢复后保留，下一轮任务和普通安全跳过不显示此说明。</summary>
    [Fact]
    public void TaskChain_LockedSummaryPersistsOnlyWithinCurrentRun()
    {
        var plan = new[]
        {
            new ApConsumptionWorkItem { WorkId = 1, Work = "F91", HardCount = 1, Hard1 = true },
            new ApConsumptionWorkItem { WorkId = 2, Work = "W", HardCount = 1, Hard1 = true },
        };
        var chain = new ApConsumptionTaskChain("device", plan);
        Assert.Empty(chain.GetLockedSummary());
        chain.MarkCurrentLocked();
        chain.MarkInterrupted("用户停止");
        Assert.Contains("未解锁已跳过（1）：F91", chain.GetExecutionSummary());
        chain.ClearInterruption();
        chain.MarkCurrentLocked();
        Assert.Contains("未解锁已跳过（2）：F91、W", chain.GetExecutionSummary());
        Assert.Throws<InvalidOperationException>(() => chain.MarkCurrentLocked());
        Assert.Equal(2, chain.SuccessCount); // 保留成功或安全跳过的既有统计口径。
        var next = new ApConsumptionTaskChain("device", plan);
        next.MarkCurrentCompleted();
        Assert.Empty(next.GetLockedSummary());
        Assert.DoesNotContain("未解锁", next.GetExecutionSummary());
    }

    /// <summary>中断独立于已完成统计；恢复保留同一作品，不跳过未实际执行的任务。</summary>
    [Fact]
    public void TaskChain_InterruptionDoesNotAdvanceAndCanBeCleared()
    {
        var chain = new ApConsumptionTaskChain("device",
            [new() { WorkId = 7, Work = "F91", HardCount = 3, Hard1 = true }]);
        chain.MarkInterrupted("作品导航重试已达上限");
        Assert.Contains("已中断：作品导航重试已达上限", chain.GetExecutionSummary());
        Assert.Equal(0, chain.CurrentIndex);
        Assert.Equal(0, chain.SuccessCount);
        chain.ClearInterruption();
        Assert.Null(chain.InterruptionReason);
        Assert.Equal(7, chain.CurrentItem!.WorkId);
        Assert.DoesNotContain("已中断", chain.GetExecutionSummary());
    }

    /// <summary>只返回作品实际存在且已勾选的 Hard 关卡。</summary>
    [Fact]
    public void SelectedHardStages_FiltersUnavailableStages()
    {
        var item = new ApConsumptionWorkItem
        {
            WorkId = 1,
            Work = "Test",
            HardCount = 3,
            Hard1 = true,
            Hard3 = true,
            Hard5 = true,
        };

        Assert.Equal(new[] { 1, 3 }, item.SelectedHardStages);
        Assert.True(item.HasSelectedStage);
        Assert.False(item.IsHard5Enabled);
    }

    /// <summary>执行计划过滤未选择作品，同时严格保持拖拽列表的先后顺序。</summary>
    [Fact]
    public void BuildExecutionPlan_PreservesOrderedSelectedWorks()
    {
        var first = new ApConsumptionWorkItem { WorkId = 3, Work = "C", HardCount = 1, Hard1 = true };
        var skipped = new ApConsumptionWorkItem { WorkId = 1, Work = "A", HardCount = 1 };
        var last = new ApConsumptionWorkItem { WorkId = 2, Work = "B", HardCount = 2, Hard2 = true };

        var plan = ApConsumptionPlanner.BuildExecutionPlan(new[] { first, skipped, last });

        Assert.Equal(new[] { 3, 2 }, plan.Select(item => item.WorkId));
    }

    /// <summary>每次任务开始建立独立链，并在完成或中止时汇总成功、失败和未执行作品。</summary>
    [Fact]
    public void TaskChain_SeparateRunsHaveIndependentProgressAndSummary()
    {
        var plan = new[]
        {
            new ApConsumptionWorkItem { WorkId = 1, Work = "A", HardCount = 1, Hard1 = true },
            new ApConsumptionWorkItem { WorkId = 2, Work = "B", HardCount = 2, Hard2 = true },
        };
        var firstRun = new ApConsumptionTaskChain("device-a", plan);
        firstRun.MarkCurrentCompleted();
        firstRun.MarkCurrentFailed("HARD 2 选中失败");
        var secondRun = new ApConsumptionTaskChain("device-a", plan);

        Assert.True(firstRun.IsCompleted);
        Assert.Equal(1, firstRun.SuccessCount);
        Assert.Equal(1, firstRun.FailureCount);
        Assert.Equal("AP任务链：成功 1，失败 1，未执行 0；失败作品：B", firstRun.GetExecutionSummary());
        Assert.Equal(0, secondRun.CurrentIndex);
        Assert.Equal("A", secondRun.CurrentItem?.Work);
        Assert.Equal("AP任务链：成功 0，失败 0，未执行 2", secondRun.GetExecutionSummary());
    }

    /// <summary>应用配置时恢复顺序与勾选状态，并把游戏更新新增作品追加在末尾。</summary>
    [Fact]
    public void ApplyProfile_RestoresSavedOrderAndAppendsNewWorks()
    {
        var current = new List<ApConsumptionWorkItem>
        {
            new() { WorkId = 1, Work = "A", HardCount = 3, Hard1 = true },
            new() { WorkId = 2, Work = "B", HardCount = 2, Hard2 = true },
            new() { WorkId = 3, Work = "C", HardCount = 1 },
        };
        var profile = new ApConsumptionProfile
        {
            Name = "测试",
            Works = new List<ApConsumptionProfileWork>
            {
                new() { WorkId = 3, HardStages = new List<int> { 1 } },
                new() { WorkId = 1, HardStages = new List<int> { 2, 5 } },
            },
        };

        ApConsumptionPlanner.ApplyProfile(profile, current);

        Assert.Equal(new[] { 3, 1, 2 }, current.Select(item => item.WorkId));
        Assert.Equal(new[] { 1 }, current[0].SelectedHardStages);
        Assert.Equal(new[] { 2 }, current[1].SelectedHardStages);
        Assert.Empty(current[2].SelectedHardStages);
    }

    /// <summary>命名配置可以覆盖同名方案并通过 JSON 往返保存。</summary>
    [Fact]
    public void ProfileService_UpsertAndSave_RoundTripsJson()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ap_profiles.json");
        var service = new ApConsumptionProfileService(new TestLog());
        var data = new ApConsumptionProfilesData();
        service.Upsert(data, new ApConsumptionProfile { Name = "日常" });
        service.Upsert(data, new ApConsumptionProfile
        {
            Name = "日常",
            Works = new List<ApConsumptionProfileWork>
            {
                new() { WorkId = 7, HardStages = new List<int> { 1, 3 } },
            },
        });

        service.Save(data, path);
        var loaded = service.Load(path);

        Assert.Single(loaded.Profiles);
        Assert.Equal("日常", loaded.LastSelectedProfile);
        Assert.Equal(new[] { 1, 3 }, loaded.Profiles[0].Works[0].HardStages);
    }

    /// <summary>测试用临时目录，测试结束后自动清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ggea-ap-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 临时文件清理失败不影响功能测试结论。
            }
        }
    }
}

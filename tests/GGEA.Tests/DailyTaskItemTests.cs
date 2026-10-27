using GGEA.App.Tasks;

namespace GGEA.Tests;

/// <summary>每日任务勾选与拖拽顺序测试。</summary>
public class DailyTaskItemTests
{
    /// <summary>执行计划过滤未勾选任务，同时严格保持拖拽列表的先后顺序。</summary>
    [Fact]
    public void BuildExecutionPlan_FiltersUncheckedAndPreservesDragOrder()
    {
        var shop = new DailyTaskItem
        {
            Kind = DailyTaskKind.ShopFreeGift,
            Name = "商店购买免费礼包",
            IsChecked = true,
        };
        var develop = new DailyTaskItem
        {
            Kind = DailyTaskKind.DevelopOnce,
            Name = "开发&强化一次",
            IsChecked = false,
        };
        var harvest = new DailyTaskItem
        {
            Kind = DailyTaskKind.HarvestBattleship,
            Name = "回收战舰（个人基地）",
            IsChecked = true,
        };
        var consumeAp = new DailyTaskItem
        {
            Kind = DailyTaskKind.ConsumeAp,
            Name = "消耗AP",
            IsChecked = true,
        };

        var plan = DailyTaskPlanner.BuildExecutionPlan(new[] { shop, develop, harvest, consumeAp });

        Assert.Equal(
            new[]
            {
                DailyTaskKind.ShopFreeGift,
                DailyTaskKind.HarvestBattleship,
                DailyTaskKind.ConsumeAp,
            },
            plan.Select(item => item.Kind));
    }

    /// <summary>全部未勾选时执行计划为空，调用方应提示用户。</summary>
    [Fact]
    public void BuildExecutionPlan_EmptyWhenNothingChecked()
    {
        var plan = DailyTaskPlanner.BuildExecutionPlan(new[]
        {
            new DailyTaskItem { Kind = DailyTaskKind.ClaimRewards, Name = "领取奖励", IsChecked = false },
        });

        Assert.Empty(plan);
    }

    /// <summary>即使旧状态仍为勾选，不受当前模拟器支持的任务也不得进入执行计划。</summary>
    [Fact]
    public void BuildExecutionPlan_FiltersUnsupportedTask()
    {
        var plan = DailyTaskPlanner.BuildExecutionPlan(new[]
        {
            new DailyTaskItem
            {
                Kind = DailyTaskKind.DevelopOnce,
                Name = "开发&强化一次",
                IsChecked = true,
                IsSupported = false,
                UnsupportedReason = "当前模拟器尚未验证缩放",
            },
            new DailyTaskItem
            {
                Kind = DailyTaskKind.ShopFreeGift,
                Name = "商店",
                IsChecked = true,
            },
        });

        var task = Assert.Single(plan);
        Assert.Equal(DailyTaskKind.ShopFreeGift, task.Kind);
    }

    /// <summary>只有带独立设置页的任务显示齿轮入口。</summary>
    [Fact]
    public void HasSettings_OnlyTrueForTasksWithSettingsPage()
    {
        Assert.True(new DailyTaskItem { Kind = DailyTaskKind.ClaimRewards }.HasSettings);
        Assert.True(new DailyTaskItem { Kind = DailyTaskKind.ConsumeAp }.HasSettings);
        Assert.False(new DailyTaskItem { Kind = DailyTaskKind.HarvestBattleship }.HasSettings);
        Assert.False(new DailyTaskItem { Kind = DailyTaskKind.ShopFreeGift }.HasSettings);
        Assert.False(new DailyTaskItem { Kind = DailyTaskKind.DevelopOnce }.HasSettings);
    }

    /// <summary>勾选使命奖励后，“领取奖励”应被移动到执行计划最后。</summary>
    [Fact]
    public void MoveClaimRewardsLast_PlacesClaimAtEnd()
    {
        var plan = DailyTaskPlanner.BuildExecutionPlan(new[]
        {
            new DailyTaskItem { Kind = DailyTaskKind.ShopFreeGift, Name = "商店", IsChecked = true },
            new DailyTaskItem { Kind = DailyTaskKind.ClaimRewards, Name = "领取奖励", IsChecked = true },
            new DailyTaskItem { Kind = DailyTaskKind.ConsumeAp, Name = "消耗AP", IsChecked = true },
        });

        var reordered = DailyTaskPlanner.MoveClaimRewardsLast(plan);

        Assert.Equal(
            new[]
            {
                DailyTaskKind.ShopFreeGift,
                DailyTaskKind.ConsumeAp,
                DailyTaskKind.ClaimRewards,
            },
            reordered.Select(item => item.Kind));
    }

    /// <summary>使命奖励追加到末尾时，“领取奖励”本身仍保留在队列原位置。</summary>
    [Fact]
    public void AppendMissionRewardLast_KeepsClaimInPlaceAndMissionAtEnd()
    {
        var plan = DailyTaskPlanner.BuildExecutionPlan(new[]
        {
            new DailyTaskItem { Kind = DailyTaskKind.ShopFreeGift, Name = "商店", IsChecked = true },
            new DailyTaskItem { Kind = DailyTaskKind.ClaimRewards, Name = "领取奖励", IsChecked = true },
            new DailyTaskItem { Kind = DailyTaskKind.ConsumeAp, Name = "消耗AP", IsChecked = true },
        });

        var reordered = DailyTaskPlanner.AppendMissionRewardLast(plan);

        Assert.Equal(
            new[]
            {
                DailyTaskKind.ShopFreeGift,
                DailyTaskKind.ClaimRewards,
                DailyTaskKind.ConsumeAp,
                DailyTaskKind.ClaimMissionReward,
            },
            reordered.Select(item => item.Kind));
    }

    /// <summary>未勾选使命奖励时，计划保持原样，不追加队尾执行。</summary>
    [Fact]
    public void BuildDailyPlan_MissionRewardNotChecked_NoAppend()
    {
        var plan = DailyTaskPlanner.BuildDailyPlan(
            new[]
            {
                new DailyTaskItem { Kind = DailyTaskKind.ClaimRewards, Name = "领取奖励", IsChecked = true },
                new DailyTaskItem { Kind = DailyTaskKind.ConsumeAp, Name = "消耗AP", IsChecked = true },
            },
            missionRewardChecked: false);

        Assert.Equal(
            new[] { DailyTaskKind.ClaimRewards, DailyTaskKind.ConsumeAp },
            plan.Select(item => item.Kind));
    }

    /// <summary>
    /// 勾选使命奖励但未勾选“领取奖励”时，不追加也不自动执行（修复未勾选却自动运行的问题）。
    /// </summary>
    [Fact]
    public void BuildDailyPlan_MissionCheckedButClaimNotChecked_NoAppend()
    {
        var source = new[]
        {
            new DailyTaskItem { Kind = DailyTaskKind.ClaimRewards, Name = "领取奖励", IsChecked = false },
            new DailyTaskItem { Kind = DailyTaskKind.ConsumeAp, Name = "消耗AP", IsChecked = true },
        };

        var plan = DailyTaskPlanner.BuildDailyPlan(source, missionRewardChecked: true);

        // 没有“领取奖励”任务时，使命奖励不应被自动加入执行计划
        Assert.Equal(new[] { DailyTaskKind.ConsumeAp }, plan.Select(item => item.Kind));
        Assert.DoesNotContain(plan, item => item.Kind == DailyTaskKind.ClaimMissionReward);
        // 源列表不能被修改（不强制勾选“领取奖励”）
        Assert.False(source[0].IsChecked);
    }

    /// <summary>
    /// 勾选使命奖励且“领取奖励”被勾选时，计划中执行一次，队列最后再追加一次（共两次）。
    /// </summary>
    [Fact]
    public void BuildDailyPlan_MissionCheckedAndClaimChecked_AppendsMissionAtEnd()
    {
        var plan = DailyTaskPlanner.BuildDailyPlan(
            new[]
            {
                new DailyTaskItem { Kind = DailyTaskKind.ShopFreeGift, Name = "商店", IsChecked = true },
                new DailyTaskItem { Kind = DailyTaskKind.ClaimRewards, Name = "领取奖励", IsChecked = true },
                new DailyTaskItem { Kind = DailyTaskKind.ConsumeAp, Name = "消耗AP", IsChecked = true },
            },
            missionRewardChecked: true);

        Assert.Equal(
            new[]
            {
                DailyTaskKind.ShopFreeGift,
                DailyTaskKind.ClaimRewards,
                DailyTaskKind.ConsumeAp,
                DailyTaskKind.ClaimMissionReward,
            },
            plan.Select(item => item.Kind));
        // 队尾追加的使命奖励项必须处于勾选状态，执行循环才会真正运行它
        Assert.True(plan.Last().IsChecked);
    }
}

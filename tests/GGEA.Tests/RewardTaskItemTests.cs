using GGEA.App.Tasks;

namespace GGEA.Tests;

/// <summary>奖励页勾选与执行顺序测试。</summary>
public class RewardTaskItemTests
{
    /// <summary>执行计划过滤未勾选奖励，同时保持列表先后顺序。</summary>
    [Fact]
    public void BuildExecutionPlan_FiltersUncheckedAndPreservesOrder()
    {
        var mail = new RewardTaskItem
        {
            Kind = RewardTaskKind.MailReward,
            Name = "领取邮件奖励",
            IsChecked = true,
        };
        var mission = new RewardTaskItem
        {
            Kind = RewardTaskKind.MissionReward,
            Name = "领取每日使命奖励",
            IsChecked = false,
        };

        var plan = RewardTaskPlanner.BuildExecutionPlan(new[] { mail, mission });

        Assert.Equal(new[] { RewardTaskKind.MailReward }, plan.Select(item => item.Kind));
    }

    /// <summary>全部未勾选时执行计划为空，调用方应提示用户。</summary>
    [Fact]
    public void BuildExecutionPlan_EmptyWhenNothingChecked()
    {
        var plan = RewardTaskPlanner.BuildExecutionPlan(new[]
        {
            new RewardTaskItem { Kind = RewardTaskKind.MissionReward, Name = "使命奖励", IsChecked = false },
        });

        Assert.Empty(plan);
    }

    /// <summary>只有使命奖励显示问号帮助标记。</summary>
    [Fact]
    public void HasHelp_OnlyTrueForMissionReward()
    {
        Assert.True(new RewardTaskItem { Kind = RewardTaskKind.MissionReward }.HasHelp);
        Assert.False(new RewardTaskItem { Kind = RewardTaskKind.MailReward }.HasHelp);
    }
}

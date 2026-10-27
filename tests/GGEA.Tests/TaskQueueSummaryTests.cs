using GGEA.App.Tasks;

namespace GGEA.Tests;

/// <summary>验证队列统计、提前退出和耗时边界，不触发真实系统通知。</summary>
public class TaskQueueSummaryTests
{
    /// <summary>混合结果正确统计，耗时使用累计分钟并舍去不足一秒的部分。</summary>
    [Theory]
    [InlineData(0, "0分0秒")]
    [InlineData(59999, "0分59秒")]
    [InlineData(60000, "1分0秒")]
    [InlineData(3662000, "61分2秒")]
    public void Completed_MixedResults_FormatsSummary(int milliseconds, string duration)
    {
        var summary = new TaskQueueSummary(3);
        summary.Record("回收战舰", true);
        summary.Record("商店购买免费礼包", false);
        summary.Record("消耗AP", true);
        Assert.Equal($"2项任务成功，1项任务失败，总计耗时{duration}\n失败任务：\n1. 商店购买免费礼包",
            summary.GetCompletionMessage(TimeSpan.FromMilliseconds(milliseconds)));
    }

    /// <summary>空计划或仅执行部分任务时，不得生成“全部完成”消息。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(3, 2)]
    public void IncompleteOrEmpty_DoesNotProduceCompletion(int planned, int completed)
    {
        var summary = new TaskQueueSummary(planned);
        for (var index = 0; index < completed; index++) summary.Record($"任务{index}", true);
        Assert.Null(summary.GetCompletionMessage(TimeSpan.FromSeconds(1)));
    }

    /// <summary>全部成功与全部失败都属于队列已经执行完毕，均需要汇总。</summary>
    [Theory]
    [InlineData(true, "全部任务执行成功，共2项")]
    [InlineData(false, "0项任务成功，2项任务失败")]
    public void Completed_UniformResults_ProducesCompletion(bool success, string expected)
    {
        var summary = new TaskQueueSummary(2);
        summary.Record("回收战舰", success);
        summary.Record("商店购买免费礼包", success);
        Assert.StartsWith(expected, summary.GetCompletionMessage(TimeSpan.Zero));
    }

    /// <summary>失败子项保留在其父任务下，计数仍按队列项；不混入成功任务。</summary>
    [Fact]
    public void Completed_FailedSubtasks_PreservesOrderAndQueueCounts()
    {
        var summary = new TaskQueueSummary(4);
        summary.Record("领取奖励", false, "领取邮件奖励、领取每日使命奖励");
        summary.Record("商店购买免费礼包", true);
        summary.Record("消耗AP", false);
        summary.Record("领取每日使命奖励", false);

        Assert.Equal(1, summary.SuccessCount);
        Assert.Equal(3, summary.FailureCount);
        Assert.Equal("1项任务成功，3项任务失败，总计耗时2分3秒\n失败任务：\n"
            + "1. 领取奖励（领取邮件奖励、领取每日使命奖励）\n2. 消耗AP\n3. 领取每日使命奖励",
            summary.GetCompletionMessage(TimeSpan.FromSeconds(123)));
    }

    /// <summary>提前退出时即使已有失败，也不能发出队列完成通知。</summary>
    [Fact]
    public void Incomplete_WithFailure_DoesNotProduceCompletion()
    {
        var summary = new TaskQueueSummary(2);
        summary.Record("商店购买免费礼包", false);
        Assert.Null(summary.GetCompletionMessage(TimeSpan.Zero));
    }

    /// <summary>当前全部每日任务同时失败的清单必须适配 Windows 通知的 255 字符正文容量。</summary>
    [Fact]
    public void Completed_AllDailyTasksFailed_FitsWindowsNotification()
    {
        var summary = new TaskQueueSummary(7);
        summary.Record("回收战舰（个人基地）", false);
        summary.Record("商店购买免费礼包", false);
        summary.Record("领取奖励", false, "领取邮件奖励、领取每日使命奖励");
        summary.Record("开发&强化一次", false);
        summary.Record("强化培育关卡", false);
        summary.Record("消耗AP", false);
        summary.Record("领取每日使命奖励", false);

        var message = summary.GetCompletionMessage(TimeSpan.FromHours(24));
        Assert.NotNull(message);
        Assert.InRange(message.Length, 1, 255);
        Assert.EndsWith("7. 领取每日使命奖励", message);
        Assert.DoesNotContain("全部任务执行成功", message);
    }
}

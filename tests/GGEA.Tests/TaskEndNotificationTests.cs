using GGEA.App.UI;

namespace GGEA.Tests;

/// <summary>验证独立任务提醒的结果语义、通知开关、窗口退出、网络去重和原生字段容量，不触发真实弹窗。</summary>
public sealed class TaskEndNotificationTests
{
    /// <summary>四种独立任务共用提醒出口，正文保留真实结果，只有业务成功才显示信息图标。</summary>
    [Theory]
    [InlineData("AP 消耗", true)]
    [InlineData("AP 消耗", false)]
    [InlineData("自动招募", true)]
    [InlineData("自动招募", false)]
    [InlineData("自动刷本", true)]
    [InlineData("自动刷本", false)]
    [InlineData("铁球工厂", true)]
    [InlineData("铁球工厂", false)]
    public void End_SendsActualResultOnce(string taskName, bool succeeded)
    {
        var notification = new TaskEndNotification(taskName) { Succeeded = succeeded };
        var sent = new List<(string Message, string Title, bool IsWarning)>();
        const string result = "真实任务结果及执行统计";
        void Show(string message, string title, bool warning) => sent.Add((message, title, warning));
        notification.Send(result, true, false, Show);
        notification.Send("不得重复发送", true, false, Show);
        Assert.Equal((result, $"GGEA · {taskName}任务结束", !succeeded), Assert.Single(sent));
    }

    /// <summary>刷本与推图终态直接显示完成/失败/已中止；正文仍来自真实轮次统计或失败原因。</summary>
    [Theory]
    [InlineData("自动刷本", true, false, "完成", "全部 10 轮刷取完成，已退出。")]
    [InlineData("自动刷本", false, false, "失败", "AP 不足，无法继续刷本，已回到选择关卡。")]
    [InlineData("自动刷本", false, true, "已中止", "自动刷本已中止。")]
    [InlineData("自动推图", true, false, "完成", "队列作品已全部完成。")]
    [InlineData("自动推图", false, false, "失败", "自动推图已停止：无法确认当前作品关卡编号和状态。")]
    [InlineData("自动推图", false, true, "已中止", "自动推图已中止。")]
    public void FarmOutcome_DistinguishesTitleAndPreservesReason(string taskName, bool succeeded,
        bool canceled, string resultTitle, string reason)
    {
        var notification = new TaskEndNotification(taskName, distinguishResult: true)
        { Succeeded = succeeded, Canceled = canceled };
        var sent = new List<(string Message, string Title, bool IsWarning)>();
        notification.Send(reason, true, false, (message, title, warning) => sent.Add((message, title, warning)));
        notification.Send("重复收尾", true, false, (_, _, _) => Assert.Fail("同一次刷本不得重复提醒"));
        Assert.Equal((reason, $"GGEA · {taskName}{resultTitle}", !succeeded), Assert.Single(sent));
    }

    /// <summary>关闭通知或关闭窗口不弹提醒，之后也不能补发旧任务的结果。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void DisabledOrClosing_DoesNotSendOrReplay(bool enabled, bool stopping)
    {
        var notification = new TaskEndNotification("自动刷本", distinguishResult: true);
        void Unexpected(string message, string title, bool warning) => Assert.Fail("不应发送通知");
        notification.Send("已中止", enabled, stopping, Unexpected);
        notification.Send("旧结果", true, false, Unexpected);
    }

    /// <summary>同一任务已报告网络错误后不重复提醒，新一轮任务仍可正常提醒。</summary>
    [Fact]
    public void NetworkAlreadyReported_SuppressesOnlyCurrentRun()
    {
        var first = new TaskEndNotification("自动刷本", distinguishResult: true);
        first.Suppress();
        first.Send("网络错误", true, false, (_, _, _) => Assert.Fail("网络错误不能重复提醒"));
        var calls = 0;
        new TaskEndNotification("自动刷本", distinguishResult: true).Send("新一轮已中止", true, false, (_, _, _) => calls++);
        Assert.Equal(1, calls);
    }

    /// <summary>真实通知出口故障不会导致同一轮再次发送；主窗口负责记录故障，不改写任务结果。</summary>
    [Fact]
    public void SenderFails_DoesNotReplay()
    {
        var notification = new TaskEndNotification("铁球工厂") { Succeeded = true };
        Assert.Throws<InvalidOperationException>(() => notification.Send("已完成", true, false,
            (_, _, _) => throw new InvalidOperationException("模拟通知出口失败")));
        notification.Send("不得重发", true, false, (_, _, _) => Assert.Fail("不应重发"));
        Assert.True(notification.Succeeded);
    }

    /// <summary>标题与正文都保留终止符空间；长异常不能导致定长原生字段越界。</summary>
    [Theory]
    [InlineData(63)]
    [InlineData(255)]
    public void LongText_FitsNativeField_WithoutSplittingUnicode(int capacity)
    {
        var exact = new string('文', capacity);
        Assert.Equal(exact, WindowsNotificationService.FitText(exact, capacity));
        Assert.Equal(new string('文', capacity - 1) + "…",
            WindowsNotificationService.FitText(exact + "字", capacity));
        var emojiAtBoundary = new string('文', capacity - 2) + "😀更多";
        Assert.Equal(new string('文', capacity - 2) + "…",
            WindowsNotificationService.FitText(emojiAtBoundary, capacity));
    }
}

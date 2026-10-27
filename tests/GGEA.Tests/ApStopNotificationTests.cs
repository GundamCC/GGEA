using GGEA.App.Tasks;
using GGEA.App.UI;

namespace GGEA.Tests;

/// <summary>AP 停止通知覆盖提前返回、重复信号、普通收尾及窗口生命周期。</summary>
public sealed class ApStopNotificationTests
{
    /// <summary>有独立任务和组合队列两种入口均即时通知；随后普通结束提醒不得重复。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stop_SendsOnceAndSuppressesOrdinaryCompletion(bool standalone)
    {
        var stop = new ApStopNotification();
        var task = standalone ? new TaskEndNotification("自动推图", true) : null;
        var messages = new List<(string, string, bool)>();
        void Show(string text, string title, bool warning) => messages.Add((text, title, warning));
        stop.Send("AP 不足，当前流程已停止", true, false, task, Show);
        stop.Send("重复信号", true, false, task, Show);
        task?.Send("普通结束", true, false, Show);
        Assert.Equal(("AP 不足，当前流程已停止", "GGEA · AP 不足", true), Assert.Single(messages));
        Assert.True(stop.Reported);
        stop.Reset();
        stop.Send("新任务", true, false, null, Show);
        Assert.Equal(2, messages.Count);
    }

    /// <summary>关闭系统通知或正在退出时不弹窗，不在下一次发送时补发旧任务。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Stop_RespectsSettingsAndShutdown(bool enabled, bool stopping)
    {
        var stop = new ApStopNotification();
        void Unexpected(string _, string __, bool ___) => throw new InvalidOperationException("不得通知");
        stop.Send("停止", enabled, stopping, null, Unexpected);
        stop.Send("不补发", true, false, null, Unexpected);
        Assert.True(stop.Reported);
    }

    /// <summary>系统通知异常不阻断已经确认的 AP 停止及后续关闭弹窗。</summary>
    [Fact]
    public void Signal_ObserverFailureDoesNotEscape()
    {
        var calls = 0;
        new ApStopSignal(_ => { calls++; throw new InvalidOperationException("通知失败"); }, new TestLog()).Report("AP 不足");
        Assert.Equal(1, calls);
    }
}

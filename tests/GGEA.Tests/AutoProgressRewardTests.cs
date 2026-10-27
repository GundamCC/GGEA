using GGEA.App.Tasks;

namespace GGEA.Tests;

public sealed partial class AutoProgressFlowTests
{
    /// <summary>战后先获得六台机体再结算；正常轮询与两分钟恢复均须识别实际奖励文字，再确认返回选关页。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FierceRewards_BeforeResult_ContinueThroughNormalAndRecovery(bool recover)
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        var reward = scene.UnitRewardFrame();
        scene.Steps(new[] { scene.Frame(("continue", 1500, 930)), scene.Frame(("continue", 1500, 930)) }
            .Concat(Enumerable.Repeat(reward, 6))
            .Append(scene.Frame(("again", 1200, 920), ("select", 1600, 920)))
            .Append(scene.Frame(("title", 200, 20))).ToArray());
        var inspected = false;
        scene.Adb.BeforeScreenshotAsync = (_, _) =>
        {
            if (recover && !inspected && scene.Adb.Taps.Count == 2)
            {
                clock.Advance(120);
                inspected = true;
            }
            return Task.CompletedTask;
        };
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run(recoveryTime: clock));
        Assert.Equal(9, scene.Adb.Taps.Count);
        Assert.All(scene.Adb.Taps.Skip(2).Take(6), point =>
        {
            Assert.InRange(point.X, 950, 970);
            Assert.InRange(point.Y, 1020, 1035);
        });
        Assert.Equal(scene.Center("select", 1600, 920), scene.Adb.Taps.Last());
        Assert.Equal(recover, scene.Log.Entries.Any(e => e.Message.Contains("奖励机体或剧情继续")));
        Assert.DoesNotContain(scene.Log.Entries, e => e.Message.Contains("已停止当前任务"));
    }
}

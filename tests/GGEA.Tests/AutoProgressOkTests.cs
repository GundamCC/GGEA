using System.IO;
using GGEA.App.Tasks;

namespace GGEA.Tests;

public sealed partial class AutoProgressFlowTests
{
    /// <summary>实际 MuMu 5556 奖励画面：略过后先点底部 OK，再由新帧选关入口确认完成。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressOk_AfterSkip_ClosesRealRewardAndConfirmsReturn(bool watch)
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        var reward = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sortie", "watch-reward-ok-mumu5556.png"));
        var skip = scene.Frame(("skip", 1603, 356));
        var returned = scene.Frame(("title", 200, 20));
        scene.Steps(watch ? [scene.Frame(), skip, scene.Frame(("execute", 500, 800)), reward, returned]
            : [skip, reward, returned]);
        scene.Adb.BeforeScreenshotAsync = (count, _) =>
        {
            if (count == 1) clock.Advance(15);
            return Task.CompletedTask;
        };
        var result = await scene.Run(watchEntry: watch, recoveryTime: clock);
        Assert.True(result == SortieFlowResult.ProgressStageCompleted,
            $"{result}; taps={string.Join(",", scene.Adb.Taps)}; logs={string.Join(" | ", scene.Log.Entries.Select(item => item.Message))}");
        var expected = new List<(int, int)>();
        if (watch) expected.Add((960, 540));
        expected.Add(scene.Center("skip", 1603, 356));
        if (watch) expected.Add(scene.Center("execute", 500, 800));
        Assert.Equal(expected, scene.Adb.Taps.Take(expected.Count));
        Assert.Equal(expected.Count + 1, scene.Adb.Taps.Count);
        Assert.InRange(scene.Adb.Taps.Last().X, 950, 970);
        Assert.InRange(scene.Adb.Taps.Last().Y, 985, 1005);
    }

    /// <summary>两分钟巡查能识别当前会话没有 SKIP 记录的真实奖励弹窗，而非返回未知画面。</summary>
    [Fact]
    public async Task ProgressOk_RecoveryRecognizesRealRewardWithoutSkipHistory()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sortie", "watch-reward-ok-mumu5556.png")),
            scene.Frame(("title", 200, 20)));
        scene.Adb.BeforeScreenshotAsync = (count, _) =>
        {
            if (count == 1) clock.Advance(120);
            return Task.CompletedTask;
        };
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run(recoveryTime: clock));
        Assert.Single(scene.Adb.Taps);
        Assert.Contains(scene.Log.Entries, item => item.Message.Contains("恢复检测识别到底部 OK"));
    }

    /// <summary>底部区域外的 OK 或同样蓝框的非 OK 按钮不能授权点击；取消仍正常传播。</summary>
    [Theory]
    [InlineData("ok", 700)]
    [InlineData("execute", 920)]
    public async Task ProgressOk_RejectsOutsideRegionAndDifferentText(string button, int y)
    {
        using var scene = new Scene();
        using var cancellation = new CancellationTokenSource();
        scene.Steps(scene.Frame(("skip", 1603, 356)), scene.Frame((button, 780, y)));
        scene.Adb.BeforeScreenshotAsync = (count, ct) =>
        {
            if (count == 3) cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.Run(ct: cancellation.Token));
        Assert.Equal(new[] { scene.Center("skip", 1603, 356) }, scene.Adb.Taps);
    }

    /// <summary>持续 OK 不表示已经通关；重复点击不刷新静止计时，第二次巡查仍停止。</summary>
    [Fact]
    public async Task ProgressOk_PersistentDialogDoesNotCompleteOrResetWatchdog()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Steps(scene.Frame(("skip", 1603, 356)), scene.Frame(("ok", 780, 930)));
        scene.Adb.BeforeScreenshotAsync = (_, _) =>
        {
            if (scene.Adb.Taps.Count >= 2) clock.Advance(120);
            return Task.CompletedTask;
        };
        Assert.Equal(SortieFlowResult.Stalled, await scene.Run(recoveryTime: clock));
        Assert.Equal(new[] { scene.Center("skip", 1603, 356), scene.Center("ok", 780, 930), scene.Center("ok", 780, 930) }, scene.Adb.Taps);
    }

    /// <summary>同帧失败比略过后的 OK 更优先，不允许奖励关闭掩盖任务失败。</summary>
    [Fact]
    public async Task ProgressOk_FailurePreemptsDialog()
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("skip", 1603, 356)), scene.Frame(("failed", 100, 20), ("ok", 780, 930)));
        Assert.Equal(SortieFlowResult.FailureDetected, await scene.Run());
        Assert.Equal(new[] { scene.Center("skip", 1603, 356), (1670, 980) }, scene.Adb.Taps);
    }
}

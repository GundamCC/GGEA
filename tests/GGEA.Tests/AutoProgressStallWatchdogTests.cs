using GGEA.App.Tasks;

namespace GGEA.Tests;

/// <summary>手动推进单调时间；流程回归不修改两分钟生产阈值，也不依赖真实等待两分钟。</summary>
internal sealed class ManualProgressTime : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public void Advance(int seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
}

/// <summary>验证卡顿的时间边界和阶段进展定义，防止点击循环不断续期。</summary>
public sealed class SortieStallWatchdogTests
{
    /// <summary>两分钟以前不恢复；同页重复观察及未知动画帧不能刷新计时或清空恢复次数。</summary>
    [Fact]
    public void SamePageAndUnknown_DoNotRenewMinute()
    {
        var clock = new ManualProgressTime();
        var watchdog = new SortieStallWatchdog(clock);
        watchdog.Observe(SortiePage.Menu);
        clock.Advance(119);
        watchdog.Observe(SortiePage.Menu);
        watchdog.Observe(SortiePage.Unknown);
        Assert.False(watchdog.TryInspect());
        clock.Advance(1);
        Assert.True(watchdog.TryInspect());
        Assert.Equal(1, watchdog.Attempts);
        Assert.False(watchdog.TryInspect());
        clock.Advance(119);
        Assert.False(watchdog.TryInspect());
        clock.Advance(1);
        watchdog.Observe(SortiePage.Menu);
        Assert.True(watchdog.TryInspect());
        Assert.Equal(2, watchdog.Attempts);
        Assert.Equal(SortieStallWatchdog.MaximumAttempts, watchdog.Attempts);
    }

    /// <summary>有真实的新阶段或确认仍在健康战斗时重新计时，不把下一关继承成已耗尽恢复次数。</summary>
    [Fact]
    public void NewPageOrHealthyBattle_RenewsObservation()
    {
        var clock = new ManualProgressTime();
        var watchdog = new SortieStallWatchdog(clock);
        watchdog.Observe(SortiePage.Story);
        clock.Advance(120);
        Assert.True(watchdog.TryInspect());
        watchdog.Observe(SortiePage.Battle);
        Assert.Equal(0, watchdog.Attempts);
        clock.Advance(119);
        Assert.False(watchdog.TryInspect());
        clock.Advance(1);
        Assert.True(watchdog.TryInspect());
        watchdog.ConfirmProgress();
        Assert.Equal(0, watchdog.Attempts);
        Assert.False(watchdog.TryInspect());
    }

    /// <summary>健康战斗之后持续未知仍在两个两分钟窗口内耗尽恢复预算，不能靠旧健康记录永久续期。</summary>
    [Fact]
    public void HealthyBattleThenPersistentUnknown_StillExhaustsRecovery()
    {
        var clock = new ManualProgressTime();
        var watchdog = new SortieStallWatchdog(clock);
        watchdog.Observe(SortiePage.Battle);
        clock.Advance(180);
        watchdog.ConfirmProgress();
        for (var attempt = 1; attempt <= SortieStallWatchdog.MaximumAttempts; attempt++)
        {
            clock.Advance(119);
            watchdog.Observe(SortiePage.Unknown);
            Assert.False(watchdog.TryInspect());
            clock.Advance(1);
            Assert.True(watchdog.TryInspect());
            Assert.Equal(attempt, watchdog.Attempts);
        }
    }

}

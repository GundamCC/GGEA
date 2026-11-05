namespace GGEA.App.Tasks;

/// <summary>推图可恢复的实际页面证据；未知帧和单纯点击均不表示流程已经前进。</summary>
internal enum SortiePage
{
    Unknown, Stage, MainStage, Home, Preparation, Reward, Story, Continue, Result,
    Menu, Skip, Battle, Download, Terrain, EmptySlots, LevelUp, TapToNext, StoryOk,
}

/// <summary>单关单调时钟：同一目标的重复点击不刷新两分钟期限，恢复也不延长外层总超时。</summary>
internal sealed class SortieStallWatchdog
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    internal const int MaximumAttempts = 2;
    private readonly TimeProvider _time;
    private long _lastProgress;
    private SortiePage _lastPage;
    internal int Attempts { get; private set; }

    /// <summary>测试注入单调时钟，直接验证 120 秒边界，不缩短产品的恢复等待。</summary>
    internal SortieStallWatchdog(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _lastProgress = _time.GetTimestamp();
    }

    /// <summary>只将新识别到的页面类型计为进展；动画、未知帧和同页点击不会刷新计时。</summary>
    internal void Observe(SortiePage page)
    {
        if (page == SortiePage.Unknown || page == _lastPage) return;
        _lastPage = page;
        ConfirmProgress();
    }

    /// <summary>恢复检查首次认出新页时刷新阶段，但本次恢复仍占一次，不能多获得一轮点击预算。</summary>
    internal void ObserveDuringInspection(SortiePage page)
    {
        if (page == SortiePage.Unknown || page == _lastPage) return;
        _lastPage = page;
        _lastProgress = _time.GetTimestamp();
        Attempts = 1;
    }

    /// <summary>确认新阶段或仍处于正常全部自动战斗；保留两分钟后的下一次健康检查。</summary>
    internal void ConfirmProgress()
    {
        _lastProgress = _time.GetTimestamp();
        Attempts = 0;
    }

    /// <summary>到期只授权检查新截图；连续第二个两分钟仍无进展时由调用方停止任务，健康战斗除外。</summary>
    internal bool TryInspect()
    {
        var now = _time.GetTimestamp();
        if (_time.GetElapsedTime(_lastProgress, now) < Interval) return false;
        _lastProgress = now;
        Attempts++;
        return true;
    }
}

using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>下载与 Logo 竞速等待：无下载不阻塞，下载遵守许可，所有成功仍需目标作品连续确认。</summary>
public sealed class HistoryPartEntryWaiterTests
{
    private static readonly TemplateMatchResult Download = new(new Roi(1000, 850, 100, 50), .99);
    private static readonly TemplateMatchResult Cancel = new(new Roi(700, 850, 100, 50), .99);
    private static HistoryStageObservation Page(int? id = 8) => new(true, null, null, null, null, id)
        { Page = HistoryPageKind.StageNodes };
    private static readonly DownloadDialogService.DownloadDialogButtons Empty = new(null, null);

    /// <summary>未命中保持慢加载观察；出现正确作品后短间隔复核，两帧一致才成功。</summary>
    [Fact]
    public async Task Candidate_UsesShortConfirmationWithoutSkippingIdentityCheck()
    {
        var reads = 0;
        var delays = new List<int>();
        var waiter = new HistoryPartEntryWaiter(_ => Task.FromResult((Page(++reads == 1 ? 9 : 8), Empty)),
            (_, _, _) => throw new InvalidOperationException("只读确认不应点击"), new TestLog(),
            (ms, _) => { delays.Add(ms); return Task.CompletedTask; });
        await waiter.WaitAsync(8, true, default);
        Assert.Equal(3, reads);
        Assert.Equal(new[] { 5000, 500 }, delays);
    }

    /// <summary>目标 Logo 先出现，第二帧确认后立即继续，不等待下载超时。</summary>
    [Fact]
    public async Task LogoFirst_CompletesWithoutClick()
    {
        var reads = 0;
        var waiter = Create(_ => { reads++; return (Page(), Empty); }, []);
        await waiter.WaitAsync(8, true, default);
        Assert.Equal(2, reads);
    }

    /// <summary>弹窗透出的 Logo 不授权继续；只点一次下载，下载后重新累计两帧确认。</summary>
    [Fact]
    public async Task DownloadFirst_ClicksOnceAndWaitsForFreshLogo()
    {
        var taps = new List<(int, int)>(); var reads = 0;
        var waiter = Create(_ => { reads++; return (Page(), reads <= 2 ? new(Download, Cancel) : Empty); }, taps);
        await waiter.WaitAsync(8, true, default);
        Assert.Equal(4, reads);
        Assert.Equal(new[] { (Download.CenterX, Download.CenterY) }, taps);
    }

    /// <summary>未允许下载时只取消并停止，不继续消费后续目标。</summary>
    [Fact]
    public async Task DownloadDenied_CancelsAndStops()
    {
        var taps = new List<(int, int)>();
        var waiter = Create(_ => (Page(), new(Download, Cancel)), taps);
        await Assert.ThrowsAsync<InvalidOperationException>(() => waiter.WaitAsync(8, false, default));
        Assert.Equal(new[] { (Cancel.CenterX, Cancel.CenterY) }, taps);
    }

    /// <summary>缺取消按钮不误点下载；错误作品 Logo 也不能结束等待。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingCancelOrWrongLogo_StopsWithoutClick(bool dialog)
    {
        var taps = new List<(int, int)>();
        var waiter = Create(_ => (Page(9), dialog ? new(Download, null) : Empty), taps);
        await Assert.ThrowsAsync<InvalidOperationException>(() => waiter.WaitAsync(8, false, default));
        Assert.Empty(taps);
    }

    /// <summary>识别过程中取消后，不得使用刚返回的按钮证据点击。</summary>
    [Fact]
    public async Task CancellationDuringObservation_PreventsClick()
    {
        using var cancellation = new CancellationTokenSource();
        var taps = new List<(int, int)>();
        var waiter = Create(_ => { cancellation.Cancel(); return (Page(), new(Download, Cancel)); }, taps);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(8, true, cancellation.Token));
        Assert.Empty(taps);
    }

    /// <summary>替换截图、点击和延时验证状态机，不依赖实机下载或真实计时。</summary>
    private static HistoryPartEntryWaiter Create(
        Func<CancellationToken, (HistoryStageObservation, DownloadDialogService.DownloadDialogButtons)> observe,
        List<(int, int)> taps) => new(ct => Task.FromResult(observe(ct)),
            (x, y, _) => { taps.Add((x, y)); return Task.CompletedTask; }, new TestLog(),
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
}

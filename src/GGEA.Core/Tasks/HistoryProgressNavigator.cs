using GGEA.App.Localization;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>历史作品总览、节点和 BOSS 页面间的有界导航；每次点击都绑定新帧中的作品身份。</summary>
public sealed class HistoryProgressNavigator(AutomationService automation, HistoryStageVision vision,
    HistoryStageCatalog catalog, HardStageEntry work, string serial, IGgeaLog log,
    Func<CancellationToken, Task> openMainStage,
    DownloadDialogService? downloadDialog = null, bool allowDownload = false)
{
    /// <summary>允许从已知历史页继续；仅未知入口才恢复主界面和主要关卡，战斗中不调用本方法。</summary>
    public async Task<HistoryProgressObservation> OpenOverviewAsync(CancellationToken ct)
    {
        var state = await ObserveAsync(ct);
        if (state.Page == HistoryPageKind.Unknown && state.Entry is null && !state.MainStageTitle)
            await openMainStage(ct);
        var opened = await new HistoryStageNavigationService(automation, vision, serial, log)
            .OpenAsync(work, catalog.Works.Count, ct);
        if (!opened.Success) throw new InvalidOperationException(opened.Message);
        return await ReadOverviewAsync(ct);
    }

    /// <summary>攻略度需要同作品连续两帧一致，未知状态由流程决策阻止推进。</summary>
    public async Task<HistoryProgressObservation> ReadOverviewAsync(CancellationToken ct)
    {
        var state = await WaitForPageAsync(HistoryPageKind.Overview, ct);
        await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        var next = await ObserveAsync(ct);
        Require(next, HistoryPageKind.Overview);
        return state.Progress is not null && state.Progress == next.Progress ? state.Progress :
            new(HistoryCompletion.Unknown, HistoryCompletion.Unknown);
    }

    /// <summary>固定按钮仅在目标作品总览及对应攻略度授权后点击；锁定 BOSS 永远不授权。</summary>
    public async Task OpenPartAsync(HistoryProgressStep part, CancellationToken ct)
    {
        if (part is not (HistoryProgressStep.Stage or HistoryProgressStep.Boss))
            throw new ArgumentOutOfRangeException(nameof(part));
        var progress = await ReadOverviewAsync(ct);
        if (HistoryProgressDecision.Decide(progress) != part)
            throw new InvalidOperationException(L.Text("攻略度发生变化或未确认，未点击历史关卡入口。"));
        var button = part == HistoryProgressStep.Stage ? HistoryProgressDecision.StageButton : HistoryProgressDecision.BossButton;
        await automation.TapAsync(serial, button.X, button.Y, ct);
        if (part == HistoryProgressStep.Stage && downloadDialog is not null)
        {
            // 两类识别共用同一帧并在后台并行；每轮全部回收，避免留下仍操作 SIFT 的竞争任务。
            var waiter = new HistoryPartEntryWaiter(async token =>
            {
                var screen = await automation.CaptureScreenAsync(serial, token);
                var pageTask = Task.Run(() => vision.Inspect(screen, work.Id, token), token);
                var downloadTask = Task.Run(() => downloadDialog.Recognize(screen), token);
                await Task.WhenAll(pageTask, downloadTask);
                return (await pageTask, await downloadTask);
            }, (x, y, token) => automation.TapAsync(serial, x, y, token), log, Task.Delay);
            await waiter.WaitAsync(work.Id, allowDownload, ct);
        }
        else
            await WaitForPageAsync(part == HistoryProgressStep.Stage ? HistoryPageKind.StageNodes : HistoryPageKind.Boss, ct);
    }

    /// <summary>结算后只从已确认的本作品节点/BOSS 页返回一层；未知页不盲点返回。</summary>
    public async Task<HistoryProgressObservation> ReturnOverviewAsync(CancellationToken ct)
    {
        var first = await ObserveAsync(ct);
        if (first.Page == HistoryPageKind.Overview && first.DetailWorkId == work.Id) return await ReadOverviewAsync(ct);
        if (first.DetailWorkId != work.Id || first.Page is not (HistoryPageKind.StageNodes or HistoryPageKind.Boss) || first.Back is null)
            throw new InvalidOperationException(L.Text("未确认本作品的节点或 BOSS 页，未点击返回。"));
        await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        var next = await ObserveAsync(ct);
        Require(next, first.Page);
        if (next.Back is null || Math.Abs(next.Back.CenterX - first.Back.CenterX) > 8 ||
            Math.Abs(next.Back.CenterY - first.Back.CenterY) > 8)
            throw new InvalidOperationException(L.Text("返回按钮未连续确认，停止历史作品推进。"));
        await automation.TapAsync(serial, next.Back.CenterX, next.Back.CenterY, ct);
        return await ReadOverviewAsync(ct);
    }

    /// <summary>节点页与 BOSS 页等待允许加载动画，成功必须有连续两帧的标题、布局和作品 Logo。</summary>
    public async Task<HistoryStageObservation> WaitForPageAsync(HistoryPageKind page, CancellationToken ct)
    {
        var confirmations = 0;
        // 原十秒观察窗口改为十五秒，不能让轮询放慢后把二十次重试扩大到百秒。
        for (var attempt = 0; attempt < GameTiming.ShortRecognitionTimeoutMs / GameTiming.RecognitionIntervalMs; attempt++)
        {
            var state = await ObserveAsync(ct);
            confirmations = state.Page == page && state.DetailWorkId == work.Id ? confirmations + 1 : 0;
            if (confirmations >= 2) return state;
            await Task.Delay(confirmations > 0 ? GameTiming.ConfirmationIntervalMs : GameTiming.RecognitionIntervalMs, ct);
        }
        throw new InvalidOperationException(L.Format($"等待历史作品 {work.Work} 的 {page} 页超时，停止操作。"));
    }

    /// <summary>截图沿用规格和网络安全入口；重型 Logo 识别不占用 WPF 线程。</summary>
    public async Task<HistoryStageObservation> ObserveAsync(CancellationToken ct)
    {
        var screen = await automation.CaptureScreenAsync(serial, ct);
        return await Task.Run(() => vision.Inspect(screen, work.Id, ct), ct);
    }

    /// <summary>供同帧节点/BOSS 识别复用，不跨截图保存授权。</summary>
    public void Require(ScreenImage screen, HistoryPageKind page, CancellationToken ct)
        => Require(vision.Inspect(screen, work.Id, ct), page);

    private void Require(HistoryStageObservation observation, HistoryPageKind page)
    {
        if (observation.DetailWorkId != work.Id || observation.Page != page)
            throw new InvalidOperationException(L.Text("历史作品身份或当前页面不符，停止后续点击。"));
    }
}

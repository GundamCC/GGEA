using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>点击历史关卡后竞争观察下载弹窗与目标节点页；下载不代表导航成功，最终仍需两帧作品确认。</summary>
internal sealed class HistoryPartEntryWaiter(
    Func<CancellationToken, Task<(HistoryStageObservation Page, DownloadDialogService.DownloadDialogButtons Buttons)>> observe,
    Func<int, int, CancellationToken, Task> tap,
    IGgeaLog log,
    Func<int, CancellationToken, Task> delay)
{
    /// <summary>下载只点击一次，沿用下载许可；下载后的最长等待为十分钟，中止始终可取消。</summary>
    public async Task WaitAsync(int workId, bool allowDownload, CancellationToken ct)
    {
        var confirmations = 0;
        var downloadClicked = false;
        var attempts = GameTiming.ShortRecognitionTimeoutMs / GameTiming.RecognitionIntervalMs;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (attempts-- > 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var (page, buttons) = await observe(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                // 弹窗可能透出背景 Logo，同帧出现时弹窗优先，不能把背景当成已进入。
                if (buttons.Download is not null)
                {
                    confirmations = 0;
                    if (!allowDownload)
                    {
                        if (buttons.Cancel is not null)
                        {
                            await tap(buttons.Cancel.CenterX, buttons.Cancel.CenterY, timeout.Token);
                            log.Warn("历史关卡检测到下载界面，未允许下载，已取消并停止当前目标。");
                            throw new InvalidOperationException("未允许下载游戏数据，历史关卡推进已停止。");
                        }
                    }
                    else if (!downloadClicked)
                    {
                        await tap(buttons.Download.CenterX, buttons.Download.CenterY, timeout.Token);
                        downloadClicked = true;
                        attempts = 1200;
                        timeout.CancelAfter(TimeSpan.FromMinutes(10));
                        log.Info("历史关卡检测到下载界面，已点击下载，继续等待目标作品 Logo 与关卡页。");
                    }
                }
                else
                {
                    confirmations = page.Page == HistoryPageKind.StageNodes && page.DetailWorkId == workId
                        ? confirmations + 1 : 0;
                    if (confirmations >= 2) return;
                }
                await delay(confirmations > 0 ? GameTiming.ConfirmationIntervalMs : GameTiming.RecognitionIntervalMs, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new InvalidOperationException("历史关卡下载后等待目标作品页面超过十分钟，已停止。");
        }
        throw new InvalidOperationException(downloadClicked
            ? "历史关卡下载后未确认目标作品 Logo 与关卡页，已停止。"
            : "等待历史关卡下载按钮或目标作品 Logo 超时，已停止；请检查下载许可或页面状态。");
    }
}

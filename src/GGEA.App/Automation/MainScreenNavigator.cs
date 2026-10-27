using GGEA.App.Device;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Automation;

/// <summary>主界面等待和恢复的唯一流程所有者：同一个总时限覆盖截图、重试、导航与最终确认。</summary>
public sealed class MainScreenNavigator(MainScreenDetector detector, IAdbService adb, IGgeaLog log,
    RecognitionEvidence? evidence = null)
{
    /// <summary>启动或点击后的只读等待；特殊弹窗终止检查优先于主界面成功判定。</summary>
    public Task<bool> WaitForMainScreenAsync(string serial, int timeoutMs = 30000, int pollIntervalMs = GameTiming.ConfirmationIntervalMs,
        CancellationToken ct = default, Func<CancellationToken, Task<bool>>? stopWhen = null)
        => RunAsync(serial, timeoutMs, pollIntervalMs, 0, null, null, stopWhen, ct);

    /// <summary>先恢复主界面，再点击既定主画面入口并重新确认；返回和未选中导航各最多点击一次。</summary>
    public Task<bool> RecoverAsync(string serial, int timeoutMs, int settleDelayMs, int activationX, int activationY,
        CancellationToken ct = default, Func<CancellationToken, Task<bool>>? closeAnnouncement = null,
        int pollIntervalMs = GameTiming.ConfirmationIntervalMs)
        => RunAsync(serial, timeoutMs, pollIntervalMs, settleDelayMs, (activationX, activationY), closeAnnouncement, null, ct);

    /// <summary>状态只属于本次调用，成功、取消、捕获异常与已知弹窗都不会提交中间帧作为识图故障。</summary>
    private async Task<bool> RunAsync(string serial, int timeoutMs, int pollIntervalMs, int settleDelayMs,
        (int X, int Y)? activation, Func<CancellationToken, Task<bool>>? closeAnnouncement,
        Func<CancellationToken, Task<bool>>? stopWhen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = Environment.TickCount64;
        var timeout = Math.Max(0, timeoutMs);
        var deadline = start + timeout;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        var token = budget.Token;
        var confirmed = 0;
        var backClicked = false;
        var homeClicked = false;
        var activationClicked = false;
        RecognitionFrame? lastFrame = null;
        log.Info($"等待主界面…（最长 {timeout / 1000.0:F1} 秒）");
        try
        {
            while (Environment.TickCount64 < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (stopWhen is not null && await stopWhen(token)) return false;
                var observation = await detector.ObserveAsync(serial, activation is not null && !activationClicked, token);
                lastFrame = observation.Frame;
                token.ThrowIfCancellationRequested();
                confirmed = observation.State == MainScreenButtonState.Activated ? confirmed + 1 : 0;
                if (confirmed >= Math.Max(1, detector.Options.ConfirmationFrames))
                {
                    if (activation is not null && !activationClicked)
                    {
                        log.Info($"主界面已确认，固定点击主画面入口: ({activation.Value.X}, {activation.Value.Y})");
                        await adb.TapAsync(serial, activation.Value.X, activation.Value.Y, token);
                        activationClicked = true;
                        confirmed = 0;
                        // 动作前的截图不能解释动作后超时；下一张有效帧才是新的诊断依据。
                        lastFrame = null;
                        await Task.Delay(Math.Max(0, settleDelayMs), token);
                        continue;
                    }
                    log.Info($"已连续确认主界面，耗时 {(Environment.TickCount64 - start) / 1000.0:F1} 秒");
                    return true;
                }

                if (activation is not null && !activationClicked)
                {
                    if (observation.State == MainScreenButtonState.Inactive && !homeClicked)
                    {
                        var match = observation.Match!;
                        log.Info($"识别到未选中主界面按钮（{match.Confidence:P1}），点击: ({match.CenterX}, {match.CenterY})");
                        await adb.TapAsync(serial, match.CenterX, match.CenterY, token);
                        homeClicked = true;
                        lastFrame = null;
                        await Task.Delay(Math.Max(0, settleDelayMs), token);
                        continue;
                    }
                    if (observation.State == MainScreenButtonState.NotFound && !homeClicked)
                    {
                        if (closeAnnouncement is not null && await closeAnnouncement(token))
                        {
                            lastFrame = null;
                            await Task.Delay(Math.Max(0, settleDelayMs), token);
                            continue;
                        }
                        // 公告探测会额外截图，返回按钮必须重新取当前帧，不能点击探测前的旧坐标。
                        if (closeAnnouncement is not null)
                        {
                            observation = await detector.ObserveAsync(serial, includeBackButton: true, token);
                            lastFrame = observation.Frame;
                        }
                        if (!backClicked && observation.State == MainScreenButtonState.NotFound && observation.BackMatch is { } back)
                        {
                            token.ThrowIfCancellationRequested();
                            log.Info($"主界面按钮未命中，点击返回按钮（{back.Confidence:P1}）: ({back.CenterX}, {back.CenterY})");
                            await adb.TapAsync(serial, back.CenterX, back.CenterY, token);
                            backClicked = true;
                            lastFrame = null;
                            await Task.Delay(Math.Max(0, settleDelayMs), token);
                            continue;
                        }
                    }
                }

                var interval = confirmed > 0 ? detector.Options.ConfirmationIntervalMs : pollIntervalMs;
                await Task.Delay(Math.Max(1, interval), token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            // 内部预算耗尽属于超时；用户取消必须继续抛出，不能落入取证路径。
        }
        ct.ThrowIfCancellationRequested();
        // 总等待预算耗尽才诊断弹窗；用户取消使用外层令牌，不能继续诊断或点击。
        if (lastFrame is not null)
            await detector.CheckFailureAsync(lastFrame.Screen, ct);
        if (lastFrame is not null && evidence is not null)
            await evidence.SaveAsync(activation is null ? "home-confirmation-timeout" : "home-recovery-timeout", lastFrame);
        log.Warn($"等待主界面超时（{(Environment.TickCount64 - start) / 1000.0:F1} 秒），请检查网络或当前弹窗");
        return false;
    }
}

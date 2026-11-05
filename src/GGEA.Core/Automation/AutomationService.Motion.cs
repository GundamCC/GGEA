using GGEA.App.Vision;

namespace GGEA.App.Automation;

public sealed partial class AutomationService
{
    /// <summary>滑动后观察画布而非固定睡眠；返回后调用方仍须重新识别目标，稳定本身不授权点击。</summary>
    public async Task WaitForCanvasStillAsync(string serial, Roi region, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GameTiming.RecognitionTimeoutMs);
        var motion = new SceneMotionGate(region);
        try
        {
            while (true)
            {
                var frame = await CaptureScreenAsync(serial, timeout.Token);
                if (motion.Observe(frame)) return;
                await Task.Delay(GameTiming.ConfirmationIntervalMs, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("画布持续变化，尚未确认滑动结束，已停止导航。");
        }
    }
}

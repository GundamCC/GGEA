using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>等待第二层确认；第一层点击未切页时最多补点一次，不重启开发批次。</summary>
public static class DevelopConfirmationWait
{
    /// <summary>先检查第二层，再检查第一层。出现未知页面或暗态后禁止补点，避免延迟响应导致重复输入。</summary>
    public static async Task<TemplateMatchResult?> WaitAsync(AutomationService automation, string serial,
        string firstTemplate, string secondTemplate, int timeoutMs, Action<string> report,
        CancellationToken ct = default, int retryAfterMs = GameTiming.AnimationSettleMs, int pollMs = GameTiming.RecognitionIntervalMs)
    {
        var start = Environment.TickCount64;
        var retryAllowed = true;
        TemplateMatchResult? previous = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var png = await automation.ScreenshotAsync(serial, ct);
            var second = await automation.MatchImageAsync(png, secondTemplate,
                new WaitOptions { Region = RecognitionRegion.BottomButtons, Threshold = .85, AllowGlobalContinuation = false }, ct);
            if (second is not null)
            {
                report($"已识别第二层执行按钮: {second}");
                return second;
            }
            if (retryAllowed)
            {
                var first = await DevelopExecutionButton.ObserveAsync(automation, png, firstTemplate, ct);
                if (first.State != DevelopButtonState.Ready || first.Match is null) retryAllowed = false;
                else
                {
                    if (previous is not null && Math.Abs(previous.CenterX - first.Match.CenterX) <= 8 &&
                        Math.Abs(previous.CenterY - first.Match.CenterY) <= 8 && Environment.TickCount64 - start >= retryAfterMs)
                    {
                        retryAllowed = false; // 先消耗唯一补点机会；异常也不能触发第二次补点。
                        report("连续确认仍在第一层且执行开发可用，补点一次执行开发；不会重新制造整批。");
                        await automation.TapAsync(serial, first.Match.CenterX, first.Match.CenterY, ct);
                    }
                    previous = first.Match;
                }
            }
            await Task.Delay(pollMs, ct);
        } while (Environment.TickCount64 - start < timeoutMs);
        report($"第二层执行按钮等待超时（{timeoutMs}ms），停止确认，不继续补点。");
        return null;
    }
}

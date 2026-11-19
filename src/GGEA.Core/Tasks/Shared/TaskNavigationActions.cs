using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>日常任务共用的导航动作，页面就绪条件与等待时长由各任务决定。</summary>
internal sealed class TaskNavigationActions(AutomationService automation, IGgeaLog log)
{
    public async Task TapHomeAsync(string serial, UiAnchor navHome, CancellationToken ct)
    {
        log.Info($"返回主界面: ({navHome.CenterX},{navHome.CenterY})");
        await automation.TapAsync(serial, navHome.CenterX, navHome.CenterY, ct);
    }

    /// <summary>在指定区域等待按钮出现，只点击实际匹配中心。</summary>
    public async Task<bool> ClickWhenAppearsAsync(
        string serial,
        string templatePath,
        Roi roi,
        string label,
        int waitTimeoutMs,
        double threshold,
        CancellationToken ct)
    {
        var wait = new WaitOptions
        {
            TimeoutMs = waitTimeoutMs,
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = threshold,
            Roi = roi,
        };

        var match = await automation.WaitForImageAsync(serial, templatePath, wait, ct);
        if (match is null)
        {
            log.Warn($"{label}未出现，流程中断");
            return false;
        }

        await automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
        log.Info($"已点击{label}: ({match.CenterX},{match.CenterY})");
        return true;
    }
}

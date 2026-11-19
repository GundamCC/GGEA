using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>资源不足后的共享退出流程；两次独立取消成功后才启动正常主界面识别。</summary>
public sealed class DevelopmentResourceRecovery(AutomationService automation,
    Func<string, CancellationToken, Task<bool>> ensureMainScreen)
{
    /// <summary>每层重新截图识别，不复用上一层坐标；任一层缺失或取消均不能继续点击。</summary>
    public async Task<bool> RecoverAsync(string serial, Action<string> report,
        CancellationToken ct = default, int timeoutMs = GameTiming.RecognitionTimeoutMs, int settleMs = GameTiming.RecognitionIntervalMs, string reason = "持有单位已达上限")
    {
        for (var layer = 1; layer <= 2; layer++)
        {
            ct.ThrowIfCancellationRequested();
            report($"{reason}，正在识别第 {layer} 次取消按钮…");
            var match = await automation.WaitForImageAsync(serial,
                ConfigService.ResolvePath("resources/templates/common/button_cancel.png"),
                new WaitOptions { Region = RecognitionRegion.BottomButtons, Threshold = .85,
                    TimeoutMs = timeoutMs, PollIntervalMs = GameTiming.RecognitionIntervalMs, AllowGlobalContinuation = false }, ct);
            if (match is null)
            {
                report($"第 {layer} 次取消按钮未识别，已停止恢复主界面。");
                return false;
            }
            await automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
            // 留出本地页面切换时间，下一层必须重新识别，不能立即连点旧截图。
            await Task.Delay(settleMs, ct);
        }
        ct.ThrowIfCancellationRequested();
        report("两次取消已完成，正在识别并返回主界面…");
        return await ensureMainScreen(serial, ct);
    }
}

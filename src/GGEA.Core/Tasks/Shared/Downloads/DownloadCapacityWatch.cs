using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>每次下载独占计时器，随等待循环退出；复用当前截图，不在后台争抢设备。</summary>
internal sealed class DownloadCapacityWatch(AutomationService automation, IGgeaLog log, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long? lastCheck;
    public void Start() => lastCheck = clock.GetTimestamp();

    public async Task CheckAsync(string serial, ScreenImage screen, CancellationToken ct)
    {
        if (lastCheck is not long last || clock.GetElapsedTime(last) < TimeSpan.FromMinutes(1)) return;
        Start();
        if (automation.GameLanguage != "zh-CN" || screen.Width != 1920 || screen.Height != 1080) return;

        // 顶部标题区分故障原因，底部文字独立授权点击；进度和教程插图都不参与识别。
        async Task<TemplateMatchResult?> Match(string name, Roi roi) =>
            await automation.MatchImageAsync(screen, "resources/templates/dialog/" + name + ".png",
                new WaitOptions { Roi = roi, Threshold = .90, AllowGlobalContinuation = false }, ct);
        if (await Match("capacity_title", new(785,42,350,100)) is null) return;
        var button = await Match("return_title", new(835,935,250,120));
        var message = L.Text("设备可用容量不足。请释放模拟器存储空间后重新下载。");
        try
        {
            if (button is not null)
            {
                await automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
                message += L.Text("已点击返回标题。");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 返回输入失败也必须终止下载，避免设备恢复策略继续操作错误页面。
            throw new InvalidOperationException(message, ex);
        }
        finally
        {
            log.Warn(message);
            automation.ReportCapacityWarning(message);
        }
        throw new InvalidOperationException(message);
    }
}

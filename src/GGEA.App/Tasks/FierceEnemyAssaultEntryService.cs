using GGEA.App.Localization;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>活动选中后的进入与 OCR 次数检查；此阶段不消费次数或发起战斗。</summary>
public sealed class FierceEnemyAssaultEntryService(AutomationService automation, IGgeaLog log,
    FierceEnemyAssaultSkipCountRecognizer counter, int timeoutMs = GameTiming.RecognitionTimeoutMs, int pollIntervalMs = GameTiming.RecognitionIntervalMs)
{
    public const string ButtonTemplate = "resources/templates/stage/event/button_to_event.png";
    public static readonly Roi ButtonRegion = new(1400, 780, 500, 200);

    /// <summary>预检按钮与模型，使资源错误在完整导航开始前暴露。</summary>
    public void Validate()
    {
        ImageCodec.DecodePng(System.IO.File.ReadAllBytes(ConfigService.ResolvePath(ButtonTemplate)));
        counter.Validate();
    }

    /// <summary>确认事件标题和按钮同帧出现后进入；等待两次一致的次数，避免过渡帧误读。</summary>
    public async Task<FierceEnemyAssaultSkipCount> EnterAndReadAsync(string serial, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Validate();
        if (!await EnterAsync(serial, ct)) return new(null, L.Text("未确认事件界面的前往事件按钮，已停止任务。"));
        return await ReadCountAsync(serial, ct);
    }

    /// <summary>只进入活动；自动战斗不受每日略过次数限制。</summary>
    public async Task<bool> EnterAsync(string serial, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var options = new WaitOptions { Roi = ButtonRegion, Threshold = .85, TimeoutMs = timeoutMs,
            PollIntervalMs = pollIntervalMs, AllowGlobalContinuation = false };
        var buttonPath = ConfigService.ResolvePath(ButtonTemplate);
        var match = await automation.WaitForImageAsync(serial, buttonPath, options, ct, async (screen, token) =>
        {
            var title = await automation.MatchImageAsync(screen, ConfigService.ResolvePath(FierceEnemyAssaultService.TitleTemplate),
                options with { Region = RecognitionRegion.PageTitle }, token);
            return title is null ? null : await automation.MatchImageAsync(screen, buttonPath, options, token);
        });
        if (match is null)
            return false;
        await automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
        return true;
    }

    /// <summary>有限观察直到连续两帧次数一致；零次立即作为正常结束结果返回，未知不放行。</summary>
    public async Task<FierceEnemyAssaultSkipCount> ReadCountAsync(string serial, CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        int? previous = null;
        var waitingReported = false;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var screen = await automation.CaptureScreenAsync(serial, ct);
            var count = await Task.Run(() => counter.Recognize(screen, ct), ct);
            if (count.Remaining is not null && count.Remaining == previous)
            {
                // 零次作为任务结束原因由调用方统一记录，避免服务与 UI 连续输出同一句提示。
                if (count.CanSkip) log.Info(count.Message);
                return count;
            }
            previous = count.Remaining;
            if (!waitingReported)
            {
                log.Info("等待强敌来袭略过次数连续两帧确认。");
                waitingReported = true;
            }
            await Task.Delay(previous is not null ? Math.Clamp(pollIntervalMs, 1, GameTiming.ConfirmationIntervalMs) : pollIntervalMs, ct);
        }
        var message = L.Text("未可靠识别强敌来袭略过次数，已停止任务。");
        log.Warn(message);
        return new(null, message);
    }
}

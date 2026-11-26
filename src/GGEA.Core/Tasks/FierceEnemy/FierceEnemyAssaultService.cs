using GGEA.App.Localization;
using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>事件导航结果；成功仅表示已点击左侧活动，不代表已进入战斗。</summary>
public sealed record FierceEnemyAssaultResult(bool Success, string Message);

/// <summary>确认关卡、事件标题后识别左侧活动；任何证据缺失都禁止后续点击。</summary>
public sealed class FierceEnemyAssaultService
{
    public const string TitleTemplate = "resources/templates/stage/event/title_event.png";
    public const string EntryTemplate = "resources/templates/stage/event/fierce_enemy_assault/fierce_enemy_assault.png";
    private readonly AutomationService _automation;
    private readonly IGgeaLog _log;
    private readonly int _timeoutMs;
    private readonly int _pollIntervalMs;
    private readonly string _titlePath;
    private readonly string _entryPath;
    private FierceEnemyAssaultRecognizer? _recognizer;

    /// <summary>素材和等待预算可替换，便于验证超时与缺失资源的安全边界。</summary>
    public FierceEnemyAssaultService(AutomationService automation, IGgeaLog log,
        int timeoutMs = GameTiming.RecognitionTimeoutMs, int pollIntervalMs = GameTiming.RecognitionIntervalMs, string? titlePath = null, string? entryPath = null)
    {
        _automation = automation;
        _log = log;
        _timeoutMs = timeoutMs;
        _pollIntervalMs = pollIntervalMs;
        _titlePath = titlePath ?? ConfigService.ResolvePath(TitleTemplate);
        _entryPath = entryPath ?? ConfigService.ResolvePath(EntryTemplate);
    }

    /// <summary>导航前解码所有素材；大 Logo 只按左侧活动缩略图尺寸缩放，不搜索右侧海报。</summary>
    public void Validate()
    {
        ImageCodec.DecodePng(File.ReadAllBytes(_titlePath));
        _recognizer ??= new FierceEnemyAssaultRecognizer(_entryPath);
    }

    /// <summary>从已确认的主界面进入关卡，再按用户指定坐标打开事件。</summary>
    public async Task<FierceEnemyAssaultResult> OpenFromHomeAsync(string serial, UiLayout mainLayout,
        int settleDelayMs = GameTiming.RecognitionIntervalMs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Validate();
        var menu = await new StageMenuEntryService(_automation, _log, timeoutMs: _timeoutMs,
            pollIntervalMs: _pollIntervalMs).OpenFromHomeAsync(serial, mainLayout, settleDelayMs, ct);
        if (!menu.Success) return new(false, menu.Message);
        await OpenEventListAsync(_automation, serial, ct);
        return await SelectFromEventAsync(serial, ct);
    }

    /// <summary>标题和活动在同一帧确认，防止页面切换后沿用旧标题授权点击。</summary>
    public async Task<FierceEnemyAssaultResult> SelectFromEventAsync(string serial, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Validate();
        var options = new WaitOptions { Region = RecognitionRegion.PageTitle, Threshold = .85,
            TimeoutMs = _timeoutMs, PollIntervalMs = _pollIntervalMs, AllowGlobalContinuation = false };
        if (!await WaitEventListAsync(_automation, serial, _titlePath, options, ct))
            return Fail(L.Text("未识别到事件标题，已停止强敌来袭导航。"));
        if (!await SelectEventBannerAsync(_automation, serial, _titlePath, options,
            (screen, token) => Task.Run(() => _recognizer!.Match(screen, token), token), ct))
            return Fail(L.Text("左上角 400×980 区域未识别到强敌来袭，已停止，未点击。"));
        return new(true, L.Text("已确认事件界面，并点击左侧强敌来袭活动。"));
    }

    internal static async Task<bool> SelectEventBannerAsync(AutomationService automation, string serial,
        string title, WaitOptions options, Func<ScreenImage,CancellationToken,Task<TemplateMatchResult?>> recognize, CancellationToken ct)
    {
        var entry = await StableObservation.WaitAsync(async () =>
        {
            var frame = await automation.CaptureScreenAsync(serial, ct);
            return await automation.MatchImageAsync(frame, title, options, ct) is null ? null : await recognize(frame, ct);
        }, (a,b) => Math.Abs(a.CenterX-b.CenterX) <= 4 && Math.Abs(a.CenterY-b.CenterY) <= 4, options.TimeoutMs, ct);
        if (entry is null) return false;
        await automation.TapAsync(serial, entry.CenterX, entry.CenterY, ct);
        automation.Log.Info($"已点击事件列表活动：({entry.CenterX},{entry.CenterY})，置信度 {entry.Confidence:P1}");
        return true;
    }

    internal static Task OpenEventListAsync(AutomationService automation, string serial, CancellationToken ct)
        => automation.TapAsync(serial, 1165, 400, ct);

    internal static async Task<bool> WaitEventListAsync(AutomationService automation, string serial,
        string title, WaitOptions options, CancellationToken ct)
    {
        if (await automation.WaitForImageAsync(serial, title, options, ct) is not null) return true;
        await automation.HandleTapToNextAsync(serial, ct);
        return await automation.WaitForImageAsync(serial, title, options, ct) is not null;
    }

    /// <summary>预期识别失败保留画面，由界面显示停止范围。</summary>
    private FierceEnemyAssaultResult Fail(string message)
    {
        _log.Warn(message);
        return new(false, message);
    }
}

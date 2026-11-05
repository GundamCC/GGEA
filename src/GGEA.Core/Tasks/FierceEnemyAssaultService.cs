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
        await _automation.TapAsync(serial, 1165, 400, ct);
        return await SelectFromEventAsync(serial, ct);
    }

    /// <summary>标题和活动在同一帧确认，防止页面切换后沿用旧标题授权点击。</summary>
    public async Task<FierceEnemyAssaultResult> SelectFromEventAsync(string serial, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Validate();
        var options = new WaitOptions { Region = RecognitionRegion.PageTitle, Threshold = .85,
            TimeoutMs = _timeoutMs, PollIntervalMs = _pollIntervalMs, AllowGlobalContinuation = false };
        if (await _automation.WaitForImageAsync(serial, _titlePath, options, ct) is null)
            return Fail(L.Text("未识别到事件标题，已停止强敌来袭导航。"));
        var entry = await _automation.WaitForImageAsync(serial, _entryPath, options, ct,
            async (screen, token) =>
            {
                if (await _automation.MatchImageAsync(screen, _titlePath, options, token) is null) return null;
                return await Task.Run(() => _recognizer!.Match(screen, token), token);
            });
        if (entry is null) return Fail(L.Text("左上角 400×980 区域未识别到强敌来袭，已停止，未点击。"));
        await _automation.TapAsync(serial, entry.CenterX, entry.CenterY, ct);
        _log.Info($"已识别并点击强敌来袭：({entry.CenterX},{entry.CenterY})，置信度 {entry.Confidence:P1}");
        return new(true, L.Text("已确认事件界面，并点击左侧强敌来袭活动。"));
    }

    /// <summary>预期识别失败保留画面，由界面显示停止范围。</summary>
    private FierceEnemyAssaultResult Fail(string message)
    {
        _log.Warn(message);
        return new(false, message);
    }
}

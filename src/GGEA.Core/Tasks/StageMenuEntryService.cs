using GGEA.App.Localization;
using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>关卡菜单的共享入口：确认左上角标题后，调用方才可进入具体关卡类型。</summary>
public sealed class StageMenuEntryService
{
    /// <summary>随包分发的关卡菜单左上角标题素材。</summary>
    public const string TitleTemplate = "resources/templates/stage/title_stage.png";
    private readonly AutomationService _automation;
    private readonly IGgeaLog _log;
    private readonly string _titleTemplatePath;
    private readonly int _timeoutMs;
    private readonly int _pollIntervalMs;

    /// <summary>默认等待标题最多二十秒；测试可注入素材路径和较短的等待预算。</summary>
    public StageMenuEntryService(AutomationService automation, IGgeaLog log,
        string? titleTemplatePath = null, int timeoutMs = GameTiming.RecognitionTimeoutMs, int pollIntervalMs = GameTiming.RecognitionIntervalMs)
    {
        _automation = automation;
        _log = log;
        _titleTemplatePath = titleTemplatePath ?? ConfigService.ResolvePath(TitleTemplate);
        _timeoutMs = timeoutMs;
        _pollIntervalMs = pollIntervalMs;
    }

    /// <summary>点击主界面关卡入口并有限等待标题；未命中时停止，不发送后续页面点击。</summary>
    public async Task<StageNavigationResult> OpenFromHomeAsync(string serial, UiLayout mainLayout,
        int settleDelayMs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var nav = mainLayout.Find("navStage")
            ?? throw new InvalidOperationException(L.Text("主界面布局缺少 navStage 锚点"));
        // 素材缺失属于资源问题，不能先点击再误报为主界面卡顿。
        if (!File.Exists(_titleTemplatePath))
            return Fail(L.Text("缺少关卡界面标题素材 title_stage.png，未进入关卡；请检查模板资源是否完整。"));

        _log.Info($"进入关卡菜单: ({nav.CenterX},{nav.CenterY})");
        await _automation.TapAsync(serial, nav.CenterX, nav.CenterY, ct);
        if (settleDelayMs <= 5000) await Task.Delay(Math.Max(0, settleDelayMs), ct);
        // 沿用统一标题区域；等待期间只观察，避免卡在主界面时触发额外的全局点击。
        var title = await _automation.WaitForImageAsync(serial, _titleTemplatePath,
            new WaitOptions
            {
                Region = RecognitionRegion.PageTitle,
                Threshold = .85,
                TimeoutMs = _timeoutMs,
                PollIntervalMs = _pollIntervalMs,
                AllowGlobalContinuation = false,
            }, ct);
        return title is not null
            ? StageNavigationResult.Ok(L.Text("已确认关卡界面标题"))
            : Fail(L.Text("未识别到关卡界面标题，已停止后续关卡流程；请检查主界面是否卡顿。"));
    }

    /// <summary>记录入口停止原因，让每日任务和 AP 流程沿用各自的界面与结果提醒。</summary>
    private StageNavigationResult Fail(string message)
    {
        _log.Warn(message);
        return StageNavigationResult.Fail(StageNavigationFailure.StageMenuNotRecognized, message);
    }
}

using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Config;
using GGEA.App.Vision;
using System.IO;
using OpenCvSharp;

namespace GGEA.App.Tasks;

/// <summary>强化培育关卡每日任务的执行结果。</summary>
public sealed record UpgradeStagesResult(bool Success, string Message)
{
    public static UpgradeStagesResult Ok(string message) => new(true, message);
    public static UpgradeStagesResult Fail(string message) => new(false, message);
}

/// <summary>
/// 强化培育关卡每日任务服务。
/// 优先识别一次略过；未出现时依次进入四种关卡类型。如果“略过”为灰，则继续下一类型；
/// 如果可略过，则复用独立略过服务完成一次略过，再返回上一页。
/// </summary>
public sealed class UpgradeStagesService
{
    private readonly AutomationService _automation;
    private readonly StageSkipService _skipService;
    private readonly IGgeaLog _log;
    private readonly StageMenuEntryService _menuEntry;
    private readonly int _recognitionTimeoutMs;
    private readonly int _pollIntervalMs;

    /// <summary>1920×1080 实机按钮周围留少量余量，排除卡片和底部导航。</summary>
    public static Roi SkipAllRoi => new(1320, 850, 410, 115);

    public UpgradeStagesService(
        AutomationService automation,
        StageSkipService skipService,
        IGgeaLog log,
        StageMenuEntryService? menuEntry = null,
        int recognitionTimeoutMs = GameTiming.RecognitionTimeoutMs,
        int pollIntervalMs = GameTiming.RecognitionIntervalMs)
    {
        _automation = automation;
        _skipService = skipService;
        _log = log;
        _menuEntry = menuEntry ?? new StageMenuEntryService(automation, log);
        _recognitionTimeoutMs = recognitionTimeoutMs;
        _pollIntervalMs = pollIntervalMs;
    }

    /// <summary>
    /// 从主界面进入关卡菜单 → UPGRADE STAGES，再执行四种强化培育类型。
    /// </summary>
    public async Task<UpgradeStagesResult> ExecuteFromMainAsync(
        string serial,
        UiLayout mainLayout,
        UiLayout stageLayout,
        UiLayout upgradeLayout,
        int networkSettleDelayMs = 3000,
        CancellationToken ct = default)
    {
        var upgradeStages = RequireAnchor(stageLayout, "upgradeStages", "关卡布局缺少 upgradeStages 锚点");

        var entry = await _menuEntry.OpenFromHomeAsync(serial, mainLayout, networkSettleDelayMs, ct);
        if (!entry.Success) return UpgradeStagesResult.Fail(entry.Message);

        _log.Info($"进入 UPGRADE STAGES: ({upgradeStages.CenterX},{upgradeStages.CenterY})");
        await _automation.TapAsync(serial, upgradeStages.CenterX, upgradeStages.CenterY, ct);
        await Task.Delay(networkSettleDelayMs, ct);

        return await ExecuteDailyAsync(serial, upgradeLayout, networkSettleDelayMs, ct);
    }

    /// <summary>确认标题后优先一次略过；按钮未出现才走四种类型的后备流程。</summary>
    public async Task<UpgradeStagesResult> ExecuteDailyAsync(
        string serial,
        UiLayout upgradeLayout,
        int networkSettleDelayMs = 3000,
        CancellationToken ct = default)
    {
        var title = await _automation.WaitForImageAsync(serial,
            ConfigService.ResolvePath("resources/templates/stage/upgrade_stage.png"),
            new WaitOptions { Roi = new Roi(0, 0, 737, 104), Threshold = .92,
                TimeoutMs = _recognitionTimeoutMs, PollIntervalMs = _pollIntervalMs,
                AllowGlobalContinuation = false }, ct);
        if (title is null) return UpgradeStagesResult.Fail("未识别到强化培育关卡标题，已停止操作");

        // 亮暗态必须来自同帧；暗态是明确的不可略过证据，不能退回旧流程继续尝试。
        var screen = await _automation.CaptureScreenAsync(serial, ct);
        var buttonWait = new WaitOptions { Roi = SkipAllRoi, Threshold = .90, AllowGlobalContinuation = false };
        var disabled = await Task.Run(() => MatchDisabledButton(screen, ct), ct);
        if (disabled is not null)
        {
            _log.Info("一次略过按钮为暗态，当前不可略过，结束强化培育任务");
            return UpgradeStagesResult.Ok("一次略过不可用，已跳过强化培育任务");
        }
        var skipAll = await _automation.MatchImageAsync(screen,
            ConfigService.ResolvePath("resources/templates/stage/skip_all.png"),
            buttonWait, ct);
        if (skipAll is not null)
        {
            _log.Info($"识别到一次略过，点击 ({skipAll.CenterX},{skipAll.CenterY})");
            await _automation.TapAsync(serial, skipAll.CenterX, skipAll.CenterY, ct);
            var execute = await _automation.WaitForImageAsync(serial,
                ConfigService.ResolvePath("resources/templates/dialog/execute.png"),
                new WaitOptions { Region = RecognitionRegion.BottomButtons,
                    Threshold = RecognitionRegions.BottomButtonThreshold,
                    TimeoutMs = _recognitionTimeoutMs, PollIntervalMs = _pollIntervalMs,
                    AllowGlobalContinuation = false }, ct);
            // 已进入确认窗口后禁止回退旧流程，防止在错误页面点击或重复消耗次数。
            if (execute is null) return UpgradeStagesResult.Fail("一次略过后未识别到底部执行按钮，已停止操作");
            await _automation.TapAsync(serial, execute.CenterX, execute.CenterY, ct);
            var result = await _skipService.CompleteExecutedSkipAsync(serial, upgradeLayout, ct);
            return result.Success ? UpgradeStagesResult.Ok("强化培育一次略过完成")
                : UpgradeStagesResult.Fail(result.Message);
        }

        _log.Info("一次略过亮态与暗态均未识别，使用逐类型后备流程");
        var back = RequireAnchor(upgradeLayout, "upgradeBack", "强化培育布局缺少 upgradeBack 锚点");
        var typeNames = new[]
        {
            "upgradeCapital",
            "upgradeUnit",
            "upgradeCharacter",
            "upgradeSupport",
        };

        foreach (var typeName in typeNames)
        {
            ct.ThrowIfCancellationRequested();
            var type = RequireAnchor(
                upgradeLayout,
                typeName,
                $"强化培育布局缺少 {typeName} 锚点");

            _log.Info($"进入 {typeName}: ({type.CenterX},{type.CenterY})");
            await _automation.TapAsync(serial, type.CenterX, type.CenterY, ct);
            await Task.Delay(networkSettleDelayMs, ct);

            var enabled = await _skipService.IsSkipEnabledAsync(serial, upgradeLayout, ct);
            if (!enabled)
            {
                _log.Warn($"{typeName} 略过为灰（次数上限），返回上一页");
                await GoBackAsync(serial, back, networkSettleDelayMs, ct);
                continue;
            }

            var skip = await _skipService.SkipCurrentAsync(serial, upgradeLayout, ct);
            if (!skip.Success)
            {
                return UpgradeStagesResult.Fail($"{typeName} 略过失败: {skip.Message}");
            }

            _log.Info($"{typeName} 略过成功，返回上一页");
            await GoBackAsync(serial, back, networkSettleDelayMs, ct);
        }

        return UpgradeStagesResult.Ok("强化培育关卡每日任务完成");
    }

    /// <summary>暗态整体亮度很低，平方差可能误收空白背景；必须同时核对按钮文字与边框纹理。</summary>
    private static TemplateMatchResult? MatchDisabledButton(ScreenImage screen, CancellationToken ct)
        => TemplateFallback.Match("resources/templates/stage/skip_all_disabled.png", path =>
    {
        var template = ImageCodec.DecodePng(File.ReadAllBytes(path));
        var match = new TemplateMatcher().Match(screen.Bgra, screen.Width, screen.Height,
            template.Bgra, template.Width, template.Height, .90, SkipAllRoi, ct: ct);
        if (match is null) return null;
        using var source = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
        using var target = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC4, template.Bgra);
        using var patch = new Mat(source, new Rect(match.X, match.Y, template.Width, template.Height));
        using var gray = new Mat();
        using var targetGray = new Mat();
        using var correlation = new Mat();
        Cv2.CvtColor(patch, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.CvtColor(target, targetGray, ColorConversionCodes.BGRA2GRAY);
        Cv2.MatchTemplate(gray, targetGray, correlation, TemplateMatchModes.CCoeffNormed);
        var score = correlation.At<float>(0, 0);
        return float.IsFinite(score) && score >= .85 ? match : null;
    }, ct);

    /// <summary>点击返回按钮并等待页面加载稳定。</summary>
    private async Task GoBackAsync(
        string serial,
        UiAnchor back,
        int networkSettleDelayMs,
        CancellationToken ct)
    {
        _log.Info($"返回上一页: ({back.CenterX},{back.CenterY})");
        await _automation.TapAsync(serial, back.CenterX, back.CenterY, ct);
        await Task.Delay(networkSettleDelayMs, ct);
    }

    /// <summary>读取必需的布局锚点，缺失时终止。</summary>
    private static UiAnchor RequireAnchor(UiLayout layout, string name, string error)
        => layout.Find(name) ?? throw new InvalidOperationException(error);
}

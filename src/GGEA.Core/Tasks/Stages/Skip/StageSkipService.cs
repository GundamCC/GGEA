using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.Localization;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>
/// 独立略过服务：负责“略过 → 执行 → SKIP RESULT → OK → 处理升级 → 确认次数上限暗态”。
/// 不依赖具体业务场景，后续奖励、活动等位置都可以复用。
/// </summary>
public sealed partial class StageSkipService
{
    private readonly AutomationService _automation;
    private readonly IGgeaLog _log;
    private readonly StageSkipOptions _options;
    private readonly RecognitionEvidence? _evidence;
    private readonly ApStopSignal? _apStopSignal;
    private readonly SkipExecutionDialogRecognizer? _executionDialog;
    // 同一服务会连续略过多个关卡；模板按需解码一次，正常返回时不加载升级素材。
    private readonly Dictionary<string, TemplateImages> _postSkipTemplates = new();

    public StageSkipService(
        AutomationService automation,
        IGgeaLog log,
        StageSkipOptions? options = null,
        RecognitionEvidence? evidence = null, ApStopSignal? apStopSignal = null)
    {
        _automation = automation;
        _log = log;
        _options = options ?? new StageSkipOptions();
        _evidence = evidence;
        _apStopSignal = apStopSignal;
        _executionDialog = _options.RecoverUnchangedExecutionDialog ? new SkipExecutionDialogRecognizer() : null;
    }

    /// <summary>只有完整配置回复 AP 标题与按钮时才允许识别，通用关闭按钮不能提供业务证据。</summary>
    private bool DetectApRecovery => !string.IsNullOrWhiteSpace(_options.ApRecoveryPromptTemplatePath) &&
        !string.IsNullOrWhiteSpace(_options.ApRecoveryCloseTemplatePath);

    /// <summary>
    /// 判断“略过”按钮当前是否可用。
    /// 在略过按钮区域识别“次数上限”暗态模板：识别到表示今日次数已用尽，不可略过。
    /// </summary>
    public async Task<bool> IsSkipEnabledAsync(
        string serial,
        UiLayout stageLayout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.SkipCountLimitTemplatePath))
        {
            return true;
        }

        var countLimit = stageLayout.Require("skipCountLimit", "关卡布局缺少 skipCountLimit 锚点");
        var wait = new WaitOptions
        {
            Threshold = _options.SkipCountLimitThreshold,
            Roi = countLimit.Roi,
        };
        var match = await _automation.FindImageAsync(
            serial,
            _options.SkipCountLimitTemplatePath,
            wait,
            ct);
        if (match is not null)
        {
            _log.Warn("略过不可用：识别到次数上限暗态");
            return false;
        }

        return true;
    }

    /// <summary>对当前选中的 HARD 关卡执行一次完整略过。</summary>
    public async Task<StageSkipResult> SkipCurrentAsync(
        string serial,
        UiLayout stageLayout,
        CancellationToken ct = default)
    {
        // 先报告当前关卡的不可用状态；素材校验仍在所有消费动作之前完成。
        if (!await IsSkipEnabledAsync(serial, stageLayout, ct))
        {
            return StageSkipResult.Fail("略过不可用（次数上限）", isUnavailable: true);
        }

        // 没有结果识别素材时，长睡眠不能替代成功证据；在消费动作之前拒绝旧兼容路径。
        if (_options.SkipResultTimeoutMs > 5000 && string.IsNullOrWhiteSpace(_options.SkipResultTemplatePath) &&
            !DetectApRecovery)
            return StageSkipResult.Fail("未配置略过结果识别素材，不能用固定等待确认执行结果。");

        var stageSkip = stageLayout.Require("stageSkip", "关卡布局缺少 stageSkip 锚点");
        var execute = stageLayout.Require("skipDialogExecute", "关卡布局缺少 skipDialogExecute 锚点");

        _log.Info($"点击略过: ({stageSkip.CenterX},{stageSkip.CenterY})");
        await _automation.TapAsync(serial, stageSkip.CenterX, stageSkip.CenterY, ct);
        await GameTiming.DelayIfPositiveAsync(_options.SkipDialogWaitMs, ct);

        // 原确认页是本次消费的身份依据；两帧稳定后再执行，不能仅凭固定等待盲点。
        SkipExecutionDialog? originalDialog = null;
        if (_executionDialog is not null)
        {
            var dialogDeadline = Environment.TickCount64 + GameTiming.ShortRecognitionTimeoutMs;
            while (Environment.TickCount64 < dialogDeadline)
            {
                var screen = await _automation.CaptureScreenAsync(serial, ct);
                var observed = await Task.Run(() => _executionDialog.Recognize(screen, ct), ct);
                if (observed is not null)
                {
                    originalDialog = await ConfirmExecutionDialogAsync(serial, observed, ct);
                    if (originalDialog is not null) break;
                }
                await DelayWithinBudgetAsync(dialogDeadline, GameTiming.ConfirmationIntervalMs, ct);
            }
            if (originalDialog is null)
                return StageSkipResult.Fail(L.Text("未连续确认略过执行弹窗及 AP/次数，未发送执行，请检查当前画面。"));
        }
        var executeX = originalDialog?.Execute.CenterX ?? execute.CenterX;
        var executeY = originalDialog?.Execute.CenterY ?? execute.CenterY;
        _log.Info($"点击执行: ({executeX},{executeY})");
        await _automation.TapAsync(serial, executeX, executeY, ct);

        return await CompleteExecutedSkipCoreAsync(serial, stageLayout, originalDialog, ct);
    }

    /// <summary>动作前使用两张新帧复核原选择；取消令牌与公共截图失败原样传播，不复用超时截图点击。</summary>
    private async Task<SkipExecutionDialog?> ConfirmExecutionDialogAsync(string serial, SkipExecutionDialog original, CancellationToken ct)
    {
        SkipExecutionDialog? current = null;
        for (var sample = 0; sample < 2; sample++)
        {
            if (sample > 0) await GameTiming.DelayIfPositiveAsync(GameTiming.ConfirmationIntervalMs, ct);
            var screen = await _automation.CaptureScreenAsync(serial, ct);
            current = await Task.Run(() => _executionDialog!.Recognize(screen, ct), ct);
            if (current is null || !await Task.Run(() => _executionDialog!.IsSameSelection(original, current, ct), ct)) return null;
        }
        return current;
    }

    /// <summary>轮询睡眠只消费剩余预算，避免短超时被完整轮询间隔拖长；用户取消正常传播。</summary>
    private static async Task DelayWithinBudgetAsync(long deadline, int intervalMs, CancellationToken ct)
    {
        var wakeAt = Math.Min(deadline, Environment.TickCount64 + Math.Max(0, intervalMs));
        ct.ThrowIfCancellationRequested();
        // 定时器可能略早唤醒；按单调时钟补足同一采样间隔，不能在截止前额外截取一帧。
        while (wakeAt - Environment.TickCount64 is var remaining && remaining > 0)
            await Task.Delay((int)Math.Min(int.MaxValue, remaining), ct);
        ct.ThrowIfCancellationRequested();
    }
}

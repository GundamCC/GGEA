using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>
/// 领取每日使命奖励服务：主界面 → 使命 → 全部领取 → 结果 OK → 返回主界面。
/// 使命奖励需要先完成其他每日任务才能领取，因此调用方应保证该步骤排在最后。
/// </summary>
public sealed class MissionRewardService
{
    private readonly AutomationService _automation;
    private readonly IGgeaLog _log;

    public MissionRewardService(AutomationService automation, IGgeaLog log)
    {
        _automation = automation;
        _log = log;
    }

    /// <summary>从主界面进入使命页并点击“全部领取”，完成后返回主界面。</summary>
    public async Task<bool> ClaimAsync(
        string serial,
        UiLayout mainLayout,
        UiLayout missionLayout,
        string claimAllTemplatePath,
        string claimAllDisabledTemplatePath,
        string okTemplatePath,
        int networkSettleDelayMs = 3000,
        int waitTimeoutMs = GameTiming.RecognitionTimeoutMs,
        double threshold = 0.85,
        CancellationToken ct = default)
    {
        var navMission = RequireAnchor(mainLayout, "navMission", "主界面布局缺少 navMission 锚点");
        var navHome = RequireAnchor(mainLayout, "navHome", "主界面布局缺少 navHome 锚点");
        var claimAll = RequireAnchor(missionLayout, "claimAll", "使命布局缺少 claimAll 锚点");

        _log.Info($"进入使命页: ({navMission.CenterX},{navMission.CenterY})");
        await _automation.TapAsync(serial, navMission.CenterX, navMission.CenterY, ct);
        if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);

        // 亮色优先，暗态命中立即返回：同一轮里先找可领取按钮，找不到再找暗态按钮。
        var deadline = Environment.TickCount64 + Math.Max(0, waitTimeoutMs);
        TemplateMatchResult? claimMatch = null;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var enabledWait = new WaitOptions
            {
                Threshold = threshold,
                Roi = claimAll.Roi,
            };
            var enabledMatch = await _automation.FindImageAsync(
                serial,
                claimAllTemplatePath,
                enabledWait,
                ct);
            if (enabledMatch is not null)
            {
                claimMatch = enabledMatch;
                break;
            }

            var disabledWait = new WaitOptions
            {
                Threshold = Math.Clamp(threshold + 0.05, 0.5, 0.99),
                Roi = claimAll.Roi,
            };
            var disabledMatch = await _automation.FindImageAsync(
                serial,
                claimAllDisabledTemplatePath,
                disabledWait,
                ct);
            if (disabledMatch is not null)
            {
                _log.Info("使命奖励已领取完毕（全部领取为暗态）");
                await ReturnToMainAsync(serial, navHome, networkSettleDelayMs, ct);
                return true;
            }

            await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
        }

        if (claimMatch is null)
        {
            // 两种状态都未命中只能说明未知，不能据此声称已领完，也不能在未知页面盲点返回。
            _log.Warn("无法确认使命奖励状态（可领取和已领取按钮均未出现），流程中止");
            return false;
        }

        // 搜索区域包含完整按钮及位置余量；只能点击识别中心，不能再把区域中心当作按钮中心。
        _log.Info($"点击全部领取: ({claimMatch.CenterX},{claimMatch.CenterY})");
        await _automation.TapAsync(serial, claimMatch.CenterX, claimMatch.CenterY, ct);
        if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);

        var okWait = new WaitOptions
        {
            TimeoutMs = waitTimeoutMs,
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = threshold,
            // 完整 OK 模板共享底部识别区域，点击使用实际匹配中心。
            Roi = RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, missionLayout.ScreenWidth, missionLayout.ScreenHeight),
        };
        var okMatch = await _automation.WaitForImageAsync(serial, okTemplatePath, okWait, ct);
        if (okMatch is null)
        {
            _log.Warn("未检测到使命领取结果 OK，流程中断");
            return false;
        }

        _log.Info($"点击领取结果 OK: ({okMatch.CenterX},{okMatch.CenterY})");
        await _automation.TapAsync(serial, okMatch.CenterX, okMatch.CenterY, ct);
        if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);
        else if (!await _automation.WaitImageDisappearAsync(serial, okTemplatePath,
                     okWait with { AllowGlobalContinuation = false }, ct))
            return false;

        await ReturnToMainAsync(serial, navHome, networkSettleDelayMs, ct);
        _log.Info("领取每日使命奖励完成");
        return true;
    }

    /// <summary>点击主界面导航并等待页面加载稳定。</summary>
    private async Task ReturnToMainAsync(
        string serial,
        UiAnchor navHome,
        int networkSettleDelayMs,
        CancellationToken ct)
    {
        _log.Info($"返回主界面: ({navHome.CenterX},{navHome.CenterY})");
        await _automation.TapAsync(serial, navHome.CenterX, navHome.CenterY, ct);
        if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);
    }

    /// <summary>读取必需的布局锚点，缺失时终止。</summary>
    private static UiAnchor RequireAnchor(UiLayout layout, string name, string error)
        => layout.Find(name) ?? throw new InvalidOperationException(error);
}

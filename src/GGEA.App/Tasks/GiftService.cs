using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>
/// 礼物服务：收取礼物箱中的全部礼物。
/// 流程：主界面 → 礼物 → 全部领取 → 领取结果 OK → 关闭礼物页。
/// </summary>
public sealed class GiftService
{
    private readonly AutomationService _automation;
    private readonly IGgeaLog _log;

    public GiftService(AutomationService automation, IGgeaLog log)
    {
        _automation = automation;
        _log = log;
    }

    /// <summary>
    /// 收取礼物箱中的全部礼物。
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="mainLayout">主界面布局（用于礼物入口 navGift）。</param>
    /// <param name="giftLayout">礼物页布局（全部领取、结果 OK、关闭）。</param>
    /// <param name="collectAllTemplatePath">“全部领取”按钮模板。</param>
    /// <param name="collectAllDisabledTemplatePath">“全部领取”灰色禁用状态模板（礼物已领完）。</param>
    /// <param name="okTemplatePath">领取结果弹窗 OK 模板。</param>
    /// <param name="closeTemplatePath">礼物页关闭按钮模板。</param>
    /// <param name="networkSettleDelayMs">网络请求加载完成的稳定等待时长。</param>
    /// <returns>是否完成收取。</returns>
    public async Task<bool> CollectGiftsAsync(
        string serial,
        UiLayout mainLayout,
        UiLayout giftLayout,
        string collectAllTemplatePath,
        string collectAllDisabledTemplatePath,
        string okTemplatePath,
        string closeTemplatePath,
        int stepDelayMs = 2500,
        int networkSettleDelayMs = 2500,
        int waitTimeoutMs = GameTiming.RecognitionTimeoutMs,
        double threshold = 0.85,
        CancellationToken ct = default)
    {
        var navGift = mainLayout.Find("navGift")
            ?? throw new InvalidOperationException("主界面布局缺少 navGift 锚点");
        // 全宽底部搜索不能沿用窄文字锚点的 .85：真实关卡页灰色背景可产生 .857 的假暗态。
        // 与其它完整底部按钮共用既有门槛，调用方要求更严格时仍保留其值。
        threshold = Math.Max(threshold, RecognitionRegions.BottomButtonThreshold);
        // 三种完整按钮都在底部搜索；布局的旧文字锚点不再限制模板尺寸。
        var bottom = RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, giftLayout.ScreenWidth, giftLayout.ScreenHeight);

        // 1. 从主界面进入礼物页
        _log.Info($"进入礼物页: ({navGift.CenterX}, {navGift.CenterY})");
        await _automation.TapAsync(serial, navGift.CenterX, navGift.CenterY, ct);
        await Task.Delay(stepDelayMs, ct);

        // 2. 判断礼物状态：
        //    亮色“全部领取”= 有礼物可领；灰色“全部领取”= 已领完，直接进入关闭
        var checkWait = new WaitOptions
        {
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = threshold,
            Roi = bottom,
        };
        var deadline = Environment.TickCount64 + waitTimeoutMs;
        var stateFound = false;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var enabled = await _automation.FindImageAsync(serial, collectAllTemplatePath, checkWait, ct);
            if (enabled is not null)
            {
                stateFound = true;
                await _automation.TapAsync(serial, enabled.CenterX, enabled.CenterY, ct);
                _log.Info($"已点击全部领取: ({enabled.CenterX},{enabled.CenterY})");
                await Task.Delay(networkSettleDelayMs, ct);

                // 3. 领取结果弹窗 OK
                if (!await ClickWhenAppearsAsync(
                        serial, okTemplatePath, bottom, "领取结果 OK 按钮", waitTimeoutMs, threshold, ct))
                {
                    return false;
                }

                await Task.Delay(networkSettleDelayMs, ct);
                break;
            }

            var disabled = await _automation.FindImageAsync(serial, collectAllDisabledTemplatePath, checkWait, ct);
            if (disabled is not null)
            {
                stateFound = true;
                _log.Info("礼物已领取完毕（全部领取为灰色），跳过领取");
                break;
            }

            await Task.Delay(checkWait.PollIntervalMs, ct);
        }

        if (!stateFound)
        {
            _log.Warn("未能确认礼物页状态（全部领取按钮未出现），流程中断");
            return false;
        }

        // 4. 关闭礼物页，回到主界面
        if (!await ClickWhenAppearsAsync(
                serial, closeTemplatePath, bottom, "礼物页关闭按钮", waitTimeoutMs, threshold, ct))
        {
            return false;
        }

        await Task.Delay(networkSettleDelayMs, ct);
        _log.Info("收取礼物完成");
        return true;
    }

    /// <summary>在指定区域等待按钮出现，仅点击实际匹配中心。</summary>
    private async Task<bool> ClickWhenAppearsAsync(
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

        var match = await _automation.WaitForImageAsync(serial, templatePath, wait, ct);
        if (match is null)
        {
            _log.Warn($"{label}未出现，流程中断");
            return false;
        }

        // 底部搜索区不是按钮本体，只能点击实际识别中心。
        var x = match.CenterX;
        var y = match.CenterY;
        await _automation.TapAsync(serial, x, y, ct);
        _log.Info($"已点击{label}: ({x},{y})");
        return true;
    }
}

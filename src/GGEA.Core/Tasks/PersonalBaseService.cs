using System.IO;
using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>
/// 收获战舰的结果状态。
/// </summary>
public enum BattleshipHarvestStatus
{
    /// <summary>已成功点击回收并关闭结果弹窗。</summary>
    Success,

    /// <summary>战舰出航中，全部回收按钮为灰色，暂不可回收。</summary>
    NotReady,

    /// <summary>等待按钮状态超时。</summary>
    Timeout,
}

/// <summary>
/// 收获战舰结果。
/// </summary>
public sealed record BattleshipHarvestResult(BattleshipHarvestStatus Status, string Message)
{
    /// <summary>流程是否正常结束；出航中无法回收属于可预期的安全略过，不计为失败。</summary>
    public bool Completed => Status is BattleshipHarvestStatus.Success or BattleshipHarvestStatus.NotReady;

    /// <summary>本次是否实际完成了战舰回收。</summary>
    public bool Success => Status == BattleshipHarvestStatus.Success;

    public static BattleshipHarvestResult Ok(string message)
        => new(BattleshipHarvestStatus.Success, message);

    public static BattleshipHarvestResult NotReady(string message)
        => new(BattleshipHarvestStatus.NotReady, message);

    public static BattleshipHarvestResult TimedOut(string message)
        => new(BattleshipHarvestStatus.Timeout, message);
}

/// <summary>
/// 个人基地服务：目前实现“收获战舰”。
/// 流程：进入个人基地 → 查找“蓝色亮边六边形”战舰区域（找不到则拖动画面）→
/// 点击战舰 → 等待右下角固定位置的“全部回收” → 点击 → 关闭结果弹窗（OK）。
/// 若“全部回收”为暗灰色（战舰出航中），则视为暂不可回收，安全跳过不做点击。
/// </summary>
public sealed class PersonalBaseService
{
    private readonly AutomationService _automation;
    private readonly BattleshipLocator _battleshipLocator;
    private readonly IGgeaLog _log;

    public PersonalBaseService(
        AutomationService automation,
        BattleshipLocator battleshipLocator,
        IGgeaLog log)
    {
        _automation = automation;
        _battleshipLocator = battleshipLocator;
        _log = log;
    }

    /// <summary>
    /// 收获战舰（完成每日任务“于战舰巡航中获得20AP”）。
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="mainLayout">主界面布局（用于个人基地入口 navBase）。</param>
    /// <param name="baseLayout">个人基地布局（全部回收、结果 OK）。</param>
    /// <param name="battleshipTemplatePath">蓝色亮边六边形战舰模板。</param>
    /// <param name="battleshipMaskPath">可选战舰模板遮罩；为空表示不使用遮罩。</param>
    /// <param name="recycleTemplatePath">可用状态的“全部回收”模板。</param>
    /// <param name="recycleDisabledTemplatePath">不可用（暗灰）状态的“全部回收”模板。</param>
    /// <param name="okTemplatePath">结果弹窗 OK 模板。</param>
    /// <param name="maxDrags">查找战舰区域时的最大拖动次数。</param>
    /// <param name="dragDurationMs">每次拖动的时长。</param>
    /// <param name="stepDelayMs">点击后的页面切换等待时长。</param>
    /// <param name="networkSettleDelayMs">点击战舰后等待网络请求加载完成的稳定时长。</param>
    /// <param name="waitTimeoutMs">等待按钮状态出现的总超时。</param>
    /// <param name="threshold">模板匹配置信度阈值。</param>
    /// <returns>收获结果（成功 / 暂不可回收 / 超时）。</returns>
    public async Task<BattleshipHarvestResult> HarvestBattleshipAsync(
        string serial,
        UiLayout mainLayout,
        UiLayout baseLayout,
        string battleshipTemplatePath,
        string? battleshipMaskPath,
        string recycleTemplatePath,
        string recycleDisabledTemplatePath,
        string okTemplatePath,
        int maxDrags = 16,
        int dragDurationMs = 700,
        int stepDelayMs = 2500,
        int networkSettleDelayMs = 3000,
        int waitTimeoutMs = 30000,
        double threshold = 0.85,
        CancellationToken ct = default,
        int pollIntervalMs = GameTiming.RecognitionIntervalMs)
    {
        var navBase = mainLayout.Find("navBase")
            ?? throw new InvalidOperationException("主界面布局缺少 navBase 锚点");
        var claimAll = baseLayout.Find("claimAll")
            ?? throw new InvalidOperationException("个人基地布局缺少 claimAll 锚点");

        // 1. 从主界面进入个人基地
        _log.Info($"进入个人基地: ({navBase.CenterX}, {navBase.CenterY})");
        await _automation.TapAsync(serial, navBase.CenterX, navBase.CenterY, ct);
        await Task.Delay(stepDelayMs, ct);

        // 2. 查找“蓝色亮边六边形”战舰区域；个人基地可拖动，未找到时自动拖动重试
        var battleship = await _battleshipLocator.FindWithPanAsync(
            serial,
            battleshipTemplatePath,
            battleshipMaskPath,
            maxDrags,
            dragDurationMs,
            threshold,
            ct);
        if (battleship is null)
        {
            _log.Warn("未找到战舰区域，收获战舰失败");
            return BattleshipHarvestResult.TimedOut("未找到战舰区域");
        }

        _log.Info($"点击战舰区域: ({battleship.CenterX},{battleship.CenterY})");
        await _automation.TapAsync(serial, battleship.CenterX, battleship.CenterY, ct);

        // 网络请求期间按钮可能尚未渲染，先等加载稳定再开始轮询。
        if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);

        // 同一帧先检查奖励 OK，再检查回收状态，避免把弹窗遮罩下的按钮当作出航中。
        var matcher = new TemplateMatcher();
        var enabledTemplate = new TemplateImages(recycleTemplatePath);
        var disabledTemplate = new TemplateImages(recycleDisabledTemplatePath);
        var okTemplate = new TemplateImages(okTemplatePath);
        var deadline = Environment.TickCount64 + waitTimeoutMs;
        var disabledStreak = 0;
        var recycleClicked = false;
        var resultClosed = false;
        var awaitingOkDisappear = false;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var screen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            // 完整按钮在统一底部区域识别，避免旧文字锚点裁掉边框。
            var reward = Match(okTemplate, RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, screen.Width, screen.Height));
            if (reward is not null)
            {
                disabledStreak = 0;
                // 关闭后必须观察到按钮消失，避免重复点击同一个未响应的弹窗。
                if (!awaitingOkDisappear)
                {
                    await _automation.TapAsync(serial, reward.CenterX, reward.CenterY, ct);
                    _log.Info("检测到基地奖励弹窗，已点击 OK，等待关闭后重新检查战舰状态");
                    awaitingOkDisappear = true;
                    resultClosed = recycleClicked;
                    if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);
                }
            }
            else
            {
                awaitingOkDisappear = false;
                if (resultClosed)
                    return BattleshipHarvestResult.Ok("已回收战舰并确认奖励弹窗关闭");

                // 点击全部回收后只等结果，不把请求处理中变灰的按钮判为出航中，也不重复回收。
                if (!recycleClicked)
                {
                    var enabled = Match(enabledTemplate, claimAll.Roi);
                    if (enabled is not null)
                    {
                        await _automation.TapAsync(serial, enabled.CenterX, enabled.CenterY, ct);
                        recycleClicked = true;
                        disabledStreak = 0;
                        deadline = Environment.TickCount64 + waitTimeoutMs;
                        if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);
                    }
                    else if (Match(disabledTemplate, claimAll.Roi) is not null)
                    {
                        if (++disabledStreak >= 2)
                        {
                            _log.Warn("战舰出航中，全部回收按钮为灰色，暂不可回收");
                            return BattleshipHarvestResult.NotReady("战舰出航中，暂不可回收");
                        }
                    }
                    else disabledStreak = 0;
                }
            }
            await Task.Delay(Math.Max(1, pollIntervalMs), ct);

            // 三种候选共享截图，状态判定不能混用不同时间的画面。
            TemplateMatchResult? Match(TemplateImages template, Roi roi)
                => template.Match(matcher, screen, threshold, roi, ct);
        }

        var message = recycleClicked ? "已点击全部回收，但未确认奖励弹窗关闭" : "等待战舰状态或奖励弹窗关闭超时";
        _log.Warn(message);
        return BattleshipHarvestResult.TimedOut(message);
    }
}

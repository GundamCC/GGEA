using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.Localization;
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
/// 个人基地服务：从主界面进入基地并执行战舰回收或角色要求。
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

    /// <summary>从已确认的主界面进入个人基地，复核地图入口后交给本轮的角色栏位流程。</summary>
    public async Task<int> AcceptCharacterRequestsAsync(string serial, UiLayout mainLayout, CancellationToken ct = default) =>
        (await RunCharacterRequestsAsync(serial, mainLayout, null, ct)).Accepted;

    public Task<CharacterRequestResult> RunCharacterRequestsAsync(string serial, UiLayout mainLayout, OnnxTextRecognizer? ocr, CancellationToken ct = default, DeliveryProtection? protection = null, DevelopRequest? development = null, SortieRequestService? sortie = null, bool fromHarvest = false) =>
        Task.Run(async () =>
        {
            using var service = new CharacterRequestService(_automation, _log, ocr, protection, development)
            {
                Sortie = sortie,
                RestockFromMethods = development is null || ocr is null ? null : (device, offer, reserve, token) =>
                    new DeliveryDevelopmentService(_automation, _log, ocr)
                        .FromMethodsAsync(device, offer, protection ?? new(), development, reserve, token),
                RestockFromList = development is null || ocr is null ? null : async (device, target, reserve, token) =>
                {
                    var production = new DeliveryDevelopmentService(_automation, _log, ocr);
                    var made = await production.TryProduceAsync(device, target, development, reserve, token);
                    await OpenRequestsAsync(device, mainLayout, token);
                    return made;
                }
            };
            await OpenRequestsAsync(serial, mainLayout, ct, fromHarvest);
            return await service.RunFromListAsync(serial, ct);
        }, ct);

    // 开发作品列表和主界面都带底部导航，从已核对的页面重新进入个人基地。
    internal async Task OpenRequestsAsync(string serial, UiLayout mainLayout, CancellationToken ct, bool fromHarvest = false)
    {
        if (fromHarvest) await ReturnFromCruiseAsync(serial, ct);
        else
        {
            var navBase = mainLayout.Require("navBase", "主界面布局缺少 navBase 锚点");
            await _automation.TapAsync(serial, navBase.CenterX, navBase.CenterY, ct);
            await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
        }
        var entry = await _battleshipLocator.FindCharacterRequestsWithPanAsync(serial,
            maxDrags: fromHarvest ? 1 : 16, ct: ct, fromHarvest: fromHarvest);
        if (entry is not null)
        {
            // 定位器已经在当前视野连续三帧确认入口。
            await _automation.TapAsync(serial, entry.CenterX, entry.CenterY, ct);
            return;
        }
        throw new InvalidOperationException(L.Text("未能确认个人基地的角色要求入口。"));
    }

    private sealed record BaseView(Roi? Back);

    // 回收按钮变灰及领取奖励都会结束回收步骤，但巡航详情仍可能留在前台。
    // 必须在确认地图标题后才把手势交给地图；禁止重复点击仍在转场中的返回按钮。
    private async Task ReturnFromCruiseAsync(string serial, CancellationToken ct)
    {
        const string mapPath = "resources/templates/base/title_personal_base.png";
        const string cruisePath = "resources/templates/base/battleship_cruise/title_cruise.png";
        if (!System.IO.File.Exists(GGEA.App.Config.ConfigService.ResolvePath(mapPath)) ||
            !System.IO.File.Exists(GGEA.App.Config.ConfigService.ResolvePath(cruisePath)))
            throw new InvalidOperationException(L.Text("未能确认个人基地的角色要求入口。"));
        var map = new TemplateImages(mapPath);
        var cruise = new TemplateImages(cruisePath);
        var back = new TemplateImages("resources/templates/stage/history_back.png");
        var matcher = new TemplateMatcher();
        async Task<BaseView?> Observe(bool allowCruise)
        {
            var frame = await _automation.CaptureScreenAsync(serial, ct);
            var mapTitle = map.Match(matcher, frame, .94, new(28, 4, 289, 89), ct);
            if (mapTitle is not null) return new BaseView(null);
            if (!allowCruise || cruise.Match(matcher, frame, .94, new(199, 4, 309, 91), ct) is null)
                return null;
            var button = back.Match(matcher, frame, .90, new(0, 0, 205, 110), ct);
            return button is null ? null : new BaseView(button.Region);
        }
        Task<BaseView?> Wait(bool allowCruise) => StableObservation.WaitAsync(
            () => Observe(allowCruise), (a, b) => a == b, GameTiming.RecognitionTimeoutMs, ct);
        var initial = await Wait(true);
        if (initial is not null)
        {
            if (initial.Back is not { } button) return;
            _log.Info("已确认战舰巡航详情，点击返回并等待个人基地地图");
            await _automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
            if (await Wait(false) is not null) return;
        }
        throw new InvalidOperationException(L.Text("未能确认战舰巡航已返回个人基地。"));
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
    public Task<BattleshipHarvestResult> HarvestBattleshipAsync(
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
        // 截图可能同步完成；将整段识别调度到线程池，避免全屏匹配占用 WPF 消息循环。
        => Task.Run(async () =>
    {
        var navBase = mainLayout.Require("navBase", "主界面布局缺少 navBase 锚点");
        var claimAll = baseLayout.Require("claimAll", "个人基地布局缺少 claimAll 锚点");

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
    }, ct);
}

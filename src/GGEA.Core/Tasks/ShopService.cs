using System.IO;
using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>
/// 商店服务：目前实现“每日任务 - 于任一商店购物”。
/// 流程：主界面 → 商店 → 高级商店（带红点入口）→ 购买免费培育包 → 确认购买 → 关闭结果弹窗。
/// 商品价格与按钮在固定 ROI 内同帧确认，连续两帧通过后才允许点击实际命中的购买位置。
/// </summary>
public sealed class ShopService
{
    private readonly AutomationService _automation;
    private readonly DiamondSafetyChecker _diamondChecker;
    private readonly IGgeaLog _log;

    public ShopService(AutomationService automation, DiamondSafetyChecker diamondChecker, IGgeaLog log)
    {
        _automation = automation;
        _diamondChecker = diamondChecker;
        _log = log;
    }

    /// <summary>
    /// 购买高级商店中的免费商品（完成每日任务“于任一商店购物”）。
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="mainLayout">主界面布局（用于商店入口 navShop）。</param>
    /// <param name="shopLayout">商店布局（高级入口、免费商品购买、确认购买、结果关闭）。</param>
    /// <param name="freeBuyTemplatePath">免费商品“购买”按钮模板。</param>
    /// <param name="freeTextTemplatePath">完整“钻石＋免费”价格栏模板，不能使用单独文字图片。</param>
    /// <param name="confirmBuyTemplatePath">确认弹窗“购买”按钮模板。</param>
    /// <param name="resultCloseTemplatePath">结果弹窗“关闭”按钮模板。</param>
    /// <param name="diamondIconTemplatePath">钻石图标模板（安全锁第一步检测）。</param>
    /// <param name="stepDelayMs">点击后的页面切换等待时长。</param>
    /// <param name="networkSettleDelayMs">网络请求加载完成的稳定等待时长。</param>
    /// <param name="waitTimeoutMs">等待按钮出现的总超时。</param>
    /// <param name="threshold">模板匹配置信度阈值。</param>
    /// <returns>流程是否正常结束；没有可购买的免费礼包时会安全略过并返回 true。</returns>
    public async Task<bool> BuyFreePremiumItemAsync(
        string serial,
        UiLayout mainLayout,
        UiLayout shopLayout,
        string freeBuyTemplatePath,
        string freeTextTemplatePath,
        string confirmBuyTemplatePath,
        string resultCloseTemplatePath,
        string diamondIconTemplatePath,
        int stepDelayMs = 2500,
        int networkSettleDelayMs = 2500,
        int waitTimeoutMs = GameTiming.RecognitionTimeoutMs,
        double threshold = 0.85,
        CancellationToken ct = default)
    {
        var navShop = mainLayout.Find("navShop")
            ?? throw new InvalidOperationException("主界面布局缺少 navShop 锚点");
        var premiumTab = shopLayout.Find("premiumTab")
            ?? throw new InvalidOperationException("商店布局缺少 premiumTab 锚点");
        var freeBuy = shopLayout.Find("freeItemBuy")
            ?? throw new InvalidOperationException("商店布局缺少 freeItemBuy 锚点");
        var freePriceRegion = shopLayout.Find("freeItemPriceRegion")
            ?? throw new InvalidOperationException("商店布局缺少 freeItemPriceRegion 锚点");
        // 通用完整关闭按钮在底部搜索，旧文字锚点不再限制模板尺寸。
        var resultClose = RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, shopLayout.ScreenWidth, shopLayout.ScreenHeight);

        // 1. 从主界面进入商店
        _log.Info($"进入商店: ({navShop.CenterX}, {navShop.CenterY})");
        await _automation.TapAsync(serial, navShop.CenterX, navShop.CenterY, ct);
        await Task.Delay(stepDelayMs, ct);

        // 2. 点击“高级商店”入口（固定位置，通常带红点）
        _log.Info($"点击高级商店: ({premiumTab.CenterX}, {premiumTab.CenterY})");
        await _automation.TapAsync(serial, premiumTab.CenterX, premiumTab.CenterY, ct);
        await Task.Delay(stepDelayMs, ct);

        // 3. 价格和购买按钮共享截图；连续两帧确认，避免列表重排后沿用旧的免费结果。
        var recognizer = new ShopFreePurchaseRecognizer(
            freeTextTemplatePath, freeBuyTemplatePath);
        TemplateMatchResult? previous = null;
        TemplateMatchResult? authorizedBuy = null;
        // 免费商品检测使用完整等待预算，容纳慢加载与双帧确认；结果弹窗沿用同一上限，最终授权只重取完整校验通过但过期的帧。
        var detectionTimeoutMs = Math.Max(0, waitTimeoutMs);
        var deadline = Environment.TickCount64 + detectionTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var png = await _automation.ScreenshotAsync(serial, ct);
            var match = await Task.Run(() => recognizer.Find(png, freePriceRegion.Roi, freeBuy.Roi, threshold), ct);
            ct.ThrowIfCancellationRequested();
            // 截图在截止前已开始；MuMu 原生通道单帧可能耗时数秒，仍须评估刚返回的有效帧，
            // 否则第二帧即使再次确认免费也会因截图完成越过截止时间而被丢弃。
            if (match is not null && previous is not null &&
                Math.Abs(match.CenterX - previous.CenterX) <= 2 && Math.Abs(match.CenterY - previous.CenterY) <= 2)
            {
                authorizedBuy = match;
                break;
            }
            previous = match;
            await Task.Delay((int)Math.Clamp(deadline - Environment.TickCount64, 0, previous is not null ? GameTiming.ConfirmationIntervalMs : GameTiming.RecognitionIntervalMs), ct);
        }
        if (authorizedBuy is null)
        {
            // 免费礼包可能已经领取或当前没有上架；只要安全返回主界面，任务流程就算正常结束。
            _log.Info($"{detectionTimeoutMs} ms 内没有可购买的免费商品，安全略过并继续后续任务");
            await ReturnToMainAsync(serial, mainLayout, networkSettleDelayMs, ct);
            return true;
        }

        await _automation.TapAsync(serial, authorizedBuy.CenterX, authorizedBuy.CenterY, ct);
        _log.Info($"已确认钻石价格为免费，点击购买: ({authorizedBuy.CenterX},{authorizedBuy.CenterY})");
        await Task.Delay(networkSettleDelayMs, ct);

        // 最终授权独立于前置商品免费识别；仅过期允许有限重取，且清空连续帧证据。
        var finalRecognizer = new ShopConfirmationRecognizer(_diamondChecker);
        PurchaseEvidence? previousEvidence = null;
        const int maxConfirmationFrames = 6;
        for (var frame = 0; frame < maxConfirmationFrames; frame++)
        {
            byte[]? png = null;
            var captureStarted = Environment.TickCount64;
            var captureFinished = captureStarted;
            PurchaseEvidence evidence;
            try
            {
                png = await _automation.ScreenshotAsync(serial, ct, retryOnFailure: false);
                captureFinished = Environment.TickCount64;
                evidence = await Task.Run(() => finalRecognizer.Evaluate(png, captureStarted,
                    shopLayout, confirmBuyTemplatePath, diamondIconTemplatePath), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (ConnectionErrorException) { throw; }
            catch (Exception ex)
            {
                if (png is null) captureFinished = Environment.TickCount64;
                evidence = new(PurchaseSafety.Unknown, $"最终确认截图失败（{ex.GetType().Name}）", null, captureStarted);
            }
            ct.ThrowIfCancellationRequested();
            _log.Info($"商店最终确认取证 {frame + 1}/{maxConfirmationFrames}：截图及解码 {captureFinished - captureStarted}ms，购买安全识别及调度 {Environment.TickCount64 - captureFinished}ms，有效期 {PurchaseEvidence.LifetimeMs}ms");
            if (!evidence.CanConfirm(Environment.TickCount64))
            {
                if ((evidence.Expired || evidence.Safety == PurchaseSafety.SafeFree) &&
                    frame < maxConfirmationFrames - 1)
                {
                    previousEvidence = null;
                    _log.Warn("商店最终确认截图证据过期，丢弃连续帧证据并重新截图；未执行购买");
                    continue;
                }
                SaveRejectedEvidence(png, evidence with
                {
                    Reason = evidence.Safety == PurchaseSafety.SafeFree ? "最终确认截图证据过期" : evidence.Reason,
                    Safety = evidence.Safety == PurchaseSafety.SafeFree ? PurchaseSafety.Unknown : evidence.Safety,
                });
                // 停在当前页面，不再盲点导航坐标；弹窗变化后导航位置也可能成为购买按钮。
                return false;
            }
            if (previousEvidence is not null)
            {
                if (evidence.Button!.Region != previousEvidence.Button!.Region)
                {
                    SaveRejectedEvidence(png, evidence with { Safety = PurchaseSafety.Unknown, Reason = "最终按钮在两帧间移动" });
                    return false;
                }
                // 授权与点击之间不再截图、等待或执行其他 UI 操作，只使用最新一帧命中的坐标。
                await _automation.TapAsync(serial, evidence.Button.CenterX, evidence.Button.CenterY, ct);
                _log.Info($"已按 SafeFree 证据点击确认购买: ({evidence.Button.CenterX},{evidence.Button.CenterY})");
                break;
            }
            if (frame == maxConfirmationFrames - 1)
            {
                SaveRejectedEvidence(png, evidence with { Safety = PurchaseSafety.Unknown,
                    Reason = "最终确认取证次数已达上限，未取得连续两帧有效证据" });
                return false;
            }
            previousEvidence = evidence;
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        }
        await Task.Delay(networkSettleDelayMs, ct);

        // 6. 等待结果弹窗并点击关闭
        if (!await ClickWhenAppearsAsync(
                serial, resultCloseTemplatePath, resultClose, "结果弹窗关闭按钮", waitTimeoutMs, threshold, ct))
        {
            await ReturnToMainAsync(serial, mainLayout, networkSettleDelayMs, ct);
            return false;
        }

        // 等待关闭结果弹窗后的页面加载完成，再返回主界面。
        await Task.Delay(networkSettleDelayMs, ct);
        await ReturnToMainAsync(serial, mainLayout, networkSettleDelayMs, ct);

        _log.Info("商店购物完成，已返回主界面");
        return true;
    }

    /// <summary>保留本次被拒绝的原始截图和原因；保存失败也绝不恢复点击。</summary>
    private void SaveRejectedEvidence(byte[]? png, PurchaseEvidence evidence)
    {
        // 证据不足只撤销本次购买，后续任务由调用方决定；不能在此误报整条流程失败。
        _log.Warn($"商店最终确认已停止：{evidence.Safety}，{evidence.Reason}；未执行购买");
        try
        {
            // 证据与普通日志放在同一程序目录下，避免反馈时遗漏用户数据目录中的截图。
            var directory = Path.Combine(AppContext.BaseDirectory, "logs", "shop");
            Directory.CreateDirectory(directory);
            var prefix = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
            if (png is not null) File.WriteAllBytes(prefix + ".png", png);
            File.WriteAllText(prefix + ".txt", $"{evidence.Safety}: {evidence.Reason}\n截图存在: {png is not null}");
            _log.Warn($"最终确认诊断已保存: {prefix}");
        }
        catch (Exception ex)
        {
            _log.Warn($"最终确认诊断保存失败（{ex.GetType().Name}），购买保持停止");
        }
    }

    /// <summary>点击主界面导航按钮并等待页面加载稳定。</summary>
    private async Task ReturnToMainAsync(
        string serial,
        UiLayout mainLayout,
        int networkSettleDelayMs,
        CancellationToken ct)
    {
        var navHome = mainLayout.Find("navHome")
            ?? throw new InvalidOperationException("主界面布局缺少 navHome 锚点");
        _log.Info($"返回主界面: ({navHome.CenterX},{navHome.CenterY})");
        await _automation.TapAsync(serial, navHome.CenterX, navHome.CenterY, ct);
        await Task.Delay(networkSettleDelayMs, ct);
    }

    /// <summary>在指定区域等待按钮出现，只点击实际匹配中心。</summary>
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

        await _automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
        _log.Info($"已点击{label}: ({match.CenterX},{match.CenterY})");
        return true;
    }
}

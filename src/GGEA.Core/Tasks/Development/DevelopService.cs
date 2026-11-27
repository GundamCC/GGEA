using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>
/// 开发服务：实现“开发&强化一次”完整流程。
/// 进入第一个开发路线图 → 查找目标机体 → 点击全部开发 → 两层确认 → 跳过立绘 → 返回主界面；
/// “全部开发”按钮为灰色禁用状态（今日已开发或无可用开发）时视为任务完成并安全跳过。
/// 保留游戏缩放，通过标准 ADB 拖动逐行或逐列搜索，连续两帧确认后才点击。
/// </summary>
public sealed partial class DevelopService : IDevelopService
{
    /// <summary>
    /// 开发入口未正常载入时的恢复点击位置。
    /// 等待首个路线图超时后点击该位置，可关闭阻塞层并重新触发开发页载入。
    /// </summary>
    private const int RoadmapRecoveryTapX = 688;
    private const int RoadmapRecoveryTapY = 342;

    /// <summary>关闭首次进入路线图时的显示设置页后，等待路线图完成绘制。</summary>
    private const int RoadmapDisplaySettingsSettleDelayMs = 1500;

    private readonly AutomationService _automation;
    private readonly IGgeaLog _log;
    private readonly TaskNavigationActions _navigation;
    // 会话内固定识别策略；旧自定义截图模板保留历史多尺度兼容行为。
    private readonly bool _fixedUnitScales;

    /// <summary>开发流程只依赖选中设备的截图与标准 ADB 输入。</summary>
    public DevelopService(AutomationService automation, IGgeaLog log, bool fixedUnitScales = false)
    {
        _automation = automation;
        _log = log;
        _navigation = new TaskNavigationActions(automation, log);
        _fixedUnitScales = fixedUnitScales;
    }

    /// <summary>
    /// 开发&强化一次：找到并点击目标机体后，点击右下角“全部开发”按钮。
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="mainLayout">主界面布局。</param>
    /// <param name="developLayout">开发页布局（第一个路线图、全部开发）。</param>
    /// <param name="roadmapFirstTemplatePath">第一个路线图模板。</param>
    /// <param name="msTemplatePath">目标机体模板。</param>
    /// <param name="msLargeTemplatePath">目标机体大图模板（与小图共同覆盖不同缩放比例）；为空表示不启用。</param>
    /// <param name="developAllTemplatePath">“全部开发”按钮模板。</param>
    /// <param name="developAllDisabledTemplatePath">“全部开发”按钮灰色禁用状态模板；为空表示不检测禁用状态。</param>
    /// <param name="executeDevelopTemplatePath">确认弹窗“执行开发”按钮模板。</param>
    /// <param name="executeConfirmTemplatePath">第二层确认弹窗“执行”按钮模板。</param>
    /// <param name="showcaseTapToNextTemplatePath">机体立绘底部 TAP TO NEXT 模板。</param>
    /// <param name="backButtonTemplatePath">开发路线图左上角返回按钮模板。</param>
    /// <param name="developTitleTemplatePath">开发页左上角页面标题模板；为空表示不识别标题。</param>
    /// <param name="networkSettleDelayMs">每次点击后等待网络加载完成的稳定时长。</param>
    /// <param name="titleThreshold">开发页标题匹配置信度阈值。</param>
    /// <param name="titleWaitMs">进入开发页后等待标题出现的总超时。</param>
    /// <param name="roadmapDisplaySettingsTitleTemplatePath">首次进入路线图时“开发路线图显示设置”标题模板；为空表示不处理。</param>
    /// <returns>是否完成开发。</returns>
    public async Task<bool> DevelopAndEnhanceOnceAsync(
        string serial,
        UiLayout mainLayout,
        UiLayout developLayout,
        string roadmapFirstTemplatePath,
        string msTemplatePath,
        string msLargeTemplatePath,
        string developAllTemplatePath,
        string developAllDisabledTemplatePath,
        string executeDevelopTemplatePath,
        string executeConfirmTemplatePath,
        string showcaseTapToNextTemplatePath,
        string backButtonTemplatePath,
        string developTitleTemplatePath,
        int maxDragsPerDirection = 0,
        int dragDurationMs = 700,
        double threshold = 0.86,
        int stepDelayMs = 2500,
        int developWaitTimeoutMs = 30000,
        int networkSettleDelayMs = 3000,
        double titleThreshold = 0.85,
        int titleWaitMs = GameTiming.ShortRecognitionTimeoutMs,
        string roadmapDisplaySettingsTitleTemplatePath = "",
        CancellationToken ct = default)
    {
        // 兼容既有调用：每日任务将无可用开发视为成功跳过。
        var result = await ExecuteAsync(serial, new DevelopRequest
        {
            MainLayout = mainLayout,
            DevelopLayout = developLayout,
            RoadmapFirstTemplatePath = roadmapFirstTemplatePath,
            MsTemplatePath = msTemplatePath,
            MsLargeTemplatePath = msLargeTemplatePath,
            DevelopAllTemplatePath = developAllTemplatePath,
            DevelopAllDisabledTemplatePath = developAllDisabledTemplatePath,
            ExecuteDevelopTemplatePath = executeDevelopTemplatePath,
            ExecuteConfirmTemplatePath = executeConfirmTemplatePath,
            ShowcaseTapToNextTemplatePath = showcaseTapToNextTemplatePath,
            BackButtonTemplatePath = backButtonTemplatePath,
            DevelopTitleTemplatePath = developTitleTemplatePath,
            MaxDragsPerDirection = maxDragsPerDirection,
            DragDurationMs = dragDurationMs,
            Threshold = threshold,
            StepDelayMs = stepDelayMs,
            DevelopWaitTimeoutMs = developWaitTimeoutMs,
            NetworkSettleDelayMs = networkSettleDelayMs,
            TitleThreshold = titleThreshold,
            TitleWaitMs = titleWaitMs,
            RoadmapDisplaySettingsTitleTemplatePath = roadmapDisplaySettingsTitleTemplatePath,
        }, ct);
        return result is DevelopResult.Completed or DevelopResult.Unavailable;
    }

    /// <summary>共享导航与目标选择阶段；每日开发和工厂使用相同的页面识别、拖动和稳定确认逻辑。</summary>
    public Task<TemplateMatchResult?> FindAndTapAsync(string serial, DevelopRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return FindAndTapMsAsync(serial, request.MainLayout, request.DevelopLayout,
            request.RoadmapFirstTemplatePath, request.MsTemplatePath, request.MsLargeTemplatePath,
            request.DevelopTitleTemplatePath, request.MaxDragsPerDirection, request.DragDurationMs,
            request.Threshold, request.StepDelayMs, request.NetworkSettleDelayMs,
            request.TitleThreshold, request.TitleWaitMs, request.RoadmapDisplaySettingsTitleTemplatePath, ct);
    }

    /// <summary>共享开发实现；不读取窗口或全局配置，由请求明确提供单次执行所需参数。</summary>
    public async Task<DevelopResult> ExecuteAsync(string serial, DevelopRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var mainLayout = request.MainLayout;
        var developLayout = request.DevelopLayout;
        var developAllTemplatePath = request.DevelopAllTemplatePath;
        var developAllDisabledTemplatePath = request.DevelopAllDisabledTemplatePath;
        var backButtonTemplatePath = request.BackButtonTemplatePath;
        var developTitleTemplatePath = request.DevelopTitleTemplatePath;
        var developWaitTimeoutMs = request.DevelopWaitTimeoutMs;
        var networkSettleDelayMs = request.NetworkSettleDelayMs;
        var titleThreshold = request.TitleThreshold;
        // 1. 找到并点击目标机体
        var ms = await FindAndTapAsync(serial, request, ct);
        if (ms is null)
        {
            _log.Warn("未找到目标机体，开发中止");
            await RecognizePageTitleOnceAsync(serial, developTitleTemplatePath, titleThreshold, ct);
            return DevelopResult.Failed;
        }

        // 2. 在固定区域轮询“全部开发”按钮状态：
        //    亮色 = 可开发；灰色禁用 = 今日已开发或无可用开发，视为任务完成并安全跳过
        var developAll = developLayout.Require("developAll", "开发布局缺少 developAll 锚点");
        var roadmapBack = developLayout.Require("roadmapBack", "开发布局缺少 roadmapBack 锚点");
        var checkWait = new WaitOptions
        {
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = 0.85,
            Roi = developAll.Roi,
        };
        var deadline = Environment.TickCount64 + developWaitTimeoutMs;
        var developAllReady = false;
        var disabledStreak = 0;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var enabled = await _automation.FindImageAsync(serial, developAllTemplatePath, checkWait, ct);
            if (enabled is not null)
            {
                developAllReady = true;
                break;
            }

            if (!string.IsNullOrEmpty(developAllDisabledTemplatePath))
            {
                var disabled = await _automation.FindImageAsync(
                    serial,
                    developAllDisabledTemplatePath,
                    checkWait,
                    ct);
                if (disabled is not null)
                {
                    // 连续两次识别到灰色状态才判定无可用开发，避免网络加载过程中的瞬时误判
                    disabledStreak++;
                    if (disabledStreak >= 2)
                    {
                        _log.Warn("全部开发按钮为灰色（今日已开发或无可用开发），跳过开发");
                        await ReturnToMainAsync(serial, mainLayout, ct);
                        return DevelopResult.Unavailable;
                    }
                }
                else
                {
                    disabledStreak = 0;
                }
            }

            // 已发现灰色候选后按确认间隔复核新帧；不能再等待五秒而耗尽短阶段预算。
            // 未命中仍保持普通轮询，连续两帧、总超时及用户取消条件不变。
            await Task.Delay(disabledStreak > 0 ? GameTiming.ConfirmationIntervalMs : checkWait.PollIntervalMs, ct);
        }

        if (!developAllReady)
        {
            _log.Warn("未检测到全部开发按钮，流程中断");
            await RecognizePageTitleOnceAsync(serial, developTitleTemplatePath, titleThreshold, ct);
            return DevelopResult.Failed;
        }

        await _automation.TapAsync(serial, developAll.CenterX, developAll.CenterY, ct);
        _log.Info($"已点击全部开发: ({developAll.CenterX},{developAll.CenterY})");
        if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);

        // 数量选择后的确认阶段由每日任务和工厂共用；失败不得继续返回或计入产出。
        var confirmation = await ConfirmAsync(serial, request, ct);
        // 资源不足由任务编排层统一执行两次取消及正常主界面恢复。
        if (confirmation != DevelopResult.Completed) return confirmation;

        // 6. 回到开发路线图后，点击左上角返回按钮
        var backWait = new WaitOptions
        {
            TimeoutMs = developWaitTimeoutMs,
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = 0.85,
            Roi = roadmapBack.Roi,
        };
        var backMatch = await _automation.WaitForImageAsync(serial, backButtonTemplatePath, backWait, ct);
        if (backMatch is null)
        {
            _log.Warn("未检测到路线图返回按钮，流程中断");
            await RecognizePageTitleOnceAsync(serial, developTitleTemplatePath, titleThreshold, ct);
            return DevelopResult.Failed;
        }

        await _automation.TapAsync(serial, roadmapBack.CenterX, roadmapBack.CenterY, ct);
        _log.Info($"已点击返回按钮: ({roadmapBack.CenterX},{roadmapBack.CenterY})");

        // 7. 返回开发列表后继续回到主界面，不在开发界面停留
        await ReturnToMainAsync(serial, mainLayout, ct);
        return DevelopResult.Completed;
    }

    /// <summary>从已选定数量的开发详情页继续两层确认与立绘跳过；停在路线图，不返回主界面。</summary>
    public Task<DevelopResult> ConfirmAsync(string serial, DevelopRequest request, CancellationToken ct = default) =>
        ConfirmAsync(serial, request, null, ct);

    internal async Task<DevelopResult> ConfirmAsync(string serial, DevelopRequest request, Func<bool>? reserve,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var executeDevelopTemplatePath = request.ExecuteDevelopTemplatePath;
        var executeConfirmTemplatePath = request.ExecuteConfirmTemplatePath;
        var developTitleTemplatePath = request.DevelopTitleTemplatePath;
        var titleThreshold = request.TitleThreshold;
        var developWaitTimeoutMs = request.DevelopWaitTimeoutMs;
        var networkSettleDelayMs = request.NetworkSettleDelayMs;
        // 所有确认素材在第一次消费性点击之前检查，避免做到一半才发现文件缺失。
        foreach (var path in new[] { executeDevelopTemplatePath, executeConfirmTemplatePath })
            if (!File.Exists(path)) throw new FileNotFoundException("缺少开发确认素材。", path);
        _automation.TapToNext.Validate(); // 消费前校验离线模型，避免执行后才发现 OCR 不可用。
        // 3. 确认弹窗中点击“执行开发”
        // 数量调整已结束，重新截图；两种任务共用暗态判定，不能沿用调整前的亮态证据。
        var button = await DevelopExecutionButton.WaitAsync(_automation, serial,
            executeDevelopTemplatePath, developWaitTimeoutMs, ct);
        if (button.State is DevelopButtonState.UnitLimitReached or DevelopButtonState.InsufficientCoins)
        {
            var shortage = button.State == DevelopButtonState.InsufficientCoins
                ? DevelopResult.InsufficientCoins : DevelopResult.UnitLimitReached;
            _log.Warn($"{shortage.GetBlockedReason()}，已停止本次开发，未点击执行开发");
            return shortage;
        }
        var executeMatch = button.Match;
        if (executeMatch is null)
        {
            _log.Warn("未检测到执行开发按钮，流程中断");
            await RecognizePageTitleOnceAsync(serial, developTitleTemplatePath, titleThreshold, ct);
            return DevelopResult.Failed;
        }

        if (reserve is not null && !reserve()) return DevelopResult.Unavailable;
        await _automation.TapAsync(serial, executeMatch.CenterX, executeMatch.CenterY, ct);
        _log.Info($"已点击执行开发: ({executeMatch.CenterX},{executeMatch.CenterY})");
        if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);

        // 4. 第二层确认弹窗“确认执行开发”中的“执行”按钮
        var executeConfirmMatch = await DevelopConfirmationWait.WaitAsync(_automation, serial,
            executeDevelopTemplatePath, executeConfirmTemplatePath, developWaitTimeoutMs, _log.Info, ct);
        if (executeConfirmMatch is null)
        {
            _log.Warn("未检测到第二层确认的执行按钮，流程中断");
            await RecognizePageTitleOnceAsync(serial, developTitleTemplatePath, titleThreshold, ct);
            return DevelopResult.Failed;
        }

        await _automation.TapAsync(serial, executeConfirmMatch.CenterX, executeConfirmMatch.CenterY, ct);
        _log.Info($"已点击第二层确认执行: ({executeConfirmMatch.CenterX},{executeConfirmMatch.CenterY})");
        if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);

        // 5. 开发完成后的机体立绘展示：底部出现 TAP TO NEXT，点击一次跳过
        var showcaseWait = new WaitOptions
        {
            TimeoutMs = developWaitTimeoutMs,
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            // 与全局共用完整文字 OCR；本阶段保留独立点击与完成真值。
            Threshold = GlobalScreenHandler.TapToNextThreshold,
            Region = RecognitionRegion.BottomButtons,
            AllowGlobalContinuation = false,
        };
        var showcaseMatch = await _automation.WaitForTapToNextAsync(
            serial,
            showcaseWait,
            ct);
        if (showcaseMatch is null)
        {
            _log.Warn("未检测到机体立绘的 TAP TO NEXT，流程中断");
            await RecognizePageTitleOnceAsync(serial, developTitleTemplatePath, titleThreshold, ct);
            return DevelopResult.Failed;
        }

        await _automation.TapAsync(serial, showcaseMatch.CenterX, showcaseMatch.CenterY, ct);
        _log.Info($"已点击 TAP TO NEXT: ({showcaseMatch.CenterX},{showcaseMatch.CenterY})");
        if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);

        return DevelopResult.Completed;
    }

    /// <summary>
    /// 从开发页任意位置返回主界面。
    /// 开发页底部导航栏常驻（navHome），因此无论当前在机体详情页还是路线图，
    /// 都可以直接点击底部“主界面”入口安全返回。
    /// </summary>
    private async Task ReturnToMainAsync(string serial, UiLayout mainLayout, CancellationToken ct)
    {
        await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
        var navHome = mainLayout.Require("navHome", "主界面布局缺少 navHome 锚点");
        await _navigation.TapHomeAsync(serial, navHome, ct);
        await Task.Delay(1500, ct);
    }
}

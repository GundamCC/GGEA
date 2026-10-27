using GGEA.App.Automation;
using System.IO;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>
/// 开发服务：实现“开发&强化一次”完整流程。
/// 进入第一个开发路线图 → 查找目标机体 → 点击全部开发 → 两层确认 → 跳过立绘 → 返回主界面；
/// “全部开发”按钮为灰色禁用状态（今日已开发或无可用开发）时视为任务完成并安全跳过。
/// 保留游戏缩放，通过标准 ADB 拖动逐行搜索，连续两帧确认后才点击。
/// </summary>
public sealed class DevelopService : IDevelopService
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
    // 会话内固定识别策略；旧自定义截图模板保留历史多尺度兼容行为。
    private readonly bool _fixedUnitScales;

    /// <summary>开发流程只依赖选中设备的截图与标准 ADB 输入。</summary>
    public DevelopService(AutomationService automation, IGgeaLog log, bool fixedUnitScales = false)
    {
        _automation = automation;
        _log = log;
        _fixedUnitScales = fixedUnitScales;
    }

    /// <summary>
    /// 进入开发页 → 点击第一个路线图 → 查找并点击目标机体。
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="mainLayout">主界面布局（用于开发入口 navDevelop）。</param>
    /// <param name="developLayout">开发页布局（第一个路线图位置）。</param>
    /// <param name="roadmapFirstTemplatePath">第一个路线图模板（用于确认已进入开发页）。</param>
    /// <param name="msTemplatePath">目标机体模板。</param>
    /// <param name="msLargeTemplatePath">目标机体大图模板（与小图共同覆盖不同缩放比例）；为空表示不启用。</param>
    /// <param name="developTitleTemplatePath">开发页左上角页面标题模板；为空表示不识别标题。</param>
    /// <param name="maxDragsPerDirection">每个方向的最大拖动次数。</param>
    /// <param name="dragDurationMs">单次拖动时长。</param>
    /// <param name="threshold">机体匹配置信度阈值。</param>
    /// <param name="stepDelayMs">点击后的页面切换等待时长。</param>
    /// <param name="networkSettleDelayMs">点击机体后等待网络加载完成的稳定时长。</param>
    /// <param name="titleThreshold">开发页标题匹配置信度阈值。</param>
    /// <param name="titleWaitMs">进入开发页后等待标题出现的总超时。</param>
    /// <param name="roadmapDisplaySettingsTitleTemplatePath">首次进入路线图时“开发路线图显示设置”标题模板；为空表示不处理。</param>
    /// <returns>找到并点击后返回匹配结果；未找到返回 null。</returns>
    public async Task<TemplateMatchResult?> FindAndTapMsAsync(
        string serial,
        UiLayout mainLayout,
        UiLayout developLayout,
        string roadmapFirstTemplatePath,
        string msTemplatePath,
        string msLargeTemplatePath,
        string developTitleTemplatePath,
        int maxDragsPerDirection = 10,
        int dragDurationMs = 700,
        double threshold = 0.86,
        int stepDelayMs = 2500,
        int networkSettleDelayMs = 3000,
        double titleThreshold = 0.85,
        int titleWaitMs = GameTiming.ShortRecognitionTimeoutMs,
        string roadmapDisplaySettingsTitleTemplatePath = "",
        CancellationToken ct = default)
    {
        var navDevelop = mainLayout.Find("navDevelop")
            ?? throw new InvalidOperationException("主界面布局缺少 navDevelop 锚点");
        var firstRoadmap = developLayout.Find("firstRoadmap")
            ?? throw new InvalidOperationException("开发布局缺少 firstRoadmap 锚点");

        // 1. 从主界面进入开发页
        _log.Info($"进入开发页: ({navDevelop.CenterX}, {navDevelop.CenterY})");
        await _automation.TapAsync(serial, navDevelop.CenterX, navDevelop.CenterY, ct);
        await Task.Delay(stepDelayMs, ct);

        // 2. 确认开发页并点击第一个路线图
        var roadmapWait = new WaitOptions
        {
            TimeoutMs = GameTiming.ShortRecognitionTimeoutMs,
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = 0.85,
            Roi = firstRoadmap.Roi,
        };
        var roadmapFound = await _automation.WaitForImageAsync(
            serial,
            roadmapFirstTemplatePath,
            roadmapWait,
            ct,
            matchTarget: (screen, token) => MatchFirstRoadmapAsync(screen, roadmapFirstTemplatePath, roadmapWait, token));
        if (roadmapFound is null)
        {
            // 入口偶发停留在未完整载入的中间页面；恢复点击会直接进入路线图页，
            // 因此随后必须用路线图标题确认页面，而不能再次等待入口卡片模板。
            _log.Warn(
                $"未检测到开发路线图，点击 ({RoadmapRecoveryTapX},{RoadmapRecoveryTapY}) 后检测路线图标题");
            await _automation.TapAsync(serial, RoadmapRecoveryTapX, RoadmapRecoveryTapY, ct);
            await Task.Delay(stepDelayMs, ct);
        }
        else
        {
            _log.Info($"点击第一个路线图: ({firstRoadmap.CenterX}, {firstRoadmap.CenterY})");
            await _automation.TapAsync(serial, firstRoadmap.CenterX, firstRoadmap.CenterY, ct);
            await Task.Delay(stepDelayMs, ct);
        }

        // 3. 识别路线图页左上角标题，确认已进入开发路线图（标题模板为空时跳过）
        if (!await WaitForDevelopTitleAsync(
                serial,
                developTitleTemplatePath,
                developLayout,
                titleThreshold,
                titleWaitMs,
                roadmapDisplaySettingsTitleTemplatePath,
                ct))
        {
            _log.Warn("未确认开发路线图页标题，流程中断");
            return null;
        }

        var match = await FindOnRoadmapAsync(serial, msTemplatePath, msLargeTemplatePath,
            maxDragsPerDirection, dragDurationMs, threshold, developTitleTemplatePath, titleThreshold, ct);
        if (match is not null)
        {
            await _automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
            if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);
        }
        return match;
    }

    /// <summary>同帧优先识别原路线图，仅在未命中时补试未完成状态；共用入口 ROI、阈值与等待预算。</summary>
    internal async Task<TemplateMatchResult?> MatchFirstRoadmapAsync(ScreenImage screen, string templatePath,
        WaitOptions wait, CancellationToken ct)
    {
        var primary = await _automation.MatchImageAsync(screen, templatePath, wait, ct);
        if (primary is not null || !Path.GetFileName(templatePath).Equals("dev_0079.png", StringComparison.OrdinalIgnoreCase))
            return primary;

        // 只扩展 0079 卡片；自定义目录沿用同目录素材，不让其他路线图误用内置替补。
        var unfinished = ConfigService.ResolvePath(Path.Combine(Path.GetDirectoryName(templatePath) ?? "", "dev_0079_unfinished.png"));
        if (!File.Exists(unfinished)) return null;
        return await _automation.MatchImageAsync(screen, unfinished, wait, ct);
    }

    /// <summary>
    /// 在已打开的路线图上搜索，返回连续两帧稳定目标但不点击，供实际画面验证复用。
    /// 官方单位先同帧检查两张放大立绘，另一单位命中时最多定向拖动两次，每次识别；失败后归位并逐行扫描。
    /// 固定工具栏不参与识别或边界比较。步长保留重叠，避免大比例机体跨屏漏检。
    /// </summary>
    public async Task<TemplateMatchResult?> FindOnRoadmapAsync(string serial,
        string msTemplatePath, string msLargeTemplatePath, int maxDragsPerDirection = 10,
        int dragDurationMs = 700, double threshold = 0.86, string titleTemplatePath = "",
        double titleThreshold = 0.85, CancellationToken ct = default)
    {
        if (maxDragsPerDirection is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(maxDragsPerDirection));
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (dragDurationMs is < 100 or > 5000) dragDurationMs = Math.Clamp(dragDurationMs, 100, 5000);
        // 透明立绘使用 RGB 相关性，恢复原有 0.86 下限；同时拒绝分数接近的其它位置。
        var wait = new WaitOptions { Threshold = Math.Max(0.86, threshold), Roi = new Roi(360, 240, 1520, 700), RoadmapFixedScales = _fixedUnitScales };
        // 总时间独立于步数；异常、取消、未知页面均停止，不把未找到当作完成开发。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        byte[]? previous = null;
        byte[]? inspectedFrame = null;
        TemplateMatchResult? result = null;

        async Task CaptureAsync()
        {
            token.ThrowIfCancellationRequested();
            previous = await _automation.ScreenshotAsync(serial, token);
            var image = ImageCodec.DecodePng(previous);
            if (image.Width != 1920 || image.Height != 1080)
                throw new InvalidOperationException("开发路线图需要 1920×1080 游戏画面，请先调整模拟器分辨率");
            if (!string.IsNullOrEmpty(titleTemplatePath) &&
                await _automation.MatchImageAsync(previous, titleTemplatePath,
                    new WaitOptions { Threshold = titleThreshold, Roi = new Roi(200, 0, 650, 150) }, token) is null)
                throw new InvalidOperationException("路线图标题已消失，停止拖动与点击");
        }

        async Task<bool> InspectAsync()
        {
            // 确认帧若发生比例切换，复用新帧再搜索一次，不能把尚未检索的新比例拖出视野。
            // 正常未命中立即返回；仅不稳定候选触发这一次有限重试。
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // 只复用字节完全一致且未确认目标的已检索帧，不用宽松的位移容差跳过新节点。
                if (inspectedFrame is not null && previous!.AsSpan().SequenceEqual(inspectedFrame)) return false;
                inspectedFrame = previous;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var found = await FindMsMatchAsync(previous!, msLargeTemplatePath, msTemplatePath, wait, token);
                _log.Info($"路线图单帧检索耗时 {elapsed.Elapsed.TotalMilliseconds:F0}ms，{(found is null ? "未命中" : "发现候选")}");
                if (found is null) return false;
                if (!await IsMatchStableAsync(async () => { await CaptureAsync(); return previous!; },
                        found.Value.TemplatePath, found.Value.Match, wait, token)) continue;
                result = found.Value.Match;
                _log.Info($"目标机体已稳定匹配: {result}");
                return true;
            }
            return false;
        }

        // 截图相似只说明未观察到位移，不能用一次未生效的手势直接授权换行或结束扫描。
        async Task<(bool Moved, bool Found)> MoveAsync(int dx, int dy)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var before = previous!;
                // 换一个画布内的起点复核同一方向，避开原起点可能覆盖的节点/控件；不改变游戏缩放。
                var sx = dx == 0 ? 1550 - attempt * 80 : 1140 - dx / 2;
                var sy = dy == 0 ? 840 - attempt * 50 : 590 - dy / 2;
                _log.Info($"路线图 ADB 拖动: ({sx},{sy}) → ({sx + dx},{sy + dy})");
                await _automation.SwipeAsync(serial, sx, sy, sx + dx, sy + dy, dragDurationMs, token);
                await Task.Delay(GameTiming.RecognitionIntervalMs, token);
                await CaptureAsync();
                if (!IsNearlySame(before, previous!))
                {
                    _log.Info($"路线图拖动后已观察到画布位移（第 {attempt + 1} 次手势）");
                    return (true, false);
                }
                // 稀疏节点、加载或粒子容差可能掩盖真实变化；本帧必须识别后才能继续拖动。
                if (await InspectAsync()) return (false, true);
                if (attempt == 0) _log.Info("路线图未观察到明显位移，换起点复核同一方向一次");
            }
            _log.Info("路线图同方向两次拖动均未观察到明显位移，按当前边界换向或结束搜索");
            return (false, false);
        }

        await CaptureAsync();
        // 两张大图共用首次画面，不增加截图或改变游戏缩放；自定义目标不推断相邻关系。
        var shortcut = GetRoadmapShortcut(msTemplatePath, msLargeTemplatePath);
        var largeWait = wait with { RoadmapFixedScales = true, RoadmapLargeOnly = true };
        TemplateMatchResult? initialTarget = null;
        TemplateMatchResult? initialOther = null;
        if (shortcut is { } hint)
        {
            initialTarget = await _automation.MatchRoadmapImageAsync(previous!, msTemplatePath, largeWait, token);
            if (File.Exists(hint.OtherTemplate))
                initialOther = await _automation.MatchRoadmapImageAsync(previous!, hint.OtherTemplate, largeWait, token);
            else
                _log.Warn("路线图快捷方向素材缺失，保留原目标搜索流程");
        }
        // 保留首次大小两档目标识别：小图命中也直接确认，不能因辅助大图存在而拖走目标。
        if (await InspectAsync()) return result;
        if (shortcut is { } direction && initialTarget is null && initialOther is not null)
        {
            var otherStable = await IsMatchStableAsync(async () => { await CaptureAsync(); return previous!; },
                direction.OtherTemplate, initialOther, largeWait, token);
            // 确认帧可能已出现目标；辅助单位不稳定时也检查最新帧，再回退归位。
            if (await InspectAsync()) return result;
            if (otherStable)
            {
                // 快捷预算按实际手势计数，不复用带两次边界复核的 MoveAsync，避免扩成四次拖动。
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    var sx = 1140 - direction.Dx / 2;
                    var sy = 840 - attempt * 50;
                    _log.Info($"放大辅助单位已确认，向{(direction.Dx > 0 ? "右" : "左")}快捷拖动（{attempt + 1}/2）并识别目标");
                    await _automation.SwipeAsync(serial, sx, sy, sx + direction.Dx, sy, dragDurationMs, token);
                    await Task.Delay(GameTiming.RecognitionIntervalMs, token);
                    await CaptureAsync();
                    // 每次手势后都识别，即使像素未变也不沿用上一轮未命中结论。
                    inspectedFrame = null;
                    if (await InspectAsync()) return result;
                }
                _log.Info("两次快捷拖动后均未确认目标，继续原路线图归位与逐行搜索");
            }
        }
        var homingMoved = false;
        // 正常归位只检查标题和位移；疑似撞边的帧额外检查目标，不能把未检索的新画面跳过。
        _log.Info("路线图定位左上角：检查页面与位移，疑似边界先识别目标再复核");
        foreach (var (dx, dy) in new[] { (0, 250), (850, 0) })
        {
            var edge = false;
            for (var i = 0; i < maxDragsPerDirection; i++)
            {
                var move = await MoveAsync(dx, dy);
                if (move.Found) return result;
                homingMoved |= move.Moved;
                if (!move.Moved) { edge = true; break; }
            }
            if (!edge)
            {
                // 预算内最后一帧仍有机会命中，但不能在边界未确认时声称覆盖全图。
                if (await InspectAsync()) return result;
                _log.Warn("路线图归位达到拖动上限，未确认边界，停止搜索"); return null;
            }
        }
        // 只有完全未移动才复用初始未命中结论；远处的相似背景不能作为已扫描证据。
        if (homingMoved && await InspectAsync()) return result;
        var horizontal = -850;
        for (var row = 0; row < maxDragsPerDirection; row++)
        {
            var edge = false;
            for (var column = 0; column < maxDragsPerDirection; column++)
            {
                var move = await MoveAsync(horizontal, 0);
                if (move.Found) return result;
                if (!move.Moved) { edge = true; break; }
                if (await InspectAsync()) return result;
            }
            if (!edge) { _log.Warn("路线图横向搜索达到上限，停止搜索"); return null; }
            var advanced = await MoveAsync(0, -250);
            if (advanced.Found) return result;
            if (!advanced.Moved) { _log.Warn("路线图扫描结束，同方向两次拖动均无明显位移，未找到稳定目标机体"); return null; }
            if (await InspectAsync()) return result;
            horizontal = -horizontal;
            _log.Info($"路线图换至第 {row + 2} 行后反向扫描");
        }
        _log.Warn("路线图搜索达到行数上限，未找到目标机体");
        return null;
    }

    /// <summary>右/左指 ADB 手势方向：找原型高达向左，找铁球向右；仅官方路径使用该相邻关系。</summary>
    private static (string OtherTemplate, int Dx)? GetRoadmapShortcut(string target, string customLarge)
    {
        if (!string.IsNullOrEmpty(customLarge)) return null;
        var prototype = ConfigService.ResolvePath("resources/templates/develop/dev_unit/ogk_g0070u00100.png");
        var ball = ConfigService.ResolvePath("resources/templates/develop/dev_unit/ogk_g0010u00700.png");
        var resolved = Path.GetFullPath(ConfigService.ResolvePath(target));
        if (string.Equals(resolved, Path.GetFullPath(prototype), StringComparison.OrdinalIgnoreCase)) return (ball, -850);
        if (string.Equals(resolved, Path.GetFullPath(ball), StringComparison.OrdinalIgnoreCase)) return (prototype, 850);
        return null;
    }

    /// <summary>
    /// 默认以单张原生素材匹配大小两档，返回命中结果与来源，供下一帧重复确认。
    /// 仅在用户另配了辅助模板时先检查辅助项，不再依赖随包的旧大小截图。
    /// </summary>
    private async Task<(string TemplatePath, TemplateMatchResult Match)?> FindMsMatchAsync(
        byte[] frame,
        string msLargeTemplatePath,
        string msTemplatePath,
        WaitOptions wait,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(msLargeTemplatePath))
        {
            var large = await _automation.MatchRoadmapImageAsync(frame, msLargeTemplatePath, wait, ct);
            if (large is not null)
            {
                _log.Info($"命中目标机体大图模板: {large}");
                return (msLargeTemplatePath, large);
            }
        }

        var target = await _automation.MatchRoadmapImageAsync(frame, msTemplatePath, wait, ct);
        if (target is not null)
        {
            _log.Info($"命中目标机体素材: {target}");
            return (msTemplatePath, target);
        }

        return null;
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
        int maxDragsPerDirection = 10,
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
        var developAll = developLayout.Find("developAll")
            ?? throw new InvalidOperationException("开发布局缺少 developAll 锚点");
        var roadmapBack = developLayout.Find("roadmapBack")
            ?? throw new InvalidOperationException("开发布局缺少 roadmapBack 锚点");
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

            await Task.Delay(checkWait.PollIntervalMs, ct);
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
    public async Task<DevelopResult> ConfirmAsync(string serial, DevelopRequest request, CancellationToken ct = default)
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
        var navHome = mainLayout.Find("navHome")
            ?? throw new InvalidOperationException("主界面布局缺少 navHome 锚点");
        _log.Info($"返回主界面: ({navHome.CenterX},{navHome.CenterY})");
        await _automation.TapAsync(serial, navHome.CenterX, navHome.CenterY, ct);
        await Task.Delay(1500, ct);
    }

    /// <summary>
    /// 识别开发页左上角页面标题，确认已进入开发页。
    /// 标题模板未配置时跳过（返回 true，不阻断流程）。
    /// </summary>
    private async Task<bool> WaitForDevelopTitleAsync(
        string serial,
        string developTitleTemplatePath,
        UiLayout developLayout,
        double titleThreshold,
        int titleWaitMs,
        string roadmapDisplaySettingsTitleTemplatePath,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(developTitleTemplatePath))
        {
            _log.Info("未配置开发页标题模板，跳过标题识别");
            return true;
        }

        var titleAnchor = developLayout.Find("developTitle");
        var titleWait = new WaitOptions
        {
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = titleThreshold,
            Roi = titleAnchor?.Roi,
        };

        var settingsTitleAnchor = developLayout.Find("roadmapDisplaySettingsTitle");
        var settingsConfirm = developLayout.Find("roadmapDisplaySettingsConfirm");
        var settingsWait = new WaitOptions
        {
            PollIntervalMs = titleWait.PollIntervalMs,
            Threshold = titleThreshold,
            Roi = settingsTitleAnchor?.Roi,
        };
        var deadline = Environment.TickCount64 + titleWaitMs;
        var settingsHandled = false;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var match = await _automation.FindImageAsync(serial, developTitleTemplatePath, titleWait, ct);
            if (match is not null)
            {
                _log.Info($"已识别开发页标题: {match}");
                return true;
            }

            // 首次进入具体作品的路线图时，游戏会用全屏设置页阻挡真实路线图。
            // 只在专用标题模板命中后点击固定的底部确认锚点，避免误触其它弹窗。
            if (!settingsHandled && !string.IsNullOrEmpty(roadmapDisplaySettingsTitleTemplatePath))
            {
                var settingsMatch = await _automation.FindImageAsync(
                    serial,
                    roadmapDisplaySettingsTitleTemplatePath,
                    settingsWait,
                    ct);
                if (settingsMatch is not null)
                {
                    if (settingsConfirm is null)
                    {
                        throw new InvalidOperationException(
                            "开发布局缺少 roadmapDisplaySettingsConfirm 锚点");
                    }

                    _log.Info($"检测到开发路线图显示设置: {settingsMatch}");
                    await _automation.TapAsync(serial, settingsConfirm.CenterX, settingsConfirm.CenterY, ct);
                    _log.Info($"已点击开发路线图显示设置的确定按钮: ({settingsConfirm.CenterX},{settingsConfirm.CenterY})");
                    settingsHandled = true;
                    await Task.Delay(RoadmapDisplaySettingsSettleDelayMs, ct);
                    // 设置页占用了原等待时间；关闭后重新给真实路线图完整的加载窗口。
                    deadline = Environment.TickCount64 + titleWaitMs;
                    continue;
                }
            }

            await Task.Delay(titleWait.PollIntervalMs, ct);
        }

        _log.Warn($"未识别到开发页左上角标题（{titleWaitMs}ms），可能未进入开发页");
        return false;
    }

    /// <summary>
    /// 失败退出前识别一次当前页面左上角标题并记入日志，便于判断失败时停留在哪个页面。
    /// 标题模板未配置时跳过。
    /// </summary>
    private async Task RecognizePageTitleOnceAsync(
        string serial,
        string developTitleTemplatePath,
        double titleThreshold,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(developTitleTemplatePath))
        {
            _log.Warn("未配置开发页标题模板，失败时跳过标题识别");
            return;
        }

        var match = await _automation.FindImageAsync(
            serial,
            developTitleTemplatePath,
            new WaitOptions { Threshold = titleThreshold },
            ct);
        _log.Info(match is not null
            ? $"失败时页面标题仍可识别: {match}"
            : "失败时页面标题未识别到（可能已离开开发页）");
    }

    /// <summary>目标机体确认帧数：连续多帧同一位置命中才点击。</summary>
    private const int MsConfirmFrames = 2;

    /// <summary>目标机体确认帧允许的中心坐标漂移（像素），超过该距离视为不同目标。</summary>
    private const int MsConfirmCenterTolerancePx = 12;

    /// <summary>
    /// 确认目标机体匹配是否稳定：在后续帧中连续命中且中心位置基本一致。
    /// 拖动动画或页面切换过程中可能出现瞬时边界匹配，稳定确认可避免误点其他机体。
    /// </summary>
    private async Task<bool> IsMatchStableAsync(
        Func<Task<byte[]>> captureFrame,
        string msTemplatePath,
        TemplateMatchResult first,
        WaitOptions wait,
        CancellationToken ct)
    {
        var current = first;
        for (var i = 1; i < MsConfirmFrames; i++)
        {
            await Task.Delay(wait.PollIntervalMs, ct);
            // 确认帧也归调用方所有；失败时用最新帧比较位移，避免把消失的瞬时目标当成拖动。
            var next = await _automation.MatchRoadmapImageAsync(await captureFrame(), msTemplatePath, wait, ct);
            if (next is null ||
                Math.Abs(next.CenterX - current.CenterX) > MsConfirmCenterTolerancePx ||
                Math.Abs(next.CenterY - current.CenterY) > MsConfirmCenterTolerancePx ||
                Math.Abs(next.Width - current.Width) > MsConfirmCenterTolerancePx ||
                Math.Abs(next.Height - current.Height) > MsConfirmCenterTolerancePx)
            {
                _log.Warn($"目标机体确认帧不稳定（第 {i + 1}/{MsConfirmFrames} 帧），继续搜索");
                return false;
            }

            current = next;
        }

        return true;
    }

    /// <summary>回弹判定采样步长。</summary>
    private const int BounceSampleStep = 8;

    /// <summary>回弹判定单像素 RGB 通道差阈值，避免等亮度彩色内容位移被灰度比较忽略。</summary>
    private const int BounceChannelThreshold = 40;

    /// <summary>回弹判定允许的差异像素比例（粒子特效下画面并非逐像素一致）。</summary>
    private const double BounceMaxDiffRatio = 0.08;

    /// <summary>
    /// 比较两张截图是否“几乎相同”（用于判断回弹）。
    /// 路线图画面带机体粒子特效，因此允许 8% 以内的局部差异；
    /// 此结果只表示未观察到明显位移；稀疏节点可能仍变化，调用方必须先检索目标并有限复核。
    /// </summary>
    internal static bool IsNearlySame(byte[] pngA, byte[] pngB)
    {
        var imageA = ImageCodec.DecodePng(pngA);
        var imageB = ImageCodec.DecodePng(pngB);
        if (imageA.Width != imageB.Width || imageA.Height != imageB.Height)
        {
            return false;
        }

        var diffCount = 0;
        var total = 0;
        // 190×175 的旧网格会把 100px 小机体的变化摊薄到阈值以下；80px 网格保留局部位移证据。
        const int tileSize = 80;
        const int tileColumns = 19;
        const int tileRows = 9;
        var tileDiff = new int[tileColumns * tileRows];
        var tileTotal = new int[tileDiff.Length];
        for (var y = 240; y < Math.Min(940, imageA.Height); y += BounceSampleStep)
        {
            for (var x = 360; x < Math.Min(1880, imageA.Width); x += BounceSampleStep)
            {
                var offset = (y * imageA.Width + x) * 4;
                var tile = (y - 240) / tileSize * tileColumns + (x - 360) / tileSize;
                tileTotal[tile]++;
                if (Math.Abs(imageA.Bgra[offset] - imageB.Bgra[offset]) > BounceChannelThreshold ||
                    Math.Abs(imageA.Bgra[offset + 1] - imageB.Bgra[offset + 1]) > BounceChannelThreshold ||
                    Math.Abs(imageA.Bgra[offset + 2] - imageB.Bgra[offset + 2]) > BounceChannelThreshold)
                {
                    diffCount++;
                    tileDiff[tile]++;
                }

                total++;
            }
        }

        // 大画布可能只有一个节点移动；检查局部网格，避免其变化被全屏空白稀释。
        return total > 0 && (double)diffCount / total < BounceMaxDiffRatio &&
            !Enumerable.Range(0, tileDiff.Length).Any(i => tileTotal[i] > 0 && (double)tileDiff[i] / tileTotal[i] > 0.25);
    }
}

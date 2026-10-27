using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>
/// 主要关卡导航服务。Logo 导航负责作品身份与页面恢复，本服务负责具体 HARD 关卡。
/// 旧 PNG 配置保留原坐标导航兼容路径；WebP 配置缺少 Logo 导航时必须停止。
/// </summary>
public sealed class StageService
{
    private const string LeftBoundaryAnchorTemplate = "mobile_suit_gundam";
    // 1920×1080 游戏画面中，“选择”按钮位于右侧 436px、距底部 120~264px 的区域。
    // 不能复用 workSelect 布局锚点：实际按钮会因文案和状态变化超出该窄框。
    private static readonly Roi WorkSelectSearchRoi = new(1484, 816, 436, 144);

    private readonly AutomationService _automation;
    private readonly IStageWorkRecognizer _recognizer;
    private readonly IStageTitleRecognizer? _titleRecognizer;
    private readonly IStageNodeRecognizer? _nodeRecognizer;
    private readonly IGgeaLog _log;
    private readonly StageNavigationOptions _options;
    private readonly IWorkLogoNavigator? _logoNavigator;
    private readonly StageMenuEntryService _menuEntry;
    // 与当前导航会话一起保存入口布局，跨作品恢复时不依赖 UI 的可变选择状态。
    private UiLayout? _mainLayout;

    public StageService(
        AutomationService automation,
        IStageWorkRecognizer recognizer,
        IGgeaLog log,
        StageNavigationOptions? options = null,
        IStageTitleRecognizer? titleRecognizer = null,
        IStageNodeRecognizer? nodeRecognizer = null,
        IWorkLogoNavigator? logoNavigator = null,
        StageMenuEntryService? menuEntry = null)
    {
        _automation = automation;
        _recognizer = recognizer;
        _titleRecognizer = titleRecognizer;
        _nodeRecognizer = nodeRecognizer;
        _log = log;
        _options = options ?? new StageNavigationOptions();
        _logoNavigator = logoNavigator;
        _menuEntry = menuEntry ?? new StageMenuEntryService(automation, log);
    }

    /// <summary>当前设备的连续关卡导航上下文。</summary>
    public StageNavigationContext Context { get; } = new();

    /// <summary>
    /// 推图使用相同作品 Logo 导航，但独立读取当前节点，不把 AP 略过的“已通关 HARD”条件放宽。
    /// 调用方先恢复主界面；本方法失败时不留下可供 AP 略过使用的可靠上下文。
    /// </summary>
    public async Task<StageNodeRecognition> EnterWorkForProgressAsync(string serial, UiLayout mainLayout,
        UiLayout stageLayout, StageWorkPositionsData positions, IReadOnlyList<HardStageEntry> works,
        HardStageEntry work, CancellationToken ct)
    {
        Context.Invalidate();
        if (_logoNavigator is null || _nodeRecognizer is null || positions.Find(work.Id) is null)
            throw new InvalidOperationException("推图作品导航、节点识别或作品坐标未配置完整。");
        if (_nodeRecognizer is StageNodeRecognizer recognizer) recognizer.ValidateForProgress();
        var entry = await _menuEntry.OpenFromHomeAsync(serial, mainLayout, _options.PageSettleMs, ct);
        if (!entry.Success) throw new InvalidOperationException(entry.Message);
        var main = RequireAnchor(stageLayout, "mainStage", "关卡布局缺少 mainStage");
        await _automation.TapAsync(serial, main.CenterX, main.CenterY, ct);
        await DelayAsync(_options.PageSettleMs, ct);
        var opened = await _logoNavigator.OpenAsync(serial, mainLayout, stageLayout, positions, works, work, ct);
        if (!opened.Result.Success) throw new InvalidOperationException(opened.Result.Message);
        return await ReadCurrentWorkForProgressAsync(serial, work, ct);
    }

    /// <summary>创建独立的原页推进读取器；历史节点只替换几何和身份校验，沿用 NEXT 与取消规则。</summary>
    public StageProgressReader CreateProgressReader(IStageNodeRecognizer nodes)
        => new(_automation, nodes, _log, _options, ClearStageContinuationsAsync);

    /// <summary>主线原页读取继续使用既有识别器，并撤销 AP 略过的旧导航上下文。</summary>
    public Task<StageNodeRecognition> ReadCurrentWorkForProgressAsync(string serial, HardStageEntry work, CancellationToken ct)
    {
        Context.Invalidate();
        return CreateProgressReader(_nodeRecognizer ?? throw new InvalidOperationException("推图节点识别未配置。"))
            .ReadCurrentWorkForProgressAsync(serial, work, ct);
    }

    /// <summary>结算后仍选中同关时，以同一 NEXT 规则推进，不猜测编号或重开作品。</summary>
    public Task<StageNodeRecognition> AdvanceAfterBattleAsync(string serial, HardStageEntry work,
        StageNodeRecognition completed, CancellationToken ct)
    {
        Context.Invalidate();
        return CreateProgressReader(_nodeRecognizer ?? throw new InvalidOperationException("推图节点识别未配置。"))
            .AdvanceAfterBattleAsync(serial, work, completed, ct);
    }
    /// <summary>
    /// 从主界面进入指定作品的具体关卡页，Logo 模式根据当前截图定位并二次验证作品；
    /// 固定复位到最左端的数学导航仅用于旧 PNG 配置。
    /// </summary>
    public async Task<StageNavigationResult> EnterWorkPageAsync(
        string serial,
        UiLayout mainLayout,
        UiLayout stageLayout,
        StageWorkPositionsData positions,
        IReadOnlyList<HardStageEntry> works,
        int workId,
        StageMapMode mode = StageMapMode.Main,
        CancellationToken ct = default)
    {
        if (mode != StageMapMode.Main)
        {
            return StageNavigationResult.Fail(
                StageNavigationFailure.UnsupportedMode,
                "挑战模式尚未启用，已保留独立导航模式入口");
        }

        var target = positions.Find(workId);
        var targetWork = works.FirstOrDefault(work => work.Id == workId);
        if (target is null || targetWork is null)
        {
            return StageNavigationResult.Fail(
                StageNavigationFailure.WorkNotConfigured,
                $"作品未配置完整地图或关卡数据: id={workId}");
        }

        _mainLayout = mainLayout;
        if (_logoNavigator is not null)
        {
            Context.Invalidate();
            var main = RequireAnchor(stageLayout, "mainStage", "关卡布局缺少 mainStage");
            var entry = await _menuEntry.OpenFromHomeAsync(serial, mainLayout, _options.PageSettleMs, ct);
            if (!entry.Success) return entry;
            await _automation.TapAsync(serial, main.CenterX, main.CenterY, ct);
            await DelayAsync(_options.PageSettleMs, ct);
            return await OpenWithLogoAsync(serial, mainLayout, stageLayout, positions, works, targetWork, mode, ct);
        }
        // WebP 目录必须启用 Logo 验证，依赖遗漏不能静默回落到旧坐标点击。
        if (targetWork.Screenshot.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
        {
            Context.Invalidate();
            return StageNavigationResult.Fail(StageNavigationFailure.InvalidContext, "Logo 导航未初始化，禁止坐标点击");
        }

        var mainStage = RequireAnchor(stageLayout, "mainStage", "关卡布局缺少 mainStage 锚点");
        var workSelect = RequireAnchor(stageLayout, "workSelect", "关卡布局缺少 workSelect 锚点");

        Context.Invalidate();
        var menuResult = await _menuEntry.OpenFromHomeAsync(serial, mainLayout, _options.PageSettleMs, ct);
        if (!menuResult.Success) return menuResult;

        _log.Info($"进入主要关卡画布: ({mainStage.CenterX},{mainStage.CenterY})");
        await _automation.TapAsync(serial, mainStage.CenterX, mainStage.CenterY, ct);
        await DelayAsync(_options.PageSettleMs, ct);

        _log.Info($"主要关卡画布查找左端锚点: {LeftBoundaryAnchorTemplate}.png");
        if (!await ResetToLeftmostAsync(serial, ct))
        {
            Context.Invalidate();
            return StageNavigationResult.Fail(
                StageNavigationFailure.InitialLocationFailed,
                $"在 {_options.ResetSearchMaxSwipes} 次滑动内未识别到左端锚点: " +
                $"{LeftBoundaryAnchorTemplate}.png");
        }

        Context.SetLocatedCanvas(serial, mode, viewOffsetX: 0);

        // 左端状态下视口偏移为 0；直接由目标绝对坐标计算屏幕落点。
        var movement = StageMovementPlanner.PlanFromViewport(
            currentViewOffsetX: 0,
            target.CanvasX,
            _options);
        await ExecuteMovementAsync(serial, movement, ct);
        await WaitForWorkTapAfterMovementAsync(serial, movement, ct);
        var workTapX = movement.TargetScreenX;

        _log.Info(
            $"从左端绝对定位作品: {targetWork.Work}，" +
            $"画布坐标 ({target.CanvasX},{target.CanvasY})，滑动 {movement.SwipeCount} 次，" +
            $"屏幕点击 ({workTapX},{target.CanvasY})");
        var openResult = await OpenWorkStageListAsync(
            serial,
            workTapX,
            target.CanvasY,
            workSelect,
            ct);
        if (!openResult.Success)
        {
            if (openResult.Failure == StageNavigationFailure.WorkStageLocked)
            {
                await ReturnWorkToCanvasAsync(
                    serial,
                    stageLayout,
                    target,
                    mode,
                    workId,
                    movement.CanvasMovement,
                    ct);
                return openResult;
            }

            Context.Invalidate();
            return openResult;
        }

        var titleResult = await ReadCurrentHardStageAsync(serial, stageLayout, targetWork, ct);
        if (!titleResult.Success)
        {
            await ReturnWorkToCanvasAsync(serial, stageLayout, target, mode, workId, null, ct);
            return StageNavigationResult.Fail(
                StageNavigationFailure.TitleNotRecognized,
                titleResult.Message);
        }

        Context.SetWorkStageList(serial, mode, workId, titleResult.HardIndex);
        var titleSuffix = titleResult.HardIndex is int hardIndex
            ? $"（当前 HARD {hardIndex}）"
            : string.Empty;
        return StageNavigationResult.Ok($"已进入作品关卡页: {targetWork.Work}{titleSuffix}");
    }

    /// <summary>
    /// 从当前作品的具体关卡页快速切换到另一作品。普通关卡页退出会将当前作品居中，
    /// 未解锁详情页退出则沿用已记录的镜头；作品定位使用可靠视口做数学导航，继续遮罩由统一入口检查。
    /// </summary>
    public async Task<StageNavigationResult> SwitchWorkAsync(
        string serial,
        UiLayout stageLayout,
        StageWorkPositionsData positions,
        IReadOnlyList<HardStageEntry> works,
        int targetWorkId,
        CancellationToken ct = default)
    {
        if (_logoNavigator is not null)
        {
            var work = works.FirstOrDefault(entry => entry.Id == targetWorkId);
            if (_mainLayout is null || work is null || positions.Find(targetWorkId) is null)
                return StageNavigationResult.Fail(StageNavigationFailure.InvalidContext, "缺少 Logo 导航会话或目标配置");
            var mode = Context.Mode;
            if (Context.Serial == serial && Context.PageState == StagePageState.WorkStageList)
            {
                var back = RequireAnchor(stageLayout, "stageBack", "关卡布局缺少 stageBack");
                await _automation.TapAsync(serial, back.CenterX, back.CenterY, ct);
                await DelayAsync(_options.PageSettleMs, ct);
            }
            Context.Invalidate();
            return await OpenWithLogoAsync(serial, _mainLayout, stageLayout, positions, works, work, mode, ct);
        }
        if (works.Any(work => work.Id == targetWorkId && work.Screenshot.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)))
            return StageNavigationResult.Fail(StageNavigationFailure.InvalidContext, "Logo 导航未初始化，禁止坐标点击");
        if (!Context.IsReliable ||
            Context.Serial != serial ||
            (Context.PageState != StagePageState.WorkStageList &&
             Context.PageState != StagePageState.MainStageCanvas) ||
            Context.CurrentWorkId is not int currentWorkId)
        {
            return StageNavigationResult.Fail(
                StageNavigationFailure.InvalidContext,
                "缺少可靠的当前作品上下文，不能执行免识图快速切换");
        }

        var current = positions.Find(currentWorkId);
        var target = positions.Find(targetWorkId);
        var targetWork = works.FirstOrDefault(work => work.Id == targetWorkId);
        if (current is null || target is null || targetWork is null)
        {
            Context.Invalidate();
            return StageNavigationResult.Fail(
                StageNavigationFailure.WorkNotConfigured,
                $"当前或目标作品地图数据不完整: {currentWorkId} -> {targetWorkId}");
        }

        var stageBack = RequireAnchor(stageLayout, "stageBack", "关卡布局缺少 stageBack 锚点");
        var workSelect = RequireAnchor(stageLayout, "workSelect", "关卡布局缺少 workSelect 锚点");

        // 正常完成关卡时需要先返回画布；遇到未解锁作品时，调用方已通过
        // ReturnWorkToCanvasAsync 回到画布，避免对画布重复点击“返回”。
        if (Context.PageState == StagePageState.WorkStageList)
        {
            await _automation.TapAsync(serial, stageBack.CenterX, stageBack.CenterY, ct);
            await DelayAsync(_options.PageSettleMs, ct);
        }

        // 从普通关卡页退出会将作品居中；未解锁详情页退出则保留点击该作品前的镜头。
        // 已保存的画布偏移优先级更高，不能无条件覆盖成“当前作品居中”。
        var centeredOffset = Math.Max(0, current.CanvasX - _options.ScreenCenterX);
        var currentViewOffset = Context.PageState == StagePageState.MainStageCanvas &&
                                Context.ViewOffsetX is double preservedViewOffset
            ? preservedViewOffset
            : centeredOffset;
        Context.SetWorkCanvas(serial, Context.Mode, currentWorkId, currentViewOffset);
        var movement = StageMovementPlanner.PlanFromViewport(currentViewOffset, target.CanvasX, _options);
        await ExecuteMovementAsync(serial, movement, ct);
        await WaitForWorkTapAfterMovementAsync(serial, movement, ct);
        var workTapX = movement.TargetScreenX;
        var finalViewOffset = currentViewOffset + movement.CanvasMovement;

        _log.Info(
            $"按作品相对距离快速切换: {currentWorkId} -> {targetWorkId}，" +
            $"距离 {target.CanvasX - current.CanvasX}px，滑动 {movement.SwipeCount} 次，" +
            $"点击 ({workTapX},{target.CanvasY})");
        var openResult = await OpenWorkStageListAsync(
            serial,
            workTapX,
            target.CanvasY,
            workSelect,
            ct);
        if (!openResult.Success)
        {
            if (openResult.Failure == StageNavigationFailure.WorkStageLocked)
            {
                await ReturnWorkToCanvasAsync(
                    serial,
                    stageLayout,
                    target,
                    Context.Mode,
                    targetWorkId,
                    finalViewOffset,
                    ct);
                return openResult;
            }

            Context.Invalidate();
            return openResult;
        }

        var titleResult = await ReadCurrentHardStageAsync(serial, stageLayout, targetWork, ct);
        if (!titleResult.Success)
        {
            await ReturnWorkToCanvasAsync(
                serial,
                stageLayout,
                target,
                Context.Mode,
                targetWorkId,
                null,
                ct);
            return StageNavigationResult.Fail(
                StageNavigationFailure.TitleNotRecognized,
                titleResult.Message);
        }

        Context.SetWorkStageList(serial, Context.Mode, targetWorkId, titleResult.HardIndex);
        var titleSuffix = titleResult.HardIndex is int hardIndex
            ? $"（当前 HARD {hardIndex}）"
            : string.Empty;
        return StageNavigationResult.Ok($"已快速切换到作品关卡页: {targetWork.Work}{titleSuffix}");
    }

    /// <summary>Logo 导航负责作品身份，原 HARD 识别继续负责进入后具体关卡的确认。</summary>
    private async Task<StageNavigationResult> OpenWithLogoAsync(string serial, UiLayout mainLayout,
        UiLayout stageLayout, StageWorkPositionsData positions, IReadOnlyList<HardStageEntry> works,
        HardStageEntry work, StageMapMode mode, CancellationToken ct)
    {
        var result = await _logoNavigator!.OpenAsync(serial, mainLayout, stageLayout, positions, works, work, ct);
        if (!result.Result.Success)
        {
            Context.Invalidate();
            if (result.Result.Failure == StageNavigationFailure.WorkStageLocked)
            {
                await ReturnWorkToCanvasAsync(serial, stageLayout, positions.Find(work.Id)!, mode,
                    work.Id, result.ViewOffsetX, ct);
                // 未测量过画布偏移时不能将猜测位置标成可靠，避免队列误用。
                if (result.ViewOffsetX is null) Context.Invalidate();
            }
            return result.Result;
        }
        var title = await ReadCurrentHardStageAsync(serial, stageLayout, work, ct);
        if (!title.Success)
        {
            await ReturnWorkToCanvasAsync(serial, stageLayout, positions.Find(work.Id)!, mode, work.Id, null, ct);
            return StageNavigationResult.Fail(StageNavigationFailure.TitleNotRecognized, title.Message);
        }
        Context.SetWorkStageList(serial, mode, work.Id, title.HardIndex);
        return StageNavigationResult.Ok($"已确认进入作品 {work.Work}，HARD {title.HardIndex}");
    }

    /// <summary>
    /// 进入作品关卡页后优先识别当前 HARD STAGE 标题。
    /// 普通用户通常已通关作品，游戏会自动落在最后一个 HARD；标题命中时可快速确认。
    /// 标题未命中时改为具体关卡识别并向调用方报告，不再擅自返回或点击其他节点。
    /// </summary>
    private async Task<(bool Success, int? HardIndex, string Message)> ReadCurrentHardStageAsync(
        string serial,
        UiLayout stageLayout,
        HardStageEntry work,
        CancellationToken ct)
    {
        await ClearStageContinuationsAsync(serial, ct);
        if (_titleRecognizer is null)
        {
            return (true, null, "未启用标题识别");
        }

        var title = RequireAnchor(stageLayout, "stageTitle", "关卡布局缺少 stageTitle 锚点");
        // 作品详情页已经由“选择”按钮确认；标题连续未命中时不必等待完整 15 秒，
        // 及时切换到用户校准过的左侧关卡轨道识别流程。
        var titleWaitMs = _nodeRecognizer is null
            ? _options.TitleWaitTimeoutMs
            : Math.Min(_options.TitleWaitTimeoutMs, _options.TitleNodeFallbackWaitMs);
        var deadline = Environment.TickCount64 + Math.Max(0, titleWaitMs);
        StageTitleMatch? match = null;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await ClearStageContinuationsAsync(serial, ct);
            match = await _titleRecognizer.RecognizeAsync(serial, title.Roi, ct: ct);
            if (match is not null)
            {
                break;
            }

            await Task.Delay(Math.Max(0, _options.TitlePollIntervalMs), ct);
        }

        if (match is not null && match.HardIndex is >= 1 && match.HardIndex <= work.HardCount)
        {
            return (true, match.HardIndex, $"识别到 HARD STAGE {match.HardIndex}");
        }

        if (_nodeRecognizer is not null)
        {
            var node = await ReadSelectedNodeAsync(serial, work, ct);
            if (node is
                {
                    Kind: WorkStageKind.Hard,
                    Number: int hardIndex,
                    Position: not null,
                    // 只有已通过 HARD 才可能进入略过流程；未通过节点即使标签误识别也必须停止，
                    // 防止把普通关卡当成 HARD 后继续消耗 AP。
                    State: WorkStageNodeState.Completed or WorkStageNodeState.Cleared,
                } && hardIndex is >= 1 and <= 5 && hardIndex <= work.HardCount)
            {
                _log.Info($"标题识别失败，已由左侧关卡轨道确认 HARD {hardIndex}: {node.Message}");
                return (true, hardIndex, $"左侧关卡轨道识别确认 HARD {hardIndex}");
            }

            Context.Invalidate();
            return node is null
                ? (false, null, "未识别到 HARD STAGE 标题，且无法确认当前具体关卡；已停止自动操作")
                : (false, null, $"未识别到 HARD STAGE 标题；{node.Message}。已停止自动操作，请确认通关进度");
        }

        if (match is null)
        {
            return (false, null, "未识别到 HARD STAGE 标题，无法确认当前具体关卡");
        }

        return (false, null, $"识别到无效 HARD STAGE {match.HardIndex}，作品仅配置 HARD 1~{work.HardCount}");
    }

    /// <summary>标题回退与普通推图共用同一节点入口，避免某条路径绕过继续遮罩及网络安全检查。</summary>
    private async Task<StageNodeRecognition?> ReadSelectedNodeAsync(string serial, HardStageEntry work, CancellationToken ct)
    {
        await ClearStageContinuationsAsync(serial, ct);
        return await _nodeRecognizer!.RecognizeSelectedAsync(serial, work, ct);
    }

    /// <summary>关卡识别授权关闭完整底部继续文字；复用会话冷却和安全规则，未知帧、略过按钮均不点击。</summary>
    private async Task ClearStageContinuationsAsync(string serial, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, _options.TitleWaitTimeoutMs);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var png = await _automation.ScreenshotAsync(serial, ct);
            var result = await _automation.GlobalScreens.HandleContinuationAsync(serial, png, ct,
                waitingMessage: "确认当前作品关卡", allowSkip: false);
            if (result == GlobalScreenResult.None) return;
            // 动作发送或冷却阻塞都不能交给节点识别器；必须用新截图确认遮罩已经消失。
            if (Environment.TickCount64 >= deadline)
            {
                var fresh = await _automation.ScreenshotAsync(serial, ct);
                if (!await _automation.GlobalScreens.IsTapToNextVisibleAsync(fresh, ct)) return;
                Context.Invalidate();
                throw new InvalidOperationException("关卡继续遮罩未在限定时间内关闭，停止当前关卡导航；请检查游戏画面。");
            }
            await DelayAsync(Math.Max(1, _options.TitlePollIntervalMs), ct);
        }
    }

    /// <summary>
    /// 根据五槽位映射点击指定 HARD 关卡。
    /// 进入作品时已经识别过当前 HARD，后续切换不再重复识别标题，直接进入略过流程。
    /// 目标必须位于当前视口的 5 个槽位内。
    /// </summary>
    public async Task<StageNavigationResult> SelectHardStageAsync(
        string serial,
        UiLayout stageLayout,
        IReadOnlyList<HardStageEntry> works,
        int workId,
        int targetHardIndex,
        CancellationToken ct = default)
    {
        var work = works.FirstOrDefault(item => item.Id == workId);
        if (work is null)
        {
            return StageNavigationResult.Fail(
                StageNavigationFailure.WorkNotConfigured,
                $"作品未配置: id={workId}");
        }

        // 只有 1 个 HARD 关卡的作品（如 3+1 关卡）无法用五槽位映射，
        // 进入后自动选中的就是唯一 HARD 关，直接确认即可。
        if (work.HardCount == 1)
        {
            if (targetHardIndex != 1)
            {
                return StageNavigationResult.Fail(
                    StageNavigationFailure.InvalidContext,
                    "作品仅一个 HARD 关卡，目标无效");
            }

            await ClearStageContinuationsAsync(serial, ct);
            if (Context.CurrentHardIndex is not int singleHardIndex)
            {
                var singleTitle = await ReadCurrentHardStageAsync(serial, stageLayout, work, ct);
                if (!singleTitle.Success || singleTitle.HardIndex != 1)
                {
                    return StageNavigationResult.Fail(
                        StageNavigationFailure.TitleNotRecognized,
                        singleTitle.Message);
                }

                singleHardIndex = singleTitle.HardIndex.Value;
            }

            if (singleHardIndex != 1)
            {
                return StageNavigationResult.Fail(
                    StageNavigationFailure.InvalidContext,
                    $"当前 HARD {singleHardIndex}，与唯一 HARD 1 不一致");
            }

            Context.SetWorkStageList(serial, Context.Mode, workId, 1);
            _log.Info("作品仅 1 个 HARD 关卡，无需槽位切换，直接使用当前选中状态");
            return StageNavigationResult.Ok("已确认 HARD 1");
        }

        if (Context.CurrentHardIndex is not int currentHardIndex)
        {
            var titleResult = await ReadCurrentHardStageAsync(serial, stageLayout, work, ct);
            if (!titleResult.Success || titleResult.HardIndex is not int readHardIndex)
            {
                return StageNavigationResult.Fail(
                    StageNavigationFailure.TitleNotRecognized,
                    titleResult.Message);
            }

            currentHardIndex = readHardIndex;
        }

        var tapResult = await TapHardStageSlotAsync(
            serial,
            stageLayout,
            work,
            currentHardIndex,
            targetHardIndex,
            ct);
        if (!tapResult.Success)
        {
            return tapResult;
        }

        // 点击后必须重新读取标题，不能只凭内存推断已切换成功。
        if (_titleRecognizer is not null)
        {
            var confirmed = await ReadCurrentHardStageAsync(serial, stageLayout, work, ct);
            if (confirmed.Success && confirmed.HardIndex == targetHardIndex)
            {
                Context.SetWorkStageList(serial, Context.Mode, workId, targetHardIndex);
                return StageNavigationResult.Ok($"已选中 HARD {targetHardIndex}");
            }

            // 右上角标题与点击前完全相同时，通常是入场动画吞掉了第一次点击。
            // 此时用左侧黄色选中节点重新确认真实位置，再按识别结果计算槽位重试一次。
            if (confirmed.Success &&
                confirmed.HardIndex == currentHardIndex &&
                confirmed.Message.StartsWith("识别到 HARD STAGE", StringComparison.Ordinal) &&
                _nodeRecognizer is not null)
            {
                var node = await ReadSelectedNodeAsync(serial, work, ct);
                if (node is
                    {
                        Kind: WorkStageKind.Hard,
                        Number: int recognizedHardIndex,
                        Position: not null,
                        State: WorkStageNodeState.Completed or WorkStageNodeState.Cleared,
                    } && recognizedHardIndex >= 1 && recognizedHardIndex <= work.HardCount)
                {
                    _log.Warn(
                        $"点击后标题仍为 HARD {currentHardIndex}，左侧节点确认 HARD {recognizedHardIndex}；" +
                        $"重新点击 HARD {targetHardIndex}");
                    var retryTap = await TapHardStageSlotAsync(
                        serial,
                        stageLayout,
                        work,
                        recognizedHardIndex,
                        targetHardIndex,
                        ct);
                    if (!retryTap.Success)
                    {
                        Context.SetWorkStageList(serial, Context.Mode, workId, recognizedHardIndex);
                        return retryTap;
                    }

                    confirmed = await ReadCurrentHardStageAsync(serial, stageLayout, work, ct);
                    if (confirmed.Success && confirmed.HardIndex == targetHardIndex)
                    {
                        Context.SetWorkStageList(serial, Context.Mode, workId, targetHardIndex);
                        return StageNavigationResult.Ok($"重试后已选中 HARD {targetHardIndex}");
                    }
                }
            }

            // 保留仍位于作品关卡页的可靠上下文，调用方可点击左上角返回主要关卡画布。
            var actualHardIndex = confirmed.Success && confirmed.HardIndex is int confirmedHardIndex
                ? confirmedHardIndex
                : currentHardIndex;
            Context.SetWorkStageList(serial, Context.Mode, workId, actualHardIndex);
            return StageNavigationResult.Fail(
                StageNavigationFailure.TitleNotRecognized,
                confirmed.Success
                    ? $"点击后仍为 HARD {confirmed.HardIndex}，预期 HARD {targetHardIndex}"
                    : confirmed.Message);
        }

        Context.SetWorkStageList(serial, Context.Mode, workId, targetHardIndex);
        return StageNavigationResult.Ok($"已选中 HARD {targetHardIndex}");
    }

    /// <summary>根据当前 HARD 位置计算目标槽位并点击；用于首次选择和动画阻挡后的单次重试。</summary>
    private async Task<StageNavigationResult> TapHardStageSlotAsync(
        string serial,
        UiLayout stageLayout,
        HardStageEntry work,
        int currentHardIndex,
        int targetHardIndex,
        CancellationToken ct)
    {
        UiAnchor slot;
        string slotDescription;
        if (work.HardCount == 5 && targetHardIndex == 1)
        {
            // 五个 HARD 的作品会把 HARD 1 固定显示在屏幕左边缘，不能按当前关卡推算普通槽位。
            slot = RequireAnchor(
                stageLayout,
                "fiveHardStage1",
                "关卡布局缺少 fiveHardStage1 锚点");
            slotDescription = "五 HARD 专用位置";
        }
        else
        {
            if (!StageSlotPlanner.TryGetSlotIndex(
                    work.NormalCount,
                    work.HardCount,
                    currentHardIndex,
                    targetHardIndex,
                    out var slotIndex,
                    work.SideStories))
            {
                return StageNavigationResult.Fail(
                    StageNavigationFailure.InvalidContext,
                    $"目标 HARD {targetHardIndex} 不在当前五槽位视口内");
            }

            slot = RequireAnchor(
                stageLayout,
                $"stageSlot{slotIndex}",
                $"关卡布局缺少 stageSlot{slotIndex} 锚点");
            slotDescription = $"槽位 {slotIndex}";
        }

        await ClearStageContinuationsAsync(serial, ct);
        _log.Info($"点击 HARD {targetHardIndex} {slotDescription}: ({slot.CenterX},{slot.CenterY})");
        await _automation.TapAsync(serial, slot.CenterX, slot.CenterY, ct);
        await DelayAsync(_options.PageSettleMs, ct);
        return StageNavigationResult.Ok($"已点击 HARD {targetHardIndex}");
    }

    /// <summary>
    /// 选关重试仍失败时，从当前作品关卡页点击左上角返回主要关卡画布，
    /// 并保留当前作品居中的可靠上下文，供 AP 任务链继续下一作品。
    /// </summary>
    public async Task<StageNavigationResult> ReturnCurrentWorkToCanvasAsync(
        string serial,
        UiLayout stageLayout,
        StageWorkPositionsData positions,
        int workId,
        CancellationToken ct = default)
    {
        var work = positions.Find(workId);
        if (work is null ||
            !Context.IsReliable ||
            Context.Serial != serial ||
            Context.PageState != StagePageState.WorkStageList ||
            Context.CurrentWorkId != workId)
        {
            return StageNavigationResult.Fail(
                StageNavigationFailure.InvalidContext,
                "缺少可靠的当前作品关卡上下文，不能安全返回主要关卡画布");
        }

        await ReturnWorkToCanvasAsync(
            serial,
            stageLayout,
            work,
            Context.Mode,
            workId,
            preservedViewOffsetX: null,
            ct);
        return StageNavigationResult.Ok("已返回主要关卡画布");
    }

    /// <summary>
    /// 略过结算返回关卡页后再次确认仍处于预期 HARD。
    /// 这一步防止网络延迟、入场动画或漏点导致流程误以为略过已经完成。
    /// </summary>
    public async Task<StageNavigationResult> ConfirmCurrentHardStageAsync(
        string serial,
        UiLayout stageLayout,
        HardStageEntry work,
        int expectedHardIndex,
        CancellationToken ct = default)
    {
        var confirmed = await ReadCurrentHardStageAsync(serial, stageLayout, work, ct);
        if (!confirmed.Success || confirmed.HardIndex != expectedHardIndex)
        {
            Context.Invalidate();
            return StageNavigationResult.Fail(
                StageNavigationFailure.TitleNotRecognized,
                confirmed.Success
                    ? $"略过后关卡为 HARD {confirmed.HardIndex}，预期 HARD {expectedHardIndex}"
                    : confirmed.Message);
        }

        Context.SetWorkStageList(serial, Context.Mode, work.Id, expectedHardIndex);
        return StageNavigationResult.Ok($"已确认仍处于 HARD {expectedHardIndex}");
    }

    /// <summary>执行规划好的标准横向滑动。</summary>
    private async Task ExecuteMovementAsync(
        string serial,
        StageMovementPlan movement,
        CancellationToken ct)
    {
        for (var i = 0; i < movement.SwipeCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var screenDragDistance = _options.SwipeLeftStartX - _options.SwipeLeftEndX;
            var isHalfFinalSwipe = i == movement.SwipeCount - 1 && movement.UsesHalfFinalSwipe;
            // 只允许固定半屏手势，不执行任意比例的短滑，确保输入动作可按日志稳定复现。
            var screenDelta = isHalfFinalSwipe
                ? Math.Max(1, screenDragDistance / 2)
                : screenDragDistance;
            var durationMs = isHalfFinalSwipe
                ? Math.Max(50, _options.SwipeDurationMs / 2)
                : _options.SwipeDurationMs;

            if (isHalfFinalSwipe)
            {
                var correction = movement.Direction > 0 ? _options.HalfSwipeLeftTapCorrection : _options.HalfSwipeRightTapCorrection;
                _log.Info($"执行末次半屏滑动: 屏幕拖动 {screenDelta}px，" +
                    $"手指{(movement.Direction > 0 ? "左" : "右")}滑，点击 X 补偿 {correction:+0;-0;0}px（已纳入视口规划）");
            }

            if (movement.Direction > 0)
            {
                // 视口向右：手指向左滑。
                var endX = _options.SwipeLeftStartX - screenDelta;
                await _automation.SwipeAsync(
                    serial,
                    _options.SwipeLeftStartX,
                    _options.SwipeY,
                    endX,
                    _options.SwipeY,
                    durationMs,
                    ct);
            }
            else
            {
                // 视口向左：手指向右滑。
                var endX = _options.SwipeLeftEndX + screenDelta;
                await _automation.SwipeAsync(
                    serial,
                    _options.SwipeLeftStartX - screenDragDistance,
                    _options.SwipeY,
                    endX,
                    _options.SwipeY,
                    durationMs,
                    ct);
            }

            // 最后一次滑动由作品节点点击前的专用稳定等待处理；
            // 长等待通过新截图确认停稳，不再以倒计时结束作为动画完成证据。
            if (i < movement.SwipeCount - 1)
            {
                await WaitForCanvasAsync(serial, _options.SwipeSettleMs, ct);
            }
        }

    }

    /// <summary>最后一次作品定位滑动后等待画布稳定；无滑动时不额外延迟。</summary>
    private Task WaitForWorkTapAfterMovementAsync(string serial, StageMovementPlan movement, CancellationToken ct)
        => movement.SwipeCount > 0
            ? WaitForCanvasAsync(serial, _options.WorkTapSettleMs, ct)
            : Task.CompletedTask;

    /// <summary>
    /// 逐次向左移动并查找初代高达锚点；识别到后执行一次大幅拖动，确保画布到达边界。
    /// </summary>
    private async Task<bool> ResetToLeftmostAsync(string serial, CancellationToken ct)
    {
        var fullCanvasRoi = new Roi(0, 80, 1920, 800);
        for (var swipeCount = 0; swipeCount <= _options.ResetSearchMaxSwipes; swipeCount++)
        {
            ct.ThrowIfCancellationRequested();
            var anchor = await _recognizer.FindAsync(
                serial,
                LeftBoundaryAnchorTemplate,
                fullCanvasRoi,
                _options.ResetAnchorThreshold,
                ct);
            if (anchor is not null)
            {
                _log.Info(
                    $"识别到左端锚点: confidence={anchor.Confidence:F3}, " +
                    $"center=({anchor.CenterX},{anchor.CenterY})，执行一次左端大幅拖动");
                await SwipeCanvasToLeftBoundaryAsync(serial, ct);

                return true;
            }

            if (swipeCount == _options.ResetSearchMaxSwipes)
            {
                break;
            }

            _log.Info($"未识别到左端锚点，继续向左拖动: {swipeCount + 1}/{_options.ResetSearchMaxSwipes}");
            await SwipeCanvasTowardLeftAsync(serial, ct);
        }

        return false;
    }

    /// <summary>手指向右滑动一次，使主要关卡画布朝最左端移动并等待稳定。</summary>
    private async Task SwipeCanvasTowardLeftAsync(string serial, CancellationToken ct)
    {
        await _automation.SwipeAsync(
            serial,
            _options.SwipeLeftEndX,
            _options.SwipeY,
            _options.SwipeLeftStartX,
            _options.SwipeY,
            _options.SwipeDurationMs,
            ct);
        await WaitForCanvasAsync(serial, _options.SwipeSettleMs, ct);
    }

    /// <summary>识别到初代高达后执行一次接近全屏宽度的拖动，使画布触碰最左边界。</summary>
    private async Task SwipeCanvasToLeftBoundaryAsync(string serial, CancellationToken ct)
    {
        await _automation.SwipeAsync(
            serial,
            _options.ResetBoundarySwipeStartX,
            _options.SwipeY,
            _options.ResetBoundarySwipeEndX,
            _options.SwipeY,
            _options.SwipeDurationMs,
            ct);
        await WaitForCanvasAsync(serial, _options.SwipeSettleMs, ct);
    }

    /// <summary>
    /// 点击作品节点，再在作品详情页识别“选择”按钮进入具体关卡列表。
    /// 先等待入场特效，再用选择按钮本身确认详情页状态；这比全屏像素差异
    /// 更能抵御背景动画、网络加载占位画面及轻微特效造成的误判。
    /// </summary>
    private async Task<StageNavigationResult> OpenWorkStageListAsync(
        string serial,
        int workX,
        int workY,
        UiAnchor workSelect,
        CancellationToken ct)
    {
        ScreenImage? canvasBeforeTap = null;
        if (HasWorkSelectTemplates())
        {
            canvasBeforeTap = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
        }

        await _automation.TapAsync(serial, workX, workY, ct);
        await DelayAsync(_options.PageSettleMs, ct);

        // 详情页可能有轻微入场动画，识别“选择”前再固定等待 0.5 秒，避免点早进入失败。
        await DelayAsync(_options.SelectTapExtraDelayMs, ct);
        if (canvasBeforeTap is not null)
        {
            var afterTap = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (!HasWorkOverviewTransition(canvasBeforeTap, afterTap))
            {
                _log.Warn("点击作品后画面未进入详情页，停止识别选择按钮");
                return StageNavigationResult.Fail(
                    StageNavigationFailure.InvalidContext,
                    "点击作品后未确认详情页切换，已停止自动操作");
            }
        }

        var selectResult = await ConfirmAndClickWorkSelectAsync(serial, workSelect, ct);
        if (!selectResult.Success)
        {
            return selectResult;
        }

        await DelayAsync(_options.PageSettleMs, ct);
        return StageNavigationResult.Ok("已识别并点击作品详情页的可用选择按钮");
    }

    /// <summary>只要配置了任一种选择按钮模板，就必须先确认已从画布切入作品详情页。</summary>
    private bool HasWorkSelectTemplates()
        => !string.IsNullOrWhiteSpace(_options.WorkSelectTemplatePath) ||
           !string.IsNullOrWhiteSpace(_options.WorkSelectClosedTemplatePath);

    /// <summary>
    /// 对比点击前后主视觉区域的稀疏像素。作品详情页会替换大面积内容；
    /// 采样可避免逐像素比较的开销，也不会受顶部固定栏和底部导航栏影响。
    /// </summary>
    private bool HasWorkOverviewTransition(ScreenImage before, ScreenImage after)
    {
        if (before.Width != after.Width || before.Height != after.Height)
        {
            return false;
        }

        var changed = 0;
        var sampled = 0;
        var left = Math.Clamp(180, 0, before.Width);
        var right = Math.Clamp(before.Width - 180, left, before.Width);
        var top = Math.Clamp(100, 0, before.Height);
        var bottom = Math.Clamp(before.Height - 160, top, before.Height);
        for (var y = top; y < bottom; y += 16)
        {
            for (var x = left; x < right; x += 16)
            {
                var offset = (y * before.Width + x) * 4;
                var delta = Math.Abs(before.Bgra[offset] - after.Bgra[offset]) +
                            Math.Abs(before.Bgra[offset + 1] - after.Bgra[offset + 1]) +
                            Math.Abs(before.Bgra[offset + 2] - after.Bgra[offset + 2]);
                sampled++;
                if (delta >= 90)
                {
                    changed++;
                }
            }
        }

        return sampled > 0 && (double)changed / sampled >= _options.WorkOverviewChangeRatio;
    }

    /// <summary>
    /// 在作品详情页的右下角区分可用和暗态“选择”按钮。暗态优先检查，
    /// 因为它表示作品尚未解锁，禁止继续点击坐标或进入略过流程。
    /// </summary>
    private async Task<StageNavigationResult> ConfirmAndClickWorkSelectAsync(
        string serial,
        UiAnchor workSelect,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.WorkSelectTemplatePath) &&
            string.IsNullOrWhiteSpace(_options.WorkSelectClosedTemplatePath))
        {
            // 未配置模板时保持旧版行为，方便已有测试和自定义布局渐进迁移。
            await _automation.TapAsync(serial, workSelect.CenterX, workSelect.CenterY, ct);
            return StageNavigationResult.Ok("未配置选择按钮模板，已按布局坐标点击");
        }

        var wait = new WaitOptions
        {
            Threshold = _options.WorkSelectThreshold,
            Roi = WorkSelectSearchRoi,
        };
        var deadline = Environment.TickCount64 + Math.Max(0, _options.WorkSelectWaitTimeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(_options.WorkSelectClosedTemplatePath))
            {
                var closed = await _automation.FindImageAsync(
                    serial,
                    _options.WorkSelectClosedTemplatePath,
                    wait,
                    ct);
                if (closed is not null)
                {
                    _log.Warn("作品选择按钮为暗态，判定该作品尚未解锁");
                    return StageNavigationResult.Fail(
                        StageNavigationFailure.WorkStageLocked,
                        "识别到选择按钮暗态，该作品尚未解锁");
                }
            }

            if (!string.IsNullOrWhiteSpace(_options.WorkSelectTemplatePath))
            {
                var enabled = await _automation.FindImageAsync(
                    serial,
                    _options.WorkSelectTemplatePath,
                    wait,
                    ct);
                if (enabled is not null)
                {
                    _log.Info($"识别到可用选择按钮: ({enabled.CenterX},{enabled.CenterY})");
                    await _automation.TapAsync(serial, enabled.CenterX, enabled.CenterY, ct);
                    return StageNavigationResult.Ok("已点击可用选择按钮");
                }
            }

            await Task.Delay(Math.Max(1, _options.WorkSelectPollIntervalMs), ct);
        }

        _log.Warn(
            $"未在选择按钮区域 {WorkSelectSearchRoi} 内识别到可用或暗态按钮，停止自动操作");
        return StageNavigationResult.Fail(
            StageNavigationFailure.InvalidContext,
            "未识别到选择按钮可用或暗态，已停止自动操作");
    }

    /// <summary>
    /// 已确认位于作品详情或关卡页时返回主要关卡画布。普通关卡页会让作品居中，
    /// 未解锁详情页则维持进入详情前的镜头；调用方必须传入对应的真实状态。
    /// </summary>
    private async Task ReturnWorkToCanvasAsync(
        string serial,
        UiLayout stageLayout,
        StageWorkPosition work,
        StageMapMode mode,
        int workId,
        double? preservedViewOffsetX,
        CancellationToken ct)
    {
        var stageBack = RequireAnchor(stageLayout, "stageBack", "关卡布局缺少 stageBack 锚点");
        await _automation.TapAsync(serial, stageBack.CenterX, stageBack.CenterY, ct);
        await DelayAsync(_options.PageSettleMs, ct);

        var viewOffset = preservedViewOffsetX ?? Math.Max(0, work.CanvasX - _options.ScreenCenterX);
        if (preservedViewOffsetX is not null)
        {
            _log.Info($"未解锁作品返回画布，保留选择前视口偏移: {viewOffset:F0}px");
        }

        Context.SetWorkCanvas(serial, mode, workId, viewOffset);
    }

    /// <summary>读取必需的布局锚点，缺失时终止导航。</summary>
    private static UiAnchor RequireAnchor(UiLayout layout, string name, string error)
        => layout.Find(name) ?? throw new InvalidOperationException(error);

    /// <summary>保留短动画设置；较长滑动等待改为观察画布，后续流程仍须核对页面与目标。</summary>
    private Task WaitForCanvasAsync(string serial, int milliseconds, CancellationToken ct)
        => milliseconds <= 5000 ? DelayAsync(milliseconds, ct)
            : _automation.WaitForCanvasStillAsync(serial, new Roi(0, 120, 1920, 760), ct);

    /// <summary>仅在配置时间大于零时执行可取消等待。</summary>
    private static Task DelayAsync(int milliseconds, CancellationToken ct)
        => milliseconds <= 0 ? Task.CompletedTask : Task.Delay(milliseconds, ct);
}

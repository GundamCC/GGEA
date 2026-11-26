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
public sealed partial class StageService
{
    private const string LeftBoundaryAnchorTemplate = "mobile_suit_gundam";
    // 1920×1080 游戏画面中，“选择”按钮位于右侧 436px、距底部 120~264px 的区域。
    // 不能复用 workSelect 布局锚点：实际按钮会因文案和状态变化超出该窄框。
    private static readonly Roi WorkSelectSearchRoi = new(1484, 816, 436, 144);

    private readonly AutomationService _automation;
    private readonly IStageWorkRecognizer _recognizer;
    private readonly IStageTitleRecognizer? _titleRecognizer;
    private readonly IStageNodeRecognizer? _nodeRecognizer;
    private readonly StageProgressReader? _progressReader;
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
        _progressReader = nodeRecognizer is null ? null : CreateProgressReader(nodeRecognizer);
        _logoNavigator = logoNavigator;
        _menuEntry = menuEntry ?? new StageMenuEntryService(automation, log);
    }

    // 活动轨道可横向拖动，先停靠末端再使用公共五槽位规划。识别器仅替换轨道几何。
    private bool _endAligned;
    internal StageService ForEndAlignedTrack(IStageNodeRecognizer nodes, IStageTitleRecognizer eventTitles)
        => new StageService(_automation, _recognizer, _log, _options, eventTitles, nodes) { _endAligned = true };

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
        var main = stageLayout.Require("mainStage", "关卡布局缺少 mainStage");
        await _automation.TapAsync(serial, main.CenterX, main.CenterY, ct);
        await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);
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
        return (_progressReader ?? throw new InvalidOperationException("推图节点识别未配置。"))
            .ReadCurrentWorkForProgressAsync(serial, work, ct);
    }

    /// <summary>结算后仍选中同关时，以同一 NEXT 规则推进，不猜测编号或重开作品。</summary>
    public Task<StageNodeRecognition> AdvanceAfterBattleAsync(string serial, HardStageEntry work,
        StageNodeRecognition completed, CancellationToken ct)
    {
        Context.Invalidate();
        return (_progressReader ?? throw new InvalidOperationException("推图节点识别未配置。"))
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
            var main = stageLayout.Require("mainStage", "关卡布局缺少 mainStage");
            var entry = await _menuEntry.OpenFromHomeAsync(serial, mainLayout, _options.PageSettleMs, ct);
            if (!entry.Success) return entry;
            await _automation.TapAsync(serial, main.CenterX, main.CenterY, ct);
            await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);
            return await OpenWithLogoAsync(serial, mainLayout, stageLayout, positions, works, targetWork, mode, ct);
        }
        // WebP 目录必须启用 Logo 验证，依赖遗漏不能静默回落到旧坐标点击。
        if (targetWork.Screenshot.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
        {
            Context.Invalidate();
            return StageNavigationResult.Fail(StageNavigationFailure.InvalidContext, "Logo 导航未初始化，禁止坐标点击");
        }

        var mainStage = stageLayout.Require("mainStage", "关卡布局缺少 mainStage 锚点");
        var workSelect = stageLayout.Require("workSelect", "关卡布局缺少 workSelect 锚点");

        Context.Invalidate();
        var menuResult = await _menuEntry.OpenFromHomeAsync(serial, mainLayout, _options.PageSettleMs, ct);
        if (!menuResult.Success) return menuResult;

        _log.Info($"进入主要关卡画布: ({mainStage.CenterX},{mainStage.CenterY})");
        await _automation.TapAsync(serial, mainStage.CenterX, mainStage.CenterY, ct);
        await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);

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
                var back = stageLayout.Require("stageBack", "关卡布局缺少 stageBack");
                await _automation.TapAsync(serial, back.CenterX, back.CenterY, ct);
                await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);
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

        var stageBack = stageLayout.Require("stageBack", "关卡布局缺少 stageBack 锚点");
        var workSelect = stageLayout.Require("workSelect", "关卡布局缺少 workSelect 锚点");

        // 正常完成关卡时需要先返回画布；遇到未解锁作品时，调用方已通过
        // ReturnWorkToCanvasAsync 回到画布，避免对画布重复点击“返回”。
        if (Context.PageState == StagePageState.WorkStageList)
        {
            await _automation.TapAsync(serial, stageBack.CenterX, stageBack.CenterY, ct);
            await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);
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
}

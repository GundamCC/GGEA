using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>Logo 导航结果；偏移只来自截图锚点，不能使用计划滑动量充当实际位移。</summary>
public sealed record WorkLogoNavigationResult(StageNavigationResult Result, double? ViewOffsetX);

/// <summary>作品列表导航边界，关卡服务不承担画布识别、重试或主界面恢复。</summary>
public interface IWorkLogoNavigator
{
    Task<WorkLogoNavigationResult> OpenAsync(string serial, UiLayout mainLayout, UiLayout stageLayout,
        StageWorkPositionsData positions, IReadOnlyList<HardStageEntry> works, HardStageEntry target,
        CancellationToken ct);
}

/// <summary>识别、点击、同帧验证作品详情；输入失效后从实际可见作品重新定位。</summary>
public sealed class WorkLogoNavigator : IWorkLogoNavigator
{
    private readonly AutomationService _automation;
    private readonly IWorkLogoVision _vision;
    private readonly Func<string, CancellationToken, Task<bool>> _returnHome;
    private readonly IGgeaLog _log;
    private readonly StageNavigationOptions _options;
    private readonly TimeProvider _time;
    private readonly RecognitionEvidence _evidence;
    private readonly StageMenuEntryService _menuEntry;
    private readonly DownloadDialogService? _downloadDialog;
    private readonly bool _allowDownload;

    /// <summary>主界面恢复由现有导航能力注入，避免关卡服务直接依赖窗口。</summary>
    public WorkLogoNavigator(AutomationService automation, IWorkLogoVision vision,
        Func<string, CancellationToken, Task<bool>> returnHome, IGgeaLog log, StageNavigationOptions options,
        TimeProvider? timeProvider = null, RecognitionEvidence? evidence = null,
        StageMenuEntryService? menuEntry = null, DownloadDialogService? downloadDialog = null,
        bool allowDownload = false)
    {
        _automation = automation;
        _vision = vision;
        _returnHome = returnHome;
        _log = log;
        _options = options;
        _downloadDialog = downloadDialog;
        _allowDownload = allowDownload;
        _menuEntry = menuEntry ?? new StageMenuEntryService(automation, log);
        _time = timeProvider ?? TimeProvider.System;
        _evidence = evidence ?? new RecognitionEvidence(System.IO.Path.Combine(AppContext.BaseDirectory,
            "logs", "recognition"), log);
    }

    /// <summary>屏幕坐标下的作品点击边界，覆盖挑战/历史入口及底部导航栏。</summary>
    public static bool IsSafeClick(int x, int y)
        => x >= 20 && x < 1900 && y >= 104 && y < 975 && !(x < 720 && y >= 830);

    /// <summary>导航无进展时有界停止；继续提示每次命中重新计时，不限制连续弹层的累计处理时长。</summary>
    public async Task<WorkLogoNavigationResult> OpenAsync(string serial, UiLayout mainLayout,
        UiLayout stageLayout, StageWorkPositionsData positions, IReadOnlyList<HardStageEntry> works,
        HardStageEntry target, CancellationToken ct)
    {
        var targetPosition = positions.Find(target.Id);
        if (targetPosition is null)
            return Fail("目标作品缺少地图坐标");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Max(1, _options.LogoNavigationTimeoutMs));
        var token = timeout.Token;
        double? measuredOffset = null;
        WorkLogoMatch? previous = null;
        var recoveries = 0;
        var observationStarted = _time.GetTimestamp();
        var waitingReported = false;
        var awaitingOverview = false;
        var waitStarted = 0L;
        byte[]? lastMissingFrame = null;
        long? lastContinuationAt = null;
        // 只比较截图测得的偏移；边界或吞手势时停止重复拖动，交给有限等待与恢复。
        double? swipeOffset = null;
        var swipeStalled = false;
        long? downloadStarted = null;
        try
        {
            for (var attempt = 0; attempt < _options.LogoNavigationMaxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                // 以采集开始时刻判断是否已给页面足够时间；本帧识图耗时不能冒充新画面的等待。
                var frameStarted = _time.GetTimestamp();
                var state = await _vision.ObserveAsync(serial, target, works, token);
                lastMissingFrame = null;
                token.ThrowIfCancellationRequested();
                // 半透明解锁弹层后仍可能识别出 Logo 和按钮；继续提示必须先于所有导航动作。
                var frame = state.ScreenshotPng ?? await _automation.ScreenshotAsync(serial, token);
                var screen = ImageCodec.DecodePng(frame);
                var download = _downloadDialog?.RecognizeForNavigation(screen);
                // 下载弹窗优先于背景作品与全局继续；只点一次，直到真实目标详情出现才恢复导航。
                if (download?.Download is not null || downloadStarted is not null)
                {
                    if (download?.Download is not null && !_allowDownload)
                    {
                        if (download.Cancel is not null)
                            await _automation.TapAsync(serial, download.Cancel.CenterX, download.Cancel.CenterY, token);
                        return Fail("主要关卡检测到下载弹窗，未允许下载游戏数据，已停止当前作品导航。");
                    }
                    if (downloadStarted is null)
                    {
                        await _automation.TapAsync(serial, download!.Download!.CenterX, download.Download.CenterY, token);
                        downloadStarted = _time.GetTimestamp();
                        timeout.CancelAfter(TimeSpan.FromMinutes(10));
                        _log.Info("主要关卡检测到下载弹窗，已点击下载，最多等待十分钟确认目标作品页面。");
                        previous = null;
                        measuredOffset = null;
                        swipeOffset = null;
                        swipeStalled = false;
                        awaitingOverview = false;
                        lastContinuationAt = null;
                    }
                    if (_time.GetElapsedTime(downloadStarted.Value) >= TimeSpan.FromMinutes(10))
                        return Fail("主要关卡下载后等待目标作品页面超过十分钟，已停止。");
                    if (download?.Download is not null || !state.MainStageVisible || !state.TargetTitleVisible ||
                        (state.SelectButton is null && !state.SelectClosed))
                    {
                        attempt--;
                        await Delay(_options.WorkSelectPollIntervalMs, token);
                        continue;
                    }
                    downloadStarted = null;
                    timeout.CancelAfter(Math.Max(1, _options.LogoNavigationTimeoutMs));
                    observationStarted = _time.GetTimestamp();
                    waitingReported = false;
                    _log.Info("下载后已确认目标作品页面，恢复主要关卡导航。");
                }
                var continuation = await _automation.GlobalScreens.HandleContinuationAsync(serial, frame, token,
                    allowSkip: state.Works.Count == 0);
                if (continuation != GlobalScreenResult.None)
                {
                    lastMissingFrame = frame;
                    lastContinuationAt = _time.GetTimestamp();
                    // 包括冷却中的命中：每次看到提示都重新给出识别预算，不累计消耗导航次数。
                    timeout.CancelAfter(Math.Max(1, _options.LogoNavigationTimeoutMs));
                    attempt = -1;
                    observationStarted = lastContinuationAt.Value;
                    waitingReported = false;
                    previous = null;
                    measuredOffset = null;
                    swipeOffset = null;
                    swipeStalled = false;
                    awaitingOverview = false;
                    await Delay(Math.Max(GameTiming.ConfirmationIntervalMs, _options.WorkSelectPollIntervalMs), token);
                    continue;
                }
                if (lastContinuationAt is long lastSeen)
                {
                    // 短暂动画空帧不等于弹层链结束；最后一次命中后持续观察五秒，每次新提示重新计时。
                    if (_time.GetElapsedTime(lastSeen).TotalMilliseconds < GameTiming.RecognitionIntervalMs)
                    {
                        attempt--;
                        await Delay(GameTiming.ConfirmationIntervalMs, token);
                        continue;
                    }
                    lastContinuationAt = null;
                    timeout.CancelAfter(Math.Max(1, _options.LogoNavigationTimeoutMs));
                    observationStarted = _time.GetTimestamp();
                    _log.Info("连续观察未再出现 TAP TO NEXT 或略过提示，恢复作品导航");
                }
                // 暗态选择按钮仍代表未解锁；必须同时证明它属于目标作品。
                if (state.TargetTitleVisible && state.SelectClosed)
                    return new(StageNavigationResult.Fail(StageNavigationFailure.WorkStageLocked,
                        "目标作品标题与暗态选择按钮已同时确认"), measuredOffset);
                if (state.TargetTitleVisible && state.SelectButton is not null)
                {
                    await _automation.TapAsync(serial, state.SelectButton.CenterX, state.SelectButton.CenterY, token);
                    await Delay(_options.PageSettleMs, token);
                    return new(StageNavigationResult.Ok("目标作品标题与选择按钮同帧确认，已点击选择"), measuredOffset);
                }

                // 点击后给网络加载和动画留出轮询时间，避免把迟到的页面当作点击失败。
                if (awaitingOverview && Environment.TickCount64 - waitStarted < _options.WorkSelectWaitTimeoutMs)
                {
                    await Delay(_options.WorkSelectPollIntervalMs, token);
                    continue;
                }
                awaitingOverview = false;
                // 标题错误但存在真实选择按钮时已处于其他作品详情，先返回再解释画布坐标。
                if (!state.TargetTitleVisible && state.MainStageVisible && (state.SelectButton is not null || state.SelectClosed))
                {
                    var back = stageLayout.Find("stageBack") ?? throw new InvalidOperationException("缺少 stageBack");
                    await _automation.TapAsync(serial, back.CenterX, back.CenterY, token);
                    await Delay(_options.PageSettleMs, token);
                    measuredOffset = null;
                    swipeOffset = null;
                    swipeStalled = false;
                    previous = null;
                    observationStarted = _time.GetTimestamp();
                    waitingReported = false;
                    continue;
                }
                var visible = state.Works.Where(match => positions.Find(match.WorkId) is not null).ToList();
                var found = visible.Where(match => match.WorkId == target.Id)
                    .OrderByDescending(match => match.Confidence).FirstOrDefault();
                if (found is not null)
                {
                    measuredOffset = targetPosition.CanvasX - found.X;
                    CheckSwipeProgress(measuredOffset.Value);
                    if (IsSafeClick(found.X, found.Y))
                    {
                        // 连续两次识别到相同目标且位置停稳才点击，拒绝滑动中的旧坐标。
                        if (previous is not null && Math.Abs(previous.X - found.X) <= 5 && Math.Abs(previous.Y - found.Y) <= 5)
                        {
                            await _automation.TapAsync(serial, found.X, found.Y, token);
                            _log.Info($"Logo 点击 {target.Work}: ({found.X},{found.Y})，分数 {found.Confidence:F3}");
                            previous = null;
                            awaitingOverview = true;
                            waitStarted = Environment.TickCount64;
                            await Delay(_options.PageSettleMs, token);
                            observationStarted = _time.GetTimestamp();
                            waitingReported = false;
                        }
                        else
                        {
                            previous = found;
                            await Delay(Math.Clamp(_options.WorkSelectPollIntervalMs, 0, GameTiming.ConfirmationIntervalMs), token);
                        }
                        continue;
                    }
                    // 左下方被固定入口覆盖时，将目标移向屏幕右侧，绝不点击挑战/历史入口。
                    if (!swipeStalled)
                    {
                        swipeOffset = measuredOffset;
                        await ShiftTowardAsync(serial, found.X - 1100, token);
                        previous = null;
                        observationStarted = _time.GetTimestamp();
                        waitingReported = false;
                        continue;
                    }
                }

                previous = null;
                if (found is null && visible.Count > 0)
                {
                    var anchor = visible.OrderByDescending(match => match.Confidence).First();
                    var position = positions.Find(anchor.WorkId)!;
                    measuredOffset = position.CanvasX - anchor.X;
                    CheckSwipeProgress(measuredOffset.Value);
                    var expectedX = targetPosition.CanvasX - measuredOffset.Value;
                    _log.Info($"根据作品 {anchor.WorkId}（{anchor.Confidence:F3}）重新定位 {target.Id}，预计 X={expectedX:F0}");
                    // 预计已在画面内部就等待识别，不强求居中；初代位于地图左端，无法移到 X=1100。
                    // 预测位置只决定是否继续拖动，绝不授权点击。
                    // 左下固定入口上方还有奖励提示，且 Logo 有面积：配置中心 Y=770 的
                    // 00 也会被遮住。此保守区域仅授权避让手势，不扩大禁点区或授权预测点击。
                    var possiblyCovered = expectedX < 820 && targetPosition.CanvasY >= 730;
                    if (!swipeStalled && (expectedX < 300 || expectedX > 1600 || possiblyCovered))
                    {
                        if (possiblyCovered)
                            _log.Info($"作品 {target.Id} 预计位于左下遮挡区域（X={expectedX:F0},Y={targetPosition.CanvasY}），先向右移出遮挡再识别");
                        swipeOffset = measuredOffset;
                        await ShiftTowardAsync(serial, expectedX - 1100, token);
                        observationStarted = _time.GetTimestamp();
                        waitingReported = false;
                        continue;
                    }
                }

                // 即使其他作品可见，也保留目标未命中的同帧证据，不能在超时后另截画面。
                lastMissingFrame = frame;

                // 页面标题也可能尚未加载，不能只为“标题已出现”的空画布提供等待。
                // 只在实际输入后重置窗口，连续空帧不会无限续期；总超时与次数上限仍生效。
                if (_time.GetElapsedTime(observationStarted, frameStarted).TotalMilliseconds <
                    _options.LogoObservationWaitTimeoutMs)
                {
                    if (!waitingReported)
                    {
                        _log.Info($"等待主要关卡作品加载（最多 {_options.LogoObservationWaitTimeoutMs / 1000.0:F1} 秒）：" + Describe(state));
                        waitingReported = true;
                    }
                    await Delay(_options.WorkSelectPollIntervalMs, token);
                    continue;
                }

                // 两种识别均失败并超过加载窗口才留证；保存实际判断帧，不能另截变化后的页面。
                if (lastMissingFrame is not null)
                {
                    _log.Warn($"作品 {target.Id} 导航未完成，保存截图诊断：" + Describe(state));
                    await _evidence.SaveAsync(visible.Count == 0 ? "main-stage-logo-and-continuation-missing" : "main-stage-target-logo-missing", serial,
                        lastMissingFrame, ImageCodec.DecodePng(lastMissingFrame));
                }
                if (recoveries++ >= _options.LogoRecoveryMaxCount)
                    return Fail($"作品导航重试已达上限，未点击选择；{Describe(state)}");
                _log.Warn($"作品观察超时，恢复 {recoveries}/{_options.LogoRecoveryMaxCount}：{Describe(state)}；" +
                    "重新进入主界面 → 关卡 → 主要关卡");
                if (!await _returnHome(serial, token))
                    return Fail("无法确认已返回主界面");
                var main = stageLayout.Find("mainStage") ?? throw new InvalidOperationException("缺少 mainStage");
                var entry = await _menuEntry.OpenFromHomeAsync(serial, mainLayout, _options.PageSettleMs, token);
                if (!entry.Success) return new WorkLogoNavigationResult(entry, null);
                await _automation.TapAsync(serial, main.CenterX, main.CenterY, token);
                await Delay(_options.PageSettleMs, token);
                measuredOffset = null;
                swipeOffset = null;
                swipeStalled = false;
                lastMissingFrame = null;
                observationStarted = _time.GetTimestamp();
                waitingReported = false;
            }
            if (lastMissingFrame is not null)
                await _evidence.SaveAsync("main-stage-logo-and-continuation-missing", serial,
                    lastMissingFrame, ImageCodec.DecodePng(lastMissingFrame));
            return Fail("作品导航超过最大观察次数");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (lastMissingFrame is not null)
                await _evidence.SaveAsync("main-stage-logo-and-continuation-missing", serial,
                    lastMissingFrame, ImageCodec.DecodePng(lastMissingFrame));
            return Fail(downloadStarted is not null
                ? "主要关卡下载后等待目标作品页面超过十分钟，已停止。"
                : "作品导航超时，已停止操作");
        }

        // 手势结束后仅消费一次待核对偏移，停滞状态保留到重建页面，避免每帧重新尝试。
        void CheckSwipeProgress(double offset)
        {
            if (swipeOffset is not double before) return;
            swipeOffset = null;
            if (Math.Abs(offset - before) > 10) return;
            swipeStalled = true;
            _log.Warn($"作品 {target.Id} 导航滑动后画布无明显位移，可能已到边界；停止重复滑动，等待识别后有限恢复");
        }
    }

    /// <summary>普通日志直接记录页面信号，避免售后只能从重复入口点击猜测识别失败阶段。</summary>
    private static string Describe(WorkLogoObservation state)
        => $"主要关卡标题={state.MainStageVisible}，目标标题={state.TargetTitleVisible}，" +
           $"选择按钮={state.SelectButton is not null}，暗态选择={state.SelectClosed}，可见作品数={state.Works.Count}" +
           (state.Diagnostic is null ? string.Empty : $"；{state.Diagnostic}");

    /// <summary>位移换算仅用来决定一次手势；不将结果当作真实坐标，距离较远时分段重新识图。</summary>
    private async Task ShiftTowardAsync(string serial, double canvasDistance, CancellationToken ct)
    {
        var ratio = (_options.SwipeLeftStartX - _options.SwipeLeftEndX) / (double)Math.Max(1, _options.SwipeCanvasDelta);
        var distance = Math.Clamp((int)Math.Round(Math.Abs(canvasDistance) * ratio), 120, 700);
        // 中间横向空带避开底部固定入口；每次最多移动约一屏，保持观察区域重叠。
        var start = canvasDistance > 0 ? 1500 : 420;
        var end = canvasDistance > 0 ? start - distance : start + distance;
        await _automation.SwipeAsync(serial, start, 400, end, 400, _options.SwipeDurationMs, ct);
        if (_options.SwipeSettleMs <= 5000) await Delay(_options.SwipeSettleMs, ct);
        else await _automation.WaitForCanvasStillAsync(serial, new Roi(0, 120, 1920, 760), ct);
    }

    /// <summary>导航失败不留下可依赖的画布偏移。</summary>
    private static WorkLogoNavigationResult Fail(string message)
        => new(StageNavigationResult.Fail(StageNavigationFailure.InvalidContext, message), null);

    /// <summary>所有等待均支持用户取消。</summary>
    private static Task Delay(int ms, CancellationToken ct) => Task.Delay(Math.Max(0, ms), ct);
}

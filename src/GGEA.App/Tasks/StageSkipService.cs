using System.IO;
using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.App.Tasks;

/// <summary>一次略过操作的执行结果。</summary>
public sealed record StageSkipResult(
    bool Success,
    string Message,
    bool IsUnavailable = false,
    bool IsApInsufficient = false)
{
    public static StageSkipResult Ok(string message) => new(true, message);
    public static StageSkipResult Fail(string message, bool isUnavailable = false)
        => new(false, message, isUnavailable);
    public static StageSkipResult ApInsufficient(string message)
        => new(false, message, IsUnavailable: true, IsApInsufficient: true);
}

/// <summary>
/// 独立略过服务：负责“略过 → 执行 → SKIP RESULT → OK → 处理升级 → 确认次数上限暗态”。
/// 不依赖具体业务场景，后续奖励、活动等位置都可以复用。
/// </summary>
public sealed class StageSkipService
{
    private readonly AutomationService _automation;
    private readonly IGgeaLog _log;
    private readonly StageSkipOptions _options;
    private readonly RecognitionEvidence? _evidence;
    private readonly ApStopSignal? _apStopSignal;
    // 同一服务会连续略过多个关卡；模板按需解码一次，正常返回时不加载升级素材。
    private readonly Dictionary<string, TemplateImages> _postSkipTemplates = new();

    public StageSkipService(
        AutomationService automation,
        IGgeaLog log,
        StageSkipOptions? options = null,
        RecognitionEvidence? evidence = null, ApStopSignal? apStopSignal = null)
    {
        _automation = automation;
        _log = log;
        _options = options ?? new StageSkipOptions();
        _evidence = evidence;
        _apStopSignal = apStopSignal;
    }

    /// <summary>只有完整配置回复 AP 标题与按钮时才允许识别，通用关闭按钮不能提供业务证据。</summary>
    private bool DetectApRecovery => !string.IsNullOrWhiteSpace(_options.ApRecoveryPromptTemplatePath) &&
        !string.IsNullOrWhiteSpace(_options.ApRecoveryCloseTemplatePath);

    /// <summary>
    /// 判断“略过”按钮当前是否可用。
    /// 在略过按钮区域识别“次数上限”暗态模板：识别到表示今日次数已用尽，不可略过。
    /// </summary>
    public async Task<bool> IsSkipEnabledAsync(
        string serial,
        UiLayout stageLayout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.SkipCountLimitTemplatePath))
        {
            return true;
        }

        var countLimit = RequireAnchor(stageLayout, "skipCountLimit", "关卡布局缺少 skipCountLimit 锚点");
        var wait = new WaitOptions
        {
            Threshold = _options.SkipCountLimitThreshold,
            Roi = countLimit.Roi,
        };
        var match = await _automation.FindImageAsync(
            serial,
            _options.SkipCountLimitTemplatePath,
            wait,
            ct);
        if (match is not null)
        {
            _log.Warn("略过不可用：识别到次数上限暗态");
            return false;
        }

        return true;
    }

    /// <summary>对当前选中的 HARD 关卡执行一次完整略过。</summary>
    public async Task<StageSkipResult> SkipCurrentAsync(
        string serial,
        UiLayout stageLayout,
        CancellationToken ct = default)
    {
        // 先报告当前关卡的不可用状态；素材校验仍在所有消费动作之前完成。
        if (!await IsSkipEnabledAsync(serial, stageLayout, ct))
        {
            return StageSkipResult.Fail("略过不可用（次数上限）", isUnavailable: true);
        }

        // 没有结果识别素材时，长睡眠不能替代成功证据；在消费动作之前拒绝旧兼容路径。
        if (_options.SkipResultTimeoutMs > 5000 && string.IsNullOrWhiteSpace(_options.SkipResultTemplatePath) &&
            !DetectApRecovery)
            return StageSkipResult.Fail("未配置略过结果识别素材，不能用固定等待确认执行结果。");

        var stageSkip = RequireAnchor(stageLayout, "stageSkip", "关卡布局缺少 stageSkip 锚点");
        var execute = RequireAnchor(stageLayout, "skipDialogExecute", "关卡布局缺少 skipDialogExecute 锚点");

        _log.Info($"点击略过: ({stageSkip.CenterX},{stageSkip.CenterY})");
        await _automation.TapAsync(serial, stageSkip.CenterX, stageSkip.CenterY, ct);
        await DelayAsync(_options.SkipDialogWaitMs, ct);

        _log.Info($"点击执行: ({execute.CenterX},{execute.CenterY})");
        await _automation.TapAsync(serial, execute.CenterX, execute.CenterY, ct);

        return await CompleteExecutedSkipAsync(serial, stageLayout, ct);
    }

    /// <summary>执行已发送后共用结果等待、AP 保护和返回确认；不会再次点击略过或执行。</summary>
    public async Task<StageSkipResult> CompleteExecutedSkipAsync(
        string serial, UiLayout stageLayout, CancellationToken ct = default)
    {
        var skipCancel = RequireAnchor(stageLayout, "skipDialogCancel", "关卡布局缺少 skipDialogCancel 锚点");
        var resultOk = RequireAnchor(stageLayout, "skipResultOk", "关卡布局缺少 skipResultOk 锚点");
        var resultTitle = string.IsNullOrWhiteSpace(_options.SkipResultTemplatePath)
            ? null
            : RequireAnchor(stageLayout, "skipResultTitle", "关卡布局缺少 skipResultTitle 锚点");
        // 诊断取证与识别共用底部区域定义，不保留已退役的窄文字锚点。
        var bottom = RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, stageLayout.ScreenWidth, stageLayout.ScreenHeight);
        var apRecoveryClose = !DetectApRecovery
            ? null
            : new UiAnchor { Name = "apRecoveryClose", X = bottom.X, Y = bottom.Y, Width = bottom.Width, Height = bottom.Height };

        if (string.IsNullOrWhiteSpace(_options.SkipResultTemplatePath) &&
            !DetectApRecovery)
        {
            await DelayAsync(_options.SkipResultTimeoutMs, ct);
        }
        else
        {
            // 单位展示可连续出现，使用确认采样频率，避免通用五秒间隔耗尽整段奖励预算。
            var resultPollMs = _options.ContinueUnitAcquisition
                ? GameTiming.ConfirmationIntervalMs : GameTiming.RecognitionIntervalMs;
            var resultReady = false;
            var acquisitionContinueCount = 0;
            var recoveringLateResult = false;
            var deadline = Environment.TickCount64 + Math.Max(0, _options.SkipResultTimeoutMs);
            var nextProgressAt = Environment.TickCount64 + 15000;
            ScreenImage? lastScreen = null;
            _log.Info($"等待 SKIP RESULT…（最长 {_options.SkipResultTimeoutMs / 1000.0:F1} 秒）");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Environment.TickCount64 >= deadline)
                {
                    // 保留略过上下文处理迟到结果；只有结果标题成立才允许 OK，不能盲目返回主页。
                    if (recoveringLateResult || resultTitle is null || _options.LateResultRecoveryTimeoutMs <= 0)
                        break;
                    recoveringLateResult = true;
                    deadline = Environment.TickCount64 + _options.LateResultRecoveryTimeoutMs;
                    _log.Warn($"略过正常等待超时，开始迟到结果恢复（最多 {_options.LateResultRecoveryTimeoutMs}ms，不重复执行略过）。");
                }

                // 同帧比较结果与 AP 弹窗，避免每轮两次 ADB 截图把短暂结果帧只交给 AP 检测而丢掉。
                // 公共截图入口仍先校验规格和网络错误；取消后不能发送结果 OK。
                var screen = await _automation.CaptureScreenAsync(serial, ct);
                lastScreen = screen;

                if (resultTitle is not null)
                {
                    var resultWait = new WaitOptions
                    {
                        Threshold = _options.SkipResultThreshold,
                        Roi = resultTitle.Roi,
                    };
                    var resultMatch = await _automation.MatchImageAsync(
                        screen,
                        _options.SkipResultTemplatePath,
                        resultWait,
                        ct);
                    if (resultMatch is not null)
                    {
                        if (recoveringLateResult) _log.Info("已确认迟到的 SKIP RESULT，继续结果收尾与返回校验。");
                        resultReady = true;
                        break;
                    }
                }

                if (apRecoveryClose is not null && await _automation.MatchImageAsync(
                    screen, _options.ApRecoveryPromptTemplatePath,
                    new WaitOptions { Threshold = 0.90, Roi = new Roi((screen.Width - 400) / 2, 0, 400, 550) }, ct) is not null)
                {
                    // 略过直接打开回复方式选择页，不出现出击前的 AP 不足询问；专属标题成立后才能查找关闭按钮。
                    var recoveryWait = new WaitOptions
                    {
                        Threshold = _options.SkipResultThreshold,
                        // 同帧底部识别通用关闭模板，不依赖旧 AP 文字裁剪区域。
                        Roi = RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, screen.Width, screen.Height),
                    };
                    var recoveryMatch = await _automation.MatchImageAsync(
                        screen,
                        _options.ApRecoveryCloseTemplatePath,
                        recoveryWait,
                        ct);
                    if (recoveryMatch is not null)
                    {
                        _apStopSignal?.Report("AP 不足，当前略过流程已停止，请补充 AP 后重新开始。");
                        _log.Warn("AP 不足，关闭回复AP弹窗");
                        await _automation.TapAsync(
                            serial,
                            recoveryMatch.CenterX,
                            recoveryMatch.CenterY,
                            ct);
                        await DelayAsync(_options.ApRecoverySettleMs, ct);

                        _log.Info("取消略过窗口");
                        await _automation.TapAsync(
                            serial,
                            skipCancel.CenterX,
                            skipCancel.CenterY,
                            ct);
                        await DelayAsync(_options.SkipDialogWaitMs, ct);

                        return StageSkipResult.ApInsufficient("AP 不足，当前略过任务结束");
                    }
                }

                // 获得单位展示可能先于 SKIP RESULT；结果和 AP 检查优先，不以继续提示代替成功证据。
                if (_options.ContinueUnitAcquisition && acquisitionContinueCount < 6)
                {
                    var next = await Task.Run(() => _automation.TapToNext.Recognize(screen, ct), ct);
                    ct.ThrowIfCancellationRequested();
                    if (next is not null)
                    {
                        _log.Info("略过获得单位展示：已确认 TAP TO NEXT，点击后继续等待 SKIP RESULT。");
                        await _automation.TapAsync(serial, next.CenterX, next.CenterY, ct);
                        // 一次略过最多获得六台机体；每次确认并点击后重新给予完整动画预算。
                        // 以点击次数限制续期，静止提示或丢失点击也不会导致无限等待。
                        acquisitionContinueCount++;
                        if (!recoveringLateResult)
                            deadline = Environment.TickCount64 + Math.Max(0, _options.SkipResultTimeoutMs);
                        _log.Info($"奖励继续 {acquisitionContinueCount}/6：继续等待下一台机体或 SKIP RESULT，剩余预算 {Math.Max(0, deadline - Environment.TickCount64)}ms。");
                        await DelayWithinBudgetAsync(deadline, resultPollMs, ct);
                        continue;
                    }
                }

                if (Environment.TickCount64 >= nextProgressAt)
                {
                    _log.Info("略过执行后尚未确认 SKIP RESULT 或 AP 弹窗，继续等待加载/奖励动画…");
                    nextProgressAt = Environment.TickCount64 + 15000;
                }
                await DelayWithinBudgetAsync(deadline, resultPollMs, ct);
            }

            if (!resultReady)
            {
                ct.ThrowIfCancellationRequested();
                if (_evidence is not null && lastScreen is not null)
                    await SaveResultTimeoutEvidenceAsync(serial, lastScreen, resultTitle, apRecoveryClose, ct);
                ct.ThrowIfCancellationRequested();
                return StageSkipResult.Fail($"等待 SKIP RESULT 超时（正常段 {_options.SkipResultTimeoutMs}ms，迟到恢复 {Math.Max(0, _options.LateResultRecoveryTimeoutMs)}ms，已点击奖励继续 {acquisitionContinueCount}/6 次），未确认略过结果页");
            }
        }

        ct.ThrowIfCancellationRequested();
        _log.Info($"点击结果 OK: ({resultOk.CenterX},{resultOk.CenterY})");
        await _automation.TapAsync(serial, resultOk.CenterX, resultOk.CenterY, ct);
        await DelayAsync(_options.SettleAfterOkMs, ct);

        if (!await ConfirmPostSkipAsync(serial, stageLayout, ct))
            return StageSkipResult.Fail(string.IsNullOrWhiteSpace(_options.PostSkipPageTemplatePath)
                ? "未确认升级提示已关闭并返回目标关卡，已停止后续操作"
                : "未确认返回强化培育关卡，已停止后续操作");

        return StageSkipResult.Ok("略过执行完成");
    }

    /// <summary>结果 OK 后按任务确认关卡页：强化培育检查专用标题，AP 任务处理升级并确认次数上限。</summary>
    public async Task<bool> ConfirmPostSkipAsync(string serial, UiLayout stageLayout, CancellationToken ct = default)
    {
        // 结果加载和返回确认分别计时；生产入口保留原 10 秒返回预算，旧调用方的短预算仍兼容。
        var timeoutMs = _options.PostSkipTimeoutMs ?? Math.Min(20000, _options.SkipResultTimeoutMs);
        if (!string.IsNullOrWhiteSpace(_options.PostSkipPageTemplatePath))
        {
            // 强化培育只确认返回标题；使用单次识别轮询，不触发全局继续提示或玩家升级识别。
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            while (Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var match = await _automation.FindImageAsync(serial, _options.PostSkipPageTemplatePath,
                    new WaitOptions { Roi = new Roi(0, 0, 737, 104), Threshold = 0.92 }, ct);
                if (match is not null)
                {
                    _log.Info("略过结果 OK 后已确认返回强化培育关卡");
                    return true;
                }
                await DelayWithinBudgetAsync(deadline, GameTiming.RecognitionIntervalMs, ct);
            }
            _log.Warn("略过结果 OK 后未识别到强化培育关卡标题");
            return false;
        }
        if (string.IsNullOrWhiteSpace(_options.SkipCountLimitTemplatePath) && _options.PostSkipConfirmation is null) return true;
        var countLimit = _options.PostSkipConfirmation is null ? RequireAnchor(stageLayout, "skipCountLimit", "关卡布局缺少 skipCountLimit 锚点") : null;
        if (!string.IsNullOrWhiteSpace(_options.PlayerRankUpTemplatePath) &&
            !string.IsNullOrWhiteSpace(_options.PlayerRankUpNextTemplatePath))
        {
            var count = _options.PostSkipConfirmation is null ? LoadPostSkipTemplate(_options.SkipCountLimitTemplatePath) : null;
            var matcher = new TemplateMatcher();
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            var nextRankCheck = Environment.TickCount64 + Math.Max(0, _options.PlayerRankUpCheckDelayMs);
            var rankUpSeen = false;
            while (Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var png = await _automation.ScreenshotAsync(serial, ct);
                var checkRank = Environment.TickCount64 >= nextRankCheck;
                var state = await Task.Run(() =>
                {
                    var screen = ImageCodec.DecodePng(png);
                    // SSD 对暗色模板可能误命中纯色过渡画面，再核对纹理，防止提前报告成功。
                    TemplateMatchResult? Match(TemplateImages templates, Roi roi, double threshold)
                        => templates.Match(template => MatchSingle(template, roi, threshold), ct);
                    // 同份素材同时通过像素与纹理，不混合语言和通用证据。
                    TemplateMatchResult? MatchSingle(ScreenImage template, Roi roi, double threshold)
                    {
                        var candidate = matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
                            template.Width, template.Height, threshold, roi);
                        if (candidate is null) return null;
                        using var source = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
                        using var target = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC4, template.Bgra);
                        using var sourceGray = new Mat(); using var targetGray = new Mat(); using var correlation = new Mat();
                        using var sourcePatch = new Mat(source, new Rect(candidate.X, candidate.Y, template.Width, template.Height));
                        Cv2.CvtColor(sourcePatch, sourceGray, ColorConversionCodes.BGRA2GRAY);
                        Cv2.CvtColor(target, targetGray, ColorConversionCodes.BGRA2GRAY);
                        Cv2.MatchTemplate(sourceGray, targetGray, correlation, TemplateMatchModes.CCoeffNormed);
                        var score = correlation.At<float>(0, 0);
                        return float.IsFinite(score) && score >= 0.80 ? candidate : null;
                    }
                    // 高置信度正常按钮直接完成；较弱命中仍保留原阈值，但需等升级检查排除遮罩。
                    var countMatch = count is not null ? Match(count, countLimit!.Roi, _options.SkipCountLimitThreshold) : null;
                    var complete = _options.PostSkipConfirmation?.Invoke(screen, ct) ?? (countMatch is not null);
                    if (countMatch is { Confidence: >= 0.98 } && !rankUpSeen)
                        return (RankUp: false, Next: (TemplateMatchResult?)null, Complete: true);
                    if (!checkRank)
                        return (RankUp: false, Next: (TemplateMatchResult?)null, Complete: false);
                    var title = LoadPostSkipTemplate(_options.PlayerRankUpTemplatePath);
                    var rankUp = Match(title, new Roi(650, 210, 620, 110), 0.92) is not null;
                    // 第一次只确认升级，动画等待后再从新截图读取继续提示。
                    return (RankUp: rankUp,
                        Next: rankUp && rankUpSeen ? Match(LoadPostSkipTemplate(_options.PlayerRankUpNextTemplatePath),
                            new Roi(780, 925, 370, 90), 0.92) : null,
                        Complete: !rankUp && complete);
                }, ct);
                ct.ThrowIfCancellationRequested();
                if (state.Complete) return true;
                if (checkRank)
                    nextRankCheck = Environment.TickCount64 + Math.Max(0, _options.PlayerRankUpPollIntervalMs);
                if (state.RankUp && !rankUpSeen)
                {
                    rankUpSeen = true;
                    var animationWait = Math.Max(0, _options.PlayerRankUpAnimationWaitMs);
                    // 只补偿一次动画等待，不因反复命中无限延长总超时。
                    deadline += animationWait;
                    _log.Info("检测到玩家升级，正在确认继续提示…");
                    if (animationWait <= 5000) await DelayAsync(animationWait, ct);
                    else await DelayAsync(GameTiming.ConfirmationIntervalMs, ct);
                    nextRankCheck = Environment.TickCount64;
                    continue;
                }
                if (state.Next is not null)
                {
                    _log.Info("检测到 PLAYER RANK UP，点击继续并等待返回关卡页");
                    await _automation.TapAsync(serial, state.Next.CenterX, state.Next.CenterY, ct);
                    // 丢失点击时允许下轮重试，动画中的旧坐标不会连续触发点击。
                    await DelayWithinBudgetAsync(deadline, GameTiming.RecognitionIntervalMs, ct);
                }
                else
                    await DelayWithinBudgetAsync(deadline, GameTiming.RecognitionIntervalMs, ct);
            }
            _log.Warn("结果 OK 后未能确认返回关卡页（升级提示或网络等待超时）");
            return false;
        }

        if (_options.PostSkipConfirmation is not null)
        {
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            while (Environment.TickCount64 < deadline)
            {
                var screen = await _automation.CaptureScreenAsync(serial, ct);
                if (await Task.Run(() => _options.PostSkipConfirmation(screen, ct), ct)) return true;
                await DelayWithinBudgetAsync(deadline, GameTiming.RecognitionIntervalMs, ct);
            }
            return false;
        }

        // 未启用升级素材的旧调用保留原有等待行为。
        {
            var wait = new WaitOptions
            {
                TimeoutMs = timeoutMs,
                PollIntervalMs = GameTiming.RecognitionIntervalMs,
                Threshold = _options.SkipCountLimitThreshold,
                Roi = countLimit!.Roi,
            };
            var countMatch = await _automation.WaitForImageAsync(
                serial,
                _options.SkipCountLimitTemplatePath,
                wait,
                ct);
            if (countMatch is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>结果等待真正超时后保存最后观察帧和本阶段模板，避免只得到后续主界面恢复的无关候选。</summary>
    private async Task SaveResultTimeoutEvidenceAsync(string serial, ScreenImage screen,
        UiAnchor? resultTitle, UiAnchor? apRecoveryClose, CancellationToken ct)
    {
        try
        {
            var candidates = await Task.Run(() =>
            {
                var matcher = new TemplateMatcher();
                var observations = new List<RecognitionCandidate>();
                foreach (var (anchor, path) in new[] { (resultTitle, _options.SkipResultTemplatePath),
                             (apRecoveryClose, _options.ApRecoveryCloseTemplatePath) })
                {
                    if (anchor is null) continue;
                    var template = LoadPostSkipTemplate(path).Primary;
                    var best = matcher.MatchCandidate(screen.Bgra, screen.Width, screen.Height, template.Bgra,
                        template.Width, template.Height, _options.SkipResultThreshold, anchor.Roi, ct);
                    observations.Add(new(Path.GetFileName(path), ImageCodec.EncodePng(template.Bgra, template.Width, template.Height),
                        template.Width, template.Height, anchor.Roi, _options.SkipResultThreshold, best));
                }
                return observations.ToArray();
            }, ct);
            ct.ThrowIfCancellationRequested();
            await _evidence!.SaveAsync("skip-result-timeout", serial,
                ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height), screen, candidates);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 取证是诊断副作用；素材或编码异常不能替换已经确认的业务超时，取消仍须向上传递。
            _log.Warn($"略过结果超时取证失败: {ex.GetType().Name}；原识别结果保持不变");
        }
    }

    /// <summary>返回确认及超时取证按需加载素材，避免正常略过重复 PNG 解码。</summary>
    private TemplateImages LoadPostSkipTemplate(string path)
    {
        if (!_postSkipTemplates.TryGetValue(path, out var template))
            _postSkipTemplates[path] = template = new TemplateImages(path);
        return template;
    }

    /// <summary>轮询睡眠只消费剩余预算，避免短超时被完整轮询间隔拖长；用户取消正常传播。</summary>
    private static async Task DelayWithinBudgetAsync(long deadline, int intervalMs, CancellationToken ct)
    {
        var wakeAt = Math.Min(deadline, Environment.TickCount64 + Math.Max(0, intervalMs));
        ct.ThrowIfCancellationRequested();
        // 定时器可能略早唤醒；按单调时钟补足同一采样间隔，不能在截止前额外截取一帧。
        while (wakeAt - Environment.TickCount64 is var remaining && remaining > 0)
            await Task.Delay((int)Math.Min(int.MaxValue, remaining), ct);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>仅在配置时间大于零时执行可取消等待。</summary>
    private static Task DelayAsync(int milliseconds, CancellationToken ct)
        => milliseconds <= 0 ? Task.CompletedTask : Task.Delay(milliseconds, ct);

    /// <summary>读取必需的布局锚点，缺失时终止。</summary>
    private static UiAnchor RequireAnchor(UiLayout layout, string name, string error)
        => layout.Find(name) ?? throw new InvalidOperationException(error);
}

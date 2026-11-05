using System.IO;
using System.Collections.Concurrent;
using GGEA.App.Device;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Automation;

/// <summary>
/// 自动化原语：WaitForImage、ClickImage、ClickIfExists、WaitImageDisappear、Tap、Swipe。
/// 统一采用「识图 → 获得目标中心 → 点击 → 确认下一页面」的思路。
/// </summary>
public sealed partial class AutomationService : IDisposable
{
    private readonly IAdbService _adb;
    private readonly TemplateMatcher _matcher;
    private readonly ResourceResolver _resources;
    private readonly IGgeaLog _log;
    /// <summary>会话共享的全局策略；异常入口复用弹窗诊断，所有任务共享继续动作状态。</summary>
    public GlobalScreenHandler GlobalScreens { get; }
    private readonly int _tapToNextCheckDelayMs;
    private readonly ScreenshotCapture _capture;

    // 模板按“完整路径 + 最后修改时间”缓存，轮询时不重复解码
    private readonly ConcurrentDictionary<string, LoadedTemplate> _templateCache = new();

    /// <summary>
    /// 创建自动化服务。
    /// </summary>
    /// <param name="tapToNextTemplatePaths">旧模板列表参数保留调用兼容性；继续文字统一使用底部 OCR，不再读取这些图片。</param>
    /// <param name="tapToNextCheckDelayMs">等待轮询持续多少毫秒仍无进展后才开始检测 TAP TO NEXT；
    /// 默认 10 秒，避免频繁全流程识别拖慢任务。</param>
    /// <param name="skipTemplatePath">可选的右上角略过模板，与继续遮罩共用等待延迟和安全优先级。</param>
    /// <param name="connectionErrorDetector">可选的连接错误拦截器；启用后每帧优先检查错误并终止等待。</param>
    public AutomationService(
        IAdbService adb,
        TemplateMatcher matcher,
        IGgeaLog log,
        IEnumerable<string>? tapToNextTemplatePaths = null,
        int tapToNextCheckDelayMs = 10000,
        ConnectionErrorDetector? connectionErrorDetector = null,
        string? skipTemplatePath = null, OnnxTextRecognizer? textOcr = null, ResourceResolver? resources = null)
    {
        _adb = adb;
        _matcher = matcher;
        _resources = resources ?? ConfigService.Resources;
        _log = log;
        GlobalScreens = new GlobalScreenHandler(adb, log, tapToNextTemplatePaths, connectionErrorDetector, skipTemplatePath, textOcr);
        _capture = new ScreenshotCapture(adb, log);
        _tapToNextCheckDelayMs = Math.Max(0, tapToNextCheckDelayMs);
    }

    /// <summary>释放本会话自建的 OCR；借用模型时不改变窗口模型的生命周期。</summary>
    public void Dispose() => GlobalScreens.Dispose();

    /// <summary>全局与专属继续目标共用 OCR，但各流程保留自己的点击和完成条件。</summary>
    public OnnxTapToNextRecognizer TapToNext => GlobalScreens.TapToNext;

    /// <summary>等待底部完整继续文字；专属入口禁用全局点击，返回后由调用方确认本阶段动作。</summary>
    public async Task<TemplateMatchResult?> WaitForTapToNextAsync(string serial, WaitOptions? options = null,
        CancellationToken ct = default)
    {
        var result = await WaitForImageOrTapToNextAsync(serial, "TAP TO NEXT OCR",
            (options ?? new WaitOptions()) with { AllowGlobalContinuation = false }, ct,
            matchTarget: (screen, token) => Task.Run(() => TapToNext.Recognize(screen, token), token));
        return result.Match;
    }

    /// <summary>
    /// 轮询截图直到目标出现或超时。
    /// </summary>
    /// <returns>匹配结果；超时返回 null。</returns>
    public async Task<TemplateMatchResult?> WaitForImageAsync(
        string serial,
        string templatePath,
        WaitOptions? options = null,
        CancellationToken ct = default,
        Func<ScreenImage, CancellationToken, Task<TemplateMatchResult?>>? matchTarget = null,
        Func<int>? nextPollIntervalMs = null)
    {
        var wait = options ?? new WaitOptions();
        // 多入口识别借用同一帧，仍由公共等待循环负责取消与全局恢复。
        var template = matchTarget is null ? PrepareTemplatePath(templatePath) : null;
        var deadline = Environment.TickCount64 + wait.TimeoutMs;
        var startedAt = Environment.TickCount64;
        // 每次等待最多补充一次恢复预算，避免连续遮罩导致无限等待。
        var recoveryBudgetGranted = false;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var checkTapToNext = Environment.TickCount64 - startedAt >= _tapToNextCheckDelayMs;
            var attempt = await TryMatchOnceCoreAsync(serial, template, wait, checkTapToNext, ct, matchTarget: matchTarget);
            ExtendRecoveryDeadline(attempt, wait, ref deadline, ref recoveryBudgetGranted);
            var match = attempt.Match;
            if (match is not null)
            {
                _log.Info($"识别成功: {Path.GetFileName(templatePath)} -> {match}");
                return match;
            }

            // 专属流程可在候选出现后短间隔复核；截图、取消和总预算仍由本循环统一管理。
            await Task.Delay(Math.Max(1, nextPollIntervalMs?.Invoke() ?? wait.PollIntervalMs), ct);
        }

        _log.Warn($"等待图片超时（{wait.TimeoutMs}ms）: {templatePath}");
        return null;
    }

    /// <summary>
    /// 等待目标图片出现；等待期间若全局 TAP TO NEXT 被点击且新截图确认遮罩消失，
    /// 视为等待完成，返回 TapToNextHandled=true（调用方不应再点击目标）。
    /// </summary>
    /// <param name="stopWhen">每帧识别前执行的终止检查；返回 true 时停止等待，优先于全局点击。</param>
    /// <param name="matchTarget">可选的专属目标识别；仍在同帧停止检查后执行，不接管等待或点击。</param>
    /// <returns>目标命中时 Match 非空；全局 TAP TO NEXT 被处理时 TapToNextHandled=true；超时或 stopWhen 请求停止时返回 (null, false)，停止原因由调用方保存。</returns>
    public async Task<(TemplateMatchResult? Match, bool TapToNextHandled)> WaitForImageOrTapToNextAsync(
        string serial,
        string templatePath,
        WaitOptions? options = null,
        CancellationToken ct = default,
        string? waitingMessage = null,
        Func<ScreenImage, CancellationToken, Task<bool>>? stopWhen = null,
        Func<ScreenImage, CancellationToken, Task<TemplateMatchResult?>>? matchTarget = null)
    {
        var wait = options ?? new WaitOptions();
        var template = matchTarget is null ? PrepareTemplatePath(templatePath) : null;
        var deadline = Environment.TickCount64 + wait.TimeoutMs;
        var startedAt = Environment.TickCount64;
        // 每次等待最多补充一次恢复预算，避免连续遮罩导致无限等待。
        var recoveryBudgetGranted = false;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var checkTapToNext = Environment.TickCount64 - startedAt >= _tapToNextCheckDelayMs;
            var attempt = await TryMatchOnceCoreAsync(serial, template, wait, checkTapToNext, ct, waitingMessage, stopWhen, matchTarget);
            ExtendRecoveryDeadline(attempt, wait, ref deadline, ref recoveryBudgetGranted);
            if (attempt.Stopped) return (null, false);
            if (attempt.Match is not null)
            {
                _log.Info($"识别成功: {Path.GetFileName(templatePath)} -> {attempt.Match}");
                return (attempt.Match, false);
            }

            if (attempt.TapToNextDisappeared)
            {
                _log.Info($"等待 {Path.GetFileName(templatePath)} 期间已点击全局 TAP TO NEXT，新截图确认遮罩消失");
                return (null, true);
            }

            await Task.Delay(wait.PollIntervalMs, ct);
        }

        _log.Warn($"等待图片超时（{wait.TimeoutMs}ms）: {templatePath}");
        return (null, false);
    }

    /// <summary>
    /// 截取一帧并识别目标（单次尝试）。
    /// </summary>
    public async Task<TemplateMatchResult?> FindImageAsync(
        string serial,
        string templatePath,
        WaitOptions? options = null,
        CancellationToken ct = default)
    {
        var wait = options ?? new WaitOptions();
        var template = PrepareTemplatePath(templatePath);
        return await TryMatchOnceAsync(serial, template, wait, checkTapToNext: false, ct);
    }

    /// <summary>透明立绘使用 RGB 掩码匹配；兼容未处理的旧模板原比例识别。</summary>
    public async Task<TemplateMatchResult?> FindRoadmapImageAsync(string serial, string templatePath,
        WaitOptions wait, CancellationToken ct = default)
    {
        var (png, _) = await CaptureValidatedAsync(serial, ct);
        return await MatchRoadmapImageAsync(png, templatePath, wait, ct);
    }

    /// <summary>复用调用方已截取的路线图帧，保证大小模板与位移检查看到同一个画面。</summary>
    public async Task<TemplateMatchResult?> MatchRoadmapImageAsync(byte[] png, string templatePath,
        WaitOptions wait, CancellationToken ct = default)
    {
        return await Task.Run(() => TemplateFallback.Match(templatePath, path =>
        {
            var template = LoadTemplate(path);
            var hasTransparency = false;
            for (var offset = 3; offset < template.Bgra.Length; offset += 4)
                if (template.Bgra[offset] < 128) { hasTransparency = true; break; }
            if (hasTransparency)
                return new RoadmapUnitRecognizer().Match(png, template.Roadmap.Value, wait.Threshold,
                    wait.Roi, ct, wait.RoadmapFixedScales, wait.RoadmapLargeOnly);
            var screen = ImageCodec.DecodePng(png);
            return MatchSingle(screen, template, wait, ct);
        }, ct, _resources), ct);
    }

    /// <summary>已有截图仅解码一次，两种素材在同帧使用相同阈值和搜索区域。</summary>
    public Task<TemplateMatchResult?> MatchImageAsync(byte[] png, string templatePath,
        WaitOptions wait, CancellationToken ct = default)
        => Task.Run(() => MatchTemplates(ImageCodec.DecodePng(png), templatePath, wait, ct), ct);

    /// <summary>公共等待已校验的截图可供多个入口复用，回退不额外截图或点击。</summary>
    public Task<TemplateMatchResult?> MatchImageAsync(ScreenImage screen, string templatePath,
        WaitOptions wait, CancellationToken ct = default)
        => Task.Run(() => MatchTemplates(screen, templatePath, wait, ct), ct);

    /// <summary>缓存按物理路径隔离，命中结果保留实际素材尺寸，避免通用素材中心偏移。</summary>
    private TemplateMatchResult? MatchTemplates(ScreenImage screen, string path, WaitOptions wait, CancellationToken ct)
        => TemplateFallback.Match(path, candidate => MatchSingle(screen, LoadTemplate(candidate), wait, ct), ct, _resources);

    /// <summary>单个素材始终使用原匹配器，不降低阈值或改变区域。</summary>
    private TemplateMatchResult? MatchSingle(ScreenImage screen, LoadedTemplate template, WaitOptions wait, CancellationToken ct)
        => _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
            template.Width, template.Height, wait.Threshold, ResolveRoi(wait, screen), ct: ct);

    /// <summary>
    /// 等待目标出现后点击其中心。
    /// </summary>
    /// <returns>是否成功完成点击。</returns>
    public async Task<bool> ClickImageAsync(
        string serial,
        string templatePath,
        WaitOptions? options = null,
        CancellationToken ct = default)
    {
        var match = await WaitForImageAsync(serial, templatePath, options, ct);
        if (match is null)
        {
            return false;
        }

        await _adb.TapAsync(serial, match.CenterX, match.CenterY, ct);
        _log.Info($"已点击目标中心 ({match.CenterX}, {match.CenterY})");
        return true;
    }

    /// <summary>
    /// 目标存在则点击，不存在则跳过（用于非关键按钮）。
    /// </summary>
    public async Task<bool> ClickIfExistsAsync(
        string serial,
        string templatePath,
        WaitOptions? options = null,
        CancellationToken ct = default)
    {
        var match = await FindImageAsync(serial, templatePath, options, ct);
        if (match is null)
        {
            _log.Info($"目标不存在，跳过点击: {templatePath}");
            return false;
        }

        await _adb.TapAsync(serial, match.CenterX, match.CenterY, ct);
        _log.Info($"已点击目标中心 ({match.CenterX}, {match.CenterY})");
        return true;
    }

    /// <summary>
    /// 等待目标连续两帧未命中；截图失败抛出异常，不能证明消失。
    /// </summary>
    /// <returns>目标是否在超时前消失。</returns>
    public async Task<bool> WaitImageDisappearAsync(
        string serial,
        string templatePath,
        WaitOptions? options = null,
        CancellationToken ct = default)
    {
        var wait = options ?? new WaitOptions();
        var template = PrepareTemplatePath(templatePath);
        var deadline = Environment.TickCount64 + wait.TimeoutMs;
        var startedAt = Environment.TickCount64;
        // 每次等待最多补充一次恢复预算，避免连续遮罩导致无限等待。
        var recoveryBudgetGranted = false;
        var absentFrames = 0;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var checkTapToNext = Environment.TickCount64 - startedAt >= _tapToNextCheckDelayMs;
            var attempt = await TryMatchOnceCoreAsync(serial, template, wait, checkTapToNext, ct);
            ExtendRecoveryDeadline(attempt, wait, ref deadline, ref recoveryBudgetGranted);
            // 动作会改变页面，不能把动作发送成功或动作前的帧计为消失证据。
            absentFrames = !attempt.FrameConsumed && attempt.Match is null ? absentFrames + 1 : 0;
            if (absentFrames >= 2)
            {
                _log.Info($"图片已消失: {Path.GetFileName(templatePath)}");
                return true;
            }

            await Task.Delay(wait.PollIntervalMs, ct);
        }

        _log.Warn($"等待图片消失超时（{wait.TimeoutMs}ms）: {templatePath}");
        return false;
    }

    public Task TapAsync(string serial, int x, int y, CancellationToken ct = default)
        => _adb.TapAsync(serial, x, y, ct);

    /// <summary>截取并校验一帧 PNG；购买最终授权等要求连续可靠观察的场景可明确禁止重试。</summary>
    public async Task<byte[]> ScreenshotAsync(string serial, CancellationToken ct = default, bool retryOnFailure = true)
    {
        var (png, _) = await _capture.CaptureAsync(serial, ct, retryOnFailure);
        return png;
    }

    /// <summary>复用公共捕获已经解码的有效画面，避免推图循环再次解码同一 PNG。</summary>
    public async Task<ScreenImage> CaptureScreenAsync(string serial, CancellationToken ct = default, bool retryOnFailure = true)
    {
        var (_, screen) = await _capture.CaptureAsync(serial, ct, retryOnFailure);
        return screen;
    }

    /// <summary>仅供专属流程读取同帧证据；回调不得点击，消费授权由调用方复核。</summary>
    internal Task<(ScreenImage Screen, T Observation)> CaptureAndObserveAsync<T>(string serial,
        Func<ScreenImage, CancellationToken, Task<T>> observe, CancellationToken ct, bool retryOnFailure = true)
        => _capture.CaptureAndObserveAsync(serial, observe, ct, retryOnFailure);

    public Task SwipeAsync(string serial, int x1, int y1, int x2, int y2, int durationMs, CancellationToken ct = default)
        => _adb.SwipeAsync(serial, x1, y1, x2, y2, durationMs, ct);

    /// <summary>执行一次真实双指向内捏合，用于把路线图等可缩放画布缩小。</summary>
    public Task PinchInAsync(string serial, int durationMs = 500, CancellationToken ct = default)
        => _adb.PinchInAsync(serial, durationMs, ct);

    public static Task DelayAsync(int milliseconds, CancellationToken ct = default)
        => Task.Delay(milliseconds, ct);

    /// <summary>截图一次并尝试匹配。</summary>
    private async Task<TemplateMatchResult?> TryMatchOnceAsync(
        string serial,
        string template,
        WaitOptions wait,
        bool checkTapToNext,
        CancellationToken ct)
    {
        var result = await TryMatchOnceCoreAsync(serial, template, wait, checkTapToNext, ct);
        return result.Match;
    }

    /// <summary>截图一次并尝试匹配；同时返回全局 TAP TO NEXT 是否被处理。</summary>
    private async Task<TryMatchResult> TryMatchOnceCoreAsync(
        string serial,
        string? template,
        WaitOptions wait,
        bool checkTapToNext,
        CancellationToken ct,
        string? waitingMessage = null,
        Func<ScreenImage, CancellationToken, Task<bool>>? stopWhen = null,
        Func<ScreenImage, CancellationToken, Task<TemplateMatchResult?>>? matchTarget = null)
    {
        var (png, screen) = await CaptureValidatedAsync(serial, ct);
        // 调用方的终止状态优先于目标和全局继续遮罩，避免点击越过失败页面。
        if (stopWhen is not null && await stopWhen(screen, ct))
            return new TryMatchResult(null, false, Stopped: true);

        // 兜底拦截：等待长时间无进展时检查画面是否出现 TAP TO NEXT，
        // 命中则点击进入下一场景，本轮不再匹配原目标，由下一次轮询重新截图确认
        var globalResult = checkTapToNext && wait.AllowGlobalContinuation
            ? await GlobalScreens.HandleContinuationAsync(serial, png, ct, waitingMessage)
            : GlobalScreenResult.None;
        if (globalResult == GlobalScreenResult.Blocked)
            return new TryMatchResult(null, true);
        if (globalResult is GlobalScreenResult.ActionSent or GlobalScreenResult.SkipActionSent)
        {
            // 点击后只读复查遮罩；仍存在时继续等待，失败时终止，绝不沿用旧坐标。
            var (freshPng, freshScreen) = await CaptureValidatedAsync(serial, ct);
            if (stopWhen is not null && await stopWhen(freshScreen, ct))
                return new TryMatchResult(null, false, Stopped: true);
            var remaining = await GlobalScreens.IsContinuationVisibleAsync(freshPng, ct, waitingMessage);
            return new TryMatchResult(null, true, globalResult == GlobalScreenResult.ActionSent && !remaining, ContinuationActionSent: true);
        }

        // 原图复查由工作线程执行，避免大区域未命中时阻塞窗口；结果返回后先响应取消再授权动作。
        // 专属文字识别仍在停止检查之后使用同一帧；更换算法不授予额外点击权限。
        var match = matchTarget is not null ? await matchTarget(screen, ct) : await Task.Run(() => MatchTemplates(screen, template!, wait, ct), ct);
        ct.ThrowIfCancellationRequested();
        return new TryMatchResult(match, false);
    }

    /// <summary>单次识图结果：目标匹配或全局 TAP TO NEXT 被处理。</summary>
    private sealed record TryMatchResult(
        TemplateMatchResult? Match, bool FrameConsumed, bool TapToNextDisappeared = false, bool Stopped = false, bool ContinuationActionSent = false);

    /// <summary>全局动作后为原目标保留完整观察窗口；只补充一次，不降低阈值或把恢复视为成功。</summary>
    private void ExtendRecoveryDeadline(TryMatchResult attempt, WaitOptions wait,
        ref long deadline, ref bool granted)
    {
        if (!attempt.ContinuationActionSent || granted) return;
        granted = true;
        deadline = Math.Max(deadline, Environment.TickCount64 + wait.TimeoutMs);
        _log.Info($"全局继续动作后重新等待原目标（恢复预算 {wait.TimeoutMs}ms，最多一次）");
    }

    /// <summary>与主界面检测共用截图校验和错误语义。</summary>
    private Task<(byte[] Png, ScreenImage Screen)> CaptureValidatedAsync(string serial, CancellationToken ct)
        => _capture.CaptureAsync(serial, ct);

    /// <summary>恢复入口只处理已识别遮罩；true 仅表示动作已发送，调用方必须重新观察页面。</summary>
    public async Task<bool> HandleTapToNextAsync(string serial, CancellationToken ct = default)
        => await GlobalScreens.HandleContinuationAsync(serial, await ScreenshotAsync(serial, ct), ct, allowSkip: false)
            == GlobalScreenResult.ActionSent;

    /// <summary>显式授权恢复时的全局继续处理；阻塞和发送动作均要求重新观察，不得继续旧页面操作。</summary>
    public async Task<GlobalScreenResult> HandleGlobalContinuationAsync(string serial, CancellationToken ct = default)
        => await GlobalScreens.HandleContinuationAsync(serial, await ScreenshotAsync(serial, ct), ct);

    /// <summary>共享区域优先，旧调用方仍可提供独立 ROI。</summary>
    private static Roi? ResolveRoi(WaitOptions wait, ScreenImage screen) => wait.Region is { } region
        ? RecognitionRegions.Resolve(region, screen.Width, screen.Height) : wait.Roi;

    /// <summary>加载模板（带缓存）。</summary>
    private string PrepareTemplatePath(string path)
    {
        // 等待前先验证主素材，避免缺失/损坏时先执行全局恢复动作；补试素材仍延迟读取。
        _ = LoadTemplate(_resources.Resolve(path));
        return path;
    }

    /// <summary>加载指定物理素材，缓存不会把通用文件再次映射为语言文件。</summary>
    private LoadedTemplate LoadTemplate(string templatePath)
    {
        var fullPath = Path.GetFullPath(templatePath);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException($"模板文件不存在: {fullPath}");
        }

        if (_templateCache.TryGetValue(fullPath, out var cached) &&
            cached.LastWriteUtc == fileInfo.LastWriteTimeUtc)
        {
            return cached;
        }

        var image = ImageCodec.DecodePng(File.ReadAllBytes(fullPath));
        var loaded = new LoadedTemplate(image.Bgra, image.Width, image.Height, fileInfo.LastWriteTimeUtc);
        _templateCache[fullPath] = loaded;
        return loaded;
    }

    /// <summary>缓存中的模板数据。</summary>
    private sealed record LoadedTemplate(byte[] Bgra, int Width, int Height, DateTime LastWriteUtc)
    {
        // 延迟准备且只保存托管数据；文件变化时随原有模板缓存一并失效，普通按钮不承担此开销。
        public Lazy<RoadmapUnitTemplate> Roadmap { get; } = new(() =>
            new RoadmapUnitTemplate(ImageCodec.EncodePng(Bgra, Width, Height)));
    }
}

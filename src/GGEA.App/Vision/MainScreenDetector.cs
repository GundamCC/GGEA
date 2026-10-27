using System.Collections.Concurrent;
using System.IO;
using GGEA.App.Config;
using GGEA.App.Automation;
using GGEA.App.Device;
using GGEA.App.Logging;

namespace GGEA.App.Vision;

/// <summary>左下角主界面按钮的识别状态。</summary>
public enum MainScreenButtonState
{
    Activated,
    Inactive,
    NotFound,
}

/// <summary>主界面按钮单帧识别结果。</summary>
public sealed record MainScreenButtonDetection(MainScreenButtonState State, TemplateMatchResult? Match)
{
    /// <summary>同一张截图内的返回按钮候选；只有调用方请求导航观察时才评估。</summary>
    public TemplateMatchResult? BackMatch { get; init; }
    internal RecognitionFrame? Frame { get; init; }
}

/// <summary>
/// 主界面检测器配置。
/// 主界面按钮与返回按钮均限定在固定小区域内，减少匹配耗时与误识别范围。
/// </summary>
public sealed record MainScreenDetectorOptions(
    string ActivatedTemplatePath,
    string InactiveTemplatePath,
    string BackTemplatePath,
    Roi ButtonRoi,
    Roi BackRoi,
    double Threshold,
    int ConfirmationFrames,
    int ConfirmationIntervalMs);

/// <summary>
/// 主界面检测器。
/// 以左下角主界面按钮的激活状态确认主界面；连续多帧命中才放行，
/// 避免加载动画或瞬时画面造成误判。未选中状态和返回按钮供调用方安全导航使用。
/// </summary>
public sealed class MainScreenDetector
{
    /// <summary>与 1920×1080 基准布局一致的默认检测配置。</summary>
    public static readonly MainScreenDetectorOptions DefaultOptions = new(
        "resources/templates/home/home_screen_button_activated.png",
        "resources/templates/home/home_screen_button.png",
        "resources/templates/common/back.png",
        // 关卡页按钮右缘超过 x=282，保留完整模板及少量位置偏移空间。
        new Roi(0, 975, 300, 105),
        new Roi(0, 0, 206, 91),
        Threshold: 0.85,
        ConfirmationFrames: 2,
        ConfirmationIntervalMs: GameTiming.ConfirmationIntervalMs);

    private readonly ScreenshotCapture _capture;
    private readonly GlobalScreenHandler? _failureDiagnostics;
    private readonly ConnectionErrorDetector? _connectionErrorDetector;
    private readonly TemplateMatcher _matcher;
    private readonly MainScreenDetectorOptions _options;
    private readonly ConcurrentDictionary<string, LoadedImage> _cache = new();

    /// <summary>等待器读取同一组确认参数，避免表现层复制帧数与间隔。</summary>
    internal MainScreenDetectorOptions Options => _options;

    /// <summary>创建只读检测器；取证和导航由流程服务负责，检测器不保存任何流程状态。</summary>
    public MainScreenDetector(IAdbService adb, TemplateMatcher matcher, IGgeaLog log,
        MainScreenDetectorOptions? options = null, ConnectionErrorDetector? connectionErrorDetector = null,
        GlobalScreenHandler? globalScreens = null)
    {
        _capture = new ScreenshotCapture(adb, log);
        // 借用组合根持有的诊断器；只读主界面检测不再创建和持有未使用的继续提示 OCR 会话。
        _failureDiagnostics = globalScreens;
        _connectionErrorDetector = connectionErrorDetector;
        _matcher = matcher;
        _options = options ?? DefaultOptions;
    }

    /// <summary>仅供导航超时后检查最后有效帧；单帧主界面观察不执行异常诊断。</summary>
    internal async Task CheckFailureAsync(ScreenImage screen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_failureDiagnostics is not null)
            await _failureDiagnostics.CheckAbnormalScreenAsync(screen, ct);
        else if (_connectionErrorDetector is not null &&
            await Task.Run(() => _connectionErrorDetector.IsVisible(screen, ct), ct))
            throw new ConnectionErrorException();
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>单帧探测仅返回观察，未命中属于正常结果；不能自行保存失败证据。</summary>
    public Task<MainScreenButtonDetection> DetectButtonAsync(string serial, CancellationToken ct = default)
        => ObserveAsync(serial, includeBackButton: false, ct);

    /// <summary>同帧评估导航状态与返回按钮，防止把不同页面的截图拼成一次决策。</summary>
    internal async Task<MainScreenButtonDetection> ObserveAsync(string serial, bool includeBackButton, CancellationToken ct)
    {
        var (png, screen) = await _capture.CaptureAsync(serial, ct);
        // 匹配可能触发精确复查，不占用 WPF 调用线程；动作前仍检查本次流程的取消令牌。
        return await Task.Run(() =>
        {
            var activated = Evaluate(screen, _options.ActivatedTemplatePath, _options.ButtonRoi, ct);
            var inactive = Evaluate(screen, _options.InactiveTemplatePath, _options.ButtonRoi, ct);
            var back = includeBackButton ? Evaluate(screen, _options.BackTemplatePath, _options.BackRoi, ct) : null;
            var activeMatch = Accepted(activated);
            var inactiveMatch = Accepted(inactive);
            var state = activeMatch is not null ? MainScreenButtonState.Activated
                : inactiveMatch is not null ? MainScreenButtonState.Inactive : MainScreenButtonState.NotFound;
            ct.ThrowIfCancellationRequested();
            return new MainScreenButtonDetection(state, activeMatch ?? inactiveMatch)
            {
                BackMatch = back is null ? null : Accepted(back),
                Frame = new RecognitionFrame(serial, png, screen, back is null ? [activated, inactive] : [activated, inactive, back]),
            };
        }, ct);
    }

    /// <summary>只读检查返回按钮，未命中不代表所在页面异常。</summary>
    public async Task<TemplateMatchResult?> FindBackButtonAsync(string serial, CancellationToken ct = default)
    {
        var (_, screen) = await _capture.CaptureAsync(serial, ct);
        return await Task.Run(() =>
        {
            var match = Accepted(Evaluate(screen, _options.BackTemplatePath, _options.BackRoi, ct));
            ct.ThrowIfCancellationRequested();
            return match;
        }, ct);
    }

    /// <summary>只读连续确认；任一帧未激活即返回 false，等待与超时由调用方流程负责。</summary>
    public async Task<bool> IsMainScreenAsync(string serial, CancellationToken ct = default)
    {
        for (var frame = 0; frame < Math.Max(1, _options.ConfirmationFrames); frame++)
        {
            if ((await DetectButtonAsync(serial, ct)).State != MainScreenButtonState.Activated) return false;
            if (frame < Math.Max(1, _options.ConfirmationFrames) - 1)
                await Task.Delay(Math.Max(0, _options.ConfirmationIntervalMs), ct);
        }
        return true;
    }
    /// <summary>沿用原匹配算法和 ROI，保留阈值过滤前的候选，以解释未命中而不重复匹配。</summary>
    private RecognitionCandidate Evaluate(ScreenImage screen, string templatePath, Roi roi, CancellationToken ct)
    {
        RecognitionCandidate? observed = null;
        foreach (var path in ConfigService.Resources.TemplateCandidates(templatePath))
        {
            ct.ThrowIfCancellationRequested();
            var template = Load(path);
            var best = _matcher.MatchCandidate(screen.Bgra, screen.Width, screen.Height,
                template.Bgra, template.Width, template.Height, _options.Threshold, roi, ct);
            observed = new RecognitionCandidate(Path.GetFileName(path), template.Png,
                template.Width, template.Height, roi, _options.Threshold, best);
            if (Accepted(observed) is not null) break;
        }
        return observed!;
    }

    /// <summary>业务仍使用原配置阈值，诊断中的低分候选绝不授权点击。</summary>
    private static TemplateMatchResult? Accepted(RecognitionCandidate candidate)
        => candidate.Best is not null && candidate.Best.Confidence >= candidate.Threshold ? candidate.Best : null;

    /// <summary>加载模板图像并按修改时间缓存，支持运行中替换素材后自动刷新。</summary>
    private LoadedImage Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException($"主界面模板不存在: {fullPath}");
        }

        if (_cache.TryGetValue(fullPath, out var cached) && cached.LastWriteUtc == fileInfo.LastWriteTimeUtc)
        {
            return cached;
        }

        var png = File.ReadAllBytes(fullPath);
        var image = ImageCodec.DecodePng(png);
        var loaded = new LoadedImage(image.Bgra, image.Width, image.Height, fileInfo.LastWriteTimeUtc, png);
        _cache[fullPath] = loaded;
        return loaded;
    }

    /// <summary>缓存中的模板像素数据。</summary>
    private sealed record LoadedImage(byte[] Bgra, int Width, int Height, DateTime LastWriteUtc, byte[] Png);
}

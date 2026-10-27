using System.Collections.Concurrent;
using GGEA.App.Device;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Automation;

/// <summary>全局继续提示的观察结果；命中但冷却中也必须阻止业务使用被遮挡的旧帧。</summary>
public enum GlobalScreenResult
{
    /// <summary>未发现可处理的全局遮罩。</summary>
    None,
    /// <summary>遮罩仍存在，尚未允许再次点击。</summary>
    Blocked,
    /// <summary>已发送动作；调用方必须重新截图，不能据此报告任务成功。</summary>
    ActionSent,
    /// <summary>已点击右上角略过；不能作为 TAP TO NEXT 等待成功的证据。</summary>
    SkipActionSent,
}

/// <summary>
/// 全局识别的统一策略所有者：异常弹窗仅由失败或停滞入口诊断，继续动作由等待/恢复入口明确授权。
/// 不接管下载许可、购买确认、战斗结算等业务决策；同一自动化会话共享按设备隔离的冷却状态。
/// </summary>
public sealed class GlobalScreenHandler : IDisposable
{
    /// <summary>OCR 平均分阈值；所有 TAP TO NEXT 场景使用同一完整文字约束。</summary>
    public const double TapToNextThreshold = OnnxTapToNextRecognizer.AverageThreshold;
    private readonly OnnxTextRecognizer? _ownedOcr;
    /// <summary>各专属流程借用的只读文字识别器；点击仍由各自流程决定。</summary>
    public OnnxTapToNextRecognizer TapToNext { get; }
    private const int CooldownMs = GameTiming.RecognitionIntervalMs;
    // 动作后短间隔读取新帧；重复点击仍受独立的五秒冷却保护。
    private const int SettleDelayMs = GameTiming.ConfirmationIntervalMs;
    private readonly IAdbService _adb;
    private readonly IGgeaLog _log;
    private readonly ConnectionErrorDetector? _connectionErrorDetector;
    private readonly TimeProvider _time;
    private readonly SkipButtonRecognizer _skipRecognizer;
    private readonly ConcurrentDictionary<string, long> _lastActionAt = new();

    /// <summary>创建会话级处理器，继续文字使用 OCR，略过保留模板，不建立后台截图或点击循环。</summary>
    public GlobalScreenHandler(IAdbService adb, IGgeaLog log,
        IEnumerable<string>? tapToNextTemplatePaths = null, ConnectionErrorDetector? connectionErrorDetector = null,
        string? skipTemplatePath = null, OnnxTextRecognizer? textOcr = null)
        : this(adb, log, tapToNextTemplatePaths, connectionErrorDetector, TimeProvider.System, skipTemplatePath, textOcr)
    {
    }

    /// <summary>允许测试以单调时钟验证冷却边界，不依赖机器速度或修改生产等待时长。</summary>
    internal GlobalScreenHandler(IAdbService adb, IGgeaLog log,
        IEnumerable<string>? tapToNextTemplatePaths, ConnectionErrorDetector? connectionErrorDetector, TimeProvider time,
        string? skipTemplatePath = null, OnnxTextRecognizer? textOcr = null)
    {
        _adb = adb;
        _log = log;
        _connectionErrorDetector = connectionErrorDetector;
        // 旧模板参数保留调用兼容性；OCR 不读取素材。注入模型时仅借用，独立会话自己释放。
        _ownedOcr = textOcr is null ? new OnnxTextRecognizer() : null;
        TapToNext = new OnnxTapToNextRecognizer(textOcr ?? _ownedOcr!, log);
        _time = time;
        _skipRecognizer = new SkipButtonRecognizer(skipTemplatePath, new TemplateMatcher(), log);
    }

    /// <summary>只释放独立会话的模型；窗口共享模型由窗口在任务全部退出后释放。</summary>
    public void Dispose() => _ownedOcr?.Dispose();

    /// <summary>仅在流程已失败、超时或停滞时检查当前帧；命中后终止本次恢复，禁止点击弹窗。</summary>
    public async Task CheckAbnormalScreenAsync(ScreenImage screen, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_connectionErrorDetector is not null &&
            await Task.Run(() => _connectionErrorDetector.IsVisible(screen, ct), ct))
            throw new ConnectionErrorException();
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>失败后的延迟诊断仅解释失败原因，不授权继续任务；复用同一网络错误规则。</summary>
    public async Task<bool> DetectConnectionErrorAfterFailureAsync(string serial, CancellationToken ct = default)
        => _connectionErrorDetector is not null &&
            await _connectionErrorDetector.DetectAfterFailureAsync(serial, ct);

    /// <summary>只读检查继续遮罩，供动作后的新帧确认使用。</summary>
    public async Task<bool> IsTapToNextVisibleAsync(byte[] png, CancellationToken ct = default,
        string? waitingMessage = null)
        => await TapToNext.RecognizeAsync(png, ct) is not null;

    /// <summary>动作后的只读确认同时排除继续和略过，仍有演出控件时不能报告继续流程完成。</summary>
    internal async Task<bool> IsContinuationVisibleAsync(byte[] png, CancellationToken ct, string? waitingMessage)
        => await IsTapToNextVisibleAsync(png, ct, waitingMessage) || await RecognizeSkipAsync(png, ct) is not null;

    /// <summary>复用当前 PNG 在工作线程识别略过，避免阻塞界面。</summary>
    private Task<TemplateMatchResult?> RecognizeSkipAsync(byte[] png, CancellationToken ct)
        => Task.Run(() => _skipRecognizer.Recognize(ImageCodec.DecodePng(png), ct), ct);

    /// <summary>
    /// 处理已经过任务终止检查的帧；异常恢复须先显式诊断。只允许一个继续动作，冷却从发送成功时计时。
    /// 调用方持有设备执行权，负责总超时、取消与下一帧确认。
    /// </summary>
    internal async Task<GlobalScreenResult> HandleContinuationAsync(string serial, byte[] png,
        CancellationToken ct, string? waitingMessage = null, bool allowSkip = true,
        RecognitionRegion tapRegion = RecognitionRegion.BottomButtons)
    {
        var match = await TapToNext.RecognizeAsync(png, ct, tapRegion);
        ct.ThrowIfCancellationRequested();
        // 登录奖励/继续目标优先于略过；一帧最多一个动作，不能把略过误报为继续目标完成。
        var skip = match is null && allowSkip ? await RecognizeSkipAsync(png, ct) : null;
        if (match is null && skip is null) return GlobalScreenResult.None;
        if (_lastActionAt.TryGetValue(serial, out var last) && _time.GetElapsedTime(last).TotalMilliseconds < CooldownMs)
            return GlobalScreenResult.Blocked;

        var x = match?.CenterX ?? skip!.CenterX;
        var y = match?.CenterY ?? skip!.CenterY;
        ct.ThrowIfCancellationRequested();
        await _adb.TapAsync(serial, x, y, ct);
        _lastActionAt[serial] = _time.GetTimestamp();
        _log.Info($"全局检测到 {(match is not null ? "TAP TO NEXT" : "右上角略过")}，已发送点击，等待新截图确认: ({x},{y})");
        await Task.Delay(SettleDelayMs, ct);
        return match is not null ? GlobalScreenResult.ActionSent : GlobalScreenResult.SkipActionSent;
    }
}

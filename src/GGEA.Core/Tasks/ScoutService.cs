using GGEA.App.Localization;
using System.IO;
using GGEA.App.Automation;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>一次招募循环需要的模板；启动前统一验证，避免点击后才发现资源缺失。</summary>
public sealed record ScoutTemplates(string Title, string Button, string ExecuteHint, string Execute, string Next, string NoCharacters, string ClosedButton);

/// <summary>明确区分正常完成和资源不足中止，由调用方决定后续导航。</summary>
public enum ScoutResult { NoCharacters, InsufficientResources }

/// <summary>招募循环的状态所有者；每个阶段独立限时，只有专属目标命中才允许点击。</summary>
public sealed class ScoutService
{
    private readonly AutomationService automation;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly int _recognitionTimeoutMs;

    /// <summary>保留短动画等待，招募结果直接轮询；每个识别阶段独立限时。</summary>
    public ScoutService(AutomationService automation) : this(automation, Task.Delay, GameTiming.RecognitionTimeoutMs) { }

    /// <summary>测试替换等待实现，验证时序与取消而不缩短产品等待时间。</summary>
    internal ScoutService(AutomationService automation, Func<int, CancellationToken, Task> delay, int recognitionTimeoutMs = GameTiming.RecognitionTimeoutMs)
    {
        this.automation = automation;
        _delay = delay;
        _recognitionTimeoutMs = recognitionTimeoutMs > 0 ? recognitionTimeoutMs : throw new ArgumentOutOfRangeException(nameof(recognitionTimeoutMs));
    }

    /// <summary>招募标题资源随程序复制与发布。</summary>
    public const string TitleTemplate = "resources/templates/scout/title_scout.png";

    /// <summary>招募按钮模板，复用用户提供的真实游戏素材。</summary>
    public const string ButtonTemplate = "resources/templates/scout/scout.png";

    /// <summary>资源不足时的暗态招募按钮，仅在正常按钮未命中后识别。</summary>
    public const string ClosedButtonTemplate = "resources/templates/scout/scout_disabled.png";

    /// <summary>CAPITAL 招募确认提示，必须与执行按钮同帧出现。</summary>
    public const string ExecuteHintTemplate = "resources/templates/dialog/execute_hint.png";
    /// <summary>确认弹窗的执行按钮。</summary>
    public const string ExecuteTemplate = "resources/templates/dialog/execute.png";
    /// <summary>招募结果页的专属继续提示，不加入全局自动点击池。</summary>
    public const string NextTemplate = "resources/templates/scout/tap_to_next.png";
    /// <summary>招募列表为空的专属模板；按钮缺失本身不作为成功依据。</summary>
    public const string NoCharactersTemplate = "resources/templates/scout/no_char_available.png";

    /// <summary>1920×1080 基准：距右边 0～444、距底边 100～270。</summary>
    public static Roi ButtonRegion => new(1476, 810, 444, 170);

    /// <summary>招募确认提示在顶部 438px、水平居中 1200px 范围内。</summary>
    public static Roi ExecuteHintRegion => new(360, 0, 1200, 438);
    /// <summary>执行按钮沿用下载按钮的居中底部 825×300 范围。</summary>
    public static Roi ExecuteRegion => DownloadDialogService.GetButtonRegion(1920, 1080);
    /// <summary>结果继续文字与所有场景共用全宽底部 200px 区域。</summary>
    public static Roi NextRegion => RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, 1920, 1080);
    /// <summary>空列表只在画面左上角 900×500 范围识别。</summary>
    public static Roi NoCharactersRegion => new(0, 0, 900, 500);

    /// <summary>同帧页面身份确认后的互斥状态：可招募、资源不足或已无可招募角色。</summary>
    private sealed record ScoutPageState(TemplateMatchResult? Button, bool NoCharacters, bool InsufficientResources = false);

    /// <summary>无可招募角色时正常结束；返回招募页后才计入完成轮次，中止和失败不伪装为成功。</summary>
    public async Task<ScoutResult> RunAsync(string serial, ScoutTemplates templates, UiLayout developLayout,
        Action<int, string> report, double titleThreshold = 0.85, CancellationToken ct = default)
    {
        var titleRegion = developLayout.Find("developTitle")?.Roi
            ?? throw new InvalidOperationException(L.Text("缺少页面标题标准区域（developTitle），请检查开发布局配置。"));
        // 在任何资源消耗动作之前确认整条链的模板都存在且可解码。
        foreach (var path in new[] { templates.Title, templates.Button, templates.ExecuteHint, templates.Execute, templates.NoCharacters, templates.ClosedButton })
        {
            ct.ThrowIfCancellationRequested();
            ImageCodec.DecodePng(await File.ReadAllBytesAsync(path, ct));
        }
        automation.TapToNext.Validate(); // 在任何招募消费之前确认 OCR 模型可用。
        var completed = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            report(completed, L.Format($"第 {completed + 1} 轮：正在确认招募页面与招募按钮…"));
            var emptyFrames = 0;
            var page = await WaitAsync(async token =>
            {
                var observed = await ObservePageAsync(serial, templates.Title, templates.Button, titleRegion,
                    titleThreshold, templates.NoCharacters, templates.ClosedButton, token);
                // 空列表模板包含背景，连续两帧（间隔 500ms）确认，排除列表尚未载入的瞬时画面。
                if (observed?.NoCharacters == true)
                    return ++emptyFrames >= 2 ? observed : null;
                emptyFrames = 0;
                return observed;
            }, L.Text("招募标题、招募按钮、资源不足或无可招募角色提示"), ct);
            ct.ThrowIfCancellationRequested();
            if (page.InsufficientResources)
            {
                report(completed, L.Format($"招募资源不足，无法进行下一次招募，已中止自动招募；已完成 {completed} 轮，准备进入主界面识别流程。"));
                return ScoutResult.InsufficientResources;
            }
            if (page.NoCharacters)
            {
                report(completed, L.Format($"自动招募完成，已完成 {completed} 轮，没有可招募的角色。"));
                return ScoutResult.NoCharacters;
            }
            await automation.TapAsync(serial, page.Button!.CenterX, page.Button.CenterY, ct);
            report(completed, L.Text("已点击招募，正在确认执行提示…"));
            await _delay(GameTiming.RecognitionIntervalMs, ct);
            // 提示与执行按钮始终匹配同一帧；捕获失败直接撤销确认，不自动重试旧授权。
            var execute = await WaitAsync(async token =>
            {
                var png = await CaptureAsync(serial, token, retryOnFailure: false);
                if (await MatchAsync(png, templates.ExecuteHint, ExecuteHintRegion, 0.85, token) is null) return null;
                return await MatchAsync(png, templates.Execute, ExecuteRegion, 0.85, token);
            }, L.Text("招募确认提示与执行按钮"), ct);
            ct.ThrowIfCancellationRequested();
            await automation.TapAsync(serial, execute.CenterX, execute.CenterY, ct);
            report(completed, L.Text("已点击执行，正在等待招募结果…"));
            // 结果可能提前出现，直接进入识别，不强制等完六秒。
            var next = await WaitAsync(async token =>
                await automation.TapToNext.RecognizeAsync(await CaptureAsync(serial, token), token),
                L.Text("招募结果 TAP TO NEXT"), ct);
            ct.ThrowIfCancellationRequested();
            await automation.TapAsync(serial, next.CenterX, next.CenterY, ct);
            report(completed, L.Text("已点击 TAP TO NEXT，正在确认返回招募页面…"));
            await WaitAsync(async token =>
                await MatchAsync(await CaptureAsync(serial, token), templates.Title, titleRegion, titleThreshold, token),
                L.Text("返回招募页面"), ct);
            completed++;
            report(completed, L.Format($"已完成 {completed} 轮招募。"));
        }
    }

    /// <summary>每个识别阶段使用独立超时；截图、匹配和轮询等待均受同一取消令牌约束。</summary>
    private async Task<T> WaitAsync<T>(Func<CancellationToken, Task<T?>> observe,
        string target, CancellationToken ct) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_recognitionTimeoutMs);
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var match = await observe(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (match is not null) return match;
                await _delay(GameTiming.RecognitionIntervalMs, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(L.Format($"等待{target}超时，已停止自动招募。"));
        }
    }

    /// <summary>只读截图保留全局网络错误拦截，不允许全局继续动作抢走招募阶段的点击。</summary>
    private async Task<byte[]> CaptureAsync(string serial, CancellationToken ct, bool retryOnFailure = true)
    {
        var png = await automation.ScreenshotAsync(serial, ct, retryOnFailure);
        var screen = ImageCodec.DecodePng(png);
        if (screen.Width != 1920 || screen.Height != 1080)
            throw new InvalidOperationException(L.Text("招募页面识别需要 1920×1080 横屏，请先调整模拟器分辨率。"));
        return png;
    }

    /// <summary>在指定帧和区域中匹配，保持同帧多目标确认，不产生额外截图或点击。</summary>
    private Task<TemplateMatchResult?> MatchAsync(byte[] png, string path, Roi region, double threshold, CancellationToken ct)
        => automation.MatchImageAsync(png, path, new WaitOptions { Threshold = threshold, Roi = region }, ct);

    /// <summary>先确认标题和正常按钮；未命中按钮时优先检查同帧空列表，再判断资源不足。</summary>
    private async Task<ScoutPageState?> ObservePageAsync(string serial, string titlePath, string buttonPath,
        Roi titleRegion, double threshold, string? noCharactersPath, string? closedButtonPath, CancellationToken ct)
    {
        var png = await CaptureAsync(serial, ct);
        if (await MatchAsync(png, titlePath, titleRegion, threshold, ct) is null) return null;
        var button = await MatchAsync(png, buttonPath, ButtonRegion, 0.85, ct);
        if (button is not null) return new ScoutPageState(button, false);
        // 无角色时招募按钮也会变暗，先识别专属空列表证据；调用方仍须连续两帧确认才完成。
        if (noCharactersPath is not null &&
            await MatchAsync(png, noCharactersPath, NoCharactersRegion, 0.85, ct) is not null)
            return new ScoutPageState(null, true);
        if (closedButtonPath is not null &&
            await MatchAsync(png, closedButtonPath, ButtonRegion, 0.85, ct) is not null)
            return new ScoutPageState(null, false, true);
        return null;
    }

    /// <summary>标题和按钮必须在同一帧命中才点击；未命中、异常或取消均不继续操作。</summary>
    public async Task<TemplateMatchResult?> StartAsync(string serial, string templatePath, string buttonTemplatePath,
        UiLayout developLayout, double threshold = 0.85, CancellationToken ct = default)
    {
        var title = developLayout.Find("developTitle")
            ?? throw new InvalidOperationException(L.Text("缺少页面标题标准区域（developTitle），请检查开发布局配置。"));
        var page = await ObservePageAsync(serial, templatePath, buttonTemplatePath, title.Roi, threshold, null, null, ct);
        var button = page?.Button;
        if (button is null) return null;
        // 点击前再次响应取消，不能把已取消任务的识别结果转成输入。
        ct.ThrowIfCancellationRequested();
        await automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
        return button;
    }
}

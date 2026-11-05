using System.IO;
using GGEA.App.Automation;
using GGEA.App.Device;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>
/// 游戏启动入口流程：
/// 打开游戏后等待健康提示页展示（约 3 秒），识别“tap to start”点击进入；
/// 之后依次检查“活动登录奖励页”与“登录奖励页”（都可能不存在），
/// 登录奖励目标迟迟未出现时点击中央唤出控制，并持续处理右上角“略过”直至目标出现；
/// 底部 TAP TO NEXT 使用完整文字 OCR 识别后点击跳过。
/// 模板来自真实游戏截图，不虚构页面与坐标。
/// </summary>
public sealed class GameEntryService
{
    private readonly AutomationService _automation;
    private readonly IGgeaLog _log;
    private readonly TemplateMatcher _matcher = new();

    public GameEntryService(AutomationService automation, IGgeaLog log)
    {
        _automation = automation;
        _log = log;
    }

    /// <summary>
    /// 执行游戏入口流程。
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="tapToStartTemplatePath">tap to start 模板完整路径。</param>
    /// <param name="splashDelayMs">健康提示页展示时长，默认 10000 毫秒。</param>
    /// <param name="networkSettleDelayMs">点击后的网络加载稳定等待时长。</param>
    /// <param name="waitOptions">tap to start 等待参数，默认超时 30 秒。</param>
    /// <param name="tapToNextTemplatePath">活动登录奖励页 tap to next 模板；为空表示不检查该页。</param>
    /// <param name="loginRewardWaitMs">等待活动登录奖励页出现的总超时，默认 8 秒。</param>
    /// <param name="tapToNextLoginRewardTemplatePath">登录奖励页 tap to next 模板；为空表示不检查该页。</param>
    /// <param name="loginRewardSkipTemplatePath">登录奖励演出右上角“略过”模板；为空时只点击中央并继续等待目标。</param>
    /// <param name="loginRewardFallbackDelayMs">目标未出现多久后点击中央并进入“略过”恢复阶段。</param>
    /// <param name="loginRewardAnnouncementTemplatePath">登录奖励结束后可能出现的公告标题模板。</param>
    /// <param name="loginRewardAnnouncementCloseTemplatePath">公告弹窗底部关闭按钮模板。</param>
    /// <param name="loginRewardAnnouncementWaitMs">静默等待公告及命中后等待关闭按钮的单阶段上限。</param>
    /// <param name="loginRewardPageWaitMs">进入恢复阶段后继续等待登录奖励目标的上限，默认 8 秒。</param>
    /// <returns>是否成功点击进入游戏。</returns>
    public async Task<bool> EnterGameAsync(
        string serial,
        string tapToStartTemplatePath,
        int splashDelayMs = 3000,
        int networkSettleDelayMs = 1500,
        WaitOptions? waitOptions = null,
        string? tapToNextTemplatePath = null,
        int loginRewardWaitMs = 8000,
        string? tapToNextLoginRewardTemplatePath = null,
        string? loginRewardSkipTemplatePath = null,
        int loginRewardFallbackDelayMs = 5000,
        string? loginRewardAnnouncementTemplatePath = null,
        string? loginRewardAnnouncementCloseTemplatePath = null,
        int loginRewardAnnouncementWaitMs = 4000,
        int loginRewardPageWaitMs = 8000,
        CancellationToken ct = default)
    {
        // 保留用户设置的短动画时间；更长加载由启动按钮识别负责。
        if (splashDelayMs <= 5000) await Task.Delay(Math.Max(0, splashDelayMs), ct);

        var wait = waitOptions ?? new WaitOptions
        {
            TimeoutMs = 30000,
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = 0.85,
        };

        _log.Info("等待 tap to start…");
        var clicked = await _automation.ClickImageAsync(serial, tapToStartTemplatePath, wait, ct);
        if (!clicked)
        {
            _log.Warn("未找到 tap to start，可能已进入游戏或页面异常");
            return false;
        }

        _log.Info("已点击 tap to start，等待进入游戏…");
        await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);

        // 活动登录奖励页（有时存在）：底部出现 TAP TO NEXT 时点击跳过
        if (!string.IsNullOrWhiteSpace(tapToNextTemplatePath))
        {
            var activityRewardRecognizer = _automation.TapToNext;
            _log.Info($"检查活动登录奖励页（最多等待 {Math.Max(0, loginRewardWaitMs)}ms）…");
            await TrySkipTapToNextAsync(
                serial,
                activityRewardRecognizer,
                networkSettleDelayMs,
                loginRewardWaitMs,
                wait.PollIntervalMs,
                ct);
        }

        // 登录奖励页可能先播放演出：五秒未出现目标时点击中央唤出控制，持续点击右上角“略过”。
        if (!string.IsNullOrWhiteSpace(tapToNextLoginRewardTemplatePath))
        {
            // 阶段所有权仍由登录流程掌握；OCR 只观察底部完整文字，不执行全局恢复点击。
            var loginRewardRecognizer = _automation.TapToNext;
            _log.Info(
                $"检查登录奖励页（先等待 {Math.Max(0, loginRewardFallbackDelayMs)}ms，" +
                $"恢复阶段最多 {Math.Max(0, loginRewardPageWaitMs)}ms）…");
            await TryReachLoginRewardAsync(
                serial,
                loginRewardRecognizer,
                loginRewardSkipTemplatePath,
                loginRewardAnnouncementTemplatePath,
                loginRewardAnnouncementCloseTemplatePath,
                networkSettleDelayMs,
                loginRewardFallbackDelayMs,
                loginRewardAnnouncementWaitMs,
                loginRewardPageWaitMs,
                wait.PollIntervalMs,
                ct);
        }

        return true;
    }

    /// <summary>公告与关闭按钮使用稳定背景模板，阈值与常规业务模板保持一致。</summary>
    private const double AnnouncementThreshold = 0.85;

    /// <summary>公告标题位于画面顶部中央的固定搜索区域。</summary>
    private const int AnnouncementRoiWidth = 200;
    private const int AnnouncementRoiHeight = 128;

    /// <summary>等待 TAP TO NEXT 出现并点击跳过；超时视为页面不存在，直接继续。</summary>
    private async Task TrySkipTapToNextAsync(
        string serial,
        OnnxTapToNextRecognizer recognizer,
        int networkSettleDelayMs,
        int timeoutMs,
        int pollIntervalMs,
        CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            byte[] png;
            try
            {
                png = await _automation.ScreenshotAsync(serial, ct);
            }
            catch (AdbException ex)
            {
                _log.Warn($"活动登录奖励截图失败，本次未处理该页面: {ex.Message}");
                return;
            }

            var match = await recognizer.RecognizeAsync(
                png,
                ct: ct);
            if (match is not null)
            {
                await _automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
                _log.Info($"检测到 TAP TO NEXT，已点击跳过（{match.CenterX},{match.CenterY}）");
                await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);
                return;
            }

            await Task.Delay(Math.Max(0, pollIntervalMs), ct);
        }

        _log.Info($"未检测到 TAP TO NEXT（{timeoutMs}ms），继续进入游戏");
    }

    /// <summary>
    /// 等待登录奖励目标；初始等待结束后点击画面中央，再循环处理右上角“略过”。
    /// 恢复循环始终先识别目标，确保目标出现后不再继续点击演出控件。
    /// </summary>
    private async Task TryReachLoginRewardAsync(
        string serial,
        OnnxTapToNextRecognizer recognizer,
        string? skipTemplatePath,
        string? announcementTemplatePath,
        string? announcementCloseTemplatePath,
        int networkSettleDelayMs,
        int fallbackDelayMs,
        int announcementWaitMs,
        int recoveryTimeoutMs,
        int pollIntervalMs,
        CancellationToken ct)
    {
        var startTick = Environment.TickCount64;
        var fallbackActivated = false;
        var recoveryDeadline = long.MaxValue;
        var skipRecognizer = new SkipButtonRecognizer(skipTemplatePath, _matcher, _log);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            byte[] png;
            try
            {
                png = await _automation.ScreenshotAsync(serial, ct);
            }
            catch (AdbException ex)
            {
                _log.Warn($"登录奖励截图失败，本次未处理该页面: {ex.Message}");
                return;
            }

            var target = await recognizer.RecognizeAsync(
                png,
                ct: ct);
            if (target is not null)
            {
                await _automation.TapAsync(serial, target.CenterX, target.CenterY, ct);
                _log.Info($"检测到登录奖励 TAP TO NEXT，已点击（{target.CenterX},{target.CenterY}）");
                await TryCloseAnnouncementAsync(
                    serial,
                    announcementTemplatePath,
                    announcementCloseTemplatePath,
                    announcementWaitMs,
                    pollIntervalMs,
                    ct);
                return;
            }

            var now = Environment.TickCount64;
            if (!fallbackActivated && now - startTick >= Math.Max(0, fallbackDelayMs))
            {
                var screen = ImageCodec.DecodePng(png);
                var centerX = screen.Width / 2;
                var centerY = screen.Height / 2;
                await _automation.TapAsync(serial, centerX, centerY, ct);
                fallbackActivated = true;
                recoveryDeadline = now + Math.Max(0, recoveryTimeoutMs);
                _log.Info($"登录奖励目标等待超时，已点击画面中央并开始识别略过: ({centerX},{centerY})");
                await Task.Delay(Math.Max(0, pollIntervalMs), ct);
                continue;
            }

            if (fallbackActivated)
            {
                if (now >= recoveryDeadline)
                {
                    break;
                }

                {
                    var screen = ImageCodec.DecodePng(png);
                    var skip = await Task.Run(() => skipRecognizer.Recognize(screen, ct), ct);
                    if (skip is not null)
                    {
                        await _automation.TapAsync(serial, skip.CenterX, skip.CenterY, ct);
                        _log.Info($"检测到登录奖励略过按钮，已点击（{skip.CenterX},{skip.CenterY}）");
                        await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);
                        continue;
                    }
                }
            }

            await Task.Delay(Math.Max(0, pollIntervalMs), ct);
        }

        _log.Info($"登录奖励恢复阶段未检测到 TAP TO NEXT（{recoveryTimeoutMs}ms），继续进入游戏");
    }

    /// <summary>加载可选模板；素材不可用时记录配置问题并返回空。</summary>
    private TemplateImages? LoadOptionalTemplate(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return new TemplateImages(path);
        }
        catch (Exception ex)
        {
            _log.Warn($"{label}模板加载失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 登录奖励后静默等待公告；只有命中公告才继续等待并点击底部关闭按钮。
    /// 公告未出现属于正常分支，不写未命中日志；返回值表示是否实际点击了关闭按钮。
    /// </summary>
    private async Task<bool> TryCloseAnnouncementAsync(
        string serial,
        string? announcementTemplatePath,
        string? closeTemplatePath,
        int waitMs,
        int pollIntervalMs,
        CancellationToken ct)
    {
        var announcementDeadline = Environment.TickCount64 + Math.Max(0, waitMs);
        var announcementTemplate = LoadOptionalTemplate(announcementTemplatePath, "公告");
        if (announcementTemplate is null)
        {
            return false;
        }

        while (Environment.TickCount64 < announcementDeadline)
        {
            ct.ThrowIfCancellationRequested();
            var screen = await TakeScreenshotAsync(serial, ct);
            if (screen is null)
            {
                return false;
            }

            var announcement = announcementTemplate.Match(_matcher, screen,
                AnnouncementThreshold, AnnouncementRoi(screen), ct);
            if (announcement is not null)
            {
                _log.Info($"检测到公告，置信度 {announcement.Confidence:P1}");
                return await TryClickAnnouncementCloseAsync(
                    serial,
                    closeTemplatePath,
                    waitMs,
                    pollIntervalMs,
                    ct);
            }

            await Task.Delay(Math.Max(0, pollIntervalMs), ct);
        }

        return false;
    }

    /// <summary>任务开始时同帧确认公告标题与关闭按钮，避免将奖励 OK 当成公告。</summary>
    public async Task<bool> TryClickAnnouncementCloseIfVisibleAsync(
        string serial,
        string? closeTemplatePath,
        string? announcementTemplatePath,
        CancellationToken ct)
    {
        var closeTemplate = LoadOptionalTemplate(closeTemplatePath, "公告关闭");
        if (closeTemplate is null)
        {
            return false;
        }

        var title = LoadOptionalTemplate(announcementTemplatePath, "公告标题");
        if (title is null) return false;
        var screen = await TakeScreenshotAsync(serial, ct);
        if (screen is null || title.Match(_matcher, screen, AnnouncementThreshold, AnnouncementRoi(screen), ct) is null)
            return false;
        return await TryTapAnnouncementCloseAsync(serial, screen, closeTemplate, ct);
    }

    /// <summary>命中公告后，在画面底部中央等待并点击关闭按钮。</summary>
    private async Task<bool> TryClickAnnouncementCloseAsync(
        string serial,
        string? closeTemplatePath,
        int waitMs,
        int pollIntervalMs,
        CancellationToken ct)
    {
        var closeTemplate = LoadOptionalTemplate(closeTemplatePath, "公告关闭");
        if (closeTemplate is null)
        {
            return false;
        }

        var deadline = Environment.TickCount64 + Math.Max(0, waitMs);
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var screen = await TakeScreenshotAsync(serial, ct);
            if (screen is null)
            {
                return false;
            }

            if (await TryTapAnnouncementCloseAsync(serial, screen, closeTemplate, ct))
            {
                return true;
            }

            await Task.Delay(Math.Max(0, pollIntervalMs), ct);
        }

        _log.Warn("已识别公告，但未在等待时间内找到关闭按钮");
        return false;
    }

    /// <summary>在底部中央匹配并点击关闭按钮，供登录等待与每日任务单帧检查共用。</summary>
    private async Task<bool> TryTapAnnouncementCloseAsync(
        string serial,
        ScreenImage screen,
        TemplateImages closeTemplate,
        CancellationToken ct)
    {
        var close = closeTemplate.Match(_matcher, screen,
            RecognitionRegions.BottomButtonThreshold, AnnouncementCloseRoi(screen), ct);
        if (close is null)
        {
            return false;
        }

        await _automation.TapAsync(serial, close.CenterX, close.CenterY, ct);
        _log.Info($"检测到公告关闭按钮，已点击（{close.CenterX},{close.CenterY}）");
        return true;
    }

    /// <summary>截取并解码当前画面；ADB 截图失败时结束公告处理。</summary>
    private async Task<ScreenImage?> TakeScreenshotAsync(string serial, CancellationToken ct)
    {
        try
        {
            return ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
        }
        catch (AdbException ex)
        {
            _log.Warn($"公告截图失败，本次未处理该页面: {ex.Message}");
            return null;
        }
    }

    /// <summary>计算画面顶部居中的 200×128 公告搜索区域。</summary>
    private static Roi AnnouncementRoi(ScreenImage screen)
    {
        var width = Math.Min(AnnouncementRoiWidth, screen.Width);
        var height = Math.Min(AnnouncementRoiHeight, screen.Height);
        return new Roi(Math.Max(0, (screen.Width - width) / 2), 0, width, height);
    }

    /// <summary>公告关闭按钮与开发确认按钮共用底部按钮区域。</summary>
    private static Roi AnnouncementCloseRoi(ScreenImage screen) =>
        RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, screen.Width, screen.Height);
}

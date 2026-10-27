using System.IO;
using GGEA.App.Automation;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>下载界面的处理结果。</summary>
public enum DownloadDialogAction
{
    /// <summary>等待超时，未出现下载界面。</summary>
    NotVisible,

    /// <summary>已点击下载按钮。</summary>
    DownloadClicked,

    /// <summary>已点击取消按钮。</summary>
    CancelClicked,
}

/// <summary>
/// 下载界面识别与处理服务。
/// 游戏在未下载出击单位动画等数据时会弹出下载界面；
/// 根据“允许 GGEA 下载游戏数据”设置，点击下载或取消按钮。
/// 该界面可能出现在多个功能流程中，因此独立成服务供各处复用。
/// </summary>
public sealed class DownloadDialogService
{
    private readonly AutomationService _automation;
    private readonly TemplateMatcher _matcher;
    private readonly IGgeaLog _log;
    private readonly string _downloadTemplatePath;
    private readonly string _cancelTemplatePath;
    private readonly double _threshold;

    public DownloadDialogService(
        AutomationService automation,
        TemplateMatcher matcher,
        IGgeaLog log,
        string downloadTemplatePath,
        string cancelTemplatePath,
        double threshold)
    {
        _automation = automation;
        _matcher = matcher;
        _log = log;
        _downloadTemplatePath = downloadTemplatePath;
        _cancelTemplatePath = cancelTemplatePath;
        _threshold = threshold;
    }

    /// <summary>下载界面按钮识别结果；未出现的按钮为 null。</summary>
    public sealed record DownloadDialogButtons(TemplateMatchResult? Download, TemplateMatchResult? Cancel);

    /// <summary>通用确认按钮的居中底部区域，下载与招募确认共用同一坐标来源。</summary>
    public static Roi GetButtonRegion(int screenWidth, int screenHeight)
        => new((screenWidth - 825) / 2, screenHeight - 300, 825, 300);

    /// <summary>
    /// 截取一帧，在屏幕下方 30% 区域识别下载/取消按钮。
    /// </summary>
    public async Task<DownloadDialogButtons> RecognizeAsync(string serial, CancellationToken ct = default)
    {
        var png = await _automation.ScreenshotAsync(serial, ct);
        return Recognize(ImageCodec.DecodePng(png));
    }

    /// <summary>在指定帧的屏幕正中下方 825×300 区域识别下载/取消按钮（不额外截图，供并发识别复用同一帧）。</summary>
    public DownloadDialogButtons Recognize(ScreenImage screen)
    {
        // 屏幕正中下方 825×300：下载界面的按钮固定在该区域（与空位出击对话框按钮区域一致）
        var roi = GetButtonRegion(screen.Width, screen.Height);
        var download = Match(screen, _downloadTemplatePath, roi);
        var cancel = Match(screen, _cancelTemplatePath, roi);
        return new DownloadDialogButtons(download, cancel);
    }

    /// <summary>作品导航额外复核按钮中央文字，避免通用蓝色外框把选择按钮误判为下载。</summary>
    public DownloadDialogButtons RecognizeForNavigation(ScreenImage screen)
    {
        var buttons = Recognize(screen);
        if (buttons.Download is null) return new(null, null);
        var text = TemplateFallback.Match(_downloadTemplatePath, candidate =>
        {
            var template = ImageCodec.DecodePng(File.ReadAllBytes(candidate));
            var x = template.Width / 5;
            var y = template.Height / 3;
            var width = template.Width - 2 * x;
            var height = template.Height / 3;
            var pixels = new byte[width * height * 4];
            for (var row = 0; row < height; row++)
                Buffer.BlockCopy(template.Bgra, ((y + row) * template.Width + x) * 4,
                    pixels, row * width * 4, width * 4);
            return _matcher.Match(screen.Bgra, screen.Width, screen.Height, pixels, width, height,
                _threshold, buttons.Download.Region);
        });
        return text is null ? new(null, null) : buttons;
    }

    /// <summary>
    /// 等待下载界面出现并处理：允许下载时点击下载按钮；
    /// 不允许时点击取消按钮并记录警告（由调用方负责通知用户）。超时未出现返回 NotVisible。
    /// </summary>
    public async Task<DownloadDialogAction> HandleIfAppearsAsync(
        string serial,
        bool allowDownload,
        int waitTimeoutMs = GameTiming.ShortRecognitionTimeoutMs,
        int pollIntervalMs = GameTiming.RecognitionIntervalMs,
        CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, waitTimeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var buttons = await RecognizeAsync(serial, ct);

            // 允许下载：命中下载按钮则点击，流程继续
            if (allowDownload && buttons.Download is not null)
            {
                await _automation.TapAsync(serial, buttons.Download.CenterX, buttons.Download.CenterY, ct);
                _log.Info(
                    $"检测到下载界面，已点击下载按钮: ({buttons.Download.CenterX},{buttons.Download.CenterY})");
                return DownloadDialogAction.DownloadClicked;
            }

            // 不允许下载：命中取消按钮则点击并提醒
            if (!allowDownload && buttons.Cancel is not null)
            {
                await _automation.TapAsync(serial, buttons.Cancel.CenterX, buttons.Cancel.CenterY, ct);
                _log.Warn(
                    $"检测到下载界面，未允许下载，已点击取消: ({buttons.Cancel.CenterX},{buttons.Cancel.CenterY})");
                return DownloadDialogAction.CancelClicked;
            }

            await Task.Delay(Math.Max(1, pollIntervalMs), ct);
        }

        return DownloadDialogAction.NotVisible;
    }

    /// <summary>在指定区域匹配模板；模板加载失败返回 null。</summary>
    private TemplateMatchResult? Match(ScreenImage screen, string templatePath, Roi roi)
    {
        try
        {
            return TemplateFallback.Match(templatePath, candidate =>
            {
                var template = ImageCodec.DecodePng(File.ReadAllBytes(candidate));
                return _matcher.Match(
                    screen.Bgra,
                    screen.Width,
                    screen.Height,
                    template.Bgra,
                    template.Width,
                    template.Height,
                    _threshold,
                    roi);
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"下载界面模板不可用，本次无法判断对应按钮: {templatePath}: {ex.Message}");
            return null;
        }
    }
}

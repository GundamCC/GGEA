using System.IO;
using GGEA.App.Device;
using GGEA.App.Logging;
using GGEA.App.Automation;

namespace GGEA.App.Vision;

/// <summary>
/// 在任务失败或停滞后的画面中识别网络错误、数据更新和日期更新弹窗，不参与正常截图轮询。
/// 网络错误标题限定顶部居中 420×350 区域；更新弹窗额外要求同帧正文或按钮证据。
/// </summary>
public sealed class ConnectionErrorDetector
{
    /// <summary>失败后等待弹窗稳定的时长；在此期间任务链不得继续执行。</summary>
    public const int FailureSettleDelayMs = 5000;

    /// <summary>顶部居中搜索区域宽度；兼容简中标题及宽 376 像素的日文标题。</summary>
    public const int SearchWidth = 420;

    /// <summary>覆盖实测标题 Y=205～275 的完整范围，同时保留原顶部标题搜索范围。</summary>
    public const int SearchHeight = 350;

    /// <summary>
    /// 连接错误标题的固定匹配阈值。
    /// 使用较严格阈值，避免“开发路线图显示设置”等顶部居中标题被相似文字误判。
    /// </summary>
    public const double MatchThreshold = 0.90;

    private readonly IAdbService _adb;
    private readonly TemplateMatcher _matcher;
    private readonly IGgeaLog _log;
    private readonly string _templatePath;
    private readonly string _updateTitlePath;
    private readonly string _updateMessagePath;
    private readonly DateUpdateDialogRecognizer _dateUpdate;

    public ConnectionErrorDetector(
        IAdbService adb,
        TemplateMatcher matcher,
        IGgeaLog log,
        string templatePath,
        string? updateTitlePath = null,
        string? updateMessagePath = null,
        DateUpdateDialogRecognizer? dateUpdate = null)
    {
        _adb = adb;
        _matcher = matcher;
        _log = log;
        _templatePath = templatePath;
        _dateUpdate = dateUpdate ?? new DateUpdateDialogRecognizer();
        // 与连接错误共用语言和目录解析；测试可注入独立素材，不更改全局语言。
        // 弹窗标题优先使用同一语言的 dialog 目录；尚未迁移的语言和自定义素材保留原位置。
        var entryDirectory = Path.GetDirectoryName(templatePath)!;
        var dialogTitle = Path.Combine(entryDirectory, "..", "dialog", "data_update_title.png");
        _updateTitlePath = updateTitlePath ?? (File.Exists(dialogTitle)
            ? Path.GetFullPath(dialogTitle) : Path.Combine(entryDirectory, "data_update_title.png"));
        _updateMessagePath = updateMessagePath ?? Path.Combine(Path.GetDirectoryName(templatePath)!, "data_update_message.png");
    }

    /// <summary>等待失败画面稳定后检测一次；网络错误或数据更新命中都要求停止当前任务。</summary>
    public async Task<bool> DetectAfterFailureAsync(
        string serial,
        CancellationToken ct = default,
        int settleDelayMs = FailureSettleDelayMs)
    {
        await Task.Delay(Math.Max(0, settleDelayMs), ct);
        return await IsVisibleAsync(serial, ct);
    }

    /// <summary>立即截取一帧，在顶部居中区域识别网络错误标题。</summary>
    public async Task<bool> IsVisibleAsync(string serial, CancellationToken ct = default)
    {
        try
        {
            var screen = ImageCodec.DecodePng(await _adb.ScreenshotAsync(serial, ct));
            return IsVisible(screen, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ConnectionErrorException)
        {
            // 设备执行层已用同一诊断器确认错误，不能把全局拦截误报为“诊断失败、未发现错误”。
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"网络错误页面检测失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>复用异常入口提供的当前帧检查错误，不额外截图，也不授权任何恢复点击。</summary>
    public bool IsVisible(ScreenImage screen, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            // 日期更新与网络中断共用停止链路；双证据只用于确认弹窗，绝不点击返回主页。
            if (_dateUpdate.Recognize(screen, ct) is not null)
            {
                _log.Warn("检测到日期更新弹窗，按网络中断规则停止任务，等待用户处理。");
                return true;
            }
            var match = TemplateFallback.Match(_templatePath, path =>
            {
                if (!File.Exists(path)) return null;
                var template = ImageCodec.DecodePng(File.ReadAllBytes(path));
                return _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
                    template.Width, template.Height, MatchThreshold, CreateSearchRoi(screen.Width, screen.Height), ct: ct);
            }, ct);

            if (match is null)
            {
                // “更新”二字不能单独决定中止，须同帧确认“数据更新、返回标题”的正文。
                // 数据更新同样只中断任务，不授权任何弹窗按钮点击。
                var title = MatchUpdatePart(screen, _updateTitlePath, CreateSearchRoi(screen.Width, screen.Height), ct);
                if (title is null) return false;
                var message = MatchUpdatePart(screen, _updateMessagePath,
                    new Roi(screen.Width / 4, screen.Height / 3, screen.Width / 2, screen.Height / 3), ct);
                if (message is null) return false;
                _log.Warn($"检测到游戏数据更新弹窗，要求返回标题；按网络中断规则停止任务，标题置信度 {title.Confidence:P1}，正文置信度 {message.Confidence:P1}");
                return true;
            }

            _log.Warn($"检测到网络错误弹窗，置信度 {match.Confidence:P1}");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 诊断失败不能覆盖原任务失败原因；记录后按“未确认网络错误”处理。
            _log.Warn($"网络错误页面检测失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>复用当前帧与固定阈值，缺失语言素材不匹配，取消和损坏素材由上层统一处理。</summary>
    private TemplateMatchResult? MatchUpdatePart(ScreenImage screen, string path, Roi roi, CancellationToken ct)
        => TemplateFallback.Match(path, candidate =>
        {
            if (!File.Exists(candidate)) return null;
            var template = ImageCodec.DecodePng(File.ReadAllBytes(candidate));
            return _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
                template.Width, template.Height, MatchThreshold, roi, ct: ct);
        }, ct);

    /// <summary>根据实际截图尺寸创建顶部水平居中的搜索区域，并安全裁剪到画面边界。</summary>
    public static Roi CreateSearchRoi(int screenWidth, int screenHeight)
    {
        var width = Math.Min(Math.Max(0, screenWidth), SearchWidth);
        var height = Math.Min(Math.Max(0, screenHeight), SearchHeight);
        return new Roi(Math.Max(0, (screenWidth - width) / 2), 0, width, height);
    }
}

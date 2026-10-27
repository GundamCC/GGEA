using GGEA.App.Automation;
using System.IO;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Logging;

namespace GGEA.App.Vision;

/// <summary>
/// 战舰区域定位器。
/// 战舰是“蓝色亮边六边形”，个人基地页面可四处拖动；
/// 使用带遮罩的模板匹配（遮罩只保留蓝色光边），未找到时按四个方向拖动画面重试。
/// </summary>
public sealed class BattleshipLocator
{
    private readonly IAdbService _adb;
    private readonly TemplateMatcher _matcher;
    private readonly IGgeaLog _log;

    public BattleshipLocator(IAdbService adb, TemplateMatcher matcher, IGgeaLog log)
    {
        _adb = adb;
        _matcher = matcher;
        _log = log;
    }

    /// <summary>对当前截图执行一次战舰区域匹配。</summary>
    public async Task<TemplateMatchResult?> FindAsync(
        string serial,
        string templatePath,
        string? maskPath,
        double threshold,
        CancellationToken ct)
    {
        var png = await _adb.ScreenshotAsync(serial, ct);
        var screen = ImageCodec.DecodePng(png);
        return TemplateFallback.Match(templatePath, path =>
        {
            var template = ImageCodec.DecodePng(File.ReadAllBytes(path));
            var mask = string.IsNullOrWhiteSpace(maskPath) ? null :
                ToMask(ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.Resources.ResolveTemplateCompanion(path, maskPath))));
            return _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
                template.Width, template.Height, threshold, mask: mask, ct: ct);
        }, ct);
    }

    /// <summary>
    /// 查找战舰区域；未找到时按“左、右、上、下”循环拖动个人基地画面，最多 maxDrags 次。
    /// </summary>
    public async Task<TemplateMatchResult?> FindWithPanAsync(
        string serial,
        string templatePath,
        string? maskPath,
        int maxDrags = 16,
        int dragDurationMs = 700,
        double threshold = 0.85,
        CancellationToken ct = default)
    {
        // 大范围拖动：左右各 700px、上下各 500px，并加入四个斜向方向
        var directions = new[]
        {
            (-700, 0), (700, 0), (0, -500), (0, 500),
            (-500, -350), (500, -350), (-500, 350), (500, 350),
        };

        for (var attempt = 0; attempt <= maxDrags; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var match = await FindAsync(serial, templatePath, maskPath, threshold, ct);
            if (match is not null)
            {
                _log.Info($"找到战舰区域: ({match.CenterX},{match.CenterY})");
                return match;
            }

            if (attempt == maxDrags)
            {
                break;
            }

            var (dx, dy) = directions[attempt % directions.Length];
            var sx = 960 - dx / 2;
            var sy = 540 - dy / 2;
            var ex = 960 + dx / 2;
            var ey = 540 + dy / 2;
            _log.Info($"未找到战舰区域，拖动画面 ({attempt + 1}/{maxDrags})…");
            await _adb.SwipeAsync(serial, sx, sy, ex, ey, dragDurationMs, ct);
            await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
        }

        _log.Warn("多次拖动后仍未找到战舰区域");
        return null;
    }

    /// <summary>将遮罩图像转为参与标记数组：白色（R&gt;128）有效，黑色屏蔽。</summary>
    private static byte[] ToMask(ScreenImage image)
    {
        var mask = new byte[image.Width * image.Height];
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = image.Bgra[i * 4 + 2] > 128 ? (byte)255 : (byte)0;
        }

        return mask;
    }
}

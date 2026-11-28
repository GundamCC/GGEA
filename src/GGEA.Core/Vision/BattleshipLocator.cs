using GGEA.App.Automation;
using System.IO;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Logging;

namespace GGEA.App.Vision;

/// <summary>
/// 在可拖动的个人基地地图上定位战舰巡航与角色要求入口。
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
    public Task<TemplateMatchResult?> FindWithPanAsync(
        string serial,
        string templatePath,
        string? maskPath,
        int maxDrags = 16,
        int dragDurationMs = 700,
        double threshold = 0.85,
        CancellationToken ct = default)
        => FindButtonWithPanAsync(serial, "战舰区域", templatePath, maskPath, maxDrags, dragDurationMs, threshold, ct);

    /// <summary>从个人基地当前视野查找角色要求文字按钮；未适配的游戏语言直接返回。</summary>
    public Task<TemplateMatchResult?> FindCharacterRequestsWithPanAsync(
        string serial, int maxDrags = 16, int dragDurationMs = 700,
        double threshold = 0.85, CancellationToken ct = default, bool fromHarvest = false)
    {
        ct.ThrowIfCancellationRequested();
        const string template = "resources/templates/base/character_requests/character_requests.png";
        if (!File.Exists(ConfigService.ResolvePath(template)))
        {
            _log.Warn("当前游戏语言缺少角色要求按钮素材");
            return Task.FromResult<TemplateMatchResult?>(null);
        }
        return FindButtonWithPanAsync(serial, "角色要求", template, null,
            fromHarvest ? Math.Min(1, maxDrags) : maxDrags, dragDurationMs, threshold, ct, fromHarvest);
    }

    private async Task<TemplateMatchResult?> FindButtonWithPanAsync(
        string serial, string buttonName, string templatePath, string? maskPath,
        int maxDrags, int dragDurationMs, double threshold, CancellationToken ct, bool fromHarvest = false)
    {
        using var matcher = new ProgressTemplateMatcher();
        async Task<TemplateMatchResult?> Observe()
        {
            var frame = ImageCodec.DecodePng(await _adb.ScreenshotAsync(serial, ct));
            return matcher.Match(frame, templatePath, new(0, 0, frame.Width, frame.Height), threshold, ct, maskPath);
        }
        // 大范围拖动：左右各 700px、上下各 500px，并加入四个斜向方向
        var directions = new[]
        {
            (-700, 0), (700, 0), (0, -500), (0, 500),
            (-500, -350), (500, -350), (-500, 350), (500, 350),
        };

        for (var attempt = 0; attempt <= maxDrags; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // 每个视野先原地等遮挡移开。最后一个视野留足动画时间，命中仍需连续三帧位置一致。
            int timeout = attempt == maxDrags ? GameTiming.RecognitionTimeoutMs : GameTiming.RecognitionIntervalMs;
            var match = await StableObservation.WaitAsync(Observe,
                (a, b) => Math.Abs(a.CenterX - b.CenterX) <= 3 && Math.Abs(a.CenterY - b.CenterY) <= 3,
                timeout, ct, requiredFrames: 3);
            if (match is not null)
            {
                _log.Info($"连续三帧找到{buttonName}: ({match.CenterX},{match.CenterY})");
                return match;
            }

            if (attempt == maxDrags)
            {
                break;
            }

            // 角色要求在舰坞右侧。手指向左拖动画布，视野才会向右移动。
            var (dx, dy) = fromHarvest ? (-1000, 0) : directions[attempt % directions.Length];
            var sx = 960 - dx / 2;
            var sy = 540 - dy / 2;
            var ex = 960 + dx / 2;
            var ey = 540 + dy / 2;
            _log.Info($"未找到{buttonName}，拖动画面 ({attempt + 1}/{maxDrags})…");
            await _adb.DragAsync(serial, sx, sy, ex, ey, dragDurationMs, ct);
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        }

        _log.Warn($"已拖动 {maxDrags} 次，仍未能稳定确认{buttonName}");
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

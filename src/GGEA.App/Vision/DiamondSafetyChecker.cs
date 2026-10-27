using System.IO;
using GGEA.App.Device;
using GGEA.App.Logging;

namespace GGEA.App.Vision;

/// <summary>
/// 钻石风险探测器：命中时否决操作，未命中不能证明免费。
/// 第一步：在弹窗左侧区域查找钻石图标（模板匹配）；
/// 第二步：图标存在时，再检测其右侧的“剩余钻石数量”是否变红。
/// 只有“钻石图标存在 且 红色数量出现”才判定为钻石将被消耗，必须立即中止。
/// 红色数量位置已覆盖三种已知弹窗：回复AP、确认机体补给、商店确认。
/// </summary>
public sealed class DiamondSafetyChecker
{
    /// <summary>判定为“红色数量”的最小红色像素数（防止噪声误判）。</summary>
    public const int DefaultRedPixelThreshold = 30;

    /// <summary>钻石图标匹配的默认置信度阈值。</summary>
    public const double DefaultIconThreshold = 0.85;

    private readonly IAdbService _adb;
    private readonly TemplateMatcher _matcher;
    private readonly IGgeaLog _log;

    public DiamondSafetyChecker(IAdbService adb, TemplateMatcher matcher, IGgeaLog log)
    {
        _adb = adb;
        _matcher = matcher;
        _log = log;
    }

    /// <summary>
    /// 对当前截图执行钻石消耗风险检测。
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="scanRegion">检测区域：从左到右覆盖“持有文字 + 钻石图标 + 剩余钻石数量”。</param>
    /// <param name="diamondIconTemplatePath">钻石图标模板路径。</param>
    /// <param name="redPixelThreshold">红色像素数量阈值。</param>
    /// <param name="iconThreshold">钻石图标匹配置信度阈值。</param>
    /// <returns>true 表示检测到风险，必须中止；false 不能作为安全授权。</returns>
    public async Task<bool> IsDiamondConsumptionRiskAsync(
        string serial,
        Roi scanRegion,
        string diamondIconTemplatePath,
        int redPixelThreshold = DefaultRedPixelThreshold,
        double iconThreshold = DefaultIconThreshold,
        CancellationToken ct = default)
    {
        var png = await _adb.ScreenshotAsync(serial, ct);
        var screen = ImageCodec.DecodePng(png);
        return HasRisk(screen, scanRegion, diamondIconTemplatePath, redPixelThreshold, iconThreshold);
    }

    /// <summary>在调用方提供的同一帧检测风险；false 仅代表未命中，绝不是购买授权。</summary>
    internal bool HasRisk(ScreenImage screen, Roi scanRegion, string diamondIconTemplatePath,
        int redPixelThreshold = DefaultRedPixelThreshold, double iconThreshold = DefaultIconThreshold)
    {
        if (scanRegion.X < 0 || scanRegion.Y < 0 || scanRegion.Width < 2 || scanRegion.Height <= 0 ||
            (long)scanRegion.X + scanRegion.Width > screen.Width ||
            (long)scanRegion.Y + scanRegion.Height > screen.Height)
            throw new InvalidOperationException("钻石风险区域超出截图边界");

        // 第一步：在检测区域左半查找钻石图标
        var iconRoi = new Roi(scanRegion.X, scanRegion.Y, scanRegion.Width / 2, scanRegion.Height);
        var iconMatch = TemplateFallback.Match(diamondIconTemplatePath, path =>
        {
            var iconImage = ImageCodec.DecodePng(File.ReadAllBytes(path));
            return _matcher.Match(screen.Bgra, screen.Width, screen.Height, iconImage.Bgra,
                iconImage.Width, iconImage.Height, iconThreshold, iconRoi);
        });

        if (iconMatch is null)
        {
            _log.Info("钻石风险检测：未检测到钻石图标，无法证明安全，仍须独立确认免费证据");
            return false;
        }

        _log.Info($"钻石安全检测：检测到钻石图标（{iconMatch.CenterX},{iconMatch.CenterY}）");

        // 第二步：图标存在时，检测其右侧的红色剩余数量
        var redLeft = Math.Min(scanRegion.X + scanRegion.Width, iconMatch.CenterX + 20);
        var redWidth = scanRegion.X + scanRegion.Width - redLeft;
        if (redWidth <= 0)
        {
            return false;
        }

        var redRoi = new Roi(redLeft, scanRegion.Y, redWidth, scanRegion.Height);
        var redCount = CountRedPixels(screen.Bgra, screen.Width, redRoi);
        if (redCount >= redPixelThreshold)
        {
            _log.Warn($"钻石安全锁触发：钻石图标存在且检测到红色数量（红色像素 {redCount} ≥ {redPixelThreshold}），禁止消耗钻石");
            return true;
        }

        _log.Info($"钻石风险未命中：红色像素 {redCount} < {redPixelThreshold}，不能据此授权购买");
        return false;
    }

    /// <summary>
    /// 统计区域内“严格红色”像素数量（R 明显高于 G/B，即红色数字/文字）。
    /// 单独提取便于单元测试。
    /// </summary>
    internal static int CountRedPixels(byte[] bgra, int width, Roi roi)
    {
        var count = 0;
        var height = bgra.Length / (width * 4);
        for (var y = roi.Y; y < roi.Y + roi.Height && y < height; y++)
        {
            var row = y * width;
            for (var x = roi.X; x < roi.X + roi.Width && x < width; x++)
            {
                var offset = (row + x) * 4;
                var r = bgra[offset + 2];
                var g = bgra[offset + 1];
                var b = bgra[offset];
                if (r > 190 && g < 120 && b < 120 && (r - g) > 70)
                {
                    count++;
                }
            }
        }

        return count;
    }
}

using System.IO;

namespace GGEA.App.Vision;

/// <summary>通用关闭按钮以中央文字与内侧纹理定位，排除随弹窗背景变化的外框；完整按钮仍须落在指定区域。</summary>
public sealed class DialogCloseRecognizer
{
    private readonly TemplateMatcher _matcher = new();
    private readonly Dictionary<string, (ScreenImage Full, Roi Inner, byte[] Pixels)> _templates = new();

    /// <summary>保持调用方阈值，返回完整按钮中心；不凭关闭文字决定当前页面或流程成功。</summary>
    public TemplateMatchResult? Match(ScreenImage screen, string path, Roi region, double threshold, CancellationToken ct)
        => TemplateFallback.Match(path, candidate => MatchSingle(screen, candidate, region, threshold, ct), ct);

    /// <summary>完整验证当前物理素材，复合条件不得跨候选拼接。</summary>
    private TemplateMatchResult? MatchSingle(ScreenImage screen, string path, Roi region, double threshold, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (full, inner, pixels) = Load(path);
        var left = Math.Max(0, region.X);
        var top = Math.Max(0, region.Y);
        var right = Math.Min(screen.Width, region.X + region.Width);
        var bottom = Math.Min(screen.Height, region.Y + region.Height);
        if (right - left < full.Width || bottom - top < full.Height) return null;
        // 先收缩搜索范围，保证内侧命中还原后的完整按钮不会越过底部区域。
        var search = new Roi(left + inner.X, top + inner.Y,
            right - left - full.Width + inner.Width, bottom - top - full.Height + inner.Height);
        var match = _matcher.Match(screen.Bgra, screen.Width, screen.Height, pixels,
            inner.Width, inner.Height, threshold, search, ct: ct);
        return match is null ? null : new TemplateMatchResult(
            new Roi(match.X - inner.X, match.Y - inner.Y, full.Width, full.Height), match.Confidence);
    }

    /// <summary>只缓存内存特征，不改写原素材；中央一半包含字形及周围纹理，避免仅靠大面积纯色授权。</summary>
    private (ScreenImage Full, Roi Inner, byte[] Pixels) Load(string path)
    {
        if (_templates.TryGetValue(path, out var cached)) return cached;
        var full = ImageCodec.DecodePng(File.ReadAllBytes(path));
        var inner = new Roi(full.Width / 4, full.Height / 4, Math.Max(1, full.Width / 2), Math.Max(1, full.Height / 2));
        var pixels = new byte[inner.Width * inner.Height * 4];
        for (var y = 0; y < inner.Height; y++)
            Buffer.BlockCopy(full.Bgra, ((inner.Y + y) * full.Width + inner.X) * 4,
                pixels, y * inner.Width * 4, inner.Width * 4);
        return _templates[path] = (full, inner, pixels);
    }
}

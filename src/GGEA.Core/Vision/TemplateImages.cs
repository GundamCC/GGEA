using System.IO;
using GGEA.App.Config;

namespace GGEA.App.Vision;

/// <summary>会话内一个逻辑素材的有序像素候选；主素材立即校验，通用素材在首次补试时解码。</summary>
internal sealed class TemplateImages
{
    private readonly Lazy<ScreenImage>[] _images;

    /// <summary>固定当前语言视图，缓存两份物理文件，不把后续解析重新指回语言素材。</summary>
    public TemplateImages(string path)
    {
        _images = ConfigService.Resources.TemplateCandidates(path)
            .Select(file => new Lazy<ScreenImage>(() => ImageCodec.DecodePng(File.ReadAllBytes(file)))).ToArray();
        _ = Primary;
    }

    public ScreenImage Primary => _images[0].Value;
    public IEnumerable<ScreenImage> Candidates => _images.Select(image => image.Value);

    /// <summary>复合校验须由同一候选完整通过；只在返回空时尝试下一份像素。</summary>
    public TemplateMatchResult? Match(Func<ScreenImage, TemplateMatchResult?> match, CancellationToken ct = default)
    {
        foreach (var image in Candidates)
        {
            ct.ThrowIfCancellationRequested();
            var result = match(image);
            ct.ThrowIfCancellationRequested();
            if (result is not null) return result;
        }
        return null;
    }

    /// <summary>在同一截图、阈值及 ROI 中逐个尝试；实际命中尺寸随结果返回。</summary>
    public TemplateMatchResult? Match(TemplateMatcher matcher, ScreenImage screen, double threshold,
        Roi? roi = null, CancellationToken ct = default)
    {
        foreach (var image in Candidates)
        {
            ct.ThrowIfCancellationRequested();
            var result = matcher.Match(screen.Bgra, screen.Width, screen.Height, image.Bgra,
                image.Width, image.Height, threshold, roi, ct: ct);
            if (result is not null) return result;
        }
        return null;
    }
}

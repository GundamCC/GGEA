using System.IO;
using GGEA.App.Logging;

namespace GGEA.App.Vision;

/// <summary>启动与全局共用的右上角略过识别；只观察，不点击，也不负责唤出演出控件。</summary>
public sealed class SkipButtonRecognizer(string? templatePath, TemplateMatcher matcher, IGgeaLog log)
{
    /// <summary>沿用启动阶段稳定背景模板的阈值。</summary>
    public const double Threshold = 0.85;
    private readonly Dictionary<string, (ScreenImage Image, DateTime Modified)> _templates = new();

    /// <summary>限定右上角 280×140 区域并裁剪到截图边界，避免其它位置同名按钮触发全局动作。</summary>
    public static Roi CreateSearchRoi(ScreenImage screen)
    {
        var width = Math.Min(280, screen.Width);
        return new Roi(screen.Width - width, 0, width, Math.Min(140, screen.Height));
    }

    /// <summary>复用当前帧；缺少可选模板时不授权点击，已加载模板按修改时间刷新。</summary>
    public TemplateMatchResult? Recognize(ScreenImage screen, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(templatePath)) return null;
        try
        {
            return TemplateFallback.Match(templatePath, path =>
            {
                var file = new FileInfo(path);
                if (!_templates.TryGetValue(path, out var cached) || !file.Exists || file.LastWriteTimeUtc != cached.Modified)
                    _templates[path] = cached = (ImageCodec.DecodePng(File.ReadAllBytes(path)), file.LastWriteTimeUtc);
                return matcher.Match(screen.Bgra, screen.Width, screen.Height, cached.Image.Bgra,
                    cached.Image.Width, cached.Image.Height, Threshold, CreateSearchRoi(screen), ct: ct);
            }, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Warn($"右上角略过模板加载失败: {ex.Message}");
            return null;
        }
    }
}

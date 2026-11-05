using System.IO;
using GGEA.App.Config;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>透明标题按有效笔画匹配；不透明旧素材继续使用白色字形与空隙确认。</summary>
public sealed class SelectStageTitleRecognizer
{
    private readonly ScreenImage _template;
    private readonly Lazy<SelectStageTitleRecognizer>? _fallback;
    private readonly byte[] _reference;
    private readonly byte[] _glyphMask;
    private readonly AlphaMaskedTemplateMatcher? _transparentMatcher;

    /// <summary>每关读取一次原有标题素材，不引入作品专属模板。</summary>
    public SelectStageTitleRecognizer(string path) : this(ConfigService.Resources.TemplateCandidates(path).ToArray()) { }

    /// <summary>次选延迟加载；语言命中不读取通用文件，损坏素材仍按原错误语义停止。</summary>
    private SelectStageTitleRecognizer(string[] candidates) : this(candidates[0], true)
    {
        if (candidates.Length > 1) _fallback = new(() => new SelectStageTitleRecognizer(candidates[1], true));
    }

    /// <summary>物理路径构造不再次解析语言，防止通用回退重新指回语言素材。</summary>
    private SelectStageTitleRecognizer(string path, bool physical)
    {
        _template = ImageCodec.DecodePng(File.ReadAllBytes(path));
        _reference = WhiteGlyphs(_template, _template.Width, _template.Height);
        _glyphMask = CreateGlyphMask(_reference, _template.Width, _template.Height);
        // Alpha 是有效像素范围，透明处不再作为黑色背景参与评分。
        if (Enumerable.Range(0, _template.Width * _template.Height).Any(i => _template.Bgra[i * 4 + 3] < 255))
            _transparentMatcher = new AlphaMaskedTemplateMatcher(_template);
        // 常量模板的归一化相关没有意义，不能让损坏素材使任意画面被认作返回页。
        if (!_reference.Contains((byte)255) || !_reference.Contains((byte)0))
            throw new InvalidOperationException("选关标题素材缺少有效字形或字间空隙。");
    }

    /// <summary>仅在页首搜索完整标题；透明与不透明素材分别使用适合的匹配路径。</summary>
    public bool Recognize(ScreenImage screen, CancellationToken ct)
        => RecognizeSingle(screen, ct) || (_fallback?.Value.RecognizeSingle(screen, ct) ?? false);

    /// <summary>当前素材的完整识别，不跨候选组合证据。</summary>
    private bool RecognizeSingle(ScreenImage screen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var width = Math.Min(1000, screen.Width);
        var height = Math.Min(120, screen.Height);
        if (width < _template.Width || height < _template.Height) return false;
        if (_transparentMatcher is not null)
            return _transparentMatcher.Match(screen, new Roi(0, 0, width, height), .90, ct) is not null;
        var pixels = WhiteGlyphs(screen, width, height);
        using var frame = Mat.FromPixelData(height, width, MatType.CV_8UC1, pixels);
        using var template = Mat.FromPixelData(_template.Height, _template.Width, MatType.CV_8UC1, _reference);
        using var scores = new Mat();
        Cv2.MatchTemplate(frame, template, scores, TemplateMatchModes.CCoeffNormed);
        ct.ThrowIfCancellationRequested();
        Cv2.MinMaxLoc(scores, out double _, out double maximum);
        if (double.IsFinite(maximum) && maximum >= .90) return true;
        // ZZ 海报的亮光进入标题背景时，整块负空间会拉低评分。只排除远离笔画的背景，
        // 保留完整字形与三像素邻域的暗描边/空隙；仍用原阈值，不能仅凭一片白色判为标题。
        using var mask = Mat.FromPixelData(_template.Height, _template.Width, MatType.CV_8UC1, _glyphMask);
        Cv2.MatchTemplate(frame, template, scores, TemplateMatchModes.CCoeffNormed, mask);
        var rows = scores.Rows;
        var columns = scores.Cols;
        for (var y = 0; y < rows; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < columns; x++)
            {
                var value = scores.At<float>(y, x);
                if (float.IsFinite(value) && value >= .90) return true;
            }
        }
        return false;
    }

    /// <summary>从既有文字生成局部校验遮罩，保留邻近负空间，避免把整块海报背景作为标题特征。</summary>
    private static byte[] CreateGlyphMask(byte[] glyphs, int width, int height)
    {
        var mask = new byte[glyphs.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (glyphs[y * width + x] == 0) continue;
            for (var my = Math.Max(0, y - 3); my <= Math.Min(height - 1, y + 3); my++)
            for (var mx = Math.Max(0, x - 3); mx <= Math.Min(width - 1, x + 3); mx++)
                mask[my * width + mx] = 255;
        }
        return mask;
    }

    /// <summary>剔除彩色海报与暗色描边；保留白色笔画和所有负空间，避免只匹配亮点。</summary>
    private static byte[] WhiteGlyphs(ScreenImage image, int width, int height)
    {
        var result = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * image.Width + x) * 4;
                var min = Math.Min(image.Bgra[i], Math.Min(image.Bgra[i + 1], image.Bgra[i + 2]));
                var max = Math.Max(image.Bgra[i], Math.Max(image.Bgra[i + 1], image.Bgra[i + 2]));
                if (image.Bgra[i + 3] >= 128 && min >= 180 && max - min <= 45) result[y * width + x] = 255;
            }
        return result;
    }
}

using System.IO;
using GGEA.App.Config;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>从原始透明 Logo 识别事件左栏缩略图；不搜索右侧同名大海报。</summary>
public sealed class FierceEnemyAssaultRecognizer
{
    private readonly ScreenImage _template;
    private readonly Lazy<FierceEnemyAssaultRecognizer>? _fallback;

    /// <summary>任务开始前解码素材，缺失或损坏必须在导航点击前暴露。</summary>
    public FierceEnemyAssaultRecognizer(string path) : this(ConfigService.Resources.TemplateCandidates(path).ToArray()) { }

    /// <summary>次选延迟加载；语言命中不读取通用文件，损坏素材仍按原错误语义停止。</summary>
    private FierceEnemyAssaultRecognizer(string[] candidates) : this(candidates[0], true)
    {
        if (candidates.Length > 1) _fallback = new(() => new FierceEnemyAssaultRecognizer(candidates[1], true));
    }

    /// <summary>物理路径构造不再次解析语言，防止通用回退重新指回语言素材。</summary>
    private FierceEnemyAssaultRecognizer(string path, bool physical) => _template = ImageCodec.DecodePng(File.ReadAllBytes(path));

    /// <summary>沿用已有 OpenCV 依赖，以透明遮罩、亮度相关性和字形相关性共同确认入口。</summary>
    public TemplateMatchResult? Match(ScreenImage screen, CancellationToken ct)
        => MatchSingle(screen, ct) ?? _fallback?.Value.MatchSingle(screen, ct);

    /// <summary>当前素材的完整识别，不跨候选组合证据。</summary>
    private TemplateMatchResult? MatchSingle(ScreenImage screen, CancellationToken ct)
    {
        using var source = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
        using var left = new Mat(source, new Rect(0, 0, Math.Min(400, screen.Width), Math.Min(980, screen.Height)));
        using var gray = new Mat();
        Cv2.CvtColor(left, gray, ColorConversionCodes.BGRA2GRAY);
        using var original = Mat.FromPixelData(_template.Height, _template.Width, MatType.CV_8UC4, _template.Bgra);
        TemplateMatchResult? best = null;
        // 实际左栏 Logo 比原素材略扁；真实截图标定范围包含缩略图的像素取整差异。
        foreach (var xScale in new[] { .35, .36, .37, .38, .39 })
        foreach (var yScale in new[] { .33, .34, .35, .36, .37 })
        {
            ct.ThrowIfCancellationRequested();
            using var scaled = new Mat();
            Cv2.Resize(original, scaled, new Size((int)Math.Round(original.Width * xScale),
                (int)Math.Round(original.Height * yScale)), 0, 0, InterpolationFlags.Area);
            using var template = new Mat();
            using var mask = new Mat();
            Cv2.CvtColor(scaled, template, ColorConversionCodes.BGRA2GRAY);
            Cv2.ExtractChannel(scaled, mask, 3);
            // 排除半透明边缘混入卡片背景的像素，保留完整 Logo 的内部明暗结构。
            Cv2.Threshold(mask, mask, 254, 255, ThresholdTypes.Binary);
            if (Cv2.CountNonZero(mask) < 32 || template.Width > gray.Width || template.Height > gray.Height) continue;
            using var scores = new Mat();
            Cv2.MatchTemplate(gray, template, scores, TemplateMatchModes.CCorrNormed, mask);
            var values = new float[scores.Rows * scores.Cols];
            Marshal.Copy(scores.Data, values, 0, values.Length);
            var index = -1;
            for (var i = 0; i < values.Length; i++)
                if (float.IsFinite(values[i]) && values[i] >= .90f && values[i] <= 1.001f &&
                    (index < 0 || values[i] > values[index])) index = i;
            if (index < 0) continue;
            var x = index % scores.Cols;
            var y = index / scores.Cols;
            using var candidate = new Mat(gray, new Rect(x, y, template.Width, template.Height));
            using var shape = new Mat();
            Cv2.MatchTemplate(candidate, template, shape, TemplateMatchModes.CCoeffNormed, mask);
            var shapeScore = shape.At<float>(0, 0);
            // 单纯白块可能具有高亮度相关性，必须再通过去均值的字形校验。
            if (!float.IsFinite(shapeScore) || shapeScore < .75f || shapeScore > 1.001f) continue;
            if (best is null || values[index] > best.Confidence)
                best = new(new Roi(x, y, template.Width, template.Height), Math.Min(1, values[index]));
        }
        return best;
    }
}

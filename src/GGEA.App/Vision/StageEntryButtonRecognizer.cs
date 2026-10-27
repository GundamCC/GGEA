using System.IO;

namespace GGEA.App.Vision;

/// <summary>关卡入口以固定文字定位，再校验对应完整按钮，防止相似橙色外框抢占真实“观看”或“出击”。</summary>
public sealed class StageEntryButtonRecognizer
{
    private readonly TemplateMatcher _matcher = new();
    private readonly Dictionary<(string Path, double Scale), (ScreenImage Full, Roi TextBounds, byte[] Text)> _templates = new();

    /// <summary>入口开始前检查素材尺寸与中央文字；缺失或没有有效字形时不能静默回退。</summary>
    public void Validate(string path, Roi searchArea)
    {
        var template = Load(path);
        if (template.Full.Width > searchArea.Width || template.Full.Height > searchArea.Height)
            throw new InvalidOperationException($"关卡入口按钮超出识别区域：{Path.GetFileName(path)}");
    }

    /// <summary>同一帧、同一位置必须同时满足外框与文字阈值；允许两像素采集偏差，返回原按钮中心供点击。</summary>
    public TemplateMatchResult? Match(ScreenImage screen, string path, Roi searchArea, double threshold,
        CancellationToken ct, double templateScale = 1)
    {
        // 尺寸校准必须由业务入口显式选择；默认仍为原尺寸，禁止无限缩放扩大匹配范围。
        if (!double.IsFinite(templateScale) || templateScale < .98 || templateScale > 1)
            throw new ArgumentOutOfRangeException(nameof(templateScale));
        return TemplateFallback.Match(path, candidate => MatchSingle(screen, candidate, searchArea, threshold, ct, templateScale), ct);
    }

    /// <summary>完整验证当前物理素材，复合条件不得跨候选拼接。</summary>
    private TemplateMatchResult? MatchSingle(ScreenImage screen, string path, Roi searchArea, double threshold,
        CancellationToken ct, double templateScale)
    {
        ct.ThrowIfCancellationRequested();
        var template = Load(path, templateScale);
        var bounds = template.TextBounds;
        var text = _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Text,
            bounds.Width, bounds.Height, threshold, searchArea, ct: ct);
        if (text is null) return null;
        // 先定位语义不同的文字，避免整图金字塔先选中另一个共框按钮后漏掉真实入口。
        // 外框只在文字对应位置复核，并严格保留完整按钮必须落在业务区域内的约束。
        var x = Math.Max(searchArea.X, text.X - bounds.X - 2);
        var y = Math.Max(searchArea.Y, text.Y - bounds.Y - 2);
        var right = Math.Min(searchArea.X + searchArea.Width, text.X - bounds.X + template.Full.Width + 2);
        var bottom = Math.Min(searchArea.Y + searchArea.Height, text.Y - bounds.Y + template.Full.Height + 2);
        var full = _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Full.Bgra,
            template.Full.Width, template.Full.Height, threshold, new Roi(x, y, right - x, bottom - y), ct: ct);
        return full is null ? null : full with { Confidence = Math.Min(full.Confidence, text.Confidence) };
    }

    /// <summary>仅从按钮中央提取亮白文字的外接框，排除发光边框；缓存原始纹理与文字背景，不生成新产品素材。</summary>
    private (ScreenImage Full, Roi TextBounds, byte[] Text) Load(string path, double scale = 1)
    {
        if (_templates.TryGetValue((path, scale), out var cached)) return cached;
        var full = ImageCodec.DecodePng(File.ReadAllBytes(path));
        if (scale != 1)
        {
            // 只缓存同一素材的内存缩放版本；文字和完整按钮从相同版本提取，不能跨尺寸拼接证据。
            using var source = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Unchanged);
            using var resized = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.Resize(source, resized, new OpenCvSharp.Size(
                (int)Math.Round(full.Width * scale), (int)Math.Round(full.Height * scale)), 0, 0, OpenCvSharp.InterpolationFlags.Linear);
            OpenCvSharp.Cv2.ImEncode(".png", resized, out var png);
            full = ImageCodec.DecodePng(png);
        }
        var left = full.Width;
        var right = -1;
        var top = full.Height;
        var bottom = -1;
        for (var y = full.Height / 4; y < full.Height * 3 / 4; y++)
            for (var x = full.Width / 5; x < full.Width * 4 / 5; x++)
            {
                var offset = (y * full.Width + x) * 4;
                var b = full.Bgra[offset];
                var g = full.Bgra[offset + 1];
                var r = full.Bgra[offset + 2];
                if (full.Bgra[offset + 3] < 128 || Math.Min(r, Math.Min(g, b)) < 200 ||
                    Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 45) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        if (right < left) throw new InvalidOperationException($"关卡入口按钮没有中央白色文字：{Path.GetFileName(path)}");
        left = Math.Max(0, left - 2);
        top = Math.Max(0, top - 2);
        right = Math.Min(full.Width - 1, right + 2);
        bottom = Math.Min(full.Height - 1, bottom + 2);
        var bounds = new Roi(left, top, right - left + 1, bottom - top + 1);
        var text = new byte[bounds.Width * bounds.Height * 4];
        for (var row = 0; row < bounds.Height; row++)
            Buffer.BlockCopy(full.Bgra, ((bounds.Y + row) * full.Width + bounds.X) * 4,
                text, row * bounds.Width * 4, bounds.Width * 4);
        return _templates[(path, scale)] = (full, bounds, text);
    }
}

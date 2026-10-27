using GGEA.App.Logging;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>底部继续文字的只读 OCR；借用窗口/会话拥有的模型，不授权点击或修改流程状态。</summary>
public sealed class OnnxTapToNextRecognizer(OnnxTextRecognizer ocr, IGgeaLog log)
{
    public const double AverageThreshold = .90;
    public const double LetterThreshold = .85;

    /// <summary>消费流程开始前检查模型；模型由拥有者在任务退出后释放。</summary>
    public void Validate() => ocr.Validate();

    /// <summary>缺省仍用底部 200 像素；推图调用方可指定专用 300 像素区域，完整文字与置信度约束不变。</summary>
    public Task<TemplateMatchResult?> RecognizeAsync(byte[] png, CancellationToken ct = default,
        RecognitionRegion region = RecognitionRegion.BottomButtons)
        => Task.Run(() => Recognize(ImageCodec.DecodePng(png), ct, region), ct);

    /// <summary>复用已完成安全检查的同一帧，返回文字外接框中心，避免额外截图改变判断依据。</summary>
    public TemplateMatchResult? Recognize(ScreenImage screen, CancellationToken ct = default,
        RecognitionRegion region = RecognitionRegion.BottomButtons)
    {
        ct.ThrowIfCancellationRequested();
        var roi = RecognitionRegions.Resolve(region, screen.Width, screen.Height);
        var mask = OnnxTextRecognizer.CreateWhiteMask(screen, roi, ct);
        var matches = new List<TemplateMatchResult>();
        byte[]? contrast = null;
        foreach (var bounds in LocateLines(mask, roi, ct))
        {
            var text = ocr.Recognize(mask, roi.Width, bounds, ct);
            if (!IsAccepted(text))
            {
                // 奖励机体的白色模型可能与文字粘连，二值化会丢失笔画层次；保留灰度后仍须完整文字及原置信度通过。
                contrast ??= CreateContrastMask(screen, roi, ct);
                // 二值候选外一像素保留抗锯齿；Bounds 已拒绝贴边候选，因此不会越出搜索区域。
                var expanded = new Roi(bounds.X - 1, bounds.Y - 1, bounds.Width + 2, bounds.Height + 2);
                text = ocr.Recognize(contrast, roi.Width, expanded, ct);
                if (!IsAccepted(text)) continue;
            }
            matches.Add(new(new Roi(roi.X + bounds.X, roi.Y + bounds.Y, bounds.Width, bounds.Height), text.Confidence));
        }
        ct.ThrowIfCancellationRequested();
        if (matches.Count != 1) return null;
        var match = matches[0];
        log.Info($"OCR 识别 TAP TO NEXT，置信度 {match.Confidence:P1}，区域 {match.Region}，中心 ({match.CenterX},{match.CenterY})");
        return match;
    }

    /// <summary>在同一候选位置保留低饱和亮度的连续层次，避免白色模型背景吞掉半透明文字的抗锯齿。</summary>
    private static byte[] CreateContrastMask(ScreenImage screen, Roi roi, CancellationToken ct)
    {
        var pixels = new byte[roi.Width * roi.Height];
        for (var y = 0; y < roi.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < roi.Width; x++)
            {
                var i = ((roi.Y + y) * screen.Width + roi.X + x) * 4;
                var light = Math.Min(screen.Bgra[i], Math.Min(screen.Bgra[i + 1], screen.Bgra[i + 2]));
                // 最暗通道抑制彩色背景；80–220 映射为灰度，不改模型的文字及逐字置信度门槛。
                pixels[y * roi.Width + x] = (byte)Math.Clamp((light - 80) * 255 / 140, 0, 255);
            }
        }
        return pixels;
    }

    /// <summary>允许大小写、空白和外侧短横线；不能替换字母、补字或只凭 NEXT 关键词点击。</summary>
    public static bool IsAccepted(OcrTextRecognition text)
    {
        var letters = text.MinimumContentConfidence ?? text.MinimumConfidence;
        return double.IsFinite(text.Confidence) && double.IsFinite(letters) &&
            text.Confidence is >= AverageThreshold and <= 1 && letters is >= LetterThreshold and <= 1 &&
            string.Concat(text.Text.Trim().Trim('-').Where(c => !char.IsWhiteSpace(c)))
                .Equals("TAPTONEXT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按白色字形的基线及间距组合完整行；忽略短横线和背景碎点，拒绝 ROI 边缘截断。</summary>
    private static IEnumerable<Roi> LocateLines(byte[] mask, Roi roi, CancellationToken ct)
    {
        using var source = Mat.FromPixelData(roi.Height, roi.Width, MatType.CV_8UC1, mask);
        using var labels = new Mat(); using var stats = new Mat(); using var centers = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(source, labels, stats, centers);
        var lines = new List<List<Roi>>();
        for (var i = 1; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var b = new Roi(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
            var area = stats.At<int>(i, 4);
            var density = (double)area / (b.Width * b.Height);
            if (b.Height is < 12 or > 72 || b.Width < 2 || b.Width > b.Height * 1.6 ||
                area < 15 || density is < .08 or > .95) continue;
            var line = lines.FirstOrDefault(items => Math.Abs(items[0].CenterY - b.CenterY) <= 4 &&
                Math.Abs(items[0].Height - b.Height) <= 6);
            if (line is null) lines.Add([b]); else line.Add(b);
        }
        foreach (var line in lines)
        {
            // 最多桥接两个因背景粘连而缺失的连通块；完整文字仍交由 OCR 严格验证。
            var group = new List<Roi>();
            foreach (var b in line.OrderBy(b => b.X))
            {
                if (group.Count > 0 && b.X - group[^1].X - group[^1].Width > Math.Max(24, b.Height * 2))
                {
                    if (Bounds(group, roi) is { } bounds) yield return bounds;
                    group.Clear();
                }
                group.Add(b);
            }
            if (Bounds(group, roi) is { } last) yield return last;
        }
    }

    /// <summary>允许个别笔画粘连后仍生成整行候选；七个连通块不是通过条件，OCR 仍必须读全九个字母。</summary>
    private static Roi? Bounds(List<Roi> group, Roi roi)
    {
        if (group.Count is < 7 or > 14) return null;
        var x = group.Min(b => b.X); var y = group.Min(b => b.Y);
        var right = group.Max(b => b.X + b.Width); var bottom = group.Max(b => b.Y + b.Height);
        if (x == 0 || y == 0 || right == roi.Width || bottom == roi.Height) return null;
        return new(x, y, right - x, bottom - y);
    }
}

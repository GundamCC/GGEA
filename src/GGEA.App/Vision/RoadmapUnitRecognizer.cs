using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>按原生或自定义立绘的透明掩码匹配单位；背景、光圈与星级不参与评分。</summary>
public sealed class RoadmapUnitRecognizer
{
    /// <summary>不同位置分数过近时不点击，避免把相似机体当作目标。</summary>
    private const double AmbiguityMargin = 0.035;

    /// <summary>只在模板上做尺度适配，使用原图 RGB 和 Alpha，不重绘立绘或改变游戏画布。</summary>
    public TemplateMatchResult? Match(byte[] screenPng, byte[] templatePng, double threshold,
        Roi? roi = null, CancellationToken ct = default)
        => Match(screenPng, new RoadmapUnitTemplate(templatePng), threshold, roi, ct);

    /// <summary>原生素材使用缓存的已校准固定模板；旧自定义截图仍按历史多尺度路径识别。</summary>
    public TemplateMatchResult? Match(byte[] screenPng, RoadmapUnitTemplate prepared, double threshold,
        Roi? roi = null, CancellationToken ct = default, bool fixedScales = false, bool largeOnly = false)
    {
        ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        using var screen = Cv2.ImDecode(screenPng, ImreadModes.Color);
        using var original = Cv2.ImDecode(prepared.OriginalPng, ImreadModes.Unchanged);
        if (screen.Empty() || original.Empty()) throw new InvalidOperationException("路线图或机体模板不是有效图片");
        // 未去背景的旧自定义模板仍由调用方做原比例匹配，不能把整个背景当作多尺度特征。
        if (original.Channels() != 4) return null;
        using var alpha = new Mat();
        Cv2.ExtractChannel(original, alpha, 3);
        if (Cv2.CountNonZero(alpha) == alpha.Total()) return null;
        using var template = new Mat();
        Cv2.CvtColor(original, template, ColorConversionCodes.BGRA2BGR);
        var bounds = roi ?? new Roi(360, 240, screen.Width - 400, screen.Height - 360);
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0 ||
            bounds.X + bounds.Width > screen.Width || bounds.Y + bounds.Height > screen.Height)
            throw new ArgumentOutOfRangeException(nameof(roi));
        using var canvas = new Mat(screen, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        if (fixedScales || largeOnly) return MatchFixedScales(canvas, bounds, prepared, threshold, ct, largeOnly);
        using var preview = new Mat();
        Cv2.Resize(canvas, preview, new Size(), 0.5, 0.5, InterpolationFlags.Area);
        using var edgeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        var candidates = new List<TemplateMatchResult>();
        var visited = new HashSet<int>();

        // 细化前先覆盖原有大小模板之间的比例；原始尺寸本身必须包含，不能被步长跳过。
        foreach (var width in new[] { template.Width }.Concat(Enumerable.Range(0, 44).Select(i => 85 + i * 8)))
            InspectWidth(width, null);
        if (candidates.Count == 0) return null;
        // 缩图只提出候选，不用于最终评分。保留不同位置，防止优化后丢失相似机体歧义检查。
        var seeds = new List<TemplateMatchResult>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Confidence))
            if (candidate.Confidence >= threshold - 0.12 && !seeds.Any(seed => SameUnit(seed, candidate)))
                seeds.Add(candidate);
        candidates.Clear();
        foreach (var seed in seeds)
        {
            visited.Clear();
            // 在候选周围以原始 RGB/Alpha 精查；尺度及缩图坐标舍入都由局部余量覆盖。
            for (var width = Math.Max(32, seed.Width - 15); width <= seed.Width + 15; width++)
                InspectWidth(width, seed);
        }
        if (candidates.Count == 0) return null;
        var best = candidates.MaxBy(candidate => candidate.Confidence)!;
        if (best.Confidence < threshold || candidates.Any(candidate =>
                !SameUnit(candidate, best) && candidate.Confidence > best.Confidence - AmbiguityMargin)) return null;
        return best;

        // 每个比例保留两个独立位置，供跨尺度的歧义判定使用。
        void InspectWidth(int width, TemplateMatchResult? seed)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(width)) return;
            var height = Math.Max(1, (int)Math.Round(template.Height * (double)width / template.Width));
            if (width > canvas.Width || height > canvas.Height) return;
            using var scaled = new Mat();
            using var mask = new Mat();
            Cv2.Resize(template, scaled, new Size(width, height), 0, 0, InterpolationFlags.Linear);
            Cv2.Resize(alpha, mask, scaled.Size(), 0, 0, InterpolationFlags.Linear);
            // 只比较完全不透明的内部像素，排除背景混色和不同模拟器抗锯齿造成的 1px 边缘差异。
            Cv2.Threshold(mask, mask, 254, 255, ThresholdTypes.Binary);
            Cv2.Erode(mask, mask, edgeKernel);
            if (Cv2.CountNonZero(mask) < 32) return;
            var searchRect = new Rect(0, 0, canvas.Width, canvas.Height);
            if (seed is not null)
            {
                var x = Math.Max(0, seed.X - bounds.X - 24);
                var y = Math.Max(0, seed.Y - bounds.Y - 24);
                searchRect = new Rect(x, y, Math.Min(canvas.Width - x, seed.Width + 48),
                    Math.Min(canvas.Height - y, seed.Height + 48));
            }
            else
            {
                Cv2.Resize(scaled, scaled, new Size(Math.Max(1, width / 2), Math.Max(1, height / 2)), 0, 0, InterpolationFlags.Area);
                Cv2.Resize(mask, mask, scaled.Size(), 0, 0, InterpolationFlags.Area);
                Cv2.Threshold(mask, mask, 254, 255, ThresholdTypes.Binary);
                if (Cv2.CountNonZero(mask) < 8) return;
            }
            using var region = new Mat(canvas, searchRect);
            var search = seed is null ? preview : region;
            if (scaled.Width > search.Width || scaled.Height > search.Height) return;
            using var scores = new Mat();
            Cv2.MatchTemplate(search, scaled, scores, TemplateMatchModes.CCorrNormed, mask);
            // 黑色空画布的归一化分母可能为零；NaN/Infinity 绝不当作高分候选。
            using var valid = new Mat();
            Cv2.InRange(scores, Scalar.All(-1.001), Scalar.All(1.001), valid);
            Cv2.BitwiseNot(valid, valid);
            scores.SetTo(-1, valid);
            for (var i = 0; i < 2; i++)
            {
                Cv2.MinMaxLoc(scores, out _, out var score, out _, out var point);
                if (!double.IsFinite(score) || score < 0) break;
                var factor = seed is null ? 2 : 1;
                candidates.Add(new TemplateMatchResult(new Roi(bounds.X + searchRect.X + point.X * factor,
                    bounds.Y + searchRect.Y + point.Y * factor, width, height),
                    Math.Clamp(score, 0, 1)));
                var left = Math.Max(0, point.X - scaled.Width / 2);
                var top = Math.Max(0, point.Y - scaled.Height / 2);
                using var suppressed = new Mat(scores, new Rect(left, top,
                    Math.Min(scores.Width - left, scaled.Width), Math.Min(scores.Height - top, scaled.Height)));
                suppressed.SetTo(-1);
            }
        }
    }

    /// <summary>默认原生素材只搜索已校准固定比例；合并各档独立位置后统一执行阈值和歧义判断。</summary>
    private static TemplateMatchResult? MatchFixedScales(Mat canvas, Roi bounds,
        RoadmapUnitTemplate prepared, double threshold, CancellationToken ct, bool largeOnly)
    {
        var candidates = new List<TemplateMatchResult>();
        // 快捷方向探测仅使用缓存的大图，仍保留同尺度不同位置的歧义拒绝。
        var variants = largeOnly ? prepared.Preferred.OrderByDescending(item => item.Key).Take(1).Select(item => item.Value)
            : prepared.Preferred.Values;
        foreach (var cached in variants)
        {
            ct.ThrowIfCancellationRequested();
            using var image = Cv2.ImDecode(cached.Image, ImreadModes.Color);
            using var mask = Cv2.ImDecode(cached.Mask, ImreadModes.Grayscale);
            if (image.Width > canvas.Width || image.Height > canvas.Height || Cv2.CountNonZero(mask) < 32) continue;
            using var scores = new Mat();
            Cv2.MatchTemplate(canvas, image, scores, TemplateMatchModes.CCorrNormed, mask);
            using var valid = new Mat();
            Cv2.InRange(scores, Scalar.All(-1.001), Scalar.All(1.001), valid);
            Cv2.BitwiseNot(valid, valid);
            scores.SetTo(-1, valid);
            // 每档保留两个相隔足够远的峰值，重复单位不得因切换档位而绕过安全拒绝。
            for (var i = 0; i < 2; i++)
            {
                ct.ThrowIfCancellationRequested();
                Cv2.MinMaxLoc(scores, out _, out var score, out _, out var point);
                if (!double.IsFinite(score) || score < 0) break;
                candidates.Add(new TemplateMatchResult(new Roi(bounds.X + point.X, bounds.Y + point.Y,
                    image.Width, image.Height), Math.Clamp(score, 0, 1)));
                var left = Math.Max(0, point.X - image.Width / 2);
                var top = Math.Max(0, point.Y - image.Height / 2);
                using var suppressed = new Mat(scores, new Rect(left, top,
                    Math.Min(scores.Width - left, image.Width), Math.Min(scores.Height - top, image.Height)));
                suppressed.SetTo(-1);
            }
        }
        ct.ThrowIfCancellationRequested();
        var best = candidates.MaxBy(candidate => candidate.Confidence);
        if (best is null || best.Confidence < threshold || candidates.Any(candidate =>
                !SameUnit(candidate, best) && candidate.Confidence > best.Confidence - AmbiguityMargin)) return null;
        return best;
    }

    /// <summary>同一机体在邻近尺度产生的峰值只算一个位置。</summary>
    private static bool SameUnit(TemplateMatchResult a, TemplateMatchResult b) =>
        Math.Abs(a.CenterX - b.CenterX) <= Math.Max(a.Width, b.Width) / 2 &&
        Math.Abs(a.CenterY - b.CenterY) <= Math.Max(a.Height, b.Height) / 2;
}

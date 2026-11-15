using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>在路线图内容区沿手势轴追踪局部纹理。多个独立图块跟随同一偏移才承认平移。</summary>
internal static class RoadmapMotion
{
    private const int Scale = 4;
    private const int Patch = 12;
    private static readonly Rect Canvas = new(360, 240, 1520, 700);

    internal static int Measure(byte[] before, byte[] after, int dx, int dy)
    {
        if (before.AsSpan().SequenceEqual(after)) return 0;
        using var first = ReadCanvas(before);
        using var next = ReadCanvas(after);
        var horizontal = dx != 0;
        var gesture = horizontal ? dx : dy;
        var limit = Math.Abs(gesture) / Scale + 3;
        var offsets = new List<int>();
        using var scores = new Mat();
        using var nearbyScores = new Mat();
        var rows = first.Rows;
        var columns = first.Cols;
        for (var y = 0; y + Patch <= rows; y += Patch)
        for (var x = 0; x + Patch <= columns; x += Patch)
        {
            var box = new Rect(x, y, Patch, Patch);
            using var anchor = new Mat(first, box);
            using var originalPosition = new Mat(next, box);
            // 排除空白、静止节点和仅有轻微闪烁的区域，保留等亮度但颜色不同的边缘。
            if (Cv2.Norm(anchor, originalPosition, NormTypes.L1) < Patch * Patch * 3 * 4) continue;
            Cv2.MeanStdDev(anchor, out Scalar _, out Scalar contrast);
            if (Math.Max(contrast.Val0, Math.Max(contrast.Val1, contrast.Val2)) < 8) continue;

            var origin = horizontal ? x : y;
            var end = Math.Clamp(origin + Math.Sign(gesture) * limit, 0,
                (horizontal ? columns : rows) - Patch);
            var low = Math.Min(origin, end);
            var span = Math.Abs(end - origin);
            if (span < 3) continue;
            var strip = horizontal ? new Rect(low, y, Patch + span, Patch)
                                   : new Rect(x, low, Patch, Patch + span);
            using var search = new Mat(next, strip);
            Cv2.MatchTemplate(search, anchor, scores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scores, out _, out var peak, out _, out var best);
            var index = horizontal ? best.X : best.Y;
            var delta = low + index - origin;
            if (peak < 0.85 || Math.Abs(delta) < 3) continue;
            // 周期性连线或动画若能在多个位置产生近似峰值，不能用来确定移动距离。
            var unique = true;
            for (var i = 0; i <= span; i++)
            {
                if (Math.Abs(i - index) <= 2) continue;
                if (scores.At<float>(horizontal ? 0 : i, horizontal ? i : 0) < peak - 0.05) continue;
                unique = false;
                break;
            }
            if (!unique) continue;
            // 粒子可在原位附近斜向飘动。若附近也能找到同一个图案，远处的相似节点不提供位移证据。
            var left = Math.Max(0, x - 2);
            var top = Math.Max(0, y - 2);
            var near = new Rect(left, top, Math.Min(columns, x + Patch + 2) - left,
                Math.Min(rows, y + Patch + 2) - top);
            using var neighborhood = new Mat(next, near);
            Cv2.MatchTemplate(neighborhood, anchor, nearbyScores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(nearbyScores, out _, out double nearbyPeak);
            if (nearbyPeak < peak - 0.05) offsets.Add(delta);
        }
        // 允许下采样带来一个像素的舍入误差；相互冲突的偏移不作为继续移动的依据。
        offsets.Sort();
        if (offsets.Count < 2) return 0;
        var median = offsets[offsets.Count / 2];
        var agreeing = offsets.Count(value => Math.Abs(value - median) <= 1);
        return agreeing >= 2 && agreeing * 2 > offsets.Count ? median * Scale : 0;
    }

    private static Mat ReadCanvas(byte[] png)
    {
        using var screen = Cv2.ImDecode(png, ImreadModes.Color);
        using var region = new Mat(screen, Canvas);
        return region.Resize(new Size(Canvas.Width / Scale, Canvas.Height / Scale),
            interpolation: InterpolationFlags.Area);
    }
}

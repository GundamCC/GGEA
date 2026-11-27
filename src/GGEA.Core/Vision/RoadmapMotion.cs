using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace GGEA.App.Vision;

/// <summary>在路线图内容区沿手势轴追踪局部纹理。多个独立图块跟随同一偏移才承认平移。</summary>
internal static class RoadmapMotion
{
    private const int Scale = 4;
    private const int Patch = 20;
    private static readonly Rect Canvas = new(360, 100, 1560, 980);

    // 补查首排时，用至少三块位移一致的纹理核对已检视区域。固定标题、进度条和小地图不参与。
    internal static bool OverlapsScannedView(byte[] first, byte[] current, int dx, int dy)
    {
        if (first.AsSpan().SequenceEqual(current)) return true;
        using var a = ReadCanvas(first);
        using var b = ReadCanvas(current);
        using var scores = new Mat();
        var offsets = new List<Point>();
        foreach (var y in new[] { 100, 219, 330 })
        foreach (var x in new[] { 90, 265, 424, 600 })
        {
            using var patch = a[new Rect(x, y, 58, 48)];
            Cv2.MeanStdDev(patch, out Scalar _, out Scalar variance);
            if (Math.Max(variance.Val0, Math.Max(variance.Val1, variance.Val2)) < 22) continue;
            int left = Math.Max(0, x - (dx == 0 ? 5 : 160)), top = Math.Max(0, y - (dy == 0 ? 5 : 85));
            using var around = b[new Rect(left, top, Math.Min(b.Width - left, (x-left)*2+patch.Width),
                Math.Min(b.Height - top, (y-top)*2+patch.Height))];
            Cv2.MatchTemplate(around, patch, scores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scores, out _, out var similarity, out _, out var at);
            if (similarity < .985) continue;
            var offset = new Point(left + at.X - x, top + at.Y - y);
            offsets.Add(offset);
            if (offsets.Count(o => Math.Abs(o.X-offset.X) <= 3 && Math.Abs(o.Y-offset.Y) <= 3) >= 3) return true;
        }
        return false;
    }

    internal static int Measure(byte[] before, byte[] after, int dx, int dy)
        => Measure(before, after, dx, dy, out _);

    internal static int Measure(byte[] before, byte[] after, int dx, int dy, out bool stationary)
    {
        stationary = before.AsSpan().SequenceEqual(after);
        if (stationary) return 0;
        using var a = ReadCanvas(before);
        using var b = ReadCanvas(after);
        var measured = TrackFeatures(a, b, dx, dy);
        if (measured.HasValue)
        {
            stationary = measured == 0;
            return measured.Value;
        }
        // 平坦色块或单条线缺少特征点，仍用 RGB 边缘复核稀疏节点。
        using var first = a.Resize(new Size(Canvas.Width / Scale, Canvas.Height / Scale), interpolation: InterpolationFlags.Area);
        using var next = b.Resize(first.Size(), interpolation: InterpolationFlags.Area);
        var horizontal = dx != 0;
        var gesture = horizontal ? dx : dy;
        var limit = Math.Abs(gesture) * 2 / Scale + 3;
        var offsets = new List<int>();
        using var scores = new Mat();
        using var nearbyScores = new Mat();
        var rows = first.Rows;
        var columns = first.Cols;
        for (var y = 0; y + Patch <= rows; y += 16)
        for (var x = 0; x + Patch <= columns; x += 16)
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
            const int sideMargin = 7;
            var side = horizontal ? y : x;
            var sideStart = Math.Max(0, side - sideMargin);
            var sideEnd = Math.Min(horizontal ? rows : columns, side + Patch + sideMargin);
            var strip = horizontal ? new Rect(low, sideStart, Patch + span, sideEnd - sideStart)
                                   : new Rect(sideStart, low, sideEnd - sideStart, Patch + span);
            using var search = new Mat(next, strip);
            Cv2.MatchTemplate(search, anchor, scores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scores, out _, out var peak, out _, out var best);
            var index = horizontal ? best.X : best.Y;
            var delta = low + index - origin;
            if (peak < 0.80 || Math.Abs(delta) < 3) continue;
            // 排除沿该方向同样吻合的其他位置，忽略同一个峰在侧向几像素内的拉伸。
            var lo = Math.Max(0, index - 3);
            var hi = Math.Min(span + 1, index + 4);
            var suppressed = horizontal ? new Rect(lo, 0, hi - lo, scores.Rows)
                                        : new Rect(0, lo, scores.Cols, hi - lo);
            using var peakBand = new Mat(scores, suppressed);
            peakBand.SetTo(-1);
            Cv2.MinMaxLoc(scores, out _, out double second);
            if (second >= peak - 0.05) continue;
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
        if (offsets.Count >= 2)
        {
            // 透视图上不同深度的部分移动量不同，允许多个相邻图块单独提供一致的位移证据。
            var group = offsets.GroupBy(v => (int)Math.Round(v / 4.0)).MaxBy(g => g.Count())!;
            var aligned = offsets.Where(v => Math.Abs(v - group.Key * 4) <= 3).Order().ToArray();
            if (aligned.Length >= 4 || aligned.Length >= 2 && aligned.Length * 2 > offsets.Count)
                return aligned[aligned.Length / 2] * Scale;
        }
        // 未能对应新位置仍可能是移出视口。仅在两帧轮廓大部分仍重合时报告静止。
        using var oldEdges = new Mat();
        using var newEdges = new Mat();
        Cv2.Canny(first, oldEdges, 60, 120);
        Cv2.Canny(next, newEdges, 60, 120);
        using var common = new Mat();
        Cv2.BitwiseAnd(oldEdges, newEdges, common);
        var edgeCount = Math.Max(Cv2.CountNonZero(oldEdges), Cv2.CountNonZero(newEdges));
        stationary = edgeCount >= 12 && Cv2.CountNonZero(common) >= edgeCount * 0.55;
        return 0;
    }

    /// <summary>半尺寸画布内自由配对，再按手势方向票选位移。透视导致的侧向偏移与触控增益不限制匹配范围。</summary>
    private static int? TrackFeatures(Mat first, Mat next, int dx, int dy)
    {
        using var sift = SIFT.Create(1200);
        using var reference = new Mat();
        using var current = new Mat();
        sift.DetectAndCompute(first, null, out var from, reference);
        sift.DetectAndCompute(next, null, out var to, current);
        if (reference.Rows < 6 || current.Rows < 6) return null;
        using var matcher = new BFMatcher(NormTypes.L2);
        var votes = new List<(float Shift, Point2f Source)>();
        var stationary = 0;
        // 同一特征可具有多种方向描述，只让同一落点参与一次投票。
        foreach (var pair in matcher.KnnMatch(reference, current, 2)
                     .Where(p => p.Length == 2 && p[0].Distance < 0.70 * p[1].Distance)
                     .Select(p => p[0]).OrderBy(p => p.Distance).DistinctBy(p => to[p.TrainIdx].Pt))
        {
            var source = from[pair.QueryIdx].Pt;
            var delta = to[pair.TrainIdx].Pt - source;
            var shift = dx == 0 ? delta.Y : delta.X;
            var cross = dx == 0 ? delta.X : delta.Y;
            if (Math.Abs(shift) <= 3 && Math.Abs(cross) <= 3) stationary++;
            if (shift * Math.Sign(dx == 0 ? dy : dx) >= 6 && Math.Abs(cross) <= 4 + Math.Abs(shift) * 0.20)
                votes.Add((shift, source));
        }
        // 单位与远景的透视速度不同。选择局部一致、空间分散的节点群，不要求整幅画面同速。
        foreach (var group in votes.GroupBy(v => (int)Math.Round(v.Shift / 8)).OrderByDescending(g => g.Count()))
        {
            var aligned = votes.Where(v => Math.Abs(v.Shift - group.Key * 8) <= 8).ToArray();
            var spread = aligned.Select(v => ((int)v.Source.X / 16, (int)v.Source.Y / 16)).Distinct().Count();
            if (aligned.Length >= 6 && spread >= 3 && aligned.Length * 3 > stationary)
                return (int)Math.Round(2 * aligned.Select(v => v.Shift).Order().ElementAt(aligned.Length / 2));
        }
        return stationary >= 16 && stationary > votes.Count * 2 ? 0 : null;
    }

    private static Mat ReadCanvas(byte[] png)
    {
        using var screen = Cv2.ImDecode(png, ImreadModes.Color);
        // 边缘处露出的半个节点也能证明位移；百分比栏及右下角按钮固定在屏幕上，必须排除。
        using var progress = new Mat(screen, new Rect(1150, 100, 770, 140));
        using var controls = new Mat(screen, new Rect(1750, 935, 170, 145));
        progress.SetTo(Scalar.All(0));
        controls.SetTo(Scalar.All(0));
        using var region = new Mat(screen, Canvas);
        return region.Resize(new Size(Canvas.Width / 2, Canvas.Height / 2),
            interpolation: InterpolationFlags.Area);
    }
}

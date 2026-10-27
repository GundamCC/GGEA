using System.IO;
using GGEA.App.Config;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace GGEA.App.Vision;

/// <summary>用单位素材内部特征识别缩放及裁切立绘；位置不能独立构成单位命中证据。</summary>
public sealed class FierceEnemyUnitRecognizer : IDisposable
{
    private readonly SIFT _sift = SIFT.Create(2200);
    private readonly Mat _descriptors = new();
    private readonly KeyPoint[] _points;
    private readonly Lazy<FierceEnemyUnitRecognizer>? _fallback;

    /// <summary>透明边缘不参与匹配，避免把轮廓与页面背景拼接处当作机体特征。</summary>
    public FierceEnemyUnitRecognizer(string path) : this(ConfigService.Resources.TemplateCandidates(path).ToArray()) { }

    /// <summary>延迟创建次选特征，各自持有并释放自己的原生缓存。</summary>
    private FierceEnemyUnitRecognizer(string[] paths) : this(paths[0], true)
    {
        if (paths.Length > 1) _fallback = new(() => new FierceEnemyUnitRecognizer(paths[1], true));
    }

    /// <summary>物理素材构造不再次做语言解析。</summary>
    private FierceEnemyUnitRecognizer(string path, bool physical)
    {
        try
        {
            using var source = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Unchanged);
            if (source.Empty() || source.Channels() != 4) throw new InvalidDataException("单位素材必须带透明通道。");
            using var gray = new Mat(); using var mask = new Mat();
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
            Cv2.ExtractChannel(source, mask, 3);
            Cv2.Threshold(mask, mask, 254, 255, ThresholdTypes.Binary);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            Cv2.Erode(mask, mask, kernel);
            _sift.DetectAndCompute(gray, mask, out _points, _descriptors);
            if (_points.Length < 12) throw new InvalidDataException("单位素材缺少足够识别特征。");
        }
        catch { Dispose(); throw; }
    }

    // 1920×1080 首页四个蓝圈的内圆；固定区域仅限定候选，单位身份仍由素材特征确认。
    private static readonly Roi[] HomeCircles = [
        new(320, 558, 204, 204), new(648, 700, 204, 204),
        new(1066, 700, 204, 204), new(1396, 558, 204, 204) ];

    /// <summary>首页只在四个内圆独立识别；中央大图和圆外像素不能参与匹配，唯一命中后点击圆心。</summary>
    public Roi? FindHomeSelection(ScreenImage screen, CancellationToken ct = default)
    {
        Roi? selected = null;
        foreach (var circle in HomeCircles)
        {
            if (FindCore(screen, circle, true, ct) is null) continue;
            if (selected is not null) return null;
            selected = circle;
        }
        return selected;
    }

    /// <summary>历史页按两列立绘分别提取特征，避免整页文字和其他卡片耗尽特征点预算。</summary>
    public Roi? FindHistorySelection(ScreenImage screen, CancellationToken ct = default)
    {
        Roi? selected = null;
        foreach (var column in new[] { new Roi(140, 170, 250, 780), new Roi(1030, 170, 260, 780) })
        {
            var match = Find(screen, column, ct);
            if (match is null) continue;
            if (selected is not null) return null;
            selected = match;
        }
        return selected;
    }

    /// <summary>双邻近比率过滤后，仅接受至少十二个分散且满足同一缩放/平移的特征，不允许旋转或透视猜测。</summary>
    public Roi? Find(ScreenImage screen, Roi region, CancellationToken ct = default)
        => FindCore(screen, region, false, ct);

    /// <summary>圆形搜索同时屏蔽检测点和描述子采样背景，矩形搜索供历史卡片与详情立绘沿用。</summary>
    private Roi? FindCore(ScreenImage screen, Roi region, bool circular, CancellationToken ct)
        => FindSingle(screen, region, circular, ct) ?? _fallback?.Value.FindSingle(screen, region, circular, ct);

    /// <summary>全部特征及一致性判定来自同一素材，回退不混用特征点。</summary>
    private Roi? FindSingle(ScreenImage screen, Roi region, bool circular, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var full = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
        using var patch = new Mat(full, new Rect(region.X, region.Y, region.Width, region.Height));
        using var gray = new Mat(); using var descriptors = new Mat();
        Cv2.CvtColor(patch, gray, ColorConversionCodes.BGRA2GRAY);
        using var mask = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.All(255));
        if (circular)
        {
            mask.SetTo(Scalar.All(0));
            Cv2.Circle(mask, new Point(region.Width / 2, region.Height / 2),
                Math.Min(region.Width, region.Height) / 2 - 2, Scalar.All(255), -1);
            using var outside = new Mat();
            Cv2.BitwiseNot(mask, outside);
            gray.SetTo(Scalar.All(0), outside);
        }
        _sift.DetectAndCompute(gray, mask, out var points, descriptors);
        if (descriptors.Rows < 2) return null;
        using var matcher = new BFMatcher(NormTypes.L2);
        var pairs = matcher.KnnMatch(_descriptors, descriptors, 2)
            .Where(m => m.Length == 2 && m[0].Distance < .65 * m[1].Distance)
            .Select(m => m[0]).OrderBy(m => m.Distance).DistinctBy(m => m.TrainIdx).Take(100).ToArray();
        if (pairs.Length < 12) return null;
        var hypotheses = new List<(double Scale, double X, double Y, DMatch[] Inliers)>();
        for (var i = 0; i < pairs.Length; i++)
        for (var j = i + 1; j < pairs.Length; j++)
        {
            ct.ThrowIfCancellationRequested();
            var a = _points[pairs[i].QueryIdx].Pt; var b = _points[pairs[j].QueryIdx].Pt;
            var p = points[pairs[i].TrainIdx].Pt; var q = points[pairs[j].TrainIdx].Pt;
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var distance = dx * dx + dy * dy;
            if (distance < 2500) continue;
            var scale = ((q.X - p.X) * dx + (q.Y - p.Y) * dy) / distance;
            if (scale < .12 || scale > 2.5) continue;
            var tx = p.X - scale * a.X; var ty = p.Y - scale * a.Y;
            var inliers = pairs.Where(m =>
            {
                var original = _points[m.QueryIdx].Pt; var found = points[m.TrainIdx].Pt;
                return Math.Abs(found.X - scale * original.X - tx) <= 4 && Math.Abs(found.Y - scale * original.Y - ty) <= 4;
            }).ToArray();
            if (inliers.Length >= 12) hypotheses.Add((scale, tx, ty, inliers));
        }
        var best = hypotheses.OrderByDescending(h => h.Inliers.Length).FirstOrDefault();
        if (best.Inliers is null || best.Inliers.Length < pairs.Length * .45) return null;
        // 两处同样可信的立绘视为歧义，不任意选择其中一个。
        if (hypotheses.Any(h => h.Inliers.Length >= best.Inliers.Length * .8 &&
            (Math.Abs(h.X - best.X) > 30 || Math.Abs(h.Y - best.Y) > 30))) return null;
        var matched = best.Inliers.Select(m => points[m.TrainIdx].Pt).ToArray();
        var x = (int)matched.Min(p => p.X); var y = (int)matched.Min(p => p.Y);
        var width = (int)matched.Max(p => p.X) - x; var height = (int)matched.Max(p => p.Y) - y;
        if (width < 35 || height < 35) return null;
        return new Roi(region.X + x, region.Y + y, width, height);
    }

    /// <summary>单位任务结束时释放原生特征缓存。</summary>
    public void Dispose() { if (_fallback?.IsValueCreated == true) _fallback.Value.Dispose(); _descriptors.Dispose(); _sift.Dispose(); }
}

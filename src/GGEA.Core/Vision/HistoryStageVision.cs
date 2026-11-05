using System.IO;
using System.Runtime.InteropServices;
using GGEA.App.Config;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace GGEA.App.Vision;

/// <summary>同一历史标题下还要区分作品总览和节点轨道，避免导航误停或在错误页面点击固定坐标。</summary>
public enum HistoryPageKind { Unknown, List, Overview, StageNodes, Boss }

/// <summary>本帧观察不保存导航状态；列表候选只能授权选中，不能授权进入关卡。</summary>
public sealed record HistoryStageObservation(bool HistoryTitle, TemplateMatchResult? Entry,
    TemplateMatchResult? Go, Roi? Candidate, int? SelectedWorkId, int? DetailWorkId)
{
    /// <summary>本帧已确认主要关卡标题；入口被 NEW 遮挡时仍可向导航层提供页面证据。</summary>
    public bool MainStageTitle { get; init; }
    /// <summary>只有已确认的其他作品详情允许使用返回按钮重新选择。</summary>
    public TemplateMatchResult? Back { get; init; }
    /// <summary>仅在作品关卡页识别攻略度；未知不能当成零进度。</summary>
    public HistoryProgressObservation? Progress { get; init; }
    public HistoryPageKind Page { get; init; }
}

/// <summary>隔离截图识别，使导航状态机可用确定性的页面序列验证。</summary>
public interface IHistoryStageVision
{
    HistoryStageObservation Inspect(ScreenImage screen, int targetId, CancellationToken ct);
}

/// <summary>历史 Logo 使用限尺度、限旋转的特征定位；高分完整纹理与相似组副标题复核才确认身份。</summary>
public sealed class HistoryStageVision : IHistoryStageVision, IDisposable
{
    public static readonly Roi ListRegion = new(0, 120, 365, 850);
    public static readonly Roi SelectedRegion = new(650, 250, 800, 350);
    public static readonly Roi DetailRegion = new(0, 700, 650, 270);
    private readonly HistoryStageCatalog _catalog;
    private readonly TemplateMatcher _matcher = new();
    private readonly SIFT _sift = SIFT.Create(0, 3, .015);
    private readonly Dictionary<int, Lazy<Logo>[]> _logos = [];
    private readonly TemplateImages _mainTitle, _entry, _go, _back;
    private readonly HistoryProgressVision _progress;
    private readonly SelectStageTitleRecognizer _historyTitle, _bossTitle;
    private readonly SelectStageTitleRecognizer? _historyNodeTitle;
    private readonly StageEntryButtonRecognizer _entryButtons = new();
    private readonly string[] _nodeButtons;

    /// <summary>一次会话加载全部竞争素材；缺任何素材立即失败，不带着不完整的比较集合操作设备。</summary>
    public HistoryStageVision(HistoryStageCatalog catalog, string stageDirectory, OnnxTextRecognizer? ocr = null)
    {
        _catalog = catalog;
        try
        {
            _progress = new HistoryProgressVision(stageDirectory, ocr);
            _historyTitle = new(ConfigService.ResolvePath(Path.Combine(stageDirectory, "history_stage.png")));
            // 日服节点页使用无描边标题，列表和总览使用描边标题；分别保留实机字形，不放宽标题阈值。
            var nodeTitle = ConfigService.ResolvePath(Path.Combine(stageDirectory, "history_stage_nodes.png"));
            if (File.Exists(nodeTitle)) _historyNodeTitle = new(nodeTitle);
            _bossTitle = new(ConfigService.ResolvePath(Path.Combine(stageDirectory, "history_boss_title.png")));
            _nodeButtons = new[] { "button_sortie_preparations.png", "button_sortie.png", "watch.png" }
                .Select(name => ConfigService.ResolvePath(Path.Combine(stageDirectory, "sortie", name))).ToArray();
            _mainTitle = Load("main_stage.png");
            _entry = Load("history_entry.png");
            _go = Load("history_go.png");
            _back = Load("history_back.png");
            foreach (var work in catalog.Works)
            {
                var variants = ConfigService.Resources.TemplateCandidates(Path.Combine(stageDirectory, "history_stage", work.Screenshot))
                    .Select(path => new Lazy<Logo>(() => new Logo(path, _sift))).ToArray();
                _logos.Add(work.Id, variants);
                _ = variants[0].Value;
            }
        }
        catch { Dispose(); throw; }
        TemplateImages Load(string file) => new(Path.Combine(stageDirectory, file));
    }

    /// <summary>页面标题与按钮必须在同帧成立；最终 Logo 严格限制在左下区域，中央预览不能冒充关卡页。</summary>
    public HistoryStageObservation Inspect(ScreenImage screen, int targetId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (screen.Width != 1920 || screen.Height != 1080)
            throw new InvalidOperationException("历史关卡导航仅支持 1920×1080 截图。");
        if (!_logos.ContainsKey(targetId)) throw new InvalidOperationException("历史作品未配置。");
        var titleRoi = RecognitionRegions.Resolve(RecognitionRegion.PageTitle, screen.Width, screen.Height);
        var historyTitle = _historyTitle.Recognize(screen, ct) || (_historyNodeTitle?.Recognize(screen, ct) ?? false);
        var bossTitle = !historyTitle && _bossTitle.Recognize(screen, ct);
        if (!historyTitle && !bossTitle)
        {
            var mainTitle = Match(screen, _mainTitle, titleRoi, ct) is not null;
            return new(false, mainTitle ? Match(screen, _entry, new(400, 825, 125, 70), ct) : null,
                null, null, null, null) { MainStageTitle = mainTitle };
        }

        using var full = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
        using var gray = new Mat();
        Cv2.CvtColor(full, gray, ColorConversionCodes.BGRA2GRAY);
        var go = historyTitle ? Match(screen, _go, new(1490, 830, 380, 130), ct) : null;
        if (go is null)
        {
            var id = Confirm(gray, DetailRegion, .3, 1.0, ct);
            var overview = historyTitle && _progress.IsOverview(screen, ct);
            var page = bossTitle ? HistoryPageKind.Boss : overview ? HistoryPageKind.Overview :
                _nodeButtons.Any(path => _entryButtons.Match(screen, path, new(1470, 754, 450, 226), .90, ct) is not null)
                    ? HistoryPageKind.StageNodes : HistoryPageKind.Unknown;
            return new(historyTitle, null, null, null, null, id)
            {
                Back = Match(screen, _back, new(0, 0, 210, 110), ct), Page = page,
                Progress = overview ? _progress.Inspect(screen, ct) : null
            };
        }

        // 按钮暗态或灰色文字不能授权进入；蓝底比例与模板共同确认可用态。
        var blue = 0;
        for (var y = go.Region.Y; y < go.Region.Y + go.Region.Height; y++)
        for (var x = go.Region.X; x < go.Region.X + go.Region.Width; x++)
        {
            var i = (y * screen.Width + x) * 4;
            if (screen.Bgra[i] > 120 && screen.Bgra[i] > screen.Bgra[i + 2] * 1.2) blue++;
        }
        if (blue < go.Region.Width * go.Region.Height / 10)
            return new(true, null, null, null, null, null);

        var selected = Confirm(gray, SelectedRegion, .65, 1.2, ct);
        using var patch = new Mat(gray, Rect(ListRegion));
        using var large = new Mat(); using var descriptors = new Mat();
        Cv2.Resize(patch, large, new Size(), 3, 3, InterpolationFlags.Linear);
        _sift.DetectAndCompute(large, null, out var points, descriptors);
        Roi? bounds = null;
        foreach (var lazy in _logos[targetId])
        {
            var logo = lazy.Value;
            var candidate = Locate(logo, points, descriptors, 3, .16, .36, ct);
            if (candidate is null) continue;
            var found = Bounds(candidate, logo, ListRegion);
            if (found.X < 0 || found.Y < ListRegion.Y || found.X + found.Width > 385 || found.Y + found.Height > 970) continue;
            bounds = found;
            break;
        }
        // 选中卡片会略微向右放大；候选仅定位已见主体，点击仍限定左侧 365 像素，身份交给中央大图复核。
        // 垂直裁断的卡片不参与点击，避免把底栏遮挡处当作完整列表项。
        if (bounds is { } b)
            bounds = b.X < 0 || b.Y < ListRegion.Y || b.X + b.Width > 385 || b.Y + b.Height > 970
                ? null : b with { Width = Math.Min(b.Width, 365 - b.X) };
        return new(true, null, go, bounds, selected, null) { Page = HistoryPageKind.List };
    }

    /// <summary>所有目录作品共同竞争，分数接近即拒绝；相似组还须独立通过底部副标题比较。</summary>
    private int? Confirm(Mat gray, Roi roi, double minScale, double maxScale, CancellationToken ct)
    {
        using var patch = new Mat(gray, Rect(roi)); using var descriptors = new Mat();
        _sift.DetectAndCompute(patch, null, out var points, descriptors);
        var scores = new List<(int Id, double Whole, double Subtitle)>();
        foreach (var (id, variants) in _logos)
        {
            (int Id, double Whole, double Subtitle)? observed = null;
            foreach (var lazy in variants)
            {
                ct.ThrowIfCancellationRequested();
                var logo = lazy.Value;
                var transform = Locate(logo, points, descriptors, 1, minScale, maxScale, ct);
                if (transform is null) continue;
                var bounds = Bounds(transform, logo, new(0, 0, roi.Width, roi.Height));
                if (bounds.X < 0 || bounds.Y < 0 || bounds.X + bounds.Width > roi.Width || bounds.Y + bounds.Height > roi.Height) continue;
                var (whole, subtitle) = Correlations(patch, logo, transform);
                // 同一作品只保留一个竞争分数；语言素材达到身份门槛后不再比较通用素材。
                if (whole >= .80 && (_catalog.ConfusableWith(id).Count == 0 || subtitle >= .60))
                { observed = (id, whole, subtitle); break; }
                if (observed is null || whole > observed.Value.Whole) observed = (id, whole, subtitle);
            }
            if (observed is { } score) scores.Add(score);
        }
        var ranked = scores.OrderByDescending(s => s.Whole).ToArray();
        if (ranked.Length == 0 || ranked[0].Whole < .80 ||
            (ranked.Length > 1 && ranked[0].Whole - ranked[1].Whole < .06)) return null;
        var winner = ranked[0];
        var family = _catalog.ConfusableWith(winner.Id);
        if (family.Count > 0 && (winner.Subtitle < .60 || scores.Any(other => other.Id != winner.Id &&
                family.Contains(other.Id) && winner.Subtitle - other.Subtitle < .15))) return null;
        return winner.Id;
    }

    /// <summary>成对特征枚举得到确定性相似变换，只接纳有限尺度与 ±4° 旋转，禁止透视猜测。</summary>
    private static Transform? Locate(Logo logo, KeyPoint[] points, Mat descriptors, int magnification,
        double minScale, double maxScale, CancellationToken ct)
    {
        if (descriptors.Rows < 2) return null;
        using var matcher = new BFMatcher(NormTypes.L2);
        var pairs = matcher.KnnMatch(logo.Descriptors, descriptors, 2)
            .Where(m => m.Length == 2 && m[0].Distance < .7 * m[1].Distance).Select(m => m[0])
            .OrderBy(m => m.Distance).DistinctBy(m => m.TrainIdx).Take(100).ToArray();
        if (pairs.Length < 12) return null;
        var src = pairs.Select(m => logo.Points[m.QueryIdx].Pt).ToArray();
        var dst = pairs.Select(m => new Point2f(points[m.TrainIdx].Pt.X / magnification,
            points[m.TrainIdx].Pt.Y / magnification)).ToArray();
        Transform? best = null; int[] bestInliers = [];
        var hypotheses = new List<(Transform Transform, int Count)>();
        for (var i = 0; i < pairs.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (var j = i + 1; j < pairs.Length; j++)
            {
                double dx = src[j].X - src[i].X, dy = src[j].Y - src[i].Y;
                var length = dx * dx + dy * dy;
                if (length < 2500) continue;
                var a = ((dst[j].X - dst[i].X) * dx + (dst[j].Y - dst[i].Y) * dy) / length;
                var b = ((dst[j].Y - dst[i].Y) * dx - (dst[j].X - dst[i].X) * dy) / length;
                var t = new Transform(a, b, dst[i].X - a * src[i].X + b * src[i].Y,
                    dst[i].Y - b * src[i].X - a * src[i].Y);
                if (!Allowed(t)) continue;
                var inliers = Enumerable.Range(0, pairs.Length).Where(k => Error(t, src[k], dst[k]) <= 2.25).ToArray();
                if (inliers.Length < 12) continue;
                hypotheses.Add((t, inliers.Length));
                if (inliers.Length > bestInliers.Length) { best = t; bestInliers = inliers; }
            }
        }
        if (best is null || bestInliers.Length < pairs.Length * .45 ||
            hypotheses.Any(h => h.Count >= bestInliers.Length * .8 &&
                (Math.Abs(h.Transform.X - best.X) > 25 || Math.Abs(h.Transform.Y - best.Y) > 25))) return null;
        // 用同一组内点最小二乘细化，避免单对关键点误差损坏副标题的像素复核。
        var sx = bestInliers.Average(k => src[k].X); var sy = bestInliers.Average(k => src[k].Y);
        var dxMean = bestInliers.Average(k => dst[k].X); var dyMean = bestInliers.Average(k => dst[k].Y);
        double numeratorA = 0, numeratorB = 0, denominator = 0;
        foreach (var k in bestInliers)
        {
            var x = src[k].X - sx; var y = src[k].Y - sy; var u = dst[k].X - dxMean; var v = dst[k].Y - dyMean;
            numeratorA += x * u + y * v; numeratorB += x * v - y * u; denominator += x * x + y * y;
        }
        if (denominator <= 0) return null;
        var aa = numeratorA / denominator; var bb = numeratorB / denominator;
        var refined = new Transform(aa, bb, dxMean - aa * sx + bb * sy, dyMean - bb * sx - aa * sy);
        return Allowed(refined) ? refined : null;
        bool Allowed(Transform t) => t.Scale >= minScale && t.Scale <= maxScale && Math.Abs(Math.Atan2(t.B, t.A)) <= Math.PI / 45;
    }

    /// <summary>相同缩放下复原纹理；忽略透明边缘，副标题只取底部 22%，排除 THUNDERBOLT 的共用主标题。</summary>
    private static (double Whole, double Subtitle) Correlations(Mat patch, Logo logo, Transform t)
    {
        using var transform = new Mat(2, 3, MatType.CV_64FC1);
        transform.Set(0, 0, t.A); transform.Set(0, 1, -t.B); transform.Set(0, 2, t.X);
        transform.Set(1, 0, t.B); transform.Set(1, 1, t.A); transform.Set(1, 2, t.Y);
        using var aligned = new Mat(); using var small = new Mat(); using var reference = new Mat();
        Cv2.WarpAffine(patch, aligned, transform, logo.Gray.Size(), InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap);
        Cv2.Resize(logo.Gray, small, new Size(Math.Max(1, (int)Math.Round(logo.Gray.Width * t.Scale)),
            Math.Max(1, (int)Math.Round(logo.Gray.Height * t.Scale))), 0, 0, InterpolationFlags.Area);
        Cv2.Resize(small, reference, logo.Gray.Size());
        var width = logo.Gray.Width; var height = logo.Gray.Height;
        var observed = new byte[width * height]; var expected = new byte[observed.Length]; var mask = new byte[observed.Length];
        Marshal.Copy(aligned.Data, observed, 0, observed.Length);
        Marshal.Copy(reference.Data, expected, 0, expected.Length);
        Marshal.Copy(logo.Mask.Data, mask, 0, mask.Length);
        return (Correlation(0), Correlation((int)(height * .78)));
        double Correlation(int top)
        {
            double sumX = 0, sumY = 0, xx = 0, yy = 0, xy = 0; var n = 0;
            for (var i = top * width; i < observed.Length; i++)
            {
                if (mask[i] == 0) continue;
                double a = expected[i], b = observed[i];
                sumX += a; sumY += b; xx += a * a; yy += b * b; xy += a * b; n++;
            }
            if (n < 32) return -1;
            var variance = (xx - sumX * sumX / n) * (yy - sumY * sumY / n);
            return variance > 0 ? (xy - sumX * sumY / n) / Math.Sqrt(variance) : -1;
        }
    }

    /// <summary>模板只负责同帧页面/按钮授权，阈值保持 90%。</summary>
    private TemplateMatchResult? Match(ScreenImage screen, TemplateImages template, Roi roi, CancellationToken ct)
        => template.Match(_matcher, screen, .90, roi, ct);
    private static Rect Rect(Roi r) => new(r.X, r.Y, r.Width, r.Height);
    private static double Error(Transform t, Point2f s, Point2f d)
        => Math.Pow(t.A * s.X - t.B * s.Y + t.X - d.X, 2) + Math.Pow(t.B * s.X + t.A * s.Y + t.Y - d.Y, 2);

    /// <summary>点击边界由实际特征变换得到，不使用作品编号或列表行序。</summary>
    private static Roi Bounds(Transform t, Logo logo, Roi roi)
    {
        var corners = new[] { new Point2f(0, 0), new Point2f(logo.Gray.Width, 0),
            new Point2f(0, logo.Gray.Height), new Point2f(logo.Gray.Width, logo.Gray.Height) };
        var xs = corners.Select(p => t.A * p.X - t.B * p.Y + t.X).ToArray();
        var ys = corners.Select(p => t.B * p.X + t.A * p.Y + t.Y).ToArray();
        return new(roi.X + (int)Math.Floor(xs.Min()), roi.Y + (int)Math.Floor(ys.Min()),
            (int)Math.Ceiling(xs.Max()) - (int)Math.Floor(xs.Min()), (int)Math.Ceiling(ys.Max()) - (int)Math.Floor(ys.Min()));
    }

    /// <summary>一次导航会话独占原生特征缓存，结束、取消或构造失败都释放。</summary>
    public void Dispose()
    {
        foreach (var variants in _logos.Values)
            foreach (var logo in variants) if (logo.IsValueCreated) logo.Value.Dispose();
        _logos.Clear();
        _sift.Dispose();
        _progress?.Dispose();
    }
    private sealed record Transform(double A, double B, double X, double Y) { public double Scale => Math.Sqrt(A * A + B * B); }

    /// <summary>Logo 原始灰度、透明遮罩与特征只初始化一次，不把原生资源留给终结器。</summary>
    private sealed class Logo : IDisposable
    {
        public Mat Gray { get; } = new();
        public Mat Mask { get; } = new();
        public Mat Descriptors { get; } = new();
        public KeyPoint[] Points { get; }
        public Logo(string path, SIFT sift)
        {
            try
            {
                using var source = Cv2.ImRead(path, ImreadModes.Unchanged);
                if (source.Empty() || source.Channels() != 4) throw new InvalidDataException($"历史 Logo 不是 RGBA 素材：{Path.GetFileName(path)}");
                Cv2.CvtColor(source, Gray, ColorConversionCodes.BGRA2GRAY);
                using var alpha = new Mat(); Cv2.ExtractChannel(source, alpha, 3);
                Cv2.Threshold(alpha, Mask, 220, 255, ThresholdTypes.Binary);
                sift.DetectAndCompute(Gray, Mask, out var points, Descriptors); Points = points;
                if (Descriptors.Rows < 12) throw new InvalidDataException($"历史 Logo 缺少可定位特征：{Path.GetFileName(path)}");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Gray.Dispose(); Mask.Dispose(); Descriptors.Dispose(); }
    }
}

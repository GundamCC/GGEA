using System.IO;
using System.Runtime.InteropServices;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Tasks;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>复用项目已有 OpenCV 解码 WebP，以透明遮罩和邻近尺度识别作品。</summary>
public sealed class WorkLogoVision : IWorkLogoVision
{
    public static readonly Roi OverviewTitleRoi = new(123, 120, 510, 180);
    public static readonly Roi MainStageTitleRoi = new(0, 0, 737, 104);
    public static readonly Roi SelectRoi = new(1484, 816, 436, 144);
    private static readonly double[] CanvasScales = [0.414, 0.417, 0.420, 0.423];
    private static readonly double[] OverviewScales = [0.417, 0.43, 0.44, 0.445, 0.45, 0.46, 0.48, 0.50, 0.55];
    private readonly IAdbService _adb;
    private readonly string _directory;
    private readonly string _mainStageTemplate;
    private readonly StageNavigationOptions _options;
    private readonly TemplateMatcher _matcher;
    // 仅缓存托管像素，短生命周期 Mat 均使用 using 释放，不将原生内存留给终结器。
    private readonly Dictionary<(string Path, int Width, int Height, bool Overview, byte Background, bool Smooth), LogoPixels> _logos = new();
    private readonly Dictionary<string, ScreenImage> _buttons = new();

    /// <summary>路径由组合根传入；截图与 CPU 匹配在后台执行，不阻塞 WPF。</summary>
    public WorkLogoVision(IAdbService adb, TemplateMatcher matcher, string directory,
        string mainStageTemplate, StageNavigationOptions options)
    {
        _adb = adb;
        _matcher = matcher;
        _directory = directory;
        _mainStageTemplate = mainStageTemplate;
        _options = options;
    }

    /// <summary>标题、按钮和页面标识共享同一帧，只有验证失败时才扫描画布作品。</summary>
    public async Task<WorkLogoObservation> ObserveAsync(string serial, HardStageEntry target,
        IReadOnlyList<HardStageEntry> works, CancellationToken ct)
    {
        var png = await _adb.ScreenshotAsync(serial, ct);
        return (await Task.Run(() => Inspect(png, target, works, ct), ct)) with { ScreenshotPng = png };
    }

    /// <summary>可对已采集的真实截图执行相同识别流程，供离线回归验证使用。</summary>
    public WorkLogoObservation Inspect(byte[] png, HardStageEntry target,
        IReadOnlyList<HardStageEntry> works, CancellationToken ct = default)
    {
        var screen = ImageCodec.DecodePng(png);
        if (screen.Width != 1920 || screen.Height != 1080)
            throw new InvalidOperationException("作品 Logo 导航仅支持已校准的 1920×1080 截图");
        using var gray = Cv2.ImDecode(png, ImreadModes.Grayscale);
        // 挑战地图也使用“主要关卡”标题及作品 Logo；左下 STORY 入口证明当前不是普通地图。
        // 将其作为错误页面交给主界面恢复，禁止使用挑战地图的位置推算普通作品坐标。
        var story = Path.Combine(Path.GetDirectoryName(_mainStageTemplate)!, "challenge_story.png");
        if (MatchControl(screen, story, new Roi(0, 830, 360, 140)) is not null)
            return new(false, null, false, false, []) { Diagnostic = "已识别挑战地图 STORY 入口" };
        var select = MatchControl(screen, _options.WorkSelectTemplatePath, SelectRoi);
        var closed = MatchControl(screen, _options.WorkSelectClosedTemplatePath, SelectRoi) is not null;
        var main = MatchControl(screen, _mainStageTemplate, MainStageTitleRoi) is not null;
        var title = MatchLogo(gray, target, OverviewTitleRoi, OverviewScales, false, ct);
        var titleVisible = title is not null && title.Confidence >= 0.80;
        // SEED 与 Recollection 的主体极相似，标题必须胜过同族模板，不能只命中共同文字。
        if (titleVisible && IsSeedFamily(target))
        {
            foreach (var other in works.Where(work => work.Id != target.Id && IsSeedFamily(work)))
            {
                var competing = MatchLogo(gray, other, OverviewTitleRoi, OverviewScales, false, ct);
                if (competing is not null && competing.Confidence >= title!.Confidence - 0.035 &&
                    !HasDistinctRecollectionSubtitle(gray, target, title, other, competing, true))
                    titleVisible = false;
            }
        }
        if (titleVisible && (select is not null || closed))
            return new(true, select, closed, main, []);

        var matches = new List<WorkLogoMatch>();
        var candidates = new List<WorkLogoMatch>();
        var missing = new List<int>();
        foreach (var work in works)
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(work.Screenshot) != work.Screenshot ||
                !File.Exists(ConfigService.ResolvePath(Path.Combine(_directory, work.Screenshot))))
                missing.Add(work.Id);
            var match = MatchLogo(gray, work, new Roi(0, 104, 1920, 871), CanvasScales, true, ct);
            if (match is not null) candidates.Add(match);
            // 画布允许较弱候选供二次验证；绝不直接将此分数作为选择/消耗 AP 的授权。
            // 初代浅色主体在 OpenCV 插值下的已核对样本约 0.799；只对此素材单独标定。
            var threshold = work.Screenshot == "logo_l_series_0010.webp" ? 0.78 : 0.80;
            if (match is not null && match.Confidence >= threshold)
                matches.Add(match);
        }
        // 同位置竞争默认拒绝；仅在 Recollection 整体占优且副标题独立命中时排除普通 SEED。
        var unambiguous = matches.Where(match => !matches.Any(other => other.WorkId != match.WorkId &&
            Math.Abs(other.X - match.X) < 100 && Math.Abs(other.Y - match.Y) < 60 &&
            other.Confidence >= match.Confidence - 0.035 &&
            !HasDistinctRecollectionSubtitle(gray, works.First(work => work.Id == match.WorkId), match,
                works.First(work => work.Id == other.WorkId), other, false))).ToArray();
        // 保留被阈值或同位置竞争拒绝的最佳候选，普通日志即可区分低分和缺素材；不额外截图。
        var best = string.Join("；", candidates.OrderByDescending(candidate => candidate.Confidence).Take(3)
            .Select(candidate => $"id={candidate.WorkId},分数={candidate.Confidence:F3},位置=({candidate.X},{candidate.Y})"));
        // 目标不一定进入前三名，单独记录以区分低分、同位置竞争和素材缺失。
        var targetCandidate = candidates.FirstOrDefault(candidate => candidate.WorkId == target.Id);
        var competitors = targetCandidate is null ? "" : string.Join(",", candidates.Where(candidate =>
                candidate.WorkId != target.Id && candidate.Confidence >= .80 && Math.Abs(candidate.X - targetCandidate.X) < 100 &&
                Math.Abs(candidate.Y - targetCandidate.Y) < 60)
            .Select(candidate => $"id={candidate.WorkId},分数={candidate.Confidence:F3}"));
        var targetDiagnostic = targetCandidate is null ? "无候选" :
            $"分数={targetCandidate.Confidence:F3},位置=({targetCandidate.X},{targetCandidate.Y}),通过={unambiguous.Any(match => match.WorkId == target.Id)}";
        return new(titleVisible, select, closed, main, unambiguous)
        {
            Diagnostic = $"目标作品 {target.Id}=[{targetDiagnostic}]，同位置候选=[{competitors}]，Logo最佳候选=[{best}]，缺失素材作品=[{string.Join(",", missing)}]"
        };
    }

    /// <summary>只对实际识别易混淆的 SEED 系列额外做竞争模板校验。</summary>
    private static bool IsSeedFamily(HardStageEntry work)
        => work.Screenshot is "logo_l_series_3000.webp" or "logo_l_series_3100.webp" or "logo_l_series_3300.webp";

    /// <summary>共同的 SEED 主体不能消除歧义；在已定位 Logo 内独立复核 Recollection 字样。
    /// 只允许整体分数更高的 Recollection 排除普通 SEED，不影响其他作品的竞争保护。</summary>
    private bool HasDistinctRecollectionSubtitle(Mat gray, HardStageEntry work, WorkLogoMatch match,
        HardStageEntry otherWork, WorkLogoMatch other, bool overview)
    {
        if (work.Screenshot != "logo_l_series_3100.webp" || otherWork.Screenshot != "logo_l_series_3000.webp" ||
            match.Confidence <= other.Confidence || Math.Abs(match.X - other.X) >= 100 || Math.Abs(match.Y - other.Y) >= 60)
            return false;
        foreach (var path in ConfigService.Resources.TemplateCandidates(Path.Combine(_directory, work.Screenshot)))
        {
            if (!File.Exists(path)) continue;
            using var original = Cv2.ImRead(path, ImreadModes.Unchanged);
            if (original.Empty() || original.Channels() != 4) continue;
            var logo = GetLogo(path, original, (double)match.Bounds.Width / original.Width, overview, 192);
            // 字样在原素材右下方；按已命中尺度映射，不重新搜索，也不借用其他位置的文字。
            var area = new Rect((int)(logo.Width * .59), (int)(logo.Height * .56),
                (int)(logo.Width * .37), (int)(logo.Height * .20));
            using var template = Mat.FromPixelData(logo.Height, logo.Width, MatType.CV_8UC1, logo.Gray);
            using var mask = Mat.FromPixelData(logo.Height, logo.Width, MatType.CV_8UC1, logo.Mask);
            using var subtitle = new Mat(template, area);
            using var subtitleMask = new Mat(mask, area);
            using var patch = new Mat(gray, new Rect(match.Bounds.X + area.X, match.Bounds.Y + area.Y, area.Width, area.Height));
            using var result = new Mat();
            Cv2.MatchTemplate(patch, subtitle, result, TemplateMatchModes.CCoeffNormed, subtitleMask);
            var score = result.At<float>(0, 0);
            if (float.IsFinite(score) && score >= .80 && score <= 1.001) return true;
        }
        return false;
    }

    /// <summary>选择按钮和主要关卡文字沿用原匹配器，并限制用户指定的搜索范围。</summary>
    private TemplateMatchResult? MatchControl(ScreenImage screen, string path, Roi roi)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return TemplateFallback.Match(path, candidate => MatchControlSingle(screen, candidate, roi,
            path == _options.WorkSelectClosedTemplatePath));
    }

    /// <summary>暗态纹理复核使用当前候选的尺寸与像素，不把语言身份当成物理路径比较。</summary>
    private TemplateMatchResult? MatchControlSingle(ScreenImage screen, string path, Roi roi, bool closed)
    {
        if (!File.Exists(path)) return null;
        if (!_buttons.TryGetValue(path, out var template))
            _buttons[path] = template = ImageCodec.DecodePng(File.ReadAllBytes(path));
        var candidate = _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
            template.Width, template.Height, _options.WorkSelectThreshold, roi);
        if (candidate is null) return null;
        // SSD 对大面积同色背景可能给出高分，再核对按钮/标题纹理，拒绝画布上的假暗态选择。
        using var source = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
        using var target = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC4, template.Bgra);
        using var sourceGray = new Mat(); using var targetGray = new Mat(); using var result = new Mat();
        Cv2.CvtColor(source, sourceGray, ColorConversionCodes.BGRA2GRAY);
        Cv2.CvtColor(target, targetGray, ColorConversionCodes.BGRA2GRAY);
        // 暗态按钮顶部可能被“特别活动举办中”的箭头覆盖；复核下方主体文字与边框，
        // 不降低相关性阈值，也不取消完整按钮的 SSD 校验。
        var topInset = closed ? template.Height / 5 : 0;
        // 暗态按钮受标牌和动态背景影响，SSD 最佳位置可能偏移一两个像素。
        // 只在原 ROI 内精修邻近位置，再于同一位置复核完整 SSD，不能用不同位置的分数拼成命中。
        var radius = closed ? 2 : 0;
        var left = Math.Max(roi.X, candidate.X - radius);
        var top = Math.Max(roi.Y, candidate.Y - radius);
        var right = Math.Min(roi.X + roi.Width - template.Width, candidate.X + radius);
        var bottom = Math.Min(roi.Y + roi.Height - template.Height, candidate.Y + radius);
        using var patch = new Mat(sourceGray, new Rect(left, top + topInset,
            right - left + template.Width, bottom - top + template.Height - topInset));
        using var texture = new Mat(targetGray, new Rect(0, topInset, template.Width, template.Height - topInset));
        Cv2.MatchTemplate(patch, texture, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out var correlation, out _, out var location);
        if (!double.IsFinite(correlation) || correlation < 0.80) return null;
        return _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
            template.Width, template.Height, _options.WorkSelectThreshold,
            new Roi(left + location.X, top + location.Y, template.Width, template.Height));
    }

    /// <summary>画布先半分辨率搜索，再原尺寸局部复核；详情标题区域直接原尺寸搜索。</summary>
    private WorkLogoMatch? MatchLogo(Mat gray, HardStageEntry work, Roi roi, double[] scales,
        bool coarse, CancellationToken ct)
    {
        // 配置只允许本目录文件，避免将任意路径当作作品素材加载。
        if (Path.GetFileName(work.Screenshot) != work.Screenshot) return null;
        var path = Path.Combine(_directory, work.Screenshot);
        WorkLogoMatch? observed = null;
        var threshold = coarse && work.Screenshot == "logo_l_series_0010.webp" ? .78 : .80;
        foreach (var candidate in ConfigService.Resources.TemplateCandidates(path))
        {
            ct.ThrowIfCancellationRequested();
            var match = MatchLogoSingle(gray, work, candidate, roi, scales, coarse, ct);
            if (match is not null && match.Confidence >= threshold) return match;
            if (coarse && work.Screenshot == "logo_l_series_0010.webp")
            {
                // 游戏缩小渲染会平滑浅色 G 的细边缘；仍使用同一 WebP、遮罩与业务阈值。
                var rendered = MatchLogoSingle(gray, work, candidate, roi, scales, coarse, ct, smooth: true);
                if (rendered is not null && rendered.Confidence >= threshold) return rendered;
                if (rendered?.Confidence > match?.Confidence) match = rendered;
            }
            if (observed is null || match?.Confidence > observed.Confidence) observed = match;
        }
        // 初代浅色 G 的原始 WebP 与游戏实际渲染存在差异。仅在原 Logo 已定位但未过门槛时，
        // 用独立采集的实机画布模板复核邻域，不能从推算坐标直接授权，也不降低原门槛。
        if (coarse && work.Screenshot == "logo_l_series_0010.webp" && observed is { Confidence: >= .70 })
        {
            var left = Math.Max(roi.X, observed.Bounds.X - 5);
            var top = Math.Max(roi.Y, observed.Bounds.Y - 5);
            var right = Math.Min(roi.X + roi.Width, observed.Bounds.X + observed.Bounds.Width + 5);
            var bottom = Math.Min(roi.Y + roi.Height, observed.Bounds.Y + observed.Bounds.Height + 5);
            foreach (var calibrated in ConfigService.Resources.TemplateCandidates(Path.Combine(_directory, "initial_canvas.png")))
            {
                var verified = MatchLogoSingle(gray, work, calibrated, new Roi(left, top, right - left, bottom - top),
                    [1.0], false, ct);
                if (verified is { Confidence: >= .90 }) return verified;
            }
        }
        return observed;
    }

    /// <summary>低于原业务门槛才补试通用 Logo；未达标观察仅用于诊断和竞争校验。</summary>
    private WorkLogoMatch? MatchLogoSingle(Mat gray, HardStageEntry work, string path, Roi roi,
        double[] scales, bool coarse, CancellationToken ct, bool smooth = false)
    {
        if (!File.Exists(path)) return null;
        using var original = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (original.Empty() || original.Channels() != 4) return null;
        using var region = new Mat(gray, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
        using var small = new Mat();
        if (coarse) Cv2.Resize(region, small, new Size(region.Width / 2, region.Height / 2), 0, 0, InterpolationFlags.Area);
        WorkLogoMatch? best = null;
        // 初代 Logo 的浅色 G 主体与白光晕接近，使用实机标定的白底；其他作品用亮灰底。
        byte background = work.Screenshot == "logo_l_series_0010.webp" ? (byte)255 : (byte)192;
        foreach (var scale in scales)
        {
            ct.ThrowIfCancellationRequested();
            var template = GetLogo(path, original, scale, !coarse, background, smooth);
            if (template.Width > region.Width || template.Height > region.Height) continue;
            var searchTemplate = coarse ? GetLogo(path, original, scale / 2, false, background, smooth) : template;
            var candidates = Peaks(coarse ? small : region, searchTemplate, coarse ? 5 : 1);
            foreach (var candidate in candidates)
            {
                int x = candidate.X, y = candidate.Y;
                double score = candidate.Score;
                if (coarse)
                {
                    x *= 2; y *= 2;
                    var left = Math.Max(0, x - 5); var top = Math.Max(0, y - 5);
                    var right = Math.Min(region.Width, x + template.Width + 5);
                    var bottom = Math.Min(region.Height, y + template.Height + 5);
                    if (right - left < template.Width || bottom - top < template.Height) continue;
                    using var local = new Mat(region, new Rect(left, top, right - left, bottom - top));
                    var refined = Peaks(local, template, 1).FirstOrDefault();
                    if (refined is null) continue;
                    x = left + refined.X; y = top + refined.Y; score = refined.Score;
                }
                if (best is null || score > best.Confidence)
                    best = new(work.Id, score, new Roi(roi.X + x, roi.Y + y, template.Width, template.Height));
            }
        }
        return best;
    }

    /// <summary>保留半透明抗锯齿：画布合成亮灰底并选主体，详情页合成暗底以保留完整光晕结构。</summary>
    private LogoPixels GetLogo(string path, Mat source, double scale, bool overview, byte background, bool smooth = false)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var key = (path, width, height, overview, background, smooth);
        if (_logos.TryGetValue(key, out var cached)) return cached;
        using var sourceGray = new Mat();
        using var sourceAlpha = new Mat();
        using var gray = new Mat();
        using var alpha = new Mat();
        Cv2.CvtColor(source, sourceGray, ColorConversionCodes.BGRA2GRAY);
        Cv2.ExtractChannel(source, sourceAlpha, 3);
        var originalPixels = new byte[source.Width * source.Height];
        var originalAlpha = new byte[originalPixels.Length];
        Marshal.Copy(sourceGray.Data, originalPixels, 0, originalPixels.Length);
        Marshal.Copy(sourceAlpha.Data, originalAlpha, 0, originalAlpha.Length);
        // 必须先合成再缩放；直接对直通 RGBA 插值会让透明像素的 RGB 污染边缘。
        for (var i = 0; i < originalPixels.Length; i++)
            originalPixels[i] = (byte)((originalPixels[i] * originalAlpha[i] +
                (overview ? 0 : background) * (255 - originalAlpha[i]) + 127) / 255);
        using var composited = Mat.FromPixelData(source.Height, source.Width, MatType.CV_8UC1, originalPixels);
        Cv2.Resize(composited, gray, new Size(width, height), 0, 0, InterpolationFlags.Lanczos4);
        Cv2.Resize(sourceAlpha, alpha, new Size(width, height), 0, 0, InterpolationFlags.Lanczos4);
        // 浅色 G 使用严格不透明区域，沿用原前景颜色以保留微弱的主体色差。
        if (!overview && background == 255)
            Cv2.Resize(sourceGray, gray, new Size(width, height), 0, 0, InterpolationFlags.Lanczos4);
        // 0.8 像素对应本次实机边缘的平滑宽度；半分辨率搜索使用等比例核，避免粗搜与精搜不一致。
        if (smooth) Cv2.GaussianBlur(gray, gray, new Size(0, 0), 0.8 * scale / 0.417);
        var pixels = new byte[width * height]; var maskBytes = new byte[pixels.Length];
        Marshal.Copy(gray.Data, pixels, 0, pixels.Length);
        Marshal.Copy(alpha.Data, maskBytes, 0, maskBytes.Length);
        for (var i = 0; i < pixels.Length; i++)
        {
            var opacity = maskBytes[i];
            maskBytes[i] = overview || opacity >= (background == 255 ? 245 : 128) ? (byte)255 : (byte)0;
        }
        return _logos[key] = new(width, height, pixels, maskBytes);
    }

    /// <summary>过滤零方差引起的 NaN/Infinity，再取互不重叠候选，避免数值异常成为高分命中。</summary>
    private static IReadOnlyList<LogoPeak> Peaks(Mat screen, LogoPixels pixels, int count)
    {
        if (screen.Width < pixels.Width || screen.Height < pixels.Height) return [];
        using var template = Mat.FromPixelData(pixels.Height, pixels.Width, MatType.CV_8UC1, pixels.Gray);
        using var mask = Mat.FromPixelData(pixels.Height, pixels.Width, MatType.CV_8UC1, pixels.Mask);
        using var result = new Mat();
        Cv2.MatchTemplate(screen, template, result, TemplateMatchModes.CCoeffNormed, mask);
        var rows = result.Rows; var cols = result.Cols;
        var scores = new float[rows * cols];
        Marshal.Copy(result.Data, scores, 0, scores.Length);
        var peaks = new List<LogoPeak>();
        for (var i = 0; i < count; i++)
        {
            var best = -1f; var at = -1;
            for (var j = 0; j < scores.Length; j++)
                if (float.IsFinite(scores[j]) && scores[j] <= 1.001f && scores[j] > best)
                { best = scores[j]; at = j; }
            if (at < 0) break;
            var x = at % cols; var y = at / cols;
            peaks.Add(new(Math.Min(1, best), x, y));
            for (var yy = Math.Max(0, y - pixels.Height + 1); yy < Math.Min(rows, y + pixels.Height); yy++)
                Array.Fill(scores, -1f, yy * cols + Math.Max(0, x - pixels.Width + 1),
                    Math.Min(cols, x + pixels.Width) - Math.Max(0, x - pixels.Width + 1));
        }
        return peaks;
    }

    /// <summary>缓存只持有托管像素，避免原生资源生命周期跨越任务。</summary>
    private sealed record LogoPixels(int Width, int Height, byte[] Gray, byte[] Mask);
    /// <summary>单次模板搜索的有限数值候选。</summary>
    private sealed record LogoPeak(double Score, int X, int Y);
}

using System.IO;

namespace GGEA.App.Vision;

/// <summary>未识别不能等价于未完成；锁定是独立的可观察状态。</summary>
public enum HistoryCompletion { Unknown, Incomplete, Complete, Locked }

/// <summary>本帧攻略度只区分是否 100%，不伪造任意百分比 OCR 数值。</summary>
public sealed record HistoryProgressObservation(HistoryCompletion Stage, HistoryCompletion Boss);

/// <summary>固定区域内用模板确认 100%，其余攻略度用 OCR 读取白色数字，排除背景机体及暗态文字；不负责点击或决定推图顺序。</summary>
public sealed class HistoryProgressVision : IDisposable
{
    // 日服的 100% 比简中更宽，向左扩展十像素以保留首位 1；右边界及标签区域保持不变。
    public static readonly Roi StageRegion = new(1305, 650, 110, 36);
    public static readonly Roi BossRegion = new(1715, 550, 110, 36);
    private readonly TemplateImages _stage, _boss, _label, _locked;
    private readonly TemplateMatcher _matcher = new();
    private readonly OnnxTextRecognizer _ocr;
    private readonly bool _ownsOcr;

    /// <summary>从实机截图裁剪的素材随发布提供；缺失时由调用方停止流程。</summary>
    public HistoryProgressVision(string directory, OnnxTextRecognizer? ocr = null)
    {
        _stage = Load("history_stage_100.png"); _boss = Load("history_boss_100.png");
        _label = Load("history_progress_label.png"); _locked = Load("history_boss_lock.png");
        _ocr = ocr ?? new OnnxTextRecognizer();
        _ownsOcr = ocr is null;
        TemplateImages Load(string name) => new(Path.Combine(directory, name));
    }

    /// <summary>作品总览的固定攻略度标签区别于节点页；数字暂时遮挡不能把总览误判成关卡轨道。</summary>
    public bool IsOverview(ScreenImage screen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return screen.Width == 1920 && screen.Height == 1080 && HasLabel(screen, new(1080, 652, 154, 30), ct);
    }

    /// <summary>调用方必须先确认历史作品身份；缺标签、缺数字、动画或非标准分辨率均不授权推图。</summary>
    public HistoryProgressObservation Inspect(ScreenImage screen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (screen.Width != 1920 || screen.Height != 1080)
            return new(HistoryCompletion.Unknown, HistoryCompletion.Unknown);
        var stage = HasLabel(screen, new(1080, 652, 154, 30), ct) ? Read(StageRegion, _stage) : HistoryCompletion.Unknown;
        var boss = HasBossLock(screen, ct)
            ? HistoryCompletion.Locked
            : HasLabel(screen, new(1490, 552, 154, 30), ct) ? Read(BossRegion, _boss) : HistoryCompletion.Unknown;
        return new(stage, boss);

        HistoryCompletion Read(Roi roi, TemplateImages templates)
        {
            if (MatchesGlyphRegion(screen, roi, templates, allowNarrow: true, ct))
                return HistoryCompletion.Complete;
            var binary = OnnxTextRecognizer.CreateWhiteMask(screen, roi, ct);
            if (!binary.Contains((byte)255)) return HistoryCompletion.Unknown;
            return ClassifyPercent(_ocr.Recognize(binary, roi.Width, new(0, 0, roi.Width, roi.Height), ct));
        }
    }

    /// <summary>在同一小范围内核对完整锁图标，统一采样后的候选也必须达到原 97% 门槛。</summary>
    private bool HasBossLock(ScreenImage screen, CancellationToken ct)
    {
        var region = new Roi(1638, 400, 40, 42);
        if (_locked.Match(_matcher, screen, .97, region, ct) is not null) return true;
        var observed = TemplateMatcher.PrepareFixedRegion(screen, region, reference: false);
        foreach (var template in _locked.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            // 此处使用不透明锁图标，不能把自定义素材的透明像素卷入背景。
            if (!TemplateMatcher.IsOpaque(template)) continue;
            var expected = TemplateMatcher.PrepareFixedRegion(template, new(0, 0, template.Width, template.Height), reference: true);
            if (_matcher.Match(observed.Bgra, observed.Width, observed.Height, expected.Bgra,
                expected.Width, expected.Height, .97, ct: ct) is not null) return true;
        }
        return false;
    }

    /// <summary>保留原始字形优先；重采样画面仍须通过完整区域交并比，空白处多余数字也计入。</summary>
    private static bool MatchesGlyphRegion(ScreenImage screen, Roi roi, TemplateImages templates,
        bool allowNarrow, CancellationToken ct)
    {
        bool Matches(bool prepare)
        {
            var observed = prepare ? TemplateMatcher.PrepareFixedRegion(screen, roi, reference: false) : screen;
            var actual = Mask(observed, prepare ? new(0, 0, roi.Width, roi.Height) : roi);
            foreach (var template in templates.Candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (template.Height != roi.Height || template.Width > roi.Width ||
                    (!allowNarrow && template.Width != roi.Width)) continue;
                var bounds = new Roi(0, 0, template.Width, template.Height);
                var expected = Mask(prepare ? TemplateMatcher.PrepareFixedRegion(template, bounds, reference: true) : template, bounds);
                if (MatchesComplete(actual, roi, expected, template.Width)) return true;
            }
            return false;
        }
        return Matches(false) || Matches(true);
    }

    private static bool MatchesComplete(bool[] actual, Roi roi, bool[] expected, int width)
    {
        // 旧素材可以在扩展区域内平移；区域之外的多余白字仍计入交并比的惩罚。
        for (var left = 0; left <= roi.Width - width; left++)
        {
            var padded = new bool[roi.Width * roi.Height];
            for (var y = 0; y < roi.Height; y++)
                Array.Copy(expected, y * width, padded, y * roi.Width + left, width);
            if (Similarity(actual, padded, 0, roi.Width, roi.Width, roi.Height) >= .90) return true;
        }
        return false;
    }

    /// <summary>仅接纳高置信度的完整整数百分比 0..99；OCR 的 100% 不能替代模板完成证据。</summary>
    internal static HistoryCompletion ClassifyPercent(OcrTextRecognition result)
    {
        var text = result.Text.Trim();
        if (result.Confidence < .90 || (result.MinimumContentConfidence ?? result.MinimumConfidence) < .85 ||
            text.Length is < 2 or > 3 || text[^1] != '%' ||
            !text[..^1].All(c => c is >= '0' and <= '9') ||
            !int.TryParse(text[..^1], out var value) || value is < 0 or > 99)
            return HistoryCompletion.Unknown;
        return HistoryCompletion.Incomplete;
    }

    /// <summary>窗口传入的 OCR 由窗口释放；独立识别器只释放自己创建的延迟加载会话。</summary>
    public void Dispose() { if (_ownsOcr) _ocr.Dispose(); }
    /// <summary>标签后方有半透明机体图，不能用背景亮度判断文字是否存在；同时比较白色笔画与负空间。</summary>
    private bool HasLabel(ScreenImage screen, Roi roi, CancellationToken ct)
        => MatchesGlyphRegion(screen, roi, _label, allowNarrow: false, ct);

    /// <summary>只保留亮且接近白色的文本核心，锁定区的灰字不会当成正常攻略度。</summary>
    private static bool[] Mask(ScreenImage image, Roi roi)
    {
        var mask = new bool[roi.Width * roi.Height];
        for (var y = 0; y < roi.Height; y++)
        for (var x = 0; x < roi.Width; x++)
        {
            var i = ((roi.Y + y) * image.Width + roi.X + x) * 4;
            var min = Math.Min(image.Bgra[i], Math.Min(image.Bgra[i + 1], image.Bgra[i + 2]));
            var max = Math.Max(image.Bgra[i], Math.Max(image.Bgra[i + 1], image.Bgra[i + 2]));
            mask[y * roi.Width + x] = min >= 180 && max - min <= 55;
        }
        return mask;
    }

    /// <summary>交并比同时惩罚缺笔和多余数字，不能仅因百分号相同就认定 100%。</summary>
    private static double Similarity(bool[] actual, bool[] expected, int left, int right, int width = 100, int height = 36)
    {
        double best = 0;
        for (var dy = -2; dy <= 2; dy++)
        for (var dx = -2; dx <= 2; dx++)
        {
            var intersection = 0; var union = 0;
            for (var y = 0; y < height; y++)
            for (var x = left; x < right; x++)
            {
                var a = x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height && actual[(y + dy) * width + x + dx];
                var b = expected[y * width + x];
                if (a && b) intersection++;
                if (a || b) union++;
            }
            if (union > 0) best = Math.Max(best, (double)intersection / union);
        }
        return best;
    }
}

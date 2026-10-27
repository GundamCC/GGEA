using System.Text.RegularExpressions;
using GGEA.App.Device;
using GGEA.App.Logging;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>在标题栏定位完整 HARD STAGE 行，借用与数字识别共用的 CPU OCR 会话。</summary>
public sealed class OnnxStageTitleRecognizer : IStageTitleRecognizer
{
    private readonly IAdbService _adb;
    private readonly IGgeaLog _log;
    private readonly OnnxTextRecognizer _ocr;

    /// <summary>会话所有者负责释放；标题识别不加载或匹配旧 PNG 模板。</summary>
    public OnnxStageTitleRecognizer(IAdbService adb, IGgeaLog log, OnnxTextRecognizer ocr)
    { _adb = adb; _log = log; _ocr = ocr; }

    /// <summary>仅接受标题 ROI 内的唯一完整高分标题；未命中属于等待状态。</summary>
    public async Task<StageTitleMatch?> RecognizeAsync(string serial, Roi titleRoi,
        double threshold = .90, CancellationToken ct = default)
    {
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        var png = await _adb.ScreenshotAsync(serial, ct);
        return await Task.Run(() =>
        {
            var screen = ImageCodec.DecodePng(png);
            var binary = OnnxTextRecognizer.CreateWhiteMask(screen, titleRoi, ct);
            var matches = new List<StageTitleMatch>();
            foreach (var bounds in LocateTitleLines(binary, titleRoi))
            {
                var text = _ocr.Recognize(binary, titleRoi.Width, bounds, ct);
                var index = OcrStageTitleDecoder.Decode(text, threshold);
                _log.Info($"HARD 标题 {text.Diagnostic}，最低字母/数字 {text.MinimumContentConfidence ?? text.MinimumConfidence:P1}；{(index is null ? "格式或置信度未达标，等待重新识别" : $"确认 HARD STAGE {index}")}。");
                if (index is null) continue;
                var region = new Roi(titleRoi.X + bounds.X, titleRoi.Y + bounds.Y, bounds.Width, bounds.Height);
                matches.Add(new(index.Value, text.Confidence, region.X + region.Width / 2,
                    region.Y + region.Height / 2, region));
            }
            if (matches.Count == 1) return matches[0];
            _log.Info(matches.Count == 0 ? "标题栏 OCR 未识别到完整 HARD STAGE，等待重新识别。"
                : "标题栏 OCR 存在多个 HARD STAGE 结果，等待重新识别。");
            return null;
        }, ct);
    }

    /// <summary>按白色字形基线找行；作品名长度可变，截取行尾完整的十个字符 HARD STAGE n。</summary>
    private static IEnumerable<Roi> LocateTitleLines(byte[] binary, Roi roi)
    {
        using var source = Mat.FromPixelData(roi.Height, roi.Width, MatType.CV_8UC1, binary);
        using var labels = new Mat(); using var stats = new Mat(); using var centers = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(source, labels, stats, centers);
        var lines = new List<List<Roi>>();
        for (var i = 1; i < count; i++)
        {
            var b = new Roi(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
            var area = stats.At<int>(i, 4);
            var density = (double)area / (b.Width * b.Height);
            if (b.Height is < 12 or > 60 || b.Width < 2 || b.Width > b.Height * 1.3 ||
                area < 15 || density is < .08 or > .95) continue;
            var line = lines.FirstOrDefault(items =>
                Math.Abs(items[0].Y + items[0].Height / 2 - b.Y - b.Height / 2) <= 4 &&
                Math.Abs(items[0].Height - b.Height) <= 6);
            if (line is null) lines.Add([b]); else line.Add(b);
        }
        foreach (var line in lines)
        {
            if (line.Count < 10) continue;
            var letters = line.OrderBy(b => b.X).TakeLast(10).ToArray();
            var x = letters.Min(b => b.X); var y = letters.Min(b => b.Y);
            var right = letters.Max(b => b.X + b.Width); var bottom = letters.Max(b => b.Y + b.Height);
            // ROI 不能裁断标题；字符间隔过大会把分离的控件拼成文字行。
            if (x == 0 || y == 0 || right == roi.Width || bottom == roi.Height ||
                letters.Zip(letters.Skip(1)).Any(pair => pair.Second.X - pair.First.X - pair.First.Width > pair.First.Height))
                continue;
            yield return new(x, y, right - x, bottom - y);
        }
    }
}

/// <summary>HARD 标题格式与可信度约束；不通过关键词模糊匹配或字母替换猜编号。</summary>
public static class OcrStageTitleDecoder
{
    /// <summary>允许模型省略词间空格，但必须读出完整 HARD STAGE 与 1～5 编号。</summary>
    public static int? Decode(OcrTextRecognition result, double threshold = .90)
    {
        // 两个词间空格不参与最低有效字符分，但平均分仍含空格；关键词与编号一字也不能缺失。
        if (result.Confidence < Math.Max(.90, threshold) ||
            (result.MinimumContentConfidence ?? result.MinimumConfidence) < .85) return null;
        var match = Regex.Match(result.Text.Trim(), @"\AHARD\s*STAGE\s*([1-5])\z", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value[0] - '0' : null;
    }
}

using System.IO;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>推图专用的灰度 SSD 加速匹配：复用每帧灰度和固定模板，原阈值及最终分数公式保持不变。</summary>
public sealed class ProgressTemplateMatcher : IDisposable
{
    /// <summary>固定模板的灰度、有效像素与原生视图由本关缓存独占，任务结束统一释放。</summary>
    private sealed record Template(Mat Gray, Mat? Mask, byte[] Pixels, byte[]? Valid,
        int Width, int Height, int Count) : IDisposable
    {
        /// <summary>释放本关拥有的原生模板，不影响共享 OCR 或其他任务。</summary>
        public void Dispose() { Gray.Dispose(); Mask?.Dispose(); }
    }

    private readonly Dictionary<string, Template> _templates = new();
    private byte[]? _lastFrame;
    private byte[] _grayPixels = [];
    private Mat? _gray;
    private bool _disposed;

    /// <summary>原生搜索定位最小平方差；只复算一个候选的精确整数 SSD，避免浮点误差改变置信度授权。</summary>
    public TemplateMatchResult? Match(ScreenImage screen, string path, Roi roi, double threshold, CancellationToken ct)
        => TemplateFallback.Match(path, candidate => MatchSingle(screen, candidate, roi, threshold, ct), ct);

    /// <summary>完整验证当前物理素材，复合条件不得跨候选拼接。</summary>
    private TemplateMatchResult? MatchSingle(ScreenImage screen, string path, Roi roi, double threshold, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(threshold) || threshold < 0 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        var template = Load(path);
        var left = Math.Max(0, roi.X);
        var top = Math.Max(0, roi.Y);
        var right = Math.Min(screen.Width, roi.X + roi.Width);
        var bottom = Math.Min(screen.Height, roi.Y + roi.Height);
        if (right - left < template.Width || bottom - top < template.Height || template.Count == 0) return null;
        if (!ReferenceEquals(screen.Bgra, _lastFrame))
        {
            _gray?.Dispose();
            _grayPixels = ToGray(screen.Bgra, ct);
            _gray = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC1, _grayPixels);
            _lastFrame = screen.Bgra;
        }
        using var region = new Mat(_gray!, new Rect(left, top, right - left, bottom - top));
        using var scores = new Mat();
        if (template.Mask is { } mask)
            Cv2.MatchTemplate(region, template.Gray, scores, TemplateMatchModes.SqDiff, mask);
        else Cv2.MatchTemplate(region, template.Gray, scores, TemplateMatchModes.SqDiff);
        ct.ThrowIfCancellationRequested();
        Cv2.MinMaxLoc(scores, out var minimum, out _, out var location, out _);
        if (!double.IsFinite(minimum)) return null;
        var x = left + location.X;
        var y = top + location.Y;
        double ssd = 0;
        for (var row = 0; row < template.Height; row++)
        {
            ct.ThrowIfCancellationRequested();
            for (var column = 0; column < template.Width; column++)
            {
                var index = row * template.Width + column;
                if (template.Valid is not null && template.Valid[index] == 0) continue;
                var delta = _grayPixels[(y + row) * screen.Width + x + column] - template.Pixels[index];
                ssd += delta * delta;
            }
        }
        var confidence = Math.Clamp(1 - Math.Sqrt(ssd / (template.Count * 255.0 * 255.0)), 0, 1);
        return confidence < threshold ? null : new(new Roi(x, y, template.Width, template.Height), confidence);
    }

    /// <summary>每关只转换一次固定模板；Alpha 小于 128 的像素继续排除，不改变透明素材的授权范围。</summary>
    private Template Load(string path)
    {
        if (_templates.TryGetValue(path, out var template)) return template;
        var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
        var pixels = ToGray(image.Bgra, default);
        var valid = new byte[pixels.Length];
        var count = 0;
        for (var i = 0; i < valid.Length; i++)
            if (image.Bgra[i * 4 + 3] >= 128) { valid[i] = 1; count++; }
        var gray = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC1, pixels);
        var mask = count == pixels.Length ? null : Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC1, valid);
        return _templates[path] = new(gray, mask, pixels, mask is null ? null : valid, image.Width, image.Height, count);
    }

    /// <summary>沿用原匹配器的整数灰度权重与舍入，不能用另一种颜色转换悄悄改变分数。</summary>
    private static byte[] ToGray(byte[] bgra, CancellationToken ct)
    {
        var gray = new byte[bgra.Length / 4];
        for (var i = 0; i < gray.Length; i++)
        {
            if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
            var offset = i * 4;
            gray[i] = (byte)((bgra[offset + 2] * 299 + bgra[offset + 1] * 587 + bgra[offset] * 114) / 1000);
        }
        return gray;
    }

    /// <summary>任务成功、失败和取消均由 using 收尾，及时释放灰度帧与模板的原生内存。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gray?.Dispose();
        foreach (var template in _templates.Values) template.Dispose();
        _templates.Clear();
        _grayPixels = [];
        _lastFrame = null;
    }
}

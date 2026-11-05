using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>复用透明文字/图标的 Alpha 遮罩匹配；透明位置不计分，有效像素保留灰度纹理与亮度约束。</summary>
public sealed class AlphaMaskedTemplateMatcher
{
    private readonly int _width, _height, _validCount;
    private readonly byte[] _mask, _gray;

    /// <summary>快照模板；Alpha 小于 128 的像素完全排除。纯色有效区域缺少辨别纹理，拒绝用于此匹配器。</summary>
    public AlphaMaskedTemplateMatcher(ScreenImage template)
    {
        _width = template.Width; _height = template.Height;
        _mask = Enumerable.Range(0, _width * _height)
            .Select(i => template.Bgra[i * 4 + 3] >= 128 ? (byte)255 : (byte)0).ToArray();
        _gray = Gray(template, new Roi(0, 0, _width, _height));
        var values = _gray.Where((_, i) => _mask[i] != 0).ToArray();
        _validCount = values.Length;
        if (_validCount == 0 || values.Distinct().Count() < 2)
            throw new ArgumentException("模板有效区域缺少可复核的灰度纹理。", nameof(template));
    }

    /// <summary>仅搜索指定区域，返回首个同时达到纹理与亮度阈值的候选；不能把返回值解释为唯一或最高分。</summary>
    public TemplateMatchResult? Match(ScreenImage screen, Roi region, double threshold, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(threshold) || threshold <= 0 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        var left = Math.Clamp(region.X, 0, screen.Width);
        var top = Math.Clamp(region.Y, 0, screen.Height);
        var right = (int)Math.Clamp((long)region.X + region.Width, left, screen.Width);
        var bottom = (int)Math.Clamp((long)region.Y + region.Height, top, screen.Height);
        var roi = new Roi(left, top, right - left, bottom - top);
        if (roi.Width < _width || roi.Height < _height) return null;
        var pixels = Gray(screen, roi);
        using var frame = Mat.FromPixelData(roi.Height, roi.Width, MatType.CV_8UC1, pixels);
        using var template = Mat.FromPixelData(_height, _width, MatType.CV_8UC1, _gray);
        using var mask = Mat.FromPixelData(_height, _width, MatType.CV_8UC1, _mask);
        using var scores = new Mat();
        Cv2.MatchTemplate(frame, template, scores, TemplateMatchModes.CCoeffNormed, mask);
        // 纯色窗口的归一化结果可能是 NaN/Infinity；逐候选拒绝，避免覆盖其他有效位置。
        var rows = scores.Rows; var columns = scores.Cols;
        for (var y = 0; y < rows; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < columns; x++)
            {
                var correlation = scores.At<float>(y, x);
                if (!float.IsFinite(correlation) || correlation < threshold) continue;
                double error = 0;
                for (var ty = 0; ty < _height; ty++)
                for (var tx = 0; tx < _width; tx++)
                {
                    var i = ty * _width + tx;
                    if (_mask[i] == 0) continue;
                    var delta = pixels[(y + ty) * roi.Width + x + tx] - _gray[i];
                    error += delta * delta;
                }
                var brightness = 1 - Math.Sqrt(error / _validCount) / 255;
                if (brightness >= threshold)
                    return new(new Roi(left + x, top + y, _width, _height), Math.Min(correlation, brightness));
            }
        }
        return null;
    }

    /// <summary>转换指定区域并拷贝为托管快照；原生 Mat 全部限于本次调用，不持有跨帧资源。</summary>
    private static byte[] Gray(ScreenImage image, Roi roi)
    {
        using var full = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC4, image.Bgra);
        using var crop = new Mat(full, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGRA2GRAY);
        var result = new byte[roi.Width * roi.Height];
        System.Runtime.InteropServices.Marshal.Copy(gray.Data, result, 0, result.Length);
        return result;
    }
}

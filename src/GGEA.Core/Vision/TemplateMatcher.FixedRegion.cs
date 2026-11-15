using OpenCvSharp;

namespace GGEA.App.Vision;

public sealed partial class TemplateMatcher
{
    /// <summary>比较标准坐标中的完整不透明区域。固定位置证据不可裁切、遮罩或按分辨率选择素材。</summary>
    internal static bool MatchesFixedRegion(ScreenImage screen, ScreenImage template, Roi region)
    {
        if (region.Width != template.Width || region.Height != template.Height ||
            region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            (long)region.X + region.Width > screen.Width || (long)region.Y + region.Height > screen.Height)
            return false;
        for (var i = 3; i < template.Bgra.Length; i += 4)
            if (template.Bgra[i] != 255) return false;

        using var full = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
        using var crop = full[new Rect(region.X, region.Y, region.Width, region.Height)];
        using var reference = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC4, template.Bgra);
        using var observed = new Mat();
        using var expected = new Mat();
        // 截图经过采样和插值，标准模板更锐利。在相近空间尺度上比较颜色，保留局部字形与空白。
        // Isolated 保证截图边界与模板使用同样的边缘延拓，不引入区域外颜色。
        var border = BorderTypes.Reflect101 | BorderTypes.Isolated;
        Cv2.GaussianBlur(crop, observed, new Size(7, 7), 1, borderType: border);
        Cv2.GaussianBlur(reference, expected, new Size(7, 7), 1.25, borderType: border);
        using var delta = new Mat();
        Cv2.Absdiff(observed, expected, delta);
        delta.GetArray(out Vec4b[] pixels);

        // 每块单独否决，包括边缘残块，避免新增数字被大面积相同背景稀释。
        for (var top = 0; top < region.Height; top += 8)
        for (var left = 0; left < region.Width; left += 8)
        {
            int bottom = Math.Min(top + 8, region.Height), right = Math.Min(left + 8, region.Width);
            var difference = 0;
            for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var pixel = pixels[y * region.Width + x];
                difference += pixel.Item0 + pixel.Item1 + pixel.Item2;
            }
            if (difference > (bottom - top) * (right - left) * 3 * 8) return false;
        }
        return true;
    }
}

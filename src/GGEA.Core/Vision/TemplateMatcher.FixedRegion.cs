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
        if (!IsOpaque(template)) return false;

        using var full = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
        using var crop = full[new Rect(region.X, region.Y, region.Width, region.Height)];
        using var reference = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC4, template.Bgra);
        using var observed = new Mat();
        using var expected = new Mat();
        // 截图经过采样和插值，标准模板更锐利。在相近空间尺度上比较颜色，保留局部字形与空白。
        // Isolated 保证截图边界与模板使用同样的边缘延拓，不引入区域外颜色。
        PrepareFixedRegion(crop, observed, reference: false);
        PrepareFixedRegion(reference, expected, reference: true);
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

    /// <summary>对已定位的同一候选复核纹理；保留任务自己的 SSD、颜色及相关性门槛。</summary>
    internal static bool MatchesFixedTexture(ScreenImage screen, ScreenImage template, Roi region, double threshold)
    {
        if (region.Width != template.Width || region.Height != template.Height ||
            region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            (long)region.X + region.Width > screen.Width || (long)region.Y + region.Height > screen.Height)
            return false;
        using var full = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
        using var crop = full[new Rect(region.X, region.Y, region.Width, region.Height)];
        using var reference = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC4, template.Bgra);
        using var observed = new Mat();
        using var expected = new Mat();
        Cv2.CvtColor(crop, observed, ColorConversionCodes.BGRA2GRAY);
        Cv2.CvtColor(reference, expected, ColorConversionCodes.BGRA2GRAY);
        return MatchesFixedTexture(observed, expected, threshold);
    }

    /// <summary>已准备好的灰度区域可携带原始字形遮罩；不扩大搜索位置或把无效分数视为命中。</summary>
    internal static bool MatchesFixedTexture(Mat observed, Mat expected, double threshold, Mat? mask = null)
    {
        if (observed.Empty() || observed.Size() != expected.Size()) return false;
        bool Passes(Mat frame, Mat reference)
        {
            using var score = new Mat();
            if (mask is null) Cv2.MatchTemplate(frame, reference, score, TemplateMatchModes.CCoeffNormed);
            else Cv2.MatchTemplate(frame, reference, score, TemplateMatchModes.CCoeffNormed, mask);
            var value = score.At<float>(0, 0);
            return float.IsFinite(value) && value >= threshold && value <= 1.001;
        }

        if (Passes(observed, expected)) return true;
        // 仅在当前候选的同一位置复核重采样纹理，仍用原遮罩和阈值；常量画面不能提供纹理证据。
        using var frame = new Mat();
        using var reference = new Mat();
        if (mask is null)
        {
            PrepareFixedRegion(observed, frame, reference: false);
            PrepareFixedRegion(expected, reference, reference: true);
        }
        else
        {
            // 对有效字形做归一化卷积；模板透明边缘、画布背景及遮罩外像素不能成为授权证据。
            void PrepareMasked(Mat input, Mat output, bool isReference)
            {
                using var values = new Mat();
                using var weights = new Mat();
                input.ConvertTo(values, MatType.CV_32F);
                mask.ConvertTo(weights, MatType.CV_32F, 1.0 / 255);
                Cv2.Multiply(values, weights, values);
                PrepareFixedRegion(values, values, isReference);
                PrepareFixedRegion(weights, weights, isReference);
                Cv2.Max(weights, 1e-6, weights);
                Cv2.Divide(values, weights, values);
                values.ConvertTo(output, input.Type());
            }
            PrepareMasked(observed, frame, false);
            PrepareMasked(expected, reference, true);
        }
        return Passes(frame, reference);
    }

    /// <summary>复核同一设备两帧的文字身份，保留原始通道差、噪点数量和最低对比度保护。</summary>
    internal static bool MatchesUnchangedText(ScreenImage original, ScreenImage current, Roi region, CancellationToken ct)
    {
        if (original.Width != current.Width || original.Height != current.Height ||
            region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            (long)region.X + region.Width > current.Width || (long)region.Y + region.Height > current.Height)
            return false;
        // 无字形的空白区域不能建立资源未变的证据。
        var minimum = 255;
        var maximum = 0;
        var changedPixels = 0;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var i = (y * original.Width + x) * 4;
                minimum = Math.Min(minimum, original.Bgra[i]);
                maximum = Math.Max(maximum, original.Bgra[i]);
                // 原生 PNG 的静止内容稳定；只容忍轻微色差和少量孤立噪点。
                // 不用整块相关系数，否则浅色 AP 数字变化会被大面积相同背景淹没。
                if ((Math.Abs(original.Bgra[i] - current.Bgra[i]) > 8 ||
                     Math.Abs(original.Bgra[i + 1] - current.Bgra[i + 1]) > 8 ||
                     Math.Abs(original.Bgra[i + 2] - current.Bgra[i + 2]) > 8) &&
                    ++changedPixels > 8) return false;
            }
        }
        return maximum - minimum >= 25;
    }

    internal static bool IsOpaque(ScreenImage image)
    {
        for (var i = 3; i < image.Bgra.Length; i += 4)
            if (image.Bgra[i] != 255) return false;
        return true;
    }

    private static void PrepareFixedRegion(Mat source, Mat destination, bool reference) =>
        Cv2.GaussianBlur(source, destination, new Size(7, 7), reference ? 1.25 : 1,
            borderType: BorderTypes.Reflect101 | BorderTypes.Isolated);

    /// <summary>需要专用字形遮罩的任务先按统一尺度准备完整区域，再执行自身的颜色和相似度门槛。</summary>
    internal static ScreenImage PrepareFixedRegion(ScreenImage image, Roi region, bool reference)
    {
        using var source = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC4, image.Bgra);
        using var patch = source[new Rect(region.X, region.Y, region.Width, region.Height)];
        using var prepared = new Mat();
        PrepareFixedRegion(patch, prepared, reference);
        var pixels = new byte[region.Width * region.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(prepared.Data, pixels, 0, pixels.Length);
        return new ScreenImage { Width = region.Width, Height = region.Height, Bgra = pixels };
    }
}

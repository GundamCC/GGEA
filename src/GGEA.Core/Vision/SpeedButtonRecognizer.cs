using System.IO;
using GGEA.App.Config;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>二倍速状态；证据不足与关闭必须分开，防止误切换已开启的按钮。</summary>
public enum SpeedButtonState { Unknown, On, Off }

/// <summary>字形位置与内部颜色共同确认的二倍速按钮。</summary>
public sealed record SpeedButtonMatch(SpeedButtonState State, TemplateMatchResult Match);

/// <summary>只比较中央双箭头与空隙，再以原始颜色判断状态；外框和地图不参与状态判断。</summary>
public sealed class SpeedButtonRecognizer
{
    private readonly Lazy<(byte[] Glyphs, int Width, int Height)>[][] _templates;

    /// <summary>缓存两种真实按钮的中央字形，不以灰度相似度决定开关状态。</summary>
    public SpeedButtonRecognizer(string onPath, string offPath)
        {
        _templates = new[] { onPath, offPath }.Select(path => ConfigService.Resources.TemplateCandidates(path)
            .Select(candidate => new Lazy<(byte[] Glyphs, int Width, int Height)>(() => Load(candidate))).ToArray()).ToArray();
        foreach (var variants in _templates) _ = variants[0].Value;
    }

    /// <summary>在速度区域确认完整双箭头，MENU、纯色块及颜色不明确的画面均不授权点击。</summary>
    public SpeedButtonMatch? Recognize(ScreenImage screen, Roi roi, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var left = Math.Max(0, roi.X);
        var top = Math.Max(0, roi.Y);
        var width = Math.Min(screen.Width, roi.X + roi.Width) - left;
        var height = Math.Min(screen.Height, roi.Y + roi.Height) - top;
        if (width <= 0 || height <= 0) return null;
        var pixels = Glyphs(screen, left, top, width, height);
        using var frame = Mat.FromPixelData(height, width, MatType.CV_8UC1, pixels);
        TemplateMatchResult? best = null;
        foreach (var variants in _templates)
        foreach (var lazy in variants)
        {
            ct.ThrowIfCancellationRequested();
            var template = lazy.Value;
            if (width < template.Width || height < template.Height) continue;
            using var reference = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC1, template.Glyphs);
            using var scores = new Mat();
            Cv2.MatchTemplate(frame, reference, scores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scores, out _, out var score, out _, out var location);
            if (double.IsFinite(score) && score >= .90)
            {
                if (best is null || score > best.Confidence)
                    best = new(new Roi(left + location.X, top + location.Y, template.Width, template.Height), score);
                break; // 当前字形已经命中，保留语言优先顺序。
            }
        }
        ct.ThrowIfCancellationRequested();
        if (best is null) return null;
        var cyan = 0;
        var white = 0;
        var dark = 0;
        for (var y = best.Y; y < best.Y + best.Height; y++)
        for (var x = best.X; x < best.X + best.Width; x++)
        {
            var i = (y * screen.Width + x) * 4;
            var b = screen.Bgra[i]; var g = screen.Bgra[i + 1]; var r = screen.Bgra[i + 2];
            var high = Math.Max(r, Math.Max(g, b));
            var low = Math.Min(r, Math.Min(g, b));
            // 呼吸动画改变绝对亮度，但青色仍同时具有蓝、绿通道优势；固定亮度门槛会漏掉暗相位。
            // 仍须先通过 90% 双箭头校验，再由至少 45% 同位置青色像素确认，暗灰与单纯蓝色不能授权开启。
            if (b > r + 25 && g > r + 15 && g >= b * .65) cyan++;
            if (low >= 190 && high - low <= 30) white++;
            if (high <= 140) dark++;
        }
        var area = best.Width * best.Height;
        var state = cyan >= area * .45 ? SpeedButtonState.On
            : cyan <= area * .05 && white >= area * .15 && dark >= area * .30 ? SpeedButtonState.Off
            : SpeedButtonState.Unknown;
        return new(state, best);
    }

    /// <summary>中央裁剪排除按钮边缘；常量素材不能产生有效归一化字形。</summary>
    private static (byte[] Glyphs, int Width, int Height) Load(string path)
    {
        var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
        var width = image.Width / 2;
        var height = image.Height * 3 / 5;
        var glyphs = Glyphs(image, image.Width / 4, image.Height / 5, width, height);
        if (!glyphs.Contains((byte)255) || !glyphs.Contains((byte)0))
            throw new InvalidOperationException("二倍速素材缺少有效双箭头字形。");
        return (glyphs, width, height);
    }

    /// <summary>白色与亮青色箭头归一到同一字形，保留箭头间空隙，状态仍读取原始 RGB。</summary>
    private static byte[] Glyphs(ScreenImage image, int left, int top, int width, int height)
    {
        var result = new byte[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = ((top + y) * image.Width + left + x) * 4;
            if (image.Bgra[i + 3] >= 128 && image.Bgra[i] >= 200 && image.Bgra[i + 1] >= 200 && image.Bgra[i + 2] >= 90)
                result[y * width + x] = 255;
        }
        return result;
    }
}

using System.IO;
using System.Text.Json;
using GGEA.App.Config;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.App.Tasks;

/// <summary>作品编号来自游戏主数据，页签从该作品实际分图读取，含普通和终极分图。</summary>
internal sealed record DevelopmentDiagram(string Id, int Tab, bool Ultimate, string Banner);
internal sealed record DevelopmentMap(string Logo, DevelopmentDiagram[] Diagrams);
internal static class DevelopmentMaps
{
    private static readonly Lazy<Dictionary<string, DevelopmentMap>> Maps = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, DevelopmentMap>>(File.ReadAllBytes(
            ConfigService.ResolvePath("resources/data/series_development_bases.json"))) ?? throw new InvalidDataException());
    internal static DevelopmentMap Get(string series) => Maps.Value[series];
}

/// <summary>每个开发卡片使用原生小 Logo 和固定画布缩放，限定五列并保留同族副标题判别。</summary>
internal sealed class DevelopmentSeriesLogo(string series)
{
    private sealed record Shape(byte[] Gray, byte[] Mask, int Width, int Height);
    private sealed record Hit(double Score, Roi Bounds);
    private readonly Shape[] _target = Prepare(series);
    private readonly Shape[]? _sibling = series switch { "320" => Prepare("330"), "330" => Prepare("320"), _ => null };

    private static Shape[] Prepare(string seriesId)
    {
        using var sprite = Cv2.ImDecode(File.ReadAllBytes(ConfigService.ResolvePath(DevelopmentMaps.Get(seriesId).Logo)), ImreadModes.Unchanged);
        if (sprite.Empty() || sprite.Channels() != 4) throw new InvalidDataException(seriesId);
        using var alpha = new Mat();
        using var original = new Mat();
        Cv2.ExtractChannel(sprite, alpha, 3);
        Cv2.CvtColor(sprite, original, ColorConversionCodes.BGRA2GRAY);
        original.GetArray(out byte[] pixels);
        alpha.GetArray(out byte[] opacityValues);
        for (var i = 0; i < pixels.Length; i++)
        {
            int a = opacityValues[i];
            pixels[i] = (byte)((pixels[i] * a + 42 * (255 - a) + 127) / 255);
        }
        using var src = Mat.FromPixelData(sprite.Height, sprite.Width, MatType.CV_8UC1, pixels);
        return new[] { .640, .643, .646 }.Select(scale =>
        {
            using var gray = new Mat();
            using var mask = new Mat();
            Cv2.Resize(src, gray, default, scale, scale, InterpolationFlags.Area);
            Cv2.Resize(alpha, mask, gray.Size(), interpolation: InterpolationFlags.Area);
            Cv2.Threshold(mask, mask, 95, 255, ThresholdTypes.Binary);
            gray.GetArray(out byte[] data);
            mask.GetArray(out byte[] opacity);
            return new Shape(data, opacity, gray.Width, gray.Height);
        }).ToArray();
    }

    internal Roi? Find(ScreenImage frame, CancellationToken ct)
    {
        if (frame.Width != 1920 || frame.Height != 1080) return null;
        using var raw = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var gray = new Mat();
        Cv2.CvtColor(raw, gray, ColorConversionCodes.BGRA2GRAY);
        Hit? best = null;
        foreach (var x in new[] { 234, 567, 900, 1233, 1566 })
        {
            var hit = Search(gray, _target, x, 250, 903, ct);
            if (hit is not { Score: >= .80 } || best is not null && best.Score >= hit.Score) continue;
            if (_sibling is not null)
            {
                var y = hit.Bounds.CenterY;
                var other = Search(gray, _sibling, x, Math.Max(250, y - 53), Math.Min(903, y + 53), ct);
                if (other is not null && other.Score + .010 >= hit.Score) continue;
            }
            best = hit;
        }
        return best?.Bounds;
    }

    private static Hit? Search(Mat gray, Shape[] shapes, int x, int top, int end, CancellationToken ct)
    {
        Hit? best = null;
        foreach (var s in shapes)
        {
            ct.ThrowIfCancellationRequested();
            if (end - top < s.Height) continue;
            var left = x - s.Width / 2 - 9;
            using var region = gray[new Rect(left, top, s.Width + 18, end - top)];
            using var templ = Mat.FromPixelData(s.Height, s.Width, MatType.CV_8UC1, s.Gray);
            using var mask = Mat.FromPixelData(s.Height, s.Width, MatType.CV_8UC1, s.Mask);
            using var scores = new Mat();
            Cv2.MatchTemplate(region, templ, scores, TemplateMatchModes.CCoeffNormed, mask);
            Cv2.PatchNaNs(scores, -1);
            Cv2.Threshold(scores, scores, 1.001, 0, ThresholdTypes.TozeroInv);
            Cv2.MinMaxLoc(scores, out _, out var peak, out _, out var point);
            if (best is null || peak > best.Score) best = new(peak, new(left + point.X, top + point.Y, s.Width, s.Height));
        }
        return best;
    }
}

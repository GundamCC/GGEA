using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.App.Tasks;

/// <summary>按主数据指定的页签纹理和颜色定位，点击前后均以当前截图核对右边框的选中态。</summary>
internal sealed class DevelopmentTabNavigator(AutomationService automation)
{
    internal sealed record TabView(Roi Bounds, bool Selected);
    internal static TabView? Read(ScreenImage frame, DevelopmentDiagram diagram, CancellationToken ct)
    {
        if (frame.Width != 1920 || frame.Height != 1080 || diagram.Tab is < 1 or > 5) return null;
        ct.ThrowIfCancellationRequested();
        using var sprite = Cv2.ImDecode(File.ReadAllBytes(ConfigService.ResolvePath(diagram.Banner)), ImreadModes.Color);
        if (sprite.Empty()) return null;
        using var art = new Mat();
        Cv2.Resize(sprite, art, new Size(281, 107), interpolation: InterpolationFlags.Area);
        // 两侧纹理排除中间的本地化作品名、左上完成标识和顶部边框扫光。
        using var mask = new Mat(art.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new Rect(7, 32, 19, 48), Scalar.White, -1);
        Cv2.Rectangle(mask, new Rect(256, 27, 17, 55), Scalar.White, -1);
        var top = 154 + 155 * (diagram.Tab - 1);
        using var source = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var area = source[new Rect(45, top, 310, 210)];
        using var color = new Mat();
        Cv2.CvtColor(area, color, ColorConversionCodes.BGRA2BGR);
        using var scores = new Mat();
        Cv2.MatchTemplate(color, art, scores, TemplateMatchModes.CCoeffNormed, mask);
        Cv2.PatchNaNs(scores, -1);
        Cv2.MinMaxLoc(scores, out _, out var score, out _, out var at);
        if (!double.IsFinite(score) || score < .60) return null;
        using var actual = color[new Rect(at.X, at.Y, art.Width, art.Height)];
        var expectedColor = Cv2.Mean(art, mask);
        var observed = Cv2.Mean(actual, mask);
        if (Math.Max(Math.Abs(observed.Val0 - expectedColor.Val0), Math.Max(Math.Abs(observed.Val1 - expectedColor.Val1),
            Math.Abs(observed.Val2 - expectedColor.Val2))) > 30) return null;

        var bounds = new Roi(45 + at.X, top + at.Y, art.Width, art.Height);
        for (var x = bounds.X + bounds.Width - 3; x <= bounds.X + bounds.Width + 3; x++)
        {
            var bright = 0;
            for (int y = bounds.CenterY - 27; y < bounds.CenterY + 27; y++)
            {
                int i = (y * frame.Width + x) * 4;
                if (frame.Bgra[i] > 225 && frame.Bgra[i + 1] > 160 && frame.Bgra[i + 2] < 165) bright++;
            }
            if (bright >= 45) return new(bounds, true);
        }
        return new(bounds, false);
    }

    internal async Task<bool> SelectAsync(string serial, DevelopmentDiagram diagram, string titlePath, CancellationToken ct)
    {
        var title = new TemplateImages(string.IsNullOrWhiteSpace(titlePath)
            ? ConfigService.ResolvePath("resources/templates/develop/title_develop.png") : titlePath);
        var matcher = new TemplateMatcher();
        Task<TabView?> Observe(bool requireSelected) => StableObservation.WaitAsync(async () =>
        {
            var f = await automation.CaptureScreenAsync(serial, ct);
            if (title.Match(matcher, f, .86, new(200, 0, 650, 150), ct) is null) return null;
            var tab = Read(f, diagram, ct);
            return requireSelected && tab is not { Selected: true } ? null : tab;
        }, (a, b) => a.Selected == b.Selected && Math.Abs(a.Bounds.X - b.Bounds.X) <= 3 &&
            Math.Abs(a.Bounds.Y - b.Bounds.Y) <= 3, GameTiming.ShortRecognitionTimeoutMs, ct);
        var current = await Observe(false);
        if (current is null) return false;
        if (current.Selected) return true;
        await automation.TapAsync(serial, current.Bounds.CenterX, current.Bounds.CenterY, ct);
        return await Observe(true) is not null;
    }
}

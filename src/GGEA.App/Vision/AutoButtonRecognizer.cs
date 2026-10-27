using System.IO;
using GGEA.App.Config;

namespace GGEA.App.Vision;

/// <summary>AUTO 的可观察状态；证据不足时不能按关闭状态授权切换。</summary>
public enum AutoButtonState { Unknown, All, Enemy, Close }

/// <summary>同帧 AUTO 字形位置、状态与匹配分数，颜色只采样已经定位的按钮内部。</summary>
public sealed record AutoButtonMatch(AutoButtonState State, TemplateMatchResult Match);

/// <summary>刷本与推图共用的 AUTO 识别：保留中央字形和字间背景，排除透明外框受地图背景影响的部分。</summary>
public sealed class AutoButtonRecognizer
{
    private const double Threshold = 0.90;
    private readonly TemplateMatcher _matcher;
    private readonly Lazy<(byte[] Pixels, int Width, int Height, AutoButtonState State)>[][] _templates;

    /// <summary>加载三种实际素材并缓存中央识别区，各执行会话复用，不把未知状态当作关闭。</summary>
    public AutoButtonRecognizer(TemplateMatcher matcher, string allPath, string enemyPath, string closePath)
    {
        _matcher = matcher;
        _templates = new[] { (allPath, AutoButtonState.All), (enemyPath, AutoButtonState.Enemy), (closePath, AutoButtonState.Close) }
            .Select(item => ConfigService.Resources.TemplateCandidates(item.Item1)
                .Select(path => new Lazy<(byte[] Pixels, int Width, int Height, AutoButtonState State)>(() => Load(path, item.Item2))).ToArray()).ToArray();
        foreach (var variants in _templates) _ = variants[0].Value;
    }

    /// <summary>先在指定区域确认 AUTO 字形，再按局部颜色比例区分青蓝、红色和关闭状态。</summary>
    public AutoButtonMatch? Recognize(ScreenImage screen, Roi roi, CancellationToken ct = default)
    {
        AutoButtonMatch? best = null;
        TemplateMatchResult? uncertain = null;
        foreach (var variants in _templates)
        foreach (var lazy in variants)
        {
            ct.ThrowIfCancellationRequested();
            var template = lazy.Value;
            var match = _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Pixels,
                template.Width, template.Height, Threshold, roi, ct: ct);
            if (match is null) continue;
            if (uncertain is null || match.Confidence > uncertain.Confidence) uncertain = match;
            // 灰度命中不能授权另一种颜色的状态：必须由该状态自己的素材同时通过 RGB 复核。
            var state = Classify(screen, match, template.State == AutoButtonState.Close);
            if (state != template.State || ColorConfidence(screen, match, template.Pixels) < .85) continue;
            if (best is null || match.Confidence > best.Match.Confidence) best = new(state, match);
            break; // 本状态语言素材已通过全部检查，不再比较通用版本。
        }
        return best ?? (uncertain is null ? null : new(AutoButtonState.Unknown, uncertain));
    }

    /// <summary>比较同一候选的三个颜色通道，拒绝灰度相似但实际色相与状态素材不一致的画面。</summary>
    private static double ColorConfidence(ScreenImage screen, TemplateMatchResult match, byte[] template)
    {
        double squared = 0;
        for (var y = 0; y < match.Height; y++)
        for (var x = 0; x < match.Width; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var difference = screen.Bgra[((match.Y + y) * screen.Width + match.X + x) * 4 + channel]
                - template[(y * match.Width + x) * 4 + channel];
            squared += difference * difference;
        }
        return 1 - Math.Sqrt(squared / (match.Width * match.Height * 3)) / 255;
    }

    /// <summary>局部颜色只解释当前模板候选，不能借其他位置的关闭素材为本候选提供证据。</summary>
    private static AutoButtonState Classify(ScreenImage screen, TemplateMatchResult best, bool isClose)
    {
        var blue = 0;
        var red = 0;
        var white = 0;
        var dark = 0;
        for (var y = best.Y; y < best.Y + best.Height; y++)
        for (var x = best.X; x < best.X + best.Width; x++)
        {
            var offset = (y * screen.Width + x) * 4;
            var b = screen.Bgra[offset];
            var g = screen.Bgra[offset + 1];
            var r = screen.Bgra[offset + 2];
            // 全部自动是青蓝色，绿、蓝通道可接近；不能要求蓝色一定高于绿色而误判亮光状态。
            if (b > r + 25 && g > r + 15) blue++;
            else if (r > b + 25 && r > g + 25) red++;
            var high = Math.Max(r, Math.Max(g, b));
            var low = Math.Min(r, Math.Min(g, b));
            if (low >= 190 && high - low <= 25) white++;
            if (high <= 140) dark++;
        }
        var area = best.Width * best.Height;
        var state = blue >= area * 0.25 && blue > red * 2 ? AutoButtonState.All
            : red >= area * 0.25 && red > blue * 2 ? AutoButtonState.Enemy
            // 关闭按钮的暗色半透明底允许少量地图混色，但还必须同时存在白字与暗色证据。
            : isClose && blue <= area * 0.05 && red <= area * 0.05 &&
                white >= area * 0.10 && dark >= area * 0.20 ? AutoButtonState.Close
            : AutoButtonState.Unknown;
        return state;
    }

    /// <summary>中央区域保留字形与负空间；拒绝无字形的单色素材，避免相近底色制造按钮命中。</summary>
    private static (byte[] Pixels, int Width, int Height, AutoButtonState State) Load(string path, AutoButtonState state)
    {
        var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
        var left = image.Width / 4;
        var top = image.Height / 5;
        var width = image.Width / 2;
        var height = image.Height * 3 / 5;
        if (width < 2 || height < 2) throw new InvalidOperationException("AUTO 模板尺寸不足。");
        var pixels = new byte[width * height * 4];
        var min = 255;
        var max = 0;
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(image.Bgra, ((top + y) * image.Width + left) * 4,
                pixels, y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                if (pixels[offset + 3] < 128) continue;
                var gray = (pixels[offset + 2] * 299 + pixels[offset + 1] * 587 + pixels[offset] * 114) / 1000;
                min = Math.Min(min, gray);
                max = Math.Max(max, gray);
            }
        }
        if (max - min < 30) throw new InvalidOperationException("AUTO 模板中央没有有效字形对比。");
        return (pixels, width, height, state);
    }
}

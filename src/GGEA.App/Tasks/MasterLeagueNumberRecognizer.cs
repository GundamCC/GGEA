using System.Globalization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>联盟积分和次数的数字行读取；先隔离同字高笔画，避免 Pt、千位逗号和背景干扰。</summary>
internal sealed class MasterLeagueNumberRecognizer(OnnxTextRecognizer ocr)
{
    /// <summary>白字/金字数值使用亮色笔画，浅色确认弹窗的蓝色数字使用深色笔画。</summary>
    public int? Read(ScreenImage screen, Roi roi, bool darkInk = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (roi.X < 0 || roi.Y < 0 || roi.Width <= 0 || roi.Height <= 0 ||
            roi.X + roi.Width > screen.Width || roi.Y + roi.Height > screen.Height) return null;
        var mask = new byte[roi.Width * roi.Height];
        for (var y = 0; y < roi.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < roi.Width; x++)
            {
                var p = ((roi.Y + y) * screen.Width + roi.X + x) * 4;
                var b = screen.Bgra[p]; var g = screen.Bgra[p + 1]; var r = screen.Bgra[p + 2];
                var minimum = Math.Min(r, Math.Min(g, b));
                var maximum = Math.Max(r, Math.Max(g, b));
                var ink = darkInk ? minimum < 160 && maximum < 245
                    : minimum >= 180 && maximum - minimum <= 50 || r >= 210 && g >= 160 && b <= 160;
                if (ink) mask[y * roi.Width + x] = 255;
            }
        }
        var bounds = IsolateDigits(mask, roi.Width, roi.Height, ct);
        if (bounds is null) return null;
        var observed = ocr.Recognize(mask, roi.Width, bounds.Value, ct);
        var decoded = Decode(observed);
        if (decoded is not null) return decoded;
        // 千位逗号被移除后留下宽空隙，可能产生低置信度空格；只重排字距，不修改字形或放宽门槛。
        var originalDigits = string.Concat(observed.Text.Where(c => !char.IsWhiteSpace(c)));
        if (originalDigits.Length is < 2 or > 7 || !originalDigits.All(char.IsAsciiDigit)) return null;
        var packed = PackDigitSpacing(mask, roi.Width, roi.Height, ct);
        if (packed is null) return null;
        var retry = ocr.Recognize(packed.Value.Pixels, packed.Value.Width,
            new Roi(0, bounds.Value.Y, packed.Value.Width, bounds.Value.Height), ct);
        var retried = Decode(retry);
        return retried is not null && retried == int.Parse(originalDigits, CultureInfo.InvariantCulture) ? retried : null;
    }

    /// <summary>按全空白列划分字形并保留原像素，统一四像素间隔，避免千位分隔留下的宽空隙。</summary>
    private static (byte[] Pixels, int Width)? PackDigitSpacing(byte[] mask, int width, int height, CancellationToken ct)
    {
        var runs = new List<(int Start, int Width)>();
        var start = -1;
        for (var x = 0; x <= width; x++)
        {
            ct.ThrowIfCancellationRequested();
            var ink = false;
            if (x < width)
                for (var y = 0; y < height; y++)
                    if (mask[y * width + x] != 0) { ink = true; break; }
            if (ink && start < 0) start = x;
            if (!ink && start >= 0) { runs.Add((start, x - start)); start = -1; }
        }
        if (runs.Count is < 2 or > 7) return null;
        var packedWidth = 4 + runs.Sum(run => run.Width) + (runs.Count - 1) * 4;
        var pixels = new byte[packedWidth * height];
        var destination = 2;
        foreach (var run in runs)
        {
            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(mask, y * width + run.Start, pixels, y * packedWidth + destination, run.Width);
            destination += run.Width + 4;
        }
        return (pixels, packedWidth);
    }

    /// <summary>只允许真实数字与词间空白；字母 O 不转换为 0，逗号已在像素阶段排除。</summary>
    internal static int? Decode(OcrTextRecognition text)
    {
        var minimum = text.MinimumContentConfidence ?? text.MinimumConfidence;
        if (!double.IsFinite(text.Confidence) || !double.IsFinite(minimum) ||
            text.Confidence is < .90 or > 1 || minimum is < .90 or > 1) return null;
        var digits = string.Concat(text.Text.Where(c => !char.IsWhiteSpace(c)));
        return digits.Length is >= 1 and <= 7 && digits.All(char.IsAsciiDigit) &&
            int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>八连通笔画按字高隔离；只删短标签/标点，不补造数字或根据目标值修正 OCR。</summary>
    private static Roi? IsolateDigits(byte[] mask, int width, int height, CancellationToken ct)
    {
        var visited = new bool[mask.Length];
        var components = new List<Roi>();
        var queue = new Queue<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (visited[start] || mask[start] == 0) continue;
            ct.ThrowIfCancellationRequested();
            queue.Enqueue(start); visited[start] = true;
            var left = start % width; var right = left; var top = start / width; var bottom = top; var area = 0;
            while (queue.TryDequeue(out var index))
            {
                var x = index % width; var y = index / width; area++;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx; var ny = y + dy;
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                        var next = ny * width + nx;
                        if (visited[next] || mask[next] == 0) continue;
                        visited[next] = true; queue.Enqueue(next);
                    }
            }
            if (area >= 20 && bottom - top >= 12)
                components.Add(new Roi(left, top, right - left + 1, bottom - top + 1));
        }
        if (components.Count == 0) return null;
        var tallest = components.Max(c => c.Height);
        var digits = components.Where(c => c.Height >= tallest * .85).ToArray();
        // 区域边缘截断或整块背景不是完整数字，不授权任何消费动作。
        if (digits.Any(c => c.X == 0 || c.Y == 0 || c.X + c.Width >= width || c.Y + c.Height >= height ||
            c.Width > c.Height)) return null;
        var minX = Math.Max(0, digits.Min(c => c.X) - 2);
        var minY = Math.Max(0, digits.Min(c => c.Y) - 2);
        var maxX = Math.Min(width, digits.Max(c => c.X + c.Width) + 2);
        var maxY = Math.Min(height, digits.Max(c => c.Y + c.Height) + 2);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (!digits.Any(c => x >= c.X && x < c.X + c.Width && y >= c.Y && y < c.Y + c.Height))
                    mask[y * width + x] = 0;
        return new Roi(minX, minY, maxX - minX, maxY - minY);
    }
}

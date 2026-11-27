using System.Globalization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>仅识别数字及分隔符。按数字行的实际对比度拉伸，让置灰库存也可读取。</summary>
internal static class UpgradeNumbers
{
    internal static string? Read(OnnxTextRecognizer ocr, ScreenImage screen, Roi roi, bool darkInk,
        CancellationToken ct, out int contrast)
    {
        var line = new byte[roi.Width * roi.Height];
        for (var y = 0; y < roi.Height; y++)
        for (var x = 0; x < roi.Width; x++)
        {
            var j = ((roi.Y + y) * screen.Width + roi.X + x) * 4;
            var gray = (screen.Bgra[j] * 114 + screen.Bgra[j+1] * 587 + screen.Bgra[j+2] * 299) / 1000;
            line[y * roi.Width + x] = (byte)(darkInk ? 255 - gray : gray);
        }
        var sorted = line.ToArray();
        Array.Sort(sorted);
        var background = sorted[sorted.Length / 2];
        contrast = sorted[sorted.Length * 99 / 100] - background;
        if (contrast < 20) return null;
        for (var i = 0; i < line.Length; i++)
            line[i] = (byte)Math.Clamp((line[i] - background - 3) * 255 / (contrast - 3), 0, 255);
        var result = ocr.Recognize(line, roi.Width, new Roi(0, 0, roi.Width, roi.Height), ct);
        return result.MinimumConfidence >= .80 ? result.Text.Trim() : null;
    }

    internal static bool TryReadPair(string? text, out int left, out int right)
    {
        left = right = 0;
        var parts = text?.Split('/');
        return parts is { Length: 2 } && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out left)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out right) && left <= right;
    }
}

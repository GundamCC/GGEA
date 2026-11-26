using System.Globalization;
using GGEA.App.Vision;
using GGEA.App.Localization;

namespace GGEA.App.Tasks;

internal readonly record struct RewardCount(int Obtained, int Total);
internal readonly record struct MedalPool(RewardCount Key, RewardCount General)
{
    public int Remaining => Key.Total - Key.Obtained + General.Total - General.Obtained;
    public bool IsNew => Key.Obtained == 0 && General.Obtained == 0;

    /// <summary>同池两类数量都只能增加，提前停止的批次按实际增量计费。</summary>
    public int GainSince(MedalPool before, int maximum)
    {
        int gain = before.Remaining - Remaining;
        if (Key.Total != before.Key.Total || General.Total != before.General.Total ||
            Key.Obtained < before.Key.Obtained || General.Obtained < before.General.Obtained ||
            gain < 1 || gain > maximum)
            throw new InvalidOperationException(L.Text("兑换前后的奖池数量不一致，已停止兑换。"));
        return gain;
    }
}

/// <summary>按分类标题的实际高度读取同行蓝色分数。奖池总数由当前页面决定。</summary>
internal sealed class MedalPoolReader(OnnxTextRecognizer ocr)
{
    public RewardCount? Count(ScreenImage frame, int headerCenterY, CancellationToken ct)
    {
        var text = Read(frame, new(1185, headerCenterY - 19, 229, 38), false, ct);
        var parts = text.Split('/');
        if (parts.Length == 2 && Number(parts[0], out var n) && Number(parts[1], out var total)
            && total is > 0 and <= 99999 && n <= total)
            return new(n, total);
        return null;
    }

    public int? Balance(ScreenImage frame, CancellationToken ct)
    {
        var text = Read(frame, new(1762, 33, 147, 48), true, ct);
        return Number(text.Replace(",", ""), out var n) ? n : null;
    }

    /// <summary>结果页“持有”右侧的当前余额，避开箭头和下一次兑换预览。</summary>
    public int? ResultBalance(ScreenImage frame, CancellationToken ct)
    {
        var text = Read(frame, new(782, 995, 147, 36), false, ct);
        return Number(text.Replace(",", ""), out var n) ? n : null;
    }

    /// <summary>批量按钮费用右对齐。用亮态按钮的实际位置读取数字，兼容次数变化。</summary>
    public int? BulkCost(ScreenImage frame, TemplateMatchResult button, CancellationToken ct)
    {
        var text = Read(frame, new(button.X + 150, button.Y + 54, 100, 33), true, ct);
        return Number(text, out int cost) && cost is > 0 and <= 500 && cost % 5 == 0 ? cost : null;
    }

    private static bool Number(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private string Read(ScreenImage frame, Roi roi, bool white, CancellationToken ct)
    {
        var pixels = new byte[roi.Width * roi.Height];
        int left = roi.Width, top = roi.Height, right = -1, bottom = -1;
        for (int y = 0; y < roi.Height; y++)
        for (int x = 0; x < roi.Width; x++)
        {
            int i = ((roi.Y + y) * frame.Width + roi.X + x) * 4;
            int b = frame.Bgra[i], g = frame.Bgra[i + 1], r = frame.Bgra[i + 2];
            int ink = white ? (Math.Min(r, Math.Min(g, b)) - 150) * 3 : (b - r - 20) * 5;
            if (ink <= 0) continue;
            pixels[y * roi.Width + x] = (byte)Math.Min(255, ink);
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        if (right < left || bottom < top) return "";
        var result = ocr.Recognize(pixels, roi.Width, new(left, top, right-left+1, bottom-top+1), ct);
        return result.Confidence >= .90 && (result.MinimumContentConfidence ?? result.MinimumConfidence) >= .80
            ? result.Text.Trim() : "";
    }
}

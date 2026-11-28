using System.Globalization;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>用当前关卡的 AP、单价和剩余次数计算本批预算。体力可以超过自然恢复上限。</summary>
internal sealed class StageSkipBudget(OnnxTextRecognizer ocr, ResourceResolver resources)
{
    internal sealed record Stage(int Ap, int Cost, int Remaining)
    {
        internal int CountFor(int needed) => Math.Min(needed, Math.Min(Remaining, Ap / Cost));
    }

    private readonly SelectStageTitleRecognizer _title = new(resources.TemplateCandidates("resources/templates/stage/sortie/title_select_stage.png").ToArray());
    internal bool OnStage(ScreenImage f, CancellationToken ct) =>
        f.Width == 1920 && f.Height == 1080 && _title.Recognize(f, ct);

    internal Stage? ReadStage(ScreenImage f, bool hard, CancellationToken ct, bool checkNormalLimit = false)
    {
        if (!OnStage(f, ct)) return null;
        var capacity = Read(f, new(1711, 31, 126, 32), false, ct)?.Split('/');
        var cost = Number(f, new(1380, 687, 84, 48), false, ct);
        if (capacity is not { Length: 2 } || !Parse(capacity[0], out var ap) ||
            !Parse(capacity[1], out var max) || max <= 0 || cost is not > 0) return null;
        int remaining = int.MaxValue;
        if (hard || checkNormalLimit)
        {
            // 雷电 NT HARD 1 的余次靠右显示；两个位置都须读出完整的分子和分母。
            var counter = ReadRemaining(f, new(1674, 631, 76, 42), ct) ??
                ReadRemaining(f, new(1683, 632, 83, 39), ct);
            if (counter.HasValue) remaining = counter.Value;
            else if (hard) return null;
        }
        return new(ap, cost.Value, remaining);
    }

    private int? ReadRemaining(ScreenImage frame, Roi roi, CancellationToken ct) =>
        UpgradeNumbers.TryReadPair(Read(frame, roi, false, ct), out int available, out int limit) && limit > 0
            ? available : null;

    internal int? Selected(ScreenImage f, CancellationToken ct) => Number(f, new(918, 437, 110, 75), true, ct);
    internal int? ProjectedAp(ScreenImage f, CancellationToken ct)
    {
        // 红色下降箭头随数值位数一起右对齐，尤其剩 0 时会移入旧的三位数区域。
        // 箭头顶部的三行高于所有数码；定位完整的连续色带后只把右边的数值交给 OCR。
        int left = -1, right = -1;
        for (int x = 1250; x < 1436; x++)
        {
            bool red = true;
            for (int y = 601; y <= 603; y++)
            {
                int i = (y * f.Width + x) * 4;
                if (f.Bgra[i + 2] - f.Bgra[i] <= 26) red = false;
            }
            if (!red) continue;
            if (left < 0) left = x;
            else if (x != right + 1) return null;
            right = x;
        }
        if (right - left is < 11 or > 19) return null;
        var start = right + 4;
        return Number(f, new(start, 592, 1446 - start, 44), true, ct);
    }
    internal int? OriginalAp(ScreenImage f, CancellationToken ct) => Number(f, new(911, 590, 133, 47), true, ct);
    private string? Read(ScreenImage f, Roi area, bool dark, CancellationToken ct) =>
        UpgradeNumbers.Read(ocr, f, area, dark, ct, out _);
    private int? Number(ScreenImage f, Roi area, bool dark, CancellationToken ct) =>
        Parse(Read(f, area, dark, ct), out var n) ? n : null;
    private static bool Parse(string? s, out int n) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
}

namespace GGEA.App.Vision;

/// <summary>同帧确认历史战斗标题和双列卡片。列表内容及完成状态可变，结构不依赖具体单位。</summary>
internal sealed class FierceHistoryListRecognizer
{
    private readonly TemplateImages _titles;
    private readonly TemplateMatcher _matcher = new();
    internal FierceHistoryListRecognizer(string titlePath) => _titles = new(titlePath);

    internal TemplateMatchResult? Match(ScreenImage screen, bool resampled, CancellationToken ct)
    {
        if (screen.Width != GameResolution.BaseWidth || screen.Height != GameResolution.BaseHeight ||
            !HasCardRows(screen, ct)) return null;
        var roi = RecognitionRegions.Resolve(RecognitionRegion.PageTitle, screen.Width, screen.Height);
        return _titles.Match(template =>
        {
            var candidate = _matcher.MatchCandidate(screen.Bgra, screen.Width, screen.Height, template.Bgra,
                template.Width, template.Height, .92, roi, ct);
            if (candidate is null) return null;
            if (candidate.Confidence >= .92) return candidate;
            // 复采仍在同一标题范围定位，额外要求高相关字形；双列行框本身不能授权手势。
            return resampled && TemplateMatcher.IsOpaque(template) &&
                TemplateMatcher.MatchesFixedTexture(screen, template, candidate.Region, .97) ? candidate : null;
        }, ct);
    }

    private static bool HasCardRows(ScreenImage screen, CancellationToken ct)
    {
        // 两侧同高的卡片横边。边框相对相邻卡片背景的亮度差不依赖红色背景动画。
        bool HasEdge(int y, int left)
        {
            var hits = 0;
            for (var x = left; x < left + 480; x += 12)
            {
                int Blue(int row) => screen.Bgra[(row * screen.Width + x) * 4];
                if (Blue(y) >= 110 && Blue(y) - Math.Min(Blue(y - 5), Blue(y + 5)) >= 50) hits++;
            }
            return hits >= 32;
        }
        var rows = new bool[960];
        for (var y = 175; y < rows.Length; y++)
        {
            ct.ThrowIfCancellationRequested();
            var center = screen.Bgra[(y * screen.Width + 960) * 4];
            if (center + 40 > screen.Bgra[(y * screen.Width + 540) * 4] ||
                center + 40 > screen.Bgra[(y * screen.Width + 1460) * 4] ||
                !HasEdge(y, 390) || !HasEdge(y, 1310)) continue;
            rows[y] = true;
            // 滚动只改变首行的纵向位置，要求至少两条卡片边框相隔一个行高。
            for (var previous = Math.Max(175, y - 194); previous <= y - 174; previous++)
                if (rows[previous]) return true;
        }
        return false;
    }
}

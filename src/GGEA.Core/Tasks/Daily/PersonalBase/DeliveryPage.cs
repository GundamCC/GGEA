using System.Globalization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>本轮交付使用的保护选项快照，两个开关均默认开启。</summary>
public sealed record DeliveryProtection(bool ProtectLimitBreak = true, bool ProtectHighLevel = true);

/// <summary>只读取确认交付弹窗。游戏已按机体筛选库存，只按卡框、等级、突破与锁定状态选卡。</summary>
internal sealed class DeliveryPage(OnnxTextRecognizer ocr, DeliveryProtection protection, int minimumLevel)
{
    private const string Root = "resources/templates/base/character_requests/";
    private readonly TemplateMatcher _matcher = new();
    private readonly TemplateImages _title = new(Root + "title_confirm_delivery.png");
    private readonly TemplateImages _submit = new(Root + "button_submit_delivery.png");
    private readonly TemplateImages _cancel = new(Root + "button_cancel_delivery.png");

    private readonly TemplateImages _lock = new(Root + "delivery_card_lock.png");
    internal sealed record Card(int X, int Top, int Level, int LimitBreaks)
    {
        public int CenterY => Top + 78;
    }
    internal sealed record View(int Count, int ThumbTop, int ThumbEnd, Card? Candidate, Roi? Submit, Roi Cancel, bool UncertainCards = false)
    {
        public bool AtBottom => ThumbEnd == -1 || ThumbEnd >= 846;
        public bool AtTop => ThumbTop == -1 || ThumbTop <= 503;
    }

    internal View? Read(ScreenImage frame, CancellationToken ct)
    {
        if (frame.Width != 1920 || frame.Height != 1080 ||
            _title.Match(_matcher, frame, .96, new(866, 62, 191, 66), ct) is null) return null;
        var cancel = _cancel.Match(_matcher, frame, .96, new(721, 958, 108, 70), ct);
        var amount = UpgradeNumbers.Read(ocr, frame, new(1000, 343, 37, 49), true, ct, out _);
        var pair = UpgradeNumbers.Read(ocr, frame, new(1337, 799, 69, 44), true, ct, out _);
        if (cancel is null || amount != "1" || pair is not ("0/1" or "1/1")) return null;
        var first = -1;
        var last = -1;

        for (int y = 501; y <= 848; y++)
        {
            int i = (y * frame.Width + 1440) * 4;
            var pixels = frame.Bgra;
            if (pixels[i] > 226 && pixels[i + 1] > 223 && pixels[i + 2] > 199)
            {
                if (first == -1) first = y;
                last = y;
            }

        }
        // 没有滚动条的短列表或空列表也有 0/1 计数。调用方在授权补开发前另行确认内容确实不能滚动。
        if (pair == "0/1")
        {
            var candidate = FindCard(frame, first >= 0, ct, out var uncertain);
            return new(0, first, last, candidate, null, cancel.Region, uncertain);
        }
        var submit = _submit.Match(_matcher, frame, .95, new(1096, 960, 111, 69), ct);
        return new(1, first, last, null, submit?.Region, cancel.Region);
    }

    private Card? FindCard(ScreenImage frame, bool scrollable, CancellationToken ct, out bool uncertain)
    {
        uncertain = false;
        var cards = new List<Card>();
        for (var x = 580; x <= 1340; x += 190)
        {
            // 右侧数量浮层会盖住部分库存，拖动前后的重叠行可用于重新检查。
            var end = x >= 900 ? 780 : 848;
            var top = -1;
            var last = -1;
            for (var y = 501; y <= end + 1; y++)
            {
                ct.ThrowIfCancellationRequested();
                if (y <= end && IsCardRow(frame, x, y))
                {
                    if (top < 0) top = y;
                    last = y;
                    continue;
                }
                if (top < 0 || (y <= end && y - last < 6)) continue;
                var height = last - top + 1;
                if (height is >= 156 and <= 180 && top >= 505 && top + 170 <= end)
                {
                    var card = ReadCard(frame, x, top, ct, out var unreadable);
                    uncertain |= unreadable;
                    if (card is not null) cards.Add(card);
                }
                else if (height >= 26 && !(scrollable && (top <= 503 || last >= end - 6)))
                    uncertain = true;
                top = -1;
            }
        }
        return cards.OrderByDescending(c => c.Top / 5).ThenBy(c => c.X).FirstOrDefault();
    }

    // 槽位左侧间隙随列表背景变化。沿卡宽横向采样以定位整张卡的上下边缘，不使用机体图案。
    private static bool IsCardRow(ScreenImage frame, int x, int y)
    {
        var p = frame.Bgra;
        var gap = (y * frame.Width + x - 86) * 4;
        var distinct = 0;
        for (var dx = -55; dx <= 55; dx += 5)
        {
            var i = (y * frame.Width + x + dx) * 4;
            if ((Math.Abs(p[i] - p[gap]) > 34 || Math.Abs(p[i + 1] - p[gap + 1]) > 34 ||
                 Math.Abs(p[i + 2] - p[gap + 2]) > 34) && ++distinct >= 8) return true;
        }
        return false;
    }

    private Card? ReadCard(ScreenImage frame, int x, int top, CancellationToken ct, out bool uncertain)
    {
        // 只有实际卡框的上边缘才能作为星级读数的基准；偏低数像素就可能把星下方的空白误读成零星。
        uncertain = true;
        for (var dx = -30; dx <= 30; dx += 30)
        {
            var i = (top * frame.Width + x + dx) * 4 + 1;
            if (frame.Bgra[i - 4 * frame.Width * 4] - frame.Bgra[i] < 18) return null;
        }
        uncertain = false;
        if (_lock.Match(_matcher, frame, .94, new(x + 23, top + 76, 62, 52), ct) is not null) return null;
        var label = UpgradeNumbers.Read(ocr, frame, new(x - 39, top + 115, 78, 37), false, ct, out var contrast);
        if (label is null || !label.StartsWith("LV", StringComparison.Ordinal) ||
            !int.TryParse(label.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var level) ||
            level is < 1 or > 100 || contrast < 90)
        {
            uncertain = true;
            return null;
        }
        if (level < minimumLevel || (protection.ProtectHighLevel && level >= 60)) return null;
        var stars = ReadLimitBreaks(frame, x, top + 163);
        if (stars is null)
        {
            uncertain = true;
            return null;
        }
        return protection.ProtectLimitBreak && stars > 0 ? null : new(x, top, level, stars.Value);
    }

    // 星级在卡底居中，金星和彩星共用同一间距。以两端的当行底色去掉边框阴影，
    // 只接受完整的 0–3 颗星；被遮挡或图形宽度、中心不一致时保持未知。
    internal static int? ReadLimitBreaks(ScreenImage frame, int x, int top)
    {
        var pixels = frame.Bgra;
        Span<int> ink = stackalloc int[91];
        ink.Clear();
        for (var y = top; y < top + 8; y++)
        {
            var left = (y * frame.Width + x - 45) * 4;
            var right = left + (ink.Length - 1) * 4;
            if (pixels[left] < 130 || pixels[left + 2] < 115) return null;
            for (var channel = 0; channel < 3; channel++)
                if (Math.Abs(pixels[left + channel] - pixels[right + channel]) > 14) return null;
            for (var i = 0; i < ink.Length; i++)
            {
                var index = left + i * 4;
                var delta = Math.Max(Math.Abs(pixels[index] - pixels[left]),
                    Math.Max(Math.Abs(pixels[index + 1] - pixels[left + 1]), Math.Abs(pixels[index + 2] - pixels[left + 2])));
                if (delta > 18) ink[i]++;
            }
        }
        var centers = new List<int>(3);
        for (var i = 0; i < ink.Length; i++)
        {
            if (ink[i] < 3) continue;
            var start = i;
            while (i < ink.Length && ink[i] >= 3) i++;
            if (i - start is < 12 or > 22) return null;
            centers.Add((start + i - 1) / 2);
        }
        if (centers.Count > 3) return null;
        for (var n = 0; n < centers.Count; n++)
            if (Math.Abs(centers[n] - (45 + (2 * n + 1 - centers.Count) * 13)) > 4) return null;
        return centers.Count;
    }
}

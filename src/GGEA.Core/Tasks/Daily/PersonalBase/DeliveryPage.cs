using System.Globalization;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.App.Tasks;

/// <summary>本轮交付使用的保护选项快照，两个开关均默认开启。</summary>
public sealed record DeliveryProtection(bool ProtectLimitBreak = true, bool ProtectHighLevel = true);

/// <summary>只读取确认交付弹窗。用当帧目标图标比对库存卡，拒绝置灰、遮挡和无法核实的卡片。</summary>
internal sealed class DeliveryPage(OnnxTextRecognizer ocr, DeliveryProtection protection, int minimumLevel)
{
    private const string Root = "resources/templates/base/character_requests/";
    private readonly TemplateMatcher _matcher = new();
    private readonly TemplateImages _title = new(Root + "title_confirm_delivery.png");
    private readonly TemplateImages _badge = new(Root + "delivery_selected_one.png");
    private readonly TemplateImages _submit = new(Root + "button_submit_delivery.png");
    private readonly TemplateImages _cancel = new(Root + "button_cancel_delivery.png");

    internal sealed record Card(int X, int ArtY, int Level);
    internal sealed record View(int Count, int ThumbTop, int ThumbEnd, Card? Candidate, Roi? Badge, Roi? Submit, Roi Cancel, bool UnreadableLevel = false)
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
            var candidate = FindCard(frame, ct, out var headerReady, out var unreadableLevel);
            return headerReady ? new(0, first, last, candidate, null, null, cancel.Region, unreadableLevel) : null;
        }
        var badge = _badge.Match(_matcher, frame, .96, new(495, 501, 929, 345), ct);
        var submit = _submit.Match(_matcher, frame, .95, new(1096, 960, 111, 69), ct);
        return new(1, first, last, null, badge?.Region, submit?.Region, cancel.Region);
    }

    private Card? FindCard(ScreenImage frame, CancellationToken ct, out bool headerReady, out bool unreadableLevel)
    {
        using var screen = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var gray = new Mat();
        Cv2.CvtColor(screen, gray, ColorConversionCodes.BGRA2GRAY);
        using var header = gray[new Rect(539, 319, 66, 49)];
        using var target = new Mat();
        Cv2.Resize(header, target, new Size(88, 65));
        Cv2.MeanStdDev(target, out var mean, out var deviation);
        headerReady = deviation.Val0 >= 15;
        unreadableLevel = false;
        if (!headerReady) return null;

        var cards = new List<Card>();
        for (int x = 580; x <= 1340; x += 190)
        {
            ct.ThrowIfCancellationRequested();
            using var column = gray[new Rect(x - 44, 529, 100, 249)];
            using var scores = new Mat();
            Cv2.MatchTemplate(column, target, scores, TemplateMatchModes.CCoeffNormed);
            while (true)
            {
                Cv2.MinMaxLoc(scores, out _, out var score, out _, out var pos);
                if (score < .94) break;
                var y = pos.Y + 529;
                using var patch = column[new Rect(pos.X, pos.Y, target.Width, target.Height)];
                var bottom = Math.Min(scores.Rows, pos.Y + 145);
                using var consumed = scores.RowRange(Math.Max(0, pos.Y - 144), bottom);
                consumed.SetTo(Scalar.All(-1));
                // 浮动数量栏会遮住右侧卡的等级或突破区；颜色差可排除被锁定的暗卡。
                if (y + 136 > 848 || (x >= 900 && y + 136 >= 781) ||
                    Math.Abs(Cv2.Mean(patch).Val0 - mean.Val0) > 19 ||
                    (protection.ProtectLimitBreak && !HasEmptyStarRow(frame, x, y))) continue;
                var label = UpgradeNumbers.Read(ocr, frame, new(x - 39, y + 78, 78, 37), false, ct, out _);
                if (label is null || !label.StartsWith("LV", StringComparison.Ordinal) ||
                    !int.TryParse(label.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var level) ||
                    level is < 1 or > 100)
                {
                    unreadableLevel = true;
                    continue;
                }
                if (level < minimumLevel || (protection.ProtectHighLevel && level >= 60)) continue;
                cards.Add(new(x, y, level));
            }
        }
        return cards.OrderByDescending(c => c.ArtY / 5).ThenBy(c => c.X).FirstOrDefault();
    }

    // 金星和彩星都会打断这条横向灰蓝色阴影。必须确认整条空白，漏认某种星形也不能放行。
    internal static bool HasEmptyStarRow(ScreenImage frame, int x, int artY)
    {
        var p = frame.Bgra;
        for (int y = artY + 126; y < artY + 134; y++)
        {
            var start = (y * frame.Width + x - 40) * 4;
            for (int i = start; i <= start + 80 * 4; i += 4)
            {
                if (p[i] < 163 || p[i + 2] < 133 || p[i] - p[i + 2] < 12) return false;
                for (int c = 0; c < 3; c++)
                    if (Math.Abs(p[i + c] - p[start + c]) > 14) return false;
            }
        }
        return true;
    }
}

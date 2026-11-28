using GGEA.App.Vision;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace GGEA.App.Tasks;

/// <summary>角色要求列表和详情共用页标题。只有列表中的角色等级标签或白锁可以确认栏位状态。</summary>
internal sealed class CharacterRequestPage(OnnxTextRecognizer? sharedOcr) : IDisposable
{
    private const string Directory = "resources/templates/base/character_requests/";
    private readonly TemplateMatcher _matcher = new();
    private readonly TemplateImages _title = new(Directory + "title_character_requests.png");
    private readonly OnnxTextRecognizer _ocr = sharedOcr ?? new();
    private readonly TemplateImages _refresh = new(Directory + "card_refresh.png");
    private readonly ScreenImage _slotLock = LoadSlotLock();

    // 每栏只向英文模型提交同一基线的四个紧邻字形，前缀长度和等级数字均不参与判断。
    internal OcrTextRecognition? ReadRank(ScreenImage screen, int slot, CancellationToken ct)
    {
        var area = new Roi(200 + slot * 535, 604, 320, 74);
        var mask = OnnxTextRecognizer.CreateWhiteMask(screen, area, ct);
        using var source = Mat.FromPixelData(area.Height, area.Width, MatType.CV_8UC1, mask);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centers = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(source, labels, stats, centers);
        var letters = new List<Roi>();
        for (var i = 1; i < count; i++)
        {
            var box = new Roi(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
            if (box.Width is >= 9 and <= 18 && box.Height is >= 15 and <= 21)
                letters.Add(box);
        }

        foreach (var first in letters)
        {
            var word = letters.Where(b => b.X >= first.X && b.X < first.X + 72 &&
                Math.Abs(b.Y - first.Y) <= 2 && Math.Abs(b.Height - first.Height) <= 2)
                .OrderBy(b => b.X).Take(4).ToArray();
            if (word.Length != 4 || word.Zip(word.Skip(1)).Any(pair =>
                pair.Second.X - (pair.First.X + pair.First.Width) is < 1 or > 7)) continue;
            var top = word.Min(b => b.Y);
            var width = word[^1].X + word[^1].Width - first.X;
            if (width is < 60 or > 72) continue;
            var bounds = new Roi(first.X, top, width, word.Max(b => b.Y + b.Height) - top);
            var result = _ocr.Recognize(mask, area.Width, bounds, ct);
            if (result.Text == "RANK" && result.Confidence >= .90 && result.MinimumConfidence >= .85)
                return result;
        }
        return null;
    }

    public void Dispose()
    {
        if (sharedOcr is null) _ocr.Dispose();
    }

    private static ScreenImage LoadSlotLock()
    {
        var image = new TemplateImages("resources/templates/stage/history_boss_lock.png").Primary;
        using var source = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC4, image.Bgra);
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(66, 68), interpolation: InterpolationFlags.Linear);
        var bytes = new byte[66 * 68 * 4];
        Marshal.Copy(resized.Data, bytes, 0, bytes.Length);
        return new ScreenImage { Bgra = bytes, Width = 66, Height = 68 };
    }

    internal bool HasTitle(ScreenImage screen, CancellationToken ct) =>
        _title.Match(_matcher, screen, .93, new Roi(200, 5, 305, 95), ct) is not null;

    /// <summary>按从左到右的解锁顺序取得本轮上限。缺失、矛盾的证据或转场都留给下一帧。</summary>
    internal int? ReadSlotCount(ScreenImage screen, CancellationToken ct) => ReadList(screen, ct)?.Count;

    internal sealed record ListState(int Count, int ActiveMask);
    internal ListState? ReadList(ScreenImage screen, CancellationToken ct)
    {
        if (!HasTitle(screen, ct)) return null;
        var count = 0;
        var mask = 0;
        for (var slot = 0; slot < 3; slot++)
        {
            var dx = slot * 535;
            var hasRank = ReadRank(screen, slot, ct) is not null;
            var locked = _matcher.Match(screen.Bgra, screen.Width, screen.Height, _slotLock.Bgra,
                _slotLock.Width, _slotLock.Height, .93, new Roi(382 + dx, 284, 90, 92), ct: ct) is not null;
            var completed = _refresh.Match(_matcher, screen, .93, new Roi(215 + dx, 598, 285, 122), ct) is not null;
            if ((hasRank ? 1 : 0) + (completed ? 1 : 0) + (locked ? 1 : 0) != 1) return null;
            if (locked) continue;
            if (count != slot) return null;
            count++;
            if (hasRank) mask |= 1 << slot;
        }
        return new(count, mask);
    }
}

using GGEA.App.Vision;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace GGEA.App.Tasks;

/// <summary>角色要求列表和详情共用页标题。只有列表中的角色等级标签或白锁可以确认栏位状态。</summary>
internal sealed class CharacterRequestPage
{
    private const string Directory = "resources/templates/base/character_requests/";
    private readonly TemplateMatcher _matcher = new();
    private readonly TemplateImages _title = new(Directory + "title_character_requests.png");
    private readonly TemplateImages _rank = new(Directory + "card_request_rank.png");
    private readonly ScreenImage _slotLock = LoadSlotLock();

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
    internal int? ReadSlotCount(ScreenImage screen, CancellationToken ct)
    {
        if (!HasTitle(screen, ct)) return null;
        var count = 0;
        for (var slot = 0; slot < 3; slot++)
        {
            var dx = slot * 535;
            var hasRank = _rank.Match(_matcher, screen, .93, new Roi(191 + dx, 604, 166, 74), ct) is not null;
            var locked = _matcher.Match(screen.Bgra, screen.Width, screen.Height, _slotLock.Bgra,
                _slotLock.Width, _slotLock.Height, .93, new Roi(382 + dx, 284, 90, 92), ct: ct) is not null;
            if (hasRank == locked) return null;
            if (hasRank)
            {
                if (count != slot) return null;
                count++;
            }
        }
        return count;
    }
}

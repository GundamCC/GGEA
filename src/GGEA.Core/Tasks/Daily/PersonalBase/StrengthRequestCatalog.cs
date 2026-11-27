using System.Globalization;
using System.IO;
using System.Text.Json;
using GGEA.App.Config;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.App.Tasks;

internal sealed record StrengthOffer(string Id, string Series, int Amount);
internal sealed record StrengthRequest(string RoleId, string Name, StrengthOffer Offer, int Progress);

/// <summary>立绘、标题固定字样与数字编号共同定位委托；进度分母再与表中数量核对。</summary>
internal sealed class StrengthRequestCatalog(OnnxTextRecognizer ocr)
{
    private sealed record Role(string Id, string Name, byte[] Portrait, Dictionary<int, StrengthOffer> Requests);
    private static readonly Lazy<Role[]> Roles = new(() => JsonSerializer.Deserialize<Role[]>(File.ReadAllText(
        ConfigService.ResolvePath("resources/data/strengthen_requests.json"))) ?? throw new InvalidDataException());
    private readonly TemplateImages _title = new("resources/templates/base/character_requests/title_strengthen_troops.png");
    private readonly TemplateMatcher _matcher = new();

    internal StrengthRequest? Read(ScreenImage frame, CancellationToken ct, out bool strengthTitle)
    {
        var role = Identify(frame, ct);
        var title = _title.Match(_matcher, frame, .93, new Roi(1340, 225, 300, 80), ct);
        strengthTitle = title is not null;
        if (role is null || title is null) return null;
        var suffix = UpgradeNumbers.Read(ocr, frame, new Roi(title.X + title.Width + 1, title.Y - 2, 27, 36), true, ct, out _);
        if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            !role.Requests.TryGetValue(number, out var offer) || offer.Amount is < 1 or > 5) return null;

        // 条件会换行，进度紧随其后；只接受分母与当前委托完全相符的一行。
        int? progress = null;
        foreach (var y in new[] { 410, 458, 506 })
        {
            var pair = UpgradeNumbers.Read(ocr, frame, new Roi(1703, y, 61, 32), true, ct, out _);
            if (!UpgradeNumbers.TryReadPair(pair, out var current, out var total) || total != offer.Amount) continue;
            if (progress.HasValue) return null;
            progress = current;
        }
        return progress.HasValue ? new(role.Id, role.Name, offer, progress.Value) : null;
    }

    private static Role? Identify(ScreenImage frame, CancellationToken ct)
    {
        using var screen = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var card = screen[new Rect(135, 195, 845, 502)];
        using var gray = new Mat();
        using var small = new Mat();
        Cv2.CvtColor(card, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.Resize(gray, small, default, .32, .32, InterpolationFlags.Area);
        using var scaled = new Mat();
        using var scores = new Mat();
        Role? best = null;
        double first = 0, second = 0;
        foreach (var role in Roles.Value)
        {
            ct.ThrowIfCancellationRequested();
            using var patch = Cv2.ImDecode(role.Portrait, ImreadModes.Grayscale);
            double similarity = 0;
            for (var side = 17; side <= 37; side += 2)
            {
                Cv2.Resize(patch, scaled, new Size(side, side), interpolation: InterpolationFlags.Area);
                Cv2.MatchTemplate(small, scaled, scores, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(scores, out _, out double score);
                similarity = Math.Max(similarity, score);
            }
            if (similarity > first) { second = first; first = similarity; best = role; }
            else second = Math.Max(second, similarity);
        }
        return first >= .90 && first - second >= .04 ? best : null;
    }
}

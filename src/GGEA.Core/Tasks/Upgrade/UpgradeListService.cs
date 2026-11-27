using System.Runtime.InteropServices;
using GGEA.App.Automation;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.App.Tasks;

public enum UpgradeSortDirection { Ascending, Descending }

/// <summary>在已打开的强化列表识别或切换当前排序方向。排序字段和列表筛选由调用方决定。</summary>
public sealed class UpgradeListService(AutomationService automation)
{
    public const string TitleTemplate = "resources/templates/upgrade/title_upgrade.png";
    private readonly TemplateImages _title = new(TitleTemplate);
    private readonly TemplateMatcher _matcher = new();
    private readonly ScreenImage _ascending = LoadIcon("sort_ascending.png", 68, InterpolationFlags.Area);
    private readonly ScreenImage _descending = LoadIcon("sort_descending.png", 67, InterpolationFlags.Linear);
    private static readonly Roi DirectionArea = new(1720, 837, 135, 115);

    private sealed record DirectionButton(UpgradeSortDirection Direction, Roi Region);

    // 两个原始精灵都是 74×69；保留原图和透明通道，在 1920×1080 识别坐标中各校准一次。
    private static ScreenImage LoadIcon(string file, int height, InterpolationFlags interpolation)
    {
        var source = new TemplateImages("resources/templates/upgrade/" + file).Primary;
        using var input = Mat.FromPixelData(source.Height, source.Width, MatType.CV_8UC4, source.Bgra);
        using var resized = new Mat();
        Cv2.Resize(input, resized, new Size(72, height), interpolation: interpolation);
        var pixels = new byte[72 * height * 4];
        Marshal.Copy(resized.Data, pixels, 0, pixels.Length);
        return new ScreenImage { Bgra = pixels, Width = 72, Height = height };
    }

    /// <summary>连续两帧确认当前方向。至多点击一次，再以目标方向的两帧图像确认切换。</summary>
    public async Task<bool> EnsureDirectionAsync(string serial, UpgradeSortDirection target, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(target)) throw new ArgumentOutOfRangeException(nameof(target));
        var current = await WaitAsync(serial, null, ct);
        if (current is null) return false;
        if (current.Direction == target) return true;
        await automation.TapAsync(serial, current.Region.CenterX, current.Region.CenterY, ct);
        return await WaitAsync(serial, target, ct) is not null;
    }

    private Task<DirectionButton?> WaitAsync(string serial, UpgradeSortDirection? target, CancellationToken ct) =>
        StableObservation.WaitAsync(async () =>
        {
            var button = Read(await automation.CaptureScreenAsync(serial, ct), ct);
            return target is null || button?.Direction == target ? button : null;
        }, (a, b) => a == b, GameTiming.RecognitionTimeoutMs, ct);

    /// <summary>在同一帧校验强化标题与唯一命中的方向图标，排除弹窗和转场。</summary>
    private DirectionButton? Read(ScreenImage screen, CancellationToken ct)
    {
        var titleArea = RecognitionRegions.Resolve(RecognitionRegion.PageTitle, screen.Width, screen.Height);
        if (_title.Match(_matcher, screen, .85, titleArea, ct) is null) return null;
        TemplateMatchResult? Match(ScreenImage icon) => _matcher.Match(screen.Bgra, screen.Width, screen.Height,
            icon.Bgra, icon.Width, icon.Height, .90, DirectionArea, ct: ct);
        var ascending = Match(_ascending);
        var descending = Match(_descending);
        if ((ascending is null) == (descending is null)) return null;
        return ascending is not null ? new(UpgradeSortDirection.Ascending, ascending.Region)
            : new(UpgradeSortDirection.Descending, descending!.Region);
    }
}

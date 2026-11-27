using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>在开发作品列表按原生 Logo 选择作品，用滚动条确认移动和边界。</summary>
internal sealed class DevelopmentSeriesNavigator(AutomationService automation, IGgeaLog log)
{
    private readonly TemplateImages _list = new("resources/templates/develop/series_list_header.png");
    private readonly TemplateImages _unitCatalog = new("resources/templates/develop/unit_catalog_card.png");
    private readonly TemplateMatcher _matcher = new();
    private sealed record CatalogView(int Top, int End, Roi? Banner);

    internal async Task<bool> OpenAsync(string serial, string series, CancellationToken ct)
    {
        var banner = new DevelopmentSeriesLogo(series);
        var view = await ReadListAsync(serial, banner, ct);
        // 开发列表可能沿用上次的滚动位置。先查当前屏，再从顶部向下覆盖所有作品。
        foreach (var down in new[] { false, true })
        {
            for (var i = 0; view.Banner is null && (down ? view.End < 884 : view.Top > 266); i++)
            {
                if (i >= 16) throw new InvalidOperationException(L.Text("无法核对补开发页面或结果，已停止本轮。"));
                var old = view;
                await automation.DragAsync(serial, 930, down ? 775 : 397, 930, down ? 405 : 767, 450, ct);
                view = await ReadListAsync(serial, banner, ct, v => v.Banner is not null ||
                    (down ? v.Top > old.Top : v.Top < old.Top));
            }
        }
        if (view.Banner is not { } work)
        {
            log.Info(L.Format($"开发列表没有找到作品 {series}。"));
            return false;
        }
        await automation.TapAsync(serial, work.CenterX, work.CenterY, ct);
        return true;
    }

    internal async Task ConfirmListAsync(string serial, CancellationToken ct) =>
        _ = await ReadListAsync(serial, null, ct);

    private async Task<CatalogView> ReadListAsync(string serial, DevelopmentSeriesLogo? banner, CancellationToken ct,
        Func<CatalogView, bool>? accept = null) => await StableObservation.WaitAsync(async () =>
    {
        var frame = await automation.CaptureScreenAsync(serial, ct);
        if (frame.Width != 1920 || frame.Height != 1080 || _list.Match(_matcher, frame, .94, new(647, 130, 389, 112), ct) is null) return null;
        int first = -1, last = -1;
        for (int y = 260; y < 895; y++)
        {
            int i = (y * frame.Width + 1821) * 4;
            if (Math.Min(frame.Bgra[i], Math.Min(frame.Bgra[i + 1], frame.Bgra[i + 2])) < 190) continue;
            if (first < 0) first = y;
            last = y;
        }
        var match = banner?.Find(frame, ct);
        if (last - first < 40)
        {
            // 不足一屏时没有滚动滑块。只在首张单位图鉴或目标作品确实可见时确认这个短列表。
            if (first >= 0 || match is null && _unitCatalog.Match(_matcher, frame, .93, new(120, 249, 335, 145), ct) is null)
                return null;
            first = 264;
            last = 885;
        }
        var v = new CatalogView(first, last, match);
        return accept is null || accept(v) ? v : null;
    }, (a, b) => a.Top == b.Top && a.End == b.End && (a.Banner is null && b.Banner is null ||
        a.Banner is { } first && b.Banner is { } second && Math.Abs(first.CenterX - second.CenterX) <= 3 &&
        Math.Abs(first.CenterY - second.CenterY) <= 3), GameTiming.RecognitionTimeoutMs, ct) ?? throw new InvalidOperationException(L.Text("无法核对补开发页面或结果，已停止本轮。"));

}

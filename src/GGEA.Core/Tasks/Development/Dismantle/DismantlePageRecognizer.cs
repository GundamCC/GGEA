using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>筛选状态不能仅根据灰度模板分数判断；无法确认时保留 Unknown。</summary>
public enum DismantleFilterState { Unknown, On, Off }

/// <summary>同一帧中已确认的解体页面控件，不代表已执行自动解体。</summary>
public sealed record DismantlePageObservation(TemplateMatchResult? AutoButton,
    TemplateMatchResult? FilterIcon, DismantleFilterState FilterState, double BlueFraction);

/// <summary>先确认解体标题，再定位控件并通过图标内部青蓝色填充分辨筛选状态。</summary>
public sealed class DismantlePageRecognizer(AutomationService automation)
{
    /// <summary>确认当前排序窗口后重新定位重置；重置后重新截图等待底部确定，未命中不盲点。</summary>
    public async Task<bool> ResetAndConfirmAsync(string serial, CancellationToken ct = default, int timeoutMs = GameTiming.RecognitionTimeoutMs)
    {
        var title = await automation.WaitForImageAsync(serial, ConfigService.ResolvePath("resources/templates/upgrade/sort.png"),
            new WaitOptions { Region = RecognitionRegion.SortTitle, Threshold = .85, TimeoutMs = timeoutMs,
                PollIntervalMs = GameTiming.RecognitionIntervalMs, AllowGlobalContinuation = false }, ct);
        if (title is null) return false;
        if (!await automation.ClickImageAsync(serial, ConfigService.ResolvePath("resources/templates/upgrade/button_reset.png"),
            new WaitOptions { Region = RecognitionRegion.SortReset, Threshold = .90, TimeoutMs = timeoutMs,
                PollIntervalMs = GameTiming.RecognitionIntervalMs, AllowGlobalContinuation = false }, ct)) return false;
        // 蓝色确定按钮有亮光特效，沿用此类按钮的 85% 阈值；只在底部 200 像素查找。
        return await automation.ClickImageAsync(serial, ConfigService.ResolvePath("resources/templates/common/button_confirm.png"),
            new WaitOptions { Region = RecognitionRegion.BottomButtons, Threshold = .85, TimeoutMs = timeoutMs,
                PollIntervalMs = GameTiming.RecognitionIntervalMs, AllowGlobalContinuation = false }, ct);
    }

    /// <summary>重新确认解体标题及 ON 状态后打开排序窗口；只定位重置按钮，不执行重置。</summary>
    public async Task<TemplateMatchResult?> OpenSortAndFindResetAsync(string serial,
        CancellationToken ct = default, int timeoutMs = GameTiming.RecognitionTimeoutMs)
    {
        var page = await RecognizeAsync(await automation.ScreenshotAsync(serial, ct), ct);
        if (page?.FilterState != DismantleFilterState.On || page.FilterIcon is not { } icon) return null;
        await automation.TapAsync(serial, icon.CenterX, icon.CenterY, ct);
        var title = await automation.WaitForImageAsync(serial, ConfigService.ResolvePath("resources/templates/upgrade/sort.png"),
            new WaitOptions { Region = RecognitionRegion.SortTitle, Threshold = .85, TimeoutMs = timeoutMs,
                PollIntervalMs = GameTiming.RecognitionIntervalMs, AllowGlobalContinuation = false }, ct);
        if (title is null) return null;
        return await automation.WaitForImageAsync(serial, ConfigService.ResolvePath("resources/templates/upgrade/button_reset.png"),
            new WaitOptions { Region = RecognitionRegion.SortReset, Threshold = .90, TimeoutMs = timeoutMs,
                PollIntervalMs = GameTiming.RecognitionIntervalMs, AllowGlobalContinuation = false }, ct);
    }

    /// <summary>只读识别同一帧；标题缺失时不把其他页面相似控件当作解体控件。</summary>
    public async Task<DismantlePageObservation?> RecognizeAsync(byte[] png, CancellationToken ct = default)
    {
        async Task<TemplateMatchResult?> Match(string name, RecognitionRegion region, double threshold)
            => await automation.MatchImageAsync(png, ConfigService.ResolvePath($"resources/templates/upgrade/{name}.png"),
                new WaitOptions { Region = region, Threshold = threshold, AllowGlobalContinuation = false }, ct);
        if (await Match("title_dismantle", RecognitionRegion.PageTitle, .85) is null) return null;
        var button = await Match("button_auto_dismantle", RecognitionRegion.AutoDismantle, .85);
        var on = await Match("filter_on", RecognitionRegion.DismantleFilter, .85);
        var off = await Match("filter_off", RecognitionRegion.DismantleFilter, .85);
        var icon = on is not null && (off is null || on.Confidence >= off.Confidence) ? on : off;
        if (icon is null) return new(button, null, DismantleFilterState.Unknown, 0);
        var fraction = GetBlueFraction(ImageCodec.DecodePng(png), icon.Region);
        // 实机 ON/OFF 的灰度分数可同时超过 85%。中心填充为青蓝色才判 ON，白色才判 OFF；过渡帧不猜测。
        var state = fraction >= .25 && on is not null ? DismantleFilterState.On
            : fraction <= .05 && off is not null ? DismantleFilterState.Off : DismantleFilterState.Unknown;
        return new(button, icon, state, fraction);
    }

    /// <summary>只采样图标中心矩形的填充，排除蓝色外边框及周围 UI 的影响。</summary>
    public static double GetBlueFraction(ScreenImage screen, Roi icon)
    {
        var left = Math.Max(0, icon.X + icon.Width * 25 / 100);
        var top = Math.Max(0, icon.Y + icon.Height * 30 / 100);
        var right = Math.Min(screen.Width, icon.X + icon.Width * 65 / 100);
        var bottom = Math.Min(screen.Height, icon.Y + icon.Height * 55 / 100);
        var total = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        if (total == 0) return .1; // 无有效采样区域时落入 Unknown，不当成“没有蓝色”的 OFF。
        var blue = 0;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var i = (y * screen.Width + x) * 4;
            var b = screen.Bgra[i]; var g = screen.Bgra[i + 1]; var r = screen.Bgra[i + 2];
            if (b >= 180 && g >= 150 && b - r >= 45 && g - r >= 35) blue++;
        }
        return (double)blue / total;
    }
}

using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>从挑战打开的已筛选强化列表进入首台低等级单位，结束后依原路径返回调用方。</summary>
internal sealed class UnitUpgradeService(AutomationService automation, OnnxTextRecognizer ocr)
{
    private readonly UpgradeListService _list = new(automation);
    private readonly UpgradeMaterialService _materials = new(automation, ocr);
    private readonly TemplateMatcher _matcher = new();
    private readonly TemplateImages _title = new(UpgradeListService.TitleTemplate);
    private readonly TemplateImages _unit = new("resources/templates/upgrade/title_unit_info.png");
    private readonly TemplateImages _lvButton = new("resources/templates/upgrade/button_lv_upgrade.png");
    private readonly TemplateImages _lvOrder = new("resources/templates/upgrade/sort_key_lv.png");
    private readonly TemplateImages _first = new("resources/templates/upgrade/first_card_lv.png");
    private readonly TemplateImages _filter = new("resources/templates/upgrade/filter_on.png");
    private readonly TemplateImages _back = new("resources/templates/common/back.png");

    internal async Task<bool> ApplyFromFilteredListAsync(string serial, int amount, CancellationToken ct)
    {
        if (!await _list.EnsureDirectionAsync(serial, UpgradeSortDirection.Ascending, ct)) throw Unknown();
        await TapWhenAsync(serial, frame =>
        {
            if (!OnList(frame, ct) ||
                _lvOrder.Match(_matcher, frame, .90, new Roi(1475, 845, 155, 70), ct) is null) return null;
            var filter = _filter.Match(_matcher, frame, .85, new Roi(1610, 846, 130, 113), ct);
            if (filter is null || DismantlePageRecognizer.GetBlueFraction(frame, filter.Region) < .25) return null;
            return _first.Match(_matcher, frame, .90, new Roi(376, 388, 100, 76), ct);
        }, ct);
        await TapWhenAsync(serial, frame => OnUnit(frame, ct) ? _lvButton.Match(_matcher, frame, .93,
            new Roi(1525, 292, 310, 115), ct) : null, ct);
        var success = await _materials.ApplyAsync(serial, amount, ct);
        await TapWhenAsync(serial, frame => OnUnit(frame, ct) ? Back(frame, ct) : null, ct);
        await TapWhenAsync(serial, frame => OnList(frame, ct) ? Back(frame, ct) : null, ct);
        return success;
    }

    private bool OnUnit(ScreenImage f, CancellationToken ct) =>
        _unit.Match(_matcher, f, .93, new Roi(175, 0, 390, 108), ct) is not null;
    private bool OnList(ScreenImage f, CancellationToken ct) =>
        _title.Match(_matcher, f, .85, RecognitionRegions.Resolve(RecognitionRegion.PageTitle, f.Width, f.Height), ct) is not null;
    private TemplateMatchResult? Back(ScreenImage f, CancellationToken ct) =>
        _back.Match(_matcher, f, .90, new Roi(0, 0, 220, 130), ct);

    private async Task TapWhenAsync(string serial, Func<ScreenImage, TemplateMatchResult?> observe, CancellationToken ct)
    {
        var match = await StableObservation.WaitAsync(async () => observe(await automation.CaptureScreenAsync(serial, ct)),
            (a,b) => a.Region == b.Region, GameTiming.RecognitionTimeoutMs, ct) ?? throw Unknown();
        await automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
    }

    private static InvalidOperationException Unknown() => new(L.Text("未能确认强化列表或单位资料，已停止本轮。"));
}

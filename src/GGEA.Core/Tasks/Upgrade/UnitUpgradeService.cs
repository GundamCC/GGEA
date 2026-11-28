using System.Globalization;
using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

internal enum UpgradeOutcome { Applied, NoMaterials, FirstUnitAtCap }

/// <summary>从挑战打开的已筛选强化列表进入首台低等级单位，结束后依原路径返回调用方。</summary>
internal sealed class UnitUpgradeService(AutomationService automation, OnnxTextRecognizer ocr)
{
    private readonly UpgradeListService _list = new(automation);
    private readonly UpgradeMaterialService _materials = new(automation, ocr);
    private readonly TemplateMatcher _matcher = new();
    private readonly TemplateImages _title = new(UpgradeListService.TitleTemplate);
    private readonly TemplateImages _unit = new("resources/templates/upgrade/title_unit_info.png");
    private readonly TemplateImages _lvButton = new("resources/templates/upgrade/button_lv_upgrade.png");
    private static readonly Roi FirstLevel = new(381, 417, 95, 34);
    private readonly TemplateImages _first = new("resources/templates/upgrade/first_card_lv.png");
    private readonly TemplateImages _filter = new("resources/templates/upgrade/filter_on.png");
    private readonly TemplateImages _back = new("resources/templates/common/back.png");

    internal async Task<UpgradeOutcome> ApplyFromFilteredListAsync(string serial, int amount, CancellationToken ct)
    {
        await SelectFirstUnitAsync(serial, ct);
        var unit = await StableObservation.WaitAsync(async () => ReadUnit(await automation.CaptureScreenAsync(serial, ct), ct),
            (a, b) => a == b, GameTiming.RecognitionTimeoutMs, ct)
            ?? throw new InvalidOperationException(L.Text("未能核对单位资料的当前等级、等级上限及按钮。"));
        UpgradeOutcome outcome;
        if (unit.Current == unit.Maximum)
        {
            await automation.TapAsync(serial, unit.Button.CenterX, unit.Button.CenterY, ct);
            outcome = UpgradeOutcome.FirstUnitAtCap;
        }
        else
        {
            await automation.TapAsync(serial, unit.Button.CenterX, unit.Button.CenterY, ct);
            outcome = await _materials.ApplyAsync(serial, amount, ct) ? UpgradeOutcome.Applied : UpgradeOutcome.NoMaterials;
            await TapWhenAsync(serial, frame => OnUnit(frame, ct) ? Back(frame, ct) : null, "未能确认单位资料的返回按钮。", ct);
        }
        await TapWhenAsync(serial, frame => OnList(frame, ct) ? Back(frame, ct) : null, "未能确认强化列表的返回按钮。", ct);
        return outcome;
    }

    internal sealed record UnitLevel(int Current, int Maximum, Roi Button);

    // 单位资料显示强化前的实际等级。两个数字分别右对齐，不读取材料预览中的预计等级。
    internal UnitLevel? ReadUnit(ScreenImage frame, CancellationToken ct)
    {
        if (!OnUnit(frame, ct)) return null;
        var current = UpgradeNumbers.Read(ocr, frame, new(1330, 309, 100, 59), false, ct, out _);
        var maximum = UpgradeNumbers.Read(ocr, frame, new(1448, 320, 60, 50), false, ct, out _);
        if (!int.TryParse(current, NumberStyles.None, CultureInfo.InvariantCulture, out var level) ||
            !int.TryParse(maximum, NumberStyles.None, CultureInfo.InvariantCulture, out var cap) ||
            level <= 0 || level > cap) return null;
        var button = level == cap ? Back(frame, ct) : _lvButton.Match(_matcher, frame, .93, new(1525, 292, 310, 115), ct);
        return button is null ? null : new(level, cap, button.Region);
    }

    // 委托挑战已限定系列，并默认按 LV 排序。仅在降序时反转方向。
    internal async Task SelectFirstUnitAsync(string serial, CancellationToken ct)
    {
        if (!await _list.EnsureDirectionAsync(serial, UpgradeSortDirection.Ascending, ct))
            throw new InvalidOperationException(L.Text("未能在强化列表确认升序按钮状态。"));
        await TapWhenAsync(serial, frame => FindFirstUnit(frame, ct), "未能确认已筛选的强化列表及首张机体卡。", ct);
    }

    internal TemplateMatchResult? FindFirstUnit(ScreenImage frame, CancellationToken ct)
    {
        if (!OnList(frame, ct)) return null;
        var filter = _filter.Match(_matcher, frame, .85, new Roi(1610, 846, 130, 113), ct);
        if (filter is null || DismantlePageRecognizer.GetBlueFraction(frame, filter.Region) < .25) return null;
        var label = _first.Match(_matcher, frame, .90, FirstLevel, ct);
        if (label is not null) return label with { Region = FirstLevel };

        // 720p 的窄笔画缩放后可能无法匹配 LV 素材，此时用完整英文等级行确认首卡。
        var mask = OnnxTextRecognizer.CreateWhiteMask(frame, FirstLevel, ct);
        var text = ocr.Recognize(mask, FirstLevel.Width, new Roi(0, 0, FirstLevel.Width, FirstLevel.Height), ct);
        var level = text.Text.AsSpan().Trim();
        if (text.MinimumConfidence < .85 || !level.StartsWith("LV", StringComparison.Ordinal) ||
            !int.TryParse(level[2..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) || value <= 0) return null;
        return new(FirstLevel, text.Confidence);
    }

    private bool OnUnit(ScreenImage f, CancellationToken ct) =>
        _unit.Match(_matcher, f, .93, new Roi(175, 0, 390, 108), ct) is not null;
    private bool OnList(ScreenImage f, CancellationToken ct) =>
        _title.Match(_matcher, f, .85, RecognitionRegions.Resolve(RecognitionRegion.PageTitle, f.Width, f.Height), ct) is not null;
    private TemplateMatchResult? Back(ScreenImage f, CancellationToken ct) =>
        _back.Match(_matcher, f, .90, new Roi(0, 0, 220, 130), ct);

    private async Task TapWhenAsync(string serial, Func<ScreenImage, TemplateMatchResult?> observe, string failure, CancellationToken ct)
    {
        var match = await StableObservation.WaitAsync(async () => observe(await automation.CaptureScreenAsync(serial, ct)),
            (a,b) => a.Region == b.Region, GameTiming.RecognitionTimeoutMs, ct) ?? throw new InvalidOperationException(L.Text(failure));
        await automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
    }
}

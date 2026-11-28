using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>从角色列表的底部导航进入指定作品。只在已识别的批量预览中开发一台，再退回开发作品列表。</summary>
internal sealed partial class DeliveryDevelopmentService(AutomationService automation, IGgeaLog log, OnnxTextRecognizer ocr)
{
    private const string Root = "resources/templates/develop/delivery/";
    private readonly TemplateMatcher _matcher = new();
    private readonly TemplateImages _detail = new(Root + "unit_detail.png");
    private readonly TemplateImages _preview = new(Root + "preview.png");
    private readonly TemplateImages _cancel = new(Root + "cancel.png");
    private static readonly Roi DetailArea = new(0, 0, 650, 117), PreviewArea = new(840, 40, 242, 110);
    private sealed record Button(Roi Area);
    private sealed record Price(bool CanDevelop);

    internal async Task<bool> TryProduceAsync(string serial, DeliveryTarget target, DevelopRequest request, Func<bool> reserve, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DevelopTitleTemplatePath))
            request = request with { DevelopTitleTemplatePath = ConfigService.ResolvePath("resources/templates/develop/title_develop.png") };
        var requests = new CharacterRequestPage();
        if (await WaitAsync(serial, f => requests.ReadList(f, ct), ct) is null) throw Unknown();
        var nav = request.MainLayout.Require("navDevelop", "缺少开发入口");
        var bar = request.MainLayout.Require("navBase", "缺少个人基地入口");
        await automation.TapAsync(serial, nav.CenterX, bar.CenterY, ct);
        var navigator = new DevelopmentSeriesNavigator(automation, log);
        if (!await navigator.OpenAsync(serial, target.Route.SeriesId, ct)) return false;
        var develop = new DevelopService(automation, log, fixedUnitScales: true);
        if (!await develop.WaitForRequestRoadmapAsync(serial, request, ct)) throw Unknown();
        log.Info(L.Format($"补开发机体 {target.UnitId}，作品 {target.Route.SeriesId}，路线图 {target.Route.DiagramId}。"));
        var map = DevelopmentMaps.Get(target.Route.SeriesId);
        var diagram = map.Diagrams.Single(g => g.Id == target.Route.DiagramId && g.Tab == target.Route.Tab);
        var ready = map.Diagrams.Length == 1 || await new DevelopmentTabNavigator(automation)
            .SelectAsync(serial, diagram, request.DevelopTitleTemplatePath, ct);
        var made = false;
        if (ready)
        {
            var unit = await develop.FindOnRoadmapAsync(serial, ConfigService.ResolvePath(target.Template), "",
                request.MaxDragsPerDirection, request.DragDurationMs, request.Threshold,
                request.DevelopTitleTemplatePath, request.TitleThreshold, ct);
            if (unit is not null)
            {
                await automation.TapAsync(serial, unit.CenterX, unit.CenterY, ct);
                made = await FromUnitDetailAsync(serial, target, request, develop, reserve, ct);
            }
        }
        if (!await develop.WaitForRequestRoadmapAsync(serial, request, ct)) throw Unknown();
        await TapOnPageAsync(serial, new(request.DevelopTitleTemplatePath), new(200, 0, 370, 125),
            new(request.BackButtonTemplatePath), new(0, 0, 225, 120), ct, .87);
        await navigator.ConfirmListAsync(serial, ct);
        return made;
    }

    private async Task<bool> FromUnitDetailAsync(string serial, DeliveryTarget target, DevelopRequest request,
        DevelopService develop, Func<bool> reserve, CancellationToken ct)
    {
        var bulk = new TemplateImages(request.DevelopAllTemplatePath);
        if (!await TryTapOnPageAsync(serial, _detail, DetailArea, bulk, new(1510, 898, 390, 171), ct, .89))
        {
            await CancelAsync(serial, _detail, DetailArea, ct);
            return false;
        }

        // 批量从根节点制造，不选择库存前置。新打开的预览默认为 1，若记住了旧数量则先重置。
        await RequirePageAsync(serial, _preview, PreviewArea, ct);
        var quantity = await WaitAsync(serial, frame => ReadNumber(frame, new(1424, 809, 54, 55), ct), ct);
        if (quantity != "1" && quantity is not null)
            await new DevelopmentQuantityService(automation).SetAsync(serial, 1, request.ExecuteDevelopTemplatePath, log.Info, ct);
        var price = await WaitAsync(serial, frame => ReadPreview(frame, target.Route, ct) is { } allowed
            ? new Price(allowed) : null, ct);
        if (price?.CanDevelop != true)
            log.Info(L.Text("补开发预览的等级、数量、总价或余额不符合交付条件。"));
        if (price?.CanDevelop == true)
        {
            var result = await develop.ConfirmAsync(serial, request, reserve, ct);
            if (result == DevelopResult.Completed) return true;
            if (result is not (DevelopResult.Unavailable or DevelopResult.UnitLimitReached or DevelopResult.InsufficientCoins)) throw Unknown();
        }
        await CancelAsync(serial, _preview, PreviewArea, ct);
        await CancelAsync(serial, _detail, DetailArea, ct);
        return false;
    }

    internal bool? ReadPreview(ScreenImage frame, DeliveryRoute route, CancellationToken ct)
    {
        if (!Visible(_preview, frame, PreviewArea, ct)) return null;
        var count = ReadNumber(frame, new(1424, 809, 54, 55), ct);
        var cost = ReadNumber(frame, new(1450, 658, 395, 54), ct);
        var level = UpgradeNumbers.Read(ocr, frame, new(168, 333, 82, 34), false, ct, out _);
        if (count is null || cost is null || level is null) return null;
        return count == "1" && level == FormattableString.Invariant($"LV{route.Level}") &&
            UpgradeNumbers.TryReadPair(cost, out var used, out _) && used == route.Capital;
    }

    private string? ReadNumber(ScreenImage frame, Roi area, CancellationToken ct) =>
        UpgradeNumbers.Read(ocr, frame, area, true, ct, out _);

    private Task CancelAsync(string serial, TemplateImages page, Roi area, CancellationToken ct) =>
        TapOnPageAsync(serial, page, area, _cancel, new(665, 920, 225, 135), ct);

    private bool Visible(TemplateImages image, ScreenImage frame, Roi area, CancellationToken ct, double threshold = .94) =>
        frame.Width == 1920 && frame.Height == 1080 && image.Match(_matcher, frame, threshold, area, ct) is not null;

    private async Task RequirePageAsync(string serial, TemplateImages page, Roi area, CancellationToken ct)
    {
        if (await WaitAsync(serial, f => Visible(page, f, area, ct) ? "ok" : null, ct) is null) throw Unknown();
    }

    private async Task<bool> TryTapOnPageAsync(string serial, TemplateImages page, Roi area, TemplateImages button,
        Roi buttonArea, CancellationToken ct, double threshold)
    {
        var match = await WaitAsync(serial, f => Visible(page, f, area, ct, threshold)
            && button.Match(_matcher, f, threshold, buttonArea, ct) is { } hit ? new Button(hit.Region) : null, ct);
        if (match is null) return false;
        await TapAsync(serial, match.Area, ct);
        return true;
    }

    private async Task TapOnPageAsync(string serial, TemplateImages page, Roi area, TemplateImages button,
        Roi buttonArea, CancellationToken ct, double threshold = .94)
    {
        if (!await TryTapOnPageAsync(serial, page, area, button, buttonArea, ct, threshold)) throw Unknown();
    }

    private Task TapAsync(string serial, Roi area, CancellationToken ct) => automation.TapAsync(serial, area.CenterX, area.CenterY, ct);

    private Task<T?> WaitAsync<T>(string serial, Func<ScreenImage, T?> read, CancellationToken ct) where T : class =>
        StableObservation.WaitAsync(async () => read(await automation.CaptureScreenAsync(serial, ct)),
            (a, b) => a.Equals(b), GameTiming.RecognitionTimeoutMs, ct);

    private static InvalidOperationException Unknown() => new(L.Text("无法核对补开发页面或结果，已停止本轮。"));
}

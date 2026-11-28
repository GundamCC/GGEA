using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

internal enum ObtainResult { Unavailable, Produced, FirstDevelopmentRequired }

internal sealed partial class DeliveryDevelopmentService
{
    private readonly TemplateImages _expand = new("resources/templates/base/character_requests/roadmap_expand.png");
    internal sealed record Landing(Roi? Unit, ScreenImage Frame);

    /// <summary>移动由游戏定位机体。只观察落点画面，保持游戏选中的分图和地图位置。</summary>
    internal async Task<ObtainResult> FromMethodsAsync(string serial, DeliveryOffer offer, DeliveryProtection protection,
        DevelopRequest request, Func<bool> reserve, CancellationToken ct)
    {
        if (!await new ObtainMethodsNavigator(automation).OpenDevelopmentAsync(serial, ct)) return ObtainResult.Unavailable;
        if (string.IsNullOrWhiteSpace(request.DevelopTitleTemplatePath))
            request = request with { DevelopTitleTemplatePath = ConfigService.ResolvePath("resources/templates/develop/title_develop.png") };
        var develop = new DevelopService(automation, log, fixedUnitScales: true);
        if (!await develop.WaitForRequestRoadmapAsync(serial, request, ct)) throw Unknown();
        var title = new TemplateImages(request.DevelopTitleTemplatePath);
        var landing = await ObserveLandingAsync(serial, title, DevelopmentRequestIndex.DeliveryTemplate(offer.UnitId),
            request.Threshold, ct);
        var result = ObtainResult.FirstDevelopmentRequired;
        if (landing.Unit is {} unit)
        {
            result = ObtainResult.Unavailable;
            var routes = DevelopmentRequestIndex.DeliveryTargets(offer, protection);
            var target = routes.Count == 1 ? routes[0] : routes.FirstOrDefault(candidate =>
            {
                var diagram = DevelopmentMaps.Get(candidate.Route.SeriesId).Diagrams.Single(g => g.Id == candidate.Route.DiagramId);
                return DevelopmentTabNavigator.Read(landing.Frame, diagram, ct) is { Selected: true };
            });
            if (target is not null)
            {
                await TapAsync(serial, unit, ct);
                if (await FromUnitDetailAsync(serial, target, request, develop, reserve, ct)) result = ObtainResult.Produced;
            }
        }
        // 此入口的路线图返回键直接回到原委托。调用方再次核对角色、编号和操作按钮。
        await TapOnPageAsync(serial, title, new(200, 0, 370, 125),
            new(request.BackButtonTemplatePath), new(0, 0, 225, 120), ct, .87);
        return result;
    }

    internal async Task<Landing> ObserveLandingAsync(string serial, TemplateImages title, string template, double threshold,
        CancellationToken ct)
    {
        var wait = new WaitOptions { Threshold = Math.Max(.86, threshold), Roi = new(360, 240, 1520, 700), RoadmapFixedScales = true };
        var path = ConfigService.ResolvePath(template);
        Landing? previous = null;
        int misses = 0;
        long firstMiss = 0;
        var deadline = Environment.TickCount64 + GameTiming.RecognitionTimeoutMs;
        do
        {
            ct.ThrowIfCancellationRequested();
            var png = await automation.ScreenshotAsync(serial, ct);
            var frame = ImageCodec.DecodePng(png);
            if (Visible(title, frame, new(200, 0, 370, 125), ct, .87) &&
                Visible(_expand, frame, new(1754, 948, 126, 127), ct, .92))
            {
                var match = await automation.MatchRoadmapImageAsync(png, path, wait, ct);
                if (match is not null && previous?.Unit is {} prior &&
                    Math.Abs(prior.CenterX - match.CenterX) <= 5 && Math.Abs(prior.CenterY - match.CenterY) <= 5)
                    return new(match.Region, frame);
                if (match is null)
                {
                    if (misses++ == 0) firstMiss = Environment.TickCount64;
                }
                else misses = 0;
                previous = new(match?.Region, frame);
            }
            else
            {
                previous = null;
                misses = 0;
            }
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        } while (Environment.TickCount64 < deadline);
        // 转场、遮罩、单帧漏检及位置不稳不能作为未首次开发的证据。
        if (previous is { Unit: null } && misses >= 2 && Environment.TickCount64 - firstMiss >= GameTiming.RecognitionIntervalMs)
            return previous;
        throw Unknown();
    }
}

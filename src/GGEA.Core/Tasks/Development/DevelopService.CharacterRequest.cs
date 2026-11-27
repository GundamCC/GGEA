using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class DevelopService
{
    /// <summary>从委托跳转的路线图开始。选左侧基准节点，只开发剩余数量，确认返回路线图后退出到原角色。</summary>
    internal async Task DevelopForCharacterAsync(string serial, DevelopmentOrder order, DevelopRequest request,
        OnnxTextRecognizer ocr, CancellationToken ct)
    {
        var amount = order.Offer.Amount - order.Progress;
        if (amount is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(order));
        var title = string.IsNullOrWhiteSpace(request.DevelopTitleTemplatePath)
            ? ConfigService.ResolvePath("resources/templates/develop/title_develop.png") : request.DevelopTitleTemplatePath;
        if (!await WaitForDevelopTitleAsync(serial, title, request.DevelopLayout, request.TitleThreshold, request.TitleWaitMs,
                request.RoadmapDisplaySettingsTitleTemplatePath, ct)) throw DevelopmentUnknown();
        var unit = await FindOnRoadmapAsync(serial, ConfigService.ResolvePath(order.Offer.Template), "",
            request.MaxDragsPerDirection, request.DragDurationMs, request.Threshold, title, request.TitleThreshold, ct,
            preferLeftEdge: true) ?? throw DevelopmentUnknown();
        await _automation.TapAsync(serial, unit.CenterX, unit.CenterY, ct);
        var bulk = await BallFactoryService.WaitForBulkDevelopmentAsync(_automation, serial, request.DevelopAllTemplatePath, ct)
            ?? throw DevelopmentUnknown();
        await _automation.TapAsync(serial, bulk.CenterX, bulk.CenterY, ct);
        await new DevelopmentQuantityService(_automation).SetAsync(serial, amount, request.ExecuteDevelopTemplatePath, _log.Info, ct);

        var onlyCapital = new TemplateImages("resources/templates/develop/capital_only.png");
        var matcher = new TemplateMatcher();
        var expectedCost = checked(order.Offer.Capital * amount);
        var verified = await StableObservation.WaitAsync(async () =>
        {
            var frame = await _automation.CaptureScreenAsync(serial, ct);
            if (onlyCapital.Match(matcher, frame, .94, new(1160, 376, 560, 95), ct) is null) return null;
            var count = UpgradeNumbers.Read(ocr, frame, new(1424, 809, 54, 55), true, ct, out _);
            var cost = UpgradeNumbers.Read(ocr, frame, new(1450, 658, 395, 54), true, ct, out _);
            return int.TryParse(count, out var quantity) && quantity == amount &&
                UpgradeNumbers.TryReadPair(cost, out var needed, out _) && needed == expectedCost ? cost : null;
        }, (first, second) => first == second, GameTiming.RecognitionTimeoutMs, ct);
        if (verified is null) throw DevelopmentUnknown();
        _log.Info(L.Format($"委托开发 {order.Offer.UnitName} ×{amount}，合计 {expectedCost} CAPITAL。"));
        if (await ConfirmAsync(serial, request, ct) != DevelopResult.Completed) throw DevelopmentUnknown();

        // 确认开发后只退出一次路线图。角色详情及委托进度由调用方重新识别。
        if (!await WaitForDevelopTitleAsync(serial, title, request.DevelopLayout, request.TitleThreshold,
                request.DevelopWaitTimeoutMs, "", ct)) throw DevelopmentUnknown();
        var back = await _automation.WaitForImageAsync(serial, request.BackButtonTemplatePath,
            new WaitOptions { Roi = new(0, 0, 240, 130), Threshold = .89, TimeoutMs = request.DevelopWaitTimeoutMs }, ct)
            ?? throw DevelopmentUnknown();
        await _automation.TapAsync(serial, back.CenterX, back.CenterY, ct);
    }

    private static InvalidOperationException DevelopmentUnknown() =>
        new(L.Text("未能核对委托开发的路线图、机体、数量、消耗或结果，已停止本轮。"));
}

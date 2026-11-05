using GGEA.App.Localization;
using System.IO;
using GGEA.App.Config;
using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>铁球工厂的开发阶段：复用主界面恢复、目标定位和开发确认，保留在路线图供后续编排。</summary>
public sealed class BallFactoryService(
    IDevelopService develop,
    Func<string, CancellationToken, Task<bool>> ensureMainScreen)
{
    /// <summary>首次及解体后从主界面定位；连续批次留在路线图重新识别居中的铁球，不返回主界面。</summary>
    public async Task<DevelopResult> DevelopBatchAsync(AutomationService automation, string serial, int quantity,
        DevelopRequest request, Action<string> report, CancellationToken ct = default, bool locateTarget = true)
    {
        if (locateTarget)
        {
            if (await LocateAsync(serial, request, report, ct) != BallFactoryLocateResult.TargetSelected) return DevelopResult.Failed;
        }
        else
        {
            report(L.Text("留在开发页面，重新识别居中的铁球并开始下一批…"));
            var unit = await WaitForCenteredUnitAsync(automation, serial, request.DevelopWaitTimeoutMs, ct);
            if (unit is null) return DevelopResult.Failed;
            await automation.TapAsync(serial, unit.CenterX, unit.CenterY, ct);
        }
        return await DevelopSelectedBatchAsync(automation, serial, quantity, request, report, ct);
    }

    /// <summary>已经选中铁球详情后的单批开发；数量重置至端点，不猜测上次保留值，也不执行导航。</summary>
    public async Task<DevelopResult> DevelopSelectedBatchAsync(AutomationService automation, string serial, int quantity,
        DevelopRequest request, Action<string> report, CancellationToken ct = default)
    {
        var bulk = await WaitForBulkDevelopmentAsync(automation, serial, request.DevelopAllTemplatePath, ct);
        if (bulk is null) return DevelopResult.Failed;
        await automation.TapAsync(serial, bulk.CenterX, bulk.CenterY, ct);
        var button = await DevelopExecutionButton.WaitAsync(automation, serial, request.ExecuteDevelopTemplatePath, GameTiming.RecognitionTimeoutMs, ct);
        if (button.Match is null) return DevelopResult.Failed;
        await new DevelopmentQuantityService(automation).SetAsync(serial, quantity, request.ExecuteDevelopTemplatePath,
            report, ct, current: null);
        // ConfirmAsync 在数量调整后重新截图判定暗态和金币红字，两种任务继续共用该逻辑。
        return (await CompleteBatchAsync(automation, serial, request, report, ct)).State;
    }

    /// <summary>用户提供的铁球原生素材；不沿用每日任务中可能自定义的目标机体。</summary>
    public const string UnitTemplate = "resources/templates/develop/dev_unit/ogk_g0010u00700.png";

    /// <summary>1920×1080 兼容检查通过后，限定在画面右下角识别批量开发入口。</summary>
    public static Roi BulkDevelopmentRoi => new(1570, 907, 350, 173);

    /// <summary>目标点击后等待批量开发按钮；仅识别，不点击或消耗资源。</summary>
    public static Task<TemplateMatchResult?> WaitForBulkDevelopmentAsync(AutomationService automation,
        string serial, string templatePath, CancellationToken ct = default) =>
        automation.WaitForImageAsync(serial, templatePath,
            new WaitOptions { Roi = BulkDevelopmentRoi, Threshold = .85, TimeoutMs = GameTiming.RecognitionTimeoutMs, PollIntervalMs = GameTiming.RecognitionIntervalMs, AllowGlobalContinuation = false }, ct);

    /// <summary>确认已选数量的一批开发，然后重新识别居中的铁球；不开始下一批或解体。</summary>
    public async Task<BallBatchResult> CompleteBatchAsync(AutomationService automation,
        string serial, DevelopRequest request, Action<string> report, CancellationToken ct = default)
    {
        report(L.Text("正在执行开发并等待 TAP TO NEXT…"));
        var confirmation = await develop.ConfirmAsync(serial, request, ct);
        if (confirmation != DevelopResult.Completed) return new(confirmation, null);
        report(L.Text("正在识别开发后居中的铁球…"));
        var unit = await WaitForCenteredUnitAsync(automation, serial, request.DevelopWaitTimeoutMs, ct);
        return new(unit is null ? DevelopResult.Failed : DevelopResult.Completed, unit);
    }

    /// <summary>只观察中央区域，两帧稳定才确认回到铁球；不拖动、不点击、不重复制造。</summary>
    public static async Task<TemplateMatchResult?> WaitForCenteredUnitAsync(AutomationService automation,
        string serial, int timeoutMs = 30000, CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        TemplateMatchResult? previous = null;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var png = await automation.ScreenshotAsync(serial, ct);
            var screen = ImageCodec.DecodePng(png);
            if (screen.Width != 1920 || screen.Height != 1080)
                throw new InvalidOperationException(L.Text("开发后机体识别仅支持 1920×1080。"));
            var match = await automation.MatchRoadmapImageAsync(png, ConfigService.ResolvePath(UnitTemplate),
                new WaitOptions { Roi = new Roi(640, 240, 640, 520), Threshold = .86, RoadmapFixedScales = true }, ct);
            if (match is not null && previous is not null && Math.Abs(match.CenterX - previous.CenterX) <= 8 &&
                Math.Abs(match.CenterY - previous.CenterY) <= 8) return match;
            previous = match;
            await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
        }
        return null;
    }

    /// <summary>先校验本阶段素材，再恢复主界面并进入开发，供每批制造复用。</summary>
    public async Task<BallFactoryLocateResult> LocateAsync(string serial, DevelopRequest request,
        Action<string> report, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = request with
        {
            MsTemplatePath = ConfigService.ResolvePath(UnitTemplate),
            MsLargeTemplatePath = "",
        };
        foreach (var path in new[] { target.RoadmapFirstTemplatePath, target.MsTemplatePath,
                     target.DevelopTitleTemplatePath, target.RoadmapDisplaySettingsTitleTemplatePath,
                     target.DevelopAllTemplatePath })
        {
            if (!string.IsNullOrEmpty(path) && !File.Exists(path))
                throw new FileNotFoundException(L.Text("缺少开发识别素材，请检查资源目录。"), path);
        }
        report(L.Text("正在识别当前页面并返回主界面…"));
        if (!await ensureMainScreen(serial, ct)) return BallFactoryLocateResult.MainScreenUnavailable;
        ct.ThrowIfCancellationRequested();
        report(L.Text("正在进入首个开发作品并搜索铁球…"));
        var match = await develop.FindAndTapAsync(serial, target, ct);
        ct.ThrowIfCancellationRequested();
        return match is null ? BallFactoryLocateResult.TargetNotFound : BallFactoryLocateResult.TargetSelected;
    }
}

/// <summary>仅表达定位阶段结果，不把点击目标计为已制造或已获得 GP。</summary>
public enum BallFactoryLocateResult
{
    /// <summary>未确认主界面，不进入开发。</summary>
    MainScreenUnavailable,
    /// <summary>未找到可稳定点击的目标，停止。</summary>
    TargetNotFound,
    /// <summary>已稳定识别并点击铁球，停留在目标页。</summary>
    TargetSelected,
}

/// <summary>工厂单批结果同时携带资源不足原因及实际单位证据，避免空结果丢失停止原因。</summary>
public sealed record BallBatchResult(DevelopResult State, TemplateMatchResult? Unit);

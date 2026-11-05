using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>略过与自动推图共用单位定位及详情复核，不读取或消费略过次数。</summary>
public sealed class FierceEnemyNavigationService(AutomationService automation, IGgeaLog log, OnnxTextRecognizer ocr)
{
    public const string SelectStageTemplate = "resources/templates/stage/event/button_select_stage.png";
    private static readonly Roi DetailPortrait = new(0, 110, 970, 700);
    /// <summary>先搜索首页，再有限浏览历史列表；每次点击单位前都必须得到素材匹配结果。</summary>
    internal async Task<bool> NavigateAsync(string serial, FierceEnemyUnitRecognizer? unit, CancellationToken ct)
    {
        if (unit is null) return await SelectStageAsync(serial, null, ct);
        var screen = await automation.CaptureScreenAsync(serial, ct);
        var hit = await Task.Run(() => unit.FindHomeSelection(screen, ct), ct);
        if (hit is { } home)
        {
            await automation.TapAsync(serial, home.X + home.Width / 2, home.Y + home.Height / 2, ct);
            return await SelectStageAsync(serial, unit, ct);
        }
        // 历史入口位置只负责导航；具体单位不使用目录编号或行序推算。
        await automation.TapAsync(serial, 1575, 415, ct);
        await Task.Delay(1200, ct);
        // 历史列表可能保留上次滚动位置；先回到顶部，再逐屏识别，最多覆盖十行。
        for (var i = 0; i < 4; i++)
            await automation.SwipeAsync(serial, 1100, 300, 1100, 900, 400, ct);
        await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
        for (var page = 0; page < 7; page++)
        {
            screen = await automation.CaptureScreenAsync(serial, ct);
            hit = await Task.Run(() => unit.FindHistorySelection(screen, ct), ct);
            if (hit is { } history)
            {
                await automation.TapAsync(serial, history.X + history.Width / 2, history.Y + history.Height / 2, ct);
                return await WaitForDetailAsync(serial, unit, ct);
            }
            if (page == 6) break;
            log.Info("当前历史列表未识别到目标单位，继续向下浏览。");
            await automation.SwipeAsync(serial, 1100, 840, 1100, 340, 650, ct);
            await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
        }
        return false;
    }

    /// <summary>识别关卡选择按钮后直接进入；“最新”不会点击单位蓝圈或进入历史列表。</summary>
    private async Task<bool> SelectStageAsync(string serial, FierceEnemyUnitRecognizer? unit, CancellationToken ct)
    {
        var select = await automation.WaitForImageAsync(serial, ConfigService.ResolvePath(SelectStageTemplate),
            new WaitOptions { Roi = FierceEnemyAssaultEntryService.ButtonRegion, Threshold = .92,
                TimeoutMs = GameTiming.ShortRecognitionTimeoutMs, AllowGlobalContinuation = false }, ct);
        if (select is null) return false;
        await automation.TapAsync(serial, select.CenterX, select.CenterY, ct);
        return await WaitForDetailAsync(serial, unit, ct);
    }

    /// <summary>“最新”只确认右侧难度；指定单位仍额外核对立绘，不把首页预览当作详情。</summary>
    internal bool IsDetail(ScreenImage screen, FierceEnemyUnitRecognizer? unit, CancellationToken ct)
        => (unit is null || unit.Find(screen, DetailPortrait, ct) is not null) &&
            Enum.GetValues<FierceEnemyDifficulty>().Any(d => new FierceEnemyDifficultyRecognizer(ocr).Find(screen, d, ct) is not null);

    /// <summary>等待详情难度区域就绪后再交给目标难度查找，允许加载动画但不进行盲点。</summary>
    internal async Task<bool> WaitForDetailAsync(string serial, FierceEnemyUnitRecognizer? unit, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + GameTiming.ShortRecognitionTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var screen = await automation.CaptureScreenAsync(serial, ct);
            if (await Task.Run(() => IsDetail(screen, unit, ct), ct)) return true;
            await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
        }
        return false;
    }

}

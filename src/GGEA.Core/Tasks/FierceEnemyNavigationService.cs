using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>略过与自动推图共用单位定位及详情复核，不读取或消费略过次数。</summary>
public sealed class FierceEnemyNavigationService(AutomationService automation, IGgeaLog log, OnnxTextRecognizer ocr,
    RecognitionEvidence? evidence = null, int timeoutMs = GameTiming.ShortRecognitionTimeoutMs,
    int pollIntervalMs = GameTiming.RecognitionIntervalMs)
{
    public const string SelectStageTemplate = "resources/templates/stage/event/button_select_stage.png";
    public const string HistoryTitleTemplate = "resources/templates/stage/event/title_history_battle.png";
    private static readonly Roi DetailPortrait = new(0, 110, 970, 700);
    private WaitOptions HomeOptions => new() { Roi = FierceEnemyAssaultEntryService.ButtonRegion, Threshold = .92,
        TimeoutMs = timeoutMs, PollIntervalMs = pollIntervalMs, AllowGlobalContinuation = false };
    private WaitOptions HistoryOptions => new() { Region = RecognitionRegion.PageTitle, Threshold = .92,
        TimeoutMs = timeoutMs, PollIntervalMs = pollIntervalMs, AllowGlobalContinuation = false };

    /// <summary>首页短间隔重读四帧，避开选中光效；首页与历史页各自确认就绪，不在未知页面滑动。</summary>
    internal async Task<bool> NavigateAsync(string serial, FierceEnemyUnitRecognizer? unit, CancellationToken ct)
    {
        if (unit is null) return await SelectStageAsync(serial, null, ct);
        ScreenImage? homeScreen = null;
        FierceEnemyUnitObservation? home = null;
        // 标题素材缺失属于资源错误，必须先于历史入口点击暴露。
        ImageCodec.DecodePng(System.IO.File.ReadAllBytes(ConfigService.ResolvePath(HistoryTitleTemplate)));
        var ready = await automation.WaitForImageAsync(serial, ConfigService.ResolvePath(SelectStageTemplate), HomeOptions, ct,
            async (screen, token) =>
            {
                homeScreen = screen;
                return await automation.MatchImageAsync(screen, ConfigService.ResolvePath(SelectStageTemplate), HomeOptions, token);
            });
        if (ready is null)
            return await FailAsync(serial, "home-not-ready", homeScreen, unit, ct);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // 第一次沿用刚确认按钮的同帧，其余重读，禁止以旧按钮授权新的未知画面。
            if (attempt > 0) homeScreen = await automation.CaptureScreenAsync(serial, ct);
            if (await automation.MatchImageAsync(homeScreen!, ConfigService.ResolvePath(SelectStageTemplate), HomeOptions, ct) is null)
                return await FailAsync(serial, "home-changed", homeScreen, unit, ct, home);
            home = await Task.Run(() => unit.ObserveHomeSelection(homeScreen!, ct), ct);
            if (home.Match is { } circle)
            {
                await automation.TapAsync(serial, circle.CenterX, circle.CenterY, ct);
                return await SelectStageAsync(serial, unit, ct);
            }
            if (attempt < 3) await Task.Delay(Math.Min(pollIntervalMs, GameTiming.ConfirmationIntervalMs), ct);
        }
        log.Info(L.Text("强敌首页连续四帧未确认目标蓝圈，转入历史列表查找。"));
        await automation.TapAsync(serial, 1575, 415, ct);
        ScreenImage? screen = null;
        var title = await automation.WaitForImageAsync(serial, ConfigService.ResolvePath(HistoryTitleTemplate), HistoryOptions, ct,
            async (frame, token) =>
            {
                screen = frame;
                return await automation.MatchImageAsync(frame, ConfigService.ResolvePath(HistoryTitleTemplate), HistoryOptions, token);
            });
        if (title is null)
            return await FailAsync(serial, "history-not-ready", screen, unit, ct, home: home, homeScreen: homeScreen);
        // 历史列表可能保留上次位置。每次手势前确认仍在历史页，避免切页后继续盲滑。
        for (var i = 0; i < 4; i++)
        {
            screen = await automation.CaptureScreenAsync(serial, ct);
            if (!await IsHistoryAsync(screen, ct))
                return await FailAsync(serial, "history-changed", screen, unit, ct, home: home, homeScreen: homeScreen);
            await automation.SwipeAsync(serial, 1100, 300, 1100, 900, 400, ct);
        }
        await Task.Delay(pollIntervalMs, ct);
        FierceEnemyUnitObservation? history = null;
        for (var page = 0; page < 7; page++)
        {
            screen = await automation.CaptureScreenAsync(serial, ct);
            if (!await IsHistoryAsync(screen, ct))
                return await FailAsync(serial, "history-changed", screen, unit, ct, home: home, homeScreen: homeScreen);
            history = await Task.Run(() => unit.ObserveHistorySelection(screen, ct), ct);
            if (history.Match is { } hit)
            {
                await automation.TapAsync(serial, hit.CenterX, hit.CenterY, ct);
                return await WaitForDetailAsync(serial, unit, ct);
            }
            if (page == 6) break;
            log.Info("当前历史列表未识别到目标单位，继续向下浏览。");
            await automation.SwipeAsync(serial, 1100, 840, 1100, 340, 650, ct);
            await Task.Delay(pollIntervalMs, ct);
        }
        return await FailAsync(serial, "history-unit-not-found", screen, unit, ct, history, home, homeScreen);
    }

    private async Task<bool> IsHistoryAsync(ScreenImage screen, CancellationToken ct)
        => await automation.MatchImageAsync(screen, ConfigService.ResolvePath(HistoryTitleTemplate), HistoryOptions, ct) is not null;

    /// <summary>识别关卡选择按钮后直接进入；“最新”不会点击单位蓝圈或进入历史列表。</summary>
    private async Task<bool> SelectStageAsync(string serial, FierceEnemyUnitRecognizer? unit, CancellationToken ct)
    {
        ScreenImage? last = null;
        var select = await automation.WaitForImageAsync(serial, ConfigService.ResolvePath(SelectStageTemplate), HomeOptions, ct,
            async (screen, token) =>
            {
                last = screen;
                return await automation.MatchImageAsync(screen, ConfigService.ResolvePath(SelectStageTemplate), HomeOptions, token);
            });
        if (select is null) return await FailAsync(serial, "select-stage-not-found", last, unit, ct);
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
        var deadline = Environment.TickCount64 + timeoutMs;
        ScreenImage? screen = null;
        while (Environment.TickCount64 < deadline)
        {
            screen = await automation.CaptureScreenAsync(serial, ct);
            if (await Task.Run(() => IsDetail(screen, unit, ct), ct)) return true;
            await Task.Delay(pollIntervalMs, ct);
        }
        return await FailAsync(serial, "detail-not-confirmed", screen, unit, ct);
    }

    /// <summary>只在实际失败时保存本次观察及首页上下文，取证失败不覆盖业务失败，取消立即上抛。</summary>
    private async Task<bool> FailAsync(string serial, string stage, ScreenImage? screen, FierceEnemyUnitRecognizer? unit,
        CancellationToken ct, FierceEnemyUnitObservation? last = null, FierceEnemyUnitObservation? home = null,
        ScreenImage? homeScreen = null)
    {
        ct.ThrowIfCancellationRequested();
        log.Warn(L.Format($"强敌单位导航未完成：{stage}。"));
        if (evidence is null || screen is null) return false;
        try
        {
            var files = unit?.EvidenceTemplates().ToList() ?? [];
            if (homeScreen is not null)
                files.Add(new("home.png", ImageCodec.EncodePng(homeScreen.Bgra, homeScreen.Width, homeScreen.Height)));
            await evidence.SaveContextAsync("fierce-unit-navigation", serial, screen,
                new { stage, targetTemplate = unit?.TemplateName ?? "latest", homeAttemptLimit = 4, historyPageLimit = 7, home, last }, files.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn(L.Format($"强敌单位导航取证失败: {ex.GetType().Name}；原识别结果保持不变"));
        }
        return false;
    }
}

using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>大师联盟阶段结果；成功只表示该方法约定的阶段完成，具体完成范围由消息说明。</summary>
public sealed record MasterLeagueResult(bool Success, string Message);

/// <summary>确认关卡后进入大师联盟，可选自动编制与空位确认，复核联盟标题后选择对手。</summary>
public sealed partial class MasterLeagueService
{
    public const string TitleTemplate = "resources/templates/stage/pvp/title_master_league.png";
    public const string FormationButtonTemplate = "resources/templates/stage/pvp/button_formation.png";
    public const string FormationTitleTemplate = "resources/templates/stage/pvp/title_battle_formation.png";
    public const string ConfirmFormationTemplate = "resources/templates/stage/pvp/button_confirm_formation.png";
    public const string EmptyFormationHintTemplate = "resources/templates/dialog/empty_unit_slot_hint.png";
    public const string ConfirmEmptyFormationTemplate = "resources/templates/stage/pvp/button_confirm_empty_formation.png";
    public const string SelectOpponentTemplate = "resources/templates/stage/pvp/button_select_opponent.png";
    public const string BattlePreparationTitleTemplate = "resources/templates/stage/pvp/title_battle_preparation.png";
    public const string ResultSelectOpponentTemplate = "resources/templates/stage/pvp/button_result_select_opponent.png";
    public const string SelectOpponentTitleTemplate = "resources/templates/stage/pvp/title_select_opponent.png";
    private static readonly WaitOptions EmptyHintOptions = new()
    { Roi = new Roi(760, 150, 400, 120), Threshold = .90, AllowGlobalContinuation = false };
    private static readonly WaitOptions EmptyConfirmOptions = new()
    { Roi = new Roi(940, 910, 410, 160), Threshold = .85, AllowGlobalContinuation = false };
    private static readonly WaitOptions OpponentOptions = new()
    { Roi = new Roi(1445, 810, 400, 160), Threshold = .85, AllowGlobalContinuation = false };
    private static readonly WaitOptions TitleOptions = new()
    {
        Roi = new Roi(200, 0, 320, 100), Threshold = .85, AllowGlobalContinuation = false,
    };
    private readonly AutomationService _automation;
    private readonly IGgeaLog _log;
    private readonly TimeProvider _time;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly AutoFormationService _formation;
    private readonly DownloadDialogService _downloadDialog;
    private readonly string _downloadHintPath;
    private readonly bool _allowDownload;

    /// <summary>生产环境使用可取消的真实延时，测试通过内部构造器记录等待而不消耗十秒。</summary>
    public MasterLeagueService(AutomationService automation, IGgeaLog log, AppConfig? config = null)
        : this(automation, log, Task.Delay, config) { }

    /// <summary>只替换时间，不替换截图、模板匹配与实际点击链路。</summary>
    internal MasterLeagueService(AutomationService automation, IGgeaLog log,
        Func<int, CancellationToken, Task> delay, AppConfig? config = null, TimeProvider? timeProvider = null)
    {
        _automation = automation; _log = log; _delay = delay;
        _time = timeProvider ?? TimeProvider.System;
        // 固定本次任务的下载许可，运行中不读取可变的界面设置。
        config ??= new AppConfig();
        _allowDownload = config.AllowGgeaDownload;
        _downloadHintPath = ConfigService.ResolvePath(config.SortieDownloadTemplate);
        _downloadDialog = new DownloadDialogService(automation, new TemplateMatcher(), log,
            ConfigService.ResolvePath(config.DownloadTemplate), ConfigService.ResolvePath(config.DownloadCancelTemplate),
            Math.Clamp(config.DefaultMatchThreshold, .5, 1));
        // 仅复用弹窗步骤，不继承推图的系列/自由部队切换与出击权限。
        _formation = new AutoFormationService(automation, new TemplateMatcher(), log,
            AutoFormationTemplates.FromConfig(new AppConfig()), delay);
    }

    /// <summary>移动页面前解码所需素材；缺失或损坏不能被误当作标题未命中并触发盲点。</summary>
    public void Validate(bool autoFormation)
    {
        var paths = autoFormation
            ? new[] { TitleTemplate, FormationButtonTemplate, FormationTitleTemplate, ConfirmFormationTemplate,
                EmptyFormationHintTemplate, ConfirmEmptyFormationTemplate, SelectOpponentTemplate }
            : new[] { TitleTemplate, SelectOpponentTemplate };
        foreach (var path in paths)
            ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(path)));
        if (autoFormation) _formation.ValidateDialog();
    }

    /// <summary>调用方已确认主界面并持有设备租约；关卡标题未确认时禁止点击联盟入口。</summary>
    public async Task<MasterLeagueResult> OpenFromHomeAsync(string serial, UiLayout mainLayout,
        bool autoFormation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Validate(autoFormation);
        var stage = await new StageMenuEntryService(_automation, _log)
            .OpenFromHomeAsync(serial, mainLayout, GameTiming.RecognitionIntervalMs, ct);
        if (!stage.Success) return new(false, stage.Message);
        await _automation.TapAsync(serial, 1668, 400, ct);
        await _delay(1000, ct);
        return await ConfirmAndEnterFormationAsync(serial, autoFormation, ct);
    }

    /// <summary>仅供已点击联盟入口后的阶段使用：最多十五次中心点击，第十次起逐次复查标题。</summary>
    public async Task<MasterLeagueResult> ConfirmAndEnterFormationAsync(string serial, bool autoFormation,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Validate(autoFormation);
        var screen = await CaptureAsync(serial, ct);
        if (await MatchTitleAsync(screen, ct) is null)
        {
            _log.Info("大师联盟标题未命中，最多点击屏幕中心十五次，每次间隔 1.2 秒；第十次起逐次识别标题。");
            // 坐标仅在标准横屏截图确认后计算；取消和 ADB 异常立即中断，不补齐剩余点击。
            var titleConfirmed = false;
            for (var index = 0; index < 15; index++)
            {
                ct.ThrowIfCancellationRequested();
                await _automation.TapAsync(serial, screen.Width / 2, screen.Height / 2, ct);
                await _delay(1200, ct);
                // 前十次用于清除入口演出；此后每次重新截图，标题出现便停止补点。
                if (index < 9) continue;
                screen = await CaptureAsync(serial, ct);
                titleConfirmed = await MatchTitleAsync(screen, ct) is not null;
                if (titleConfirmed) break;
            }
            if (!titleConfirmed)
                return Fail("十五次点击后仍未识别到大师联盟标题，已停止大师联盟流程。");
        }
        _log.Info("已识别左上角大师联盟标题，确认进入大师联盟界面。");
        if (!autoFormation) return await WaitForLeagueAndSelectOpponentAsync(serial, false, ct);

        // 标题与按钮使用同一帧，不能沿用旧标题授权点击另一页面上的相似按钮。
        var button = await _automation.MatchImageAsync(screen, ConfigService.ResolvePath(FormationButtonTemplate),
            new WaitOptions { Roi = new Roi(1060, 810, 400, 160), Threshold = .85,
                AllowGlobalContinuation = false }, ct);
        if (button is null) return Fail("未识别到编制按钮，已停止，未点击编制。");
        ct.ThrowIfCancellationRequested();
        await _automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
        var title = await _automation.WaitForImageAsync(serial, ConfigService.ResolvePath(FormationTitleTemplate),
            TitleOptions with { PollIntervalMs = 1000 }, ct);
        if (title is null) return Fail("未识别到对战编制标题，已停止大师联盟流程。");
        return await AutoFormCurrentPageAsync(serial, ct);
    }

    /// <summary>对战编制页执行与推图相同的自动编制弹窗，再确认本页编制并复核返回联盟。</summary>
    public async Task<MasterLeagueResult> AutoFormCurrentPageAsync(string serial, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Validate(true);
        var screen = await CaptureAsync(serial, ct);
        if (await _automation.MatchImageAsync(screen, ConfigService.ResolvePath(FormationTitleTemplate), TitleOptions, ct) is null)
            return Fail("未识别到对战编制标题，已停止大师联盟流程。");
        _log.Info("已确认对战编制页，使用自动推图共用流程执行自动编制。");
        await _formation.ExecuteDialogAsync(serial, screen, ct);

        // 弹窗的“确定”和页面的“确定编制”属于两个阶段；必须重新确认页面，不能复用旧坐标。
        var confirm = await _automation.WaitForImageAsync(serial, ConfigService.ResolvePath(ConfirmFormationTemplate),
            new WaitOptions { Roi = new Roi(1470, 910, 410, 160), Threshold = .85,
                PollIntervalMs = 1000, AllowGlobalContinuation = false }, ct,
            async (frame, token) =>
            {
                if (await _automation.MatchImageAsync(frame, ConfigService.ResolvePath(FormationTitleTemplate), TitleOptions, token) is null)
                    return null;
                return await _automation.MatchImageAsync(frame, ConfigService.ResolvePath(ConfirmFormationTemplate),
                    new WaitOptions { Roi = new Roi(1470, 910, 410, 160), Threshold = .85 }, token);
            });
        if (confirm is null) return Fail("未同时确认对战编制标题与确定编制按钮，已停止大师联盟流程。");
        ct.ThrowIfCancellationRequested();
        await _automation.TapAsync(serial, confirm.CenterX, confirm.CenterY, ct);
        return await WaitForLeagueAndSelectOpponentAsync(serial, true, ct);
    }

    /// <summary>合并直接返回和空位提示分支；每轮读取新帧，同帧标题与按钮共同授权选择对手。</summary>
    internal async Task<MasterLeagueResult> WaitForLeagueAndSelectOpponentAsync(string serial,
        bool allowEmptyConfirmation, CancellationToken ct)
    {
        var emptyConfirmed = false;
        // 固定最多二十轮；空位提示只确认一次，未消失也不能无限重复点击。
        for (var poll = 0; poll < 20; poll++)
        {
            ct.ThrowIfCancellationRequested();
            var screen = await CaptureAsync(serial, ct);
            if (allowEmptyConfirmation && await _automation.MatchImageAsync(screen,
                ConfigService.ResolvePath(EmptyFormationHintTemplate), EmptyHintOptions, ct) is not null)
            {
                // 提示优先于背景标题，不能透过弹窗点击背景的选择对手按钮。
                if (!emptyConfirmed)
                {
                    var confirm = await _automation.MatchImageAsync(screen,
                        ConfigService.ResolvePath(ConfirmEmptyFormationTemplate), EmptyConfirmOptions, ct);
                    if (confirm is not null)
                    {
                        ct.ThrowIfCancellationRequested();
                        await _automation.TapAsync(serial, confirm.CenterX, confirm.CenterY, ct);
                        emptyConfirmed = true;
                        _log.Info("已识别编制空位提示并点击对话框中的确定编制，等待返回大师联盟。");
                    }
                }
            }
            else if (await MatchTitleAsync(screen, ct) is not null)
            {
                var opponent = await _automation.MatchImageAsync(screen,
                    ConfigService.ResolvePath(SelectOpponentTemplate), OpponentOptions, ct);
                if (opponent is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    await _automation.TapAsync(serial, opponent.CenterX, opponent.CenterY, ct);
                    _log.Info("已重新识别大师联盟标题，并点击选择对战对手。");
                    return new(true, "已确认大师联盟界面并点击选择对战对手。");
                }
            }
            if (poll < 19) await _delay(1000, ct);
        }
        return Fail("未确认大师联盟标题及选择对战对手按钮，已停止；若空位提示仍在，请检查确认结果。");
    }

    /// <summary>供选定对手并点击对战后的阶段调用；只确认准备标题并挑战一次，不推断战斗结果。</summary>
    public async Task<MasterLeagueResult> ChallengePreparedBattleAsync(string serial, CancellationToken ct = default,
        Func<ScreenImage, CancellationToken, int?>? readRemaining = null)
    {
        ct.ThrowIfCancellationRequested();
        var path = ConfigService.ResolvePath(BattlePreparationTitleTemplate);
        var title = ImageCodec.DecodePng(File.ReadAllBytes(path));
        var mask = MasterLeagueVision.CreateWhiteTextMask(title);
        var matcher = new TemplateMatcher();
        // 等待转场时禁止中心点击或全局继续；标题未出现不能消耗挑战次数。
        for (var poll = 0; poll < 20; poll++)
        {
            var screen = await CaptureAsync(serial, ct);
            if (await Task.Run(() => matcher.Match(screen.Bgra, screen.Width, screen.Height,
                title.Bgra, title.Width, title.Height, .90, TitleOptions.Roi, mask, ct), ct) is not null)
            {
                if (readRemaining is not null)
                {
                    var remaining = await Task.Run(() => readRemaining(screen, ct), ct);
                    if (remaining == 0)
                    {
                        _log.Info("当前对手挑战次数为零，已停止，未点击挑战。");
                        return new(false, "当前对手挑战次数已耗尽，已停止。");
                    }
                    if (remaining is null)
                    {
                        if (poll < 19) await _delay(1000, ct);
                        continue;
                    }
                }
                ct.ThrowIfCancellationRequested();
                await _automation.TapAsync(serial, 1675, 1000, ct);
                _log.Info("已确认对战准备标题并点击挑战，等待进入具体对战界面。");
                return new(true, "已点击挑战，尚未确认战斗结果。");
            }
            if (poll < 19) await _delay(1000, ct);
        }
        return Fail("未可靠确认对战准备标题及可用挑战次数，已停止，未点击挑战。");
    }

    /// <summary>仅在结算 OCR 与橙色按钮同帧确认后返回；点击一次，再验证真正回到对手页。</summary>
    public async Task<MasterLeagueResult> ReturnFromBattleResultAsync(string serial,
        MasterLeagueResultRecognizer resultRecognizer, CancellationToken ct = default,
        Func<ScreenImage, CancellationToken, bool>? isOpponentPageReady = null)
    {
        ct.ThrowIfCancellationRequested();
        resultRecognizer.Validate();
        _automation.TapToNext.Validate();
        foreach (var path in new[] { ResultSelectOpponentTemplate, SelectOpponentTitleTemplate })
            ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(path)));
        var opponentTitle = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(SelectOpponentTitleTemplate)));
        var opponentTitleMask = MasterLeagueVision.CreateWhiteTextMask(opponentTitle);
        var titleMatcher = new TemplateMatcher();
        var screen = await CaptureAsync(serial, ct);
        if (!await Task.Run(() => resultRecognizer.IsResult(screen, ct), ct))
            return Fail("未可靠识别中央 BATTLE SCORE，未点击结算返回按钮。");
        var button = await _automation.MatchImageAsync(screen, ConfigService.ResolvePath(ResultSelectOpponentTemplate),
            new WaitOptions { Roi = new Roi(1495, 905, 410, 160), Threshold = .85,
                AllowGlobalContinuation = false }, ct);
        if (button is null) return Fail("未识别到结算页橙色选择对战对手按钮，已停止返回操作。");
        ct.ThrowIfCancellationRequested();
        await _automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
        // 晋级动画可能晚于首次截图；整个返回阶段持续观察继续提示与目标页，不因一次未命中退出。
        var pages = 0;
        var readyFrames = 0;
        var opponentTitleSeen = false;
        var waitMs = GameTiming.RecognitionIntervalMs;
        for (var poll = 0; poll < 60; poll++)
        {
            await _delay(waitMs, ct);
            waitMs = 1000;
            screen = await CaptureAsync(serial, ct);
            // 挑战模式解锁提示位于 Y≈856，需使用底部 300 像素的继续专用区域。
            var next = await Task.Run(() => _automation.TapToNext.Recognize(screen, ct, RecognitionRegion.ProgressTapToNext), ct);
            if (next is not null)
            {
                readyFrames = 0;
                if (pages < 3)
                {
                    ct.ThrowIfCancellationRequested();
                    await _automation.TapAsync(serial, next.CenterX, next.CenterY, ct);
                    pages++;
                    waitMs = GameTiming.RecognitionIntervalMs;
                    _log.Info($"结算返回后检测到 TAP TO NEXT，已处理第 {pages}/3 页。");
                }
                // 达到三页上限后仍可等待页面消退，但绝不再点击。
                continue;
            }
            var titleVisible = await Task.Run(() => titleMatcher.Match(screen.Bgra, screen.Width, screen.Height,
                opponentTitle.Bgra, opponentTitle.Width, opponentTitle.Height, .90,
                new Roi(200, 0, 440, 100), opponentTitleMask, ct), ct) is not null;
            opponentTitleSeen |= titleVisible;
            // 主循环要求完整数字表；独立阶段至少同帧确认标题和对战按钮，防止动画背景标题提前放行。
            var ready = titleVisible && (isOpponentPageReady is not null
                ? await Task.Run(() => isOpponentPageReady(screen, ct), ct)
                : await _automation.MatchImageAsync(screen, ConfigService.ResolvePath("resources/templates/stage/pvp/button_battle.png"),
                    OpponentOptions, ct) is not null);
            readyFrames = ready ? readyFrames + 1 : 0;
            if (readyFrames >= 2)
                return new(true, "已连续确认返回选择对战对手界面。");
        }
        return Fail(opponentTitleSeen
            ? "已识别对手选择页标题，但积分、次数或页面信息未完整确认，已停止后续操作。"
            : "已点击结算返回，但未确认对手选择页，已停止后续操作。");
    }

    /// <summary>使用有效新截图，在固定坐标输入前拒绝非标准分辨率。</summary>
    private async Task<ScreenImage> CaptureAsync(string serial, CancellationToken ct)
    {
        var screen = await _automation.CaptureScreenAsync(serial, ct);
        if (screen.Width != 1920 || screen.Height != 1080)
            throw new InvalidOperationException("大师联盟需要 1920×1080 横屏画面，已停止点击。");
        return screen;
    }

    /// <summary>只在左上角标题区域匹配，避免页面其他位置的同名文字误判。</summary>
    private Task<TemplateMatchResult?> MatchTitleAsync(ScreenImage screen, CancellationToken ct)
        => _automation.MatchImageAsync(screen, ConfigService.ResolvePath(TitleTemplate), TitleOptions, ct);

    /// <summary>识别证据不足时报告当前任务停止，不改变其他任务的状态。</summary>
    private MasterLeagueResult Fail(string message)
    { _log.Warn(message); return new(false, message); }
}

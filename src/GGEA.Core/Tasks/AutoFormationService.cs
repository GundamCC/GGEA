using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>编制原因：空槽、已取消的地形提示，或历史首关主动初始化。</summary>
public enum FormationIssue { EmptySlot, Terrain, Initial }

/// <summary>部队身份；调整权限限制自动编制，最低出击条件允许只读查看两支部队。</summary>
public enum FormationTeam { Series, Free }

/// <summary>区分已确认编制与未找到目标；编制完成不代表所有空位均已补齐。</summary>
public enum AutoFormationResult { NoTarget, Adjusted }

/// <summary>出击前编制检查结果；允许出击仅表示获准部队符合当前策略，不表示战斗已完成。</summary>
public enum FormationPreflightResult { Ready, NoUnits }

/// <summary>本关卡的部队选择与编制记录；历史首关由作品流程持有，跨导航恢复保留确认记录。</summary>
public sealed class AutoFormationSession
{
    // 新准备页可能保留上次选中的自由部队；未知选择必须先显式切换，不能默认把截图当作系列部队。
    public FormationTeam? CurrentTeam { get; internal set; }
    internal HashSet<(FormationIssue Issue, FormationTeam Team)> Adjusted { get; } = new();
}

/// <summary>素材来源独立于流程控制；测试可使用隔离资源，不需要连接真实模拟器。</summary>
public sealed record AutoFormationTemplates(
    string Sortie, string EmptyUnit, string EmptyPilot, string UnableToSortie,
    string AutoFormButton, string AutoFormTitle, string Confirm, string LockedUnit)
{
    /// <summary>按当前仓库的单位/驾驶员拆分素材构造路径，不依赖已移除的旧单位文件名。</summary>
    public static AutoFormationTemplates FromConfig(AppConfig config) => new(
        ConfigService.ResolvePath(config.SortieTemplate),
        ConfigService.ResolvePath("resources/templates/stage/sortie/empty_unit_slot.png"),
        ConfigService.ResolvePath("resources/templates/stage/sortie/empty_pilot_slot.png"),
        ConfigService.ResolvePath("resources/templates/stage/sortie/unable_to_sortie.png"),
        ConfigService.ResolvePath("resources/templates/stage/sortie/button_auto-form.png"),
        ConfigService.ResolvePath("resources/templates/stage/sortie/auto-form.png"),
        ConfigService.ResolvePath("resources/templates/common/button_confirm.png"),
        ConfigService.ResolvePath("resources/templates/stage/sortie/locked_unit_slot.png"));
}

/// <summary>
/// 出击前空槽检查与出击提示取消后的定向编制：先确认准备页，再按权限检查系列/自由部队。
/// 所有确认点击均由同帧标题和按钮共同授权；每个问题每支部队最多调整一次。
/// </summary>
public sealed class AutoFormationService
{
    public const string NoUnitsMessage = "系列部队与自由部队均未编入任何单位，至少需要一个单位才能出击；“依旧出击”不能跳过此条件，已中止当前推图，请先编入可出击的单位。";
    public static readonly Roi SlotsRoi = new(300, 175, 1620, 590);
    // 1920×1080 准备页有五列单位卡；只检查上方单位区，不能把下方驾驶员或支援人员的加号算成空单位。
    private static readonly Roi[] UnitSlots = Enumerable.Range(0, 5)
        .Select(index => new Roi(315 + 313 * index, 210, 326, 372)).ToArray();
    private static readonly Roi PilotSlotsRoi = new(300, 570, 1620, 195);
    public static readonly Roi BottomButtonsRoi = new(0, 810, 1920, 270);
    // 顶部横向居中的 1060×150 区域，完整容纳弹窗标题及模板边缘。
    public static readonly Roi TitleRoi = new(430, 0, 1060, 150);
    public static readonly Roi SortieRoi = new(1440, 810, 480, 270);
    private const double Threshold = 0.90;
    // 确定按钮包含动态亮光；仅此目标使用用户指定的 85%，标题授权仍为 90%。
    private const double ConfirmThreshold = 0.85;
    private readonly AutomationService _automation;
    private readonly TemplateMatcher _matcher;
    private readonly IGgeaLog _log;
    private readonly AutoFormationTemplates _paths;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly int _maxPolls;
    private readonly Dictionary<string, ScreenImage> _templates = new();

    /// <summary>轮询有固定上限；延时可替换以验证取消、权限和页面流转，不改变生产等待时间。</summary>
    public AutoFormationService(AutomationService automation, TemplateMatcher matcher, IGgeaLog log,
        AutoFormationTemplates paths, Func<int, CancellationToken, Task>? delay = null, int maxPolls = GameTiming.RecognitionTimeoutMs / GameTiming.RecognitionIntervalMs)
    {
        _automation = automation;
        _matcher = matcher;
        _log = log;
        _paths = paths;
        _delay = delay ?? Task.Delay;
        _maxPolls = Math.Max(1, maxPolls);
    }

    /// <summary>仅预检所选策略真正使用的素材；缺失或透明空图在执行任何点击前明确失败。</summary>
    public void Validate(AutoProgressSettings settings)
    {
        Load(_paths.Sortie);
        // 即使依旧出击或撤销调整权限，也必须检查两队是否完全没有单位。
        Load(_paths.EmptyUnit); Load(_paths.LockedUnit);
        if (!settings.CanAutoForm || (settings.EmptySlotAction != FormationIssueAction.AutoForm &&
                                     settings.TerrainAction != FormationIssueAction.AutoForm)) return;
        Load(_paths.AutoFormButton); Load(_paths.AutoFormTitle); Load(_paths.Confirm);
        if (settings.EmptySlotAction == FormationIssueAction.AutoForm)
        { Load(_paths.EmptyUnit); Load(_paths.EmptyPilot); }
        if (settings.TerrainAction == FormationIssueAction.AutoForm) Load(_paths.UnableToSortie);
    }

    /// <summary>在准备页确认出击按钮，但不点击；此步骤也保证截图规格和网络错误校验已经通过。</summary>
    public async Task<TemplateMatchResult> WaitForSortieAsync(string serial, CancellationToken ct)
        => (await WaitForAsync(serial, _paths.Sortie, SortieRoi, "出击准备页的出击按钮", ct)).Match;

    /// <summary>恢复只读复核准备页的带 AP 出击按钮与自动编制按钮；单个相似橙框不能授权重新编制。</summary>
    public Task<bool> IsPreparationPageAsync(ScreenImage screen, CancellationToken ct)
        => Task.Run(() => Match(screen, _paths.Sortie, SortieRoi, ct) is not null &&
            Match(screen, _paths.AutoFormButton, BottomButtonsRoi, ct) is not null, ct);

    /// <summary>出击前按权限尝试编制；剩余空位允许依旧出击，但两队均无单位仍不可出击。</summary>
    public async Task<FormationPreflightResult> PrepareForSortieAsync(string serial, AutoProgressSettings settings,
        AutoFormationSession session, Action<string> report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        settings = settings.Snapshot();
        if (!settings.CanAutoForm || settings.EmptySlotAction != FormationIssueAction.AutoForm)
            return await CheckMinimumRosterAsync(serial, session, ct);
        var adjusted = false;
        foreach (var team in new[] { FormationTeam.Series, FormationTeam.Free })
        {
            if (!IsAllowed(team, settings)) continue;
            var screen = await SelectTeamAsync(serial, team, session, ct);
            if (!HasEmptySlot(screen, ct))
            {
                _log.Info($"出击前检查：{TeamName(team)}未命中单位或驾驶员空槽。");
                continue;
            }
            // 本队已经确认过编制（包括地形编制）时，不为剩余空位反复编制。
            if (session.Adjusted.Any(adjustment => adjustment.Team == team))
            {
                ReportRemainingSlots(team, report);
                continue;
            }
            await AdjustTeamAsync(serial, FormationIssue.EmptySlot, team, screen, settings, session, report, ct);
            adjusted = true;
        }
        // 两支部队共用可出击资源；后一次编制可能改变先前部队，最终复查不能沿用编制前的截图。
        if (adjusted)
            foreach (var team in new[] { FormationTeam.Series, FormationTeam.Free })
            {
                if (!IsAllowed(team, settings)) continue;
                if (!HasEmptySlot(await SelectTeamAsync(serial, team, session, ct), ct)) continue;
                ReportRemainingSlots(team, report);
            }
        report("出击前空槽检查完成，继续校验最低出击条件。");
        return await CheckMinimumRosterAsync(serial, session, ct);
    }

    /// <summary>历史首关明确要求两队初始化，不依赖空槽或日常修复权限；会话保留确认记录，导航恢复不重复编制。</summary>
    public async Task InitializeBothTeamsAsync(string serial, AutoProgressSettings settings,
        AutoFormationSession session, Action<string> report, CancellationToken ct)
    {
        // 先验证全部编制素材，避免第一队已修改后才发现第二步缺少必要资源。
        Load(_paths.Sortie); Load(_paths.AutoFormButton); Load(_paths.AutoFormTitle); Load(_paths.Confirm);
        foreach (var team in new[] { FormationTeam.Series, FormationTeam.Free })
        {
            ct.ThrowIfCancellationRequested();
            if (session.Adjusted.Contains((FormationIssue.Initial, team))) continue;
            var screen = await SelectTeamAsync(serial, team, session, ct);
            await AdjustTeamAsync(serial, FormationIssue.Initial, team, screen, settings, session, report, ct);
        }
    }

    /// <summary>最低出击条件独立于调整策略；只读检查可提前结束，结束后恢复原部队，不扩大编制权限。</summary>
    private async Task<FormationPreflightResult> CheckMinimumRosterAsync(string serial,
        AutoFormationSession session, CancellationToken ct)
    {
        var originalTeam = session.CurrentTeam ?? FormationTeam.Series;
        var otherTeam = originalTeam == FormationTeam.Series ? FormationTeam.Free : FormationTeam.Series;
        var result = FormationPreflightResult.NoUnits;
        foreach (var team in new[] { originalTeam, otherTeam })
        {
            if (HasNoUnits(await SelectTeamAsync(serial, team, session, ct), ct)) continue;
            result = FormationPreflightResult.Ready;
            break;
        }
        if (session.CurrentTeam != originalTeam) await SelectTeamAsync(serial, originalTeam, session, ct);
        if (result == FormationPreflightResult.NoUnits) _log.Warn(NoUnitsMessage);
        return result;
    }

    /// <summary>整队无单位必须由五个栏位均为空或禁用共同证明；一个空槽、驾驶员空槽均不能判定整队为空。</summary>
    internal bool HasNoUnits(ScreenImage screen, CancellationToken ct)
        => UnitSlots.All(roi => Match(screen, _paths.EmptyUnit, roi, ct) is not null ||
                                Match(screen, _paths.LockedUnit, roi, ct) is not null);

    /// <summary>对已取消的出击提示执行一次获准的修复；未命中不操作，也不伪报编制成功。</summary>
    public async Task<AutoFormationResult> AdjustAsync(string serial, FormationIssue issue, AutoProgressSettings settings,
        AutoFormationSession session, Action<string> report, CancellationToken ct)
    {
        settings = settings.Snapshot();
        if (!settings.CanAutoForm) return AutoFormationResult.NoTarget;
        await WaitForSortieAsync(serial, ct);
        foreach (var team in new[] { FormationTeam.Series, FormationTeam.Free })
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAllowed(team, settings)) continue;
            if (session.Adjusted.Contains((issue, team))) continue;
            var screen = await SelectTeamAsync(serial, team, session, ct);
            var found = issue == FormationIssue.Terrain
                ? Match(screen, _paths.UnableToSortie, SlotsRoi, ct) is not null
                : HasEmptySlot(screen, ct);
            if (!found)
            {
                _log.Info($"{TeamName(team)}未命中{IssueName(issue)}，不调整该部队。");
                continue;
            }

            return await AdjustTeamAsync(serial, issue, team, screen, settings, session, report, ct);
        }
        _log.Warn($"获准部队中未发现可继续处理的{IssueName(issue)}，停止本关出击，保留当前画面。");
        return AutoFormationResult.NoTarget;
    }

    /// <summary>切换后重新确认准备页；此方法只选中部队，是否可编制由调用方的权限校验决定。</summary>
    private async Task<ScreenImage> SelectTeamAsync(string serial, FormationTeam team,
        AutoFormationSession session, CancellationToken ct)
    {
        await WaitForSortieAsync(serial, ct);
        if (session.CurrentTeam != team)
        {
            await _automation.TapAsync(serial, 145, team == FormationTeam.Free ? 680 : 445, ct);
            await _delay(GameTiming.RecognitionIntervalMs, ct);
            session.CurrentTeam = team;
        }
        var screen = await CaptureAsync(serial, ct);
        if (Match(screen, _paths.Sortie, SortieRoi, ct) is null)
            throw new InvalidOperationException("检查部队时未确认出击准备页，停止自动编制。");
        return screen;
    }

    /// <summary>准备页和弹窗恢复共用一次编制及确认链，资源与重试记录只由本关卡会话拥有。</summary>
    private async Task<AutoFormationResult> AdjustTeamAsync(string serial, FormationIssue issue, FormationTeam team,
        ScreenImage screen, AutoProgressSettings settings, AutoFormationSession session,
        Action<string> report, CancellationToken ct)
    {
        report(issue == FormationIssue.Initial
            ? $"历史首关初始化：正在自动编制{TeamName(team)}…"
            : $"{TeamName(team)}检测到{IssueName(issue)}，正在自动编制…");
        await ExecuteDialogAsync(serial, screen, ct);
        var preparation = await WaitForAsync(serial, _paths.Sortie, SortieRoi, "出击准备页的出击按钮", ct);
        session.Adjusted.Add((issue, team));
        // 同帧复查只报告剩余空位；是否满足至少一个单位由出击前的两队检查决定。
        if (settings.EmptySlotAction == FormationIssueAction.AutoForm && HasEmptySlot(preparation.Screen, ct))
            ReportRemainingSlots(team, report);
        report($"{TeamName(team)}已确认自动编制并返回准备页，继续检查出击编制。");
        return AutoFormationResult.Adjusted;
    }

    /// <summary>推图和大师联盟共用弹窗素材预检，不要求调用方具有推图的出击或空槽素材。</summary>
    public void ValidateDialog()
    { Load(_paths.AutoFormButton); Load(_paths.AutoFormTitle); Load(_paths.Confirm); }

    /// <summary>调用方用同帧证据确认所属编制页；这里只执行自动编制弹窗，不选择部队或决定返回页。</summary>
    public async Task ExecuteDialogAsync(string serial, ScreenImage screen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateDialog();
        var button = Match(screen, _paths.AutoFormButton, BottomButtonsRoi, ct)
            ?? throw new InvalidOperationException("未识别到底部自动编制按钮，已停止自动编制。");
        await _automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
        // 标题缺失绝不点击“确定”；每次重试都重新截图，避免弹窗改变后使用旧坐标。
        TemplateMatchResult? confirm = null;
        for (var poll = 0; poll < _maxPolls; poll++)
        {
            var dialog = await CaptureAsync(serial, ct);
            if (Match(dialog, _paths.AutoFormTitle, TitleRoi, ct) is not null)
                confirm = Match(dialog, _paths.Confirm, BottomButtonsRoi, ct, ConfirmThreshold);
            if (confirm is not null) break;
            await _delay(GameTiming.RecognitionIntervalMs, ct);
        }
        if (confirm is null) throw new InvalidOperationException("未同时确认自动编制标题与底部确定按钮，已停止。");
        await _automation.TapAsync(serial, confirm.CenterX, confirm.CenterY, ct);
        await _delay(GameTiming.RecognitionIntervalMs, ct);
    }

    /// <summary>剩余空位是编制资源有限时的正常回退，只说明将依旧出击，不伪报阵容已补齐。</summary>
    private static void ReportRemainingSlots(FormationTeam team, Action<string> report)
        => report($"{TeamName(team)}自动编制后仍存在单位或驾驶员空位，将依旧出击；出击前仍须通过至少一个单位的检查。");

    /// <summary>空槽修复共用部队权限判断；最低出击条件的只读查看不授予编制权限。</summary>
    private static bool IsAllowed(FormationTeam team, AutoProgressSettings settings)
        => team == FormationTeam.Series ? settings.AdjustSeriesTeam : settings.AdjustFreeTeam;

    /// <summary>单位或驾驶员任一空槽都表示阵容未补齐，首次检查与编制后复查采用相同区域和阈值。</summary>
    private bool HasEmptySlot(ScreenImage screen, CancellationToken ct)
        => UnitSlots.Any(roi => Match(screen, _paths.EmptyUnit, roi, ct) is not null) ||
           Match(screen, _paths.EmptyPilot, PilotSlotsRoi, ct) is not null;

    /// <summary>复用项目匹配器及透明遮罩；安全截图不匹配时不得进入后续点击。</summary>
    private TemplateMatchResult? Match(ScreenImage screen, string path, Roi roi, CancellationToken ct,
        double threshold = Threshold)
    {
        return TemplateFallback.Match(path, candidate =>
        {
            var template = Load(candidate);
            return _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
                template.Width, template.Height, threshold, roi, ct: ct);
        }, ct);
    }

    /// <summary>仅缓存本次服务使用的模板，任务结束后自然释放；不吞掉资源读取异常。</summary>
    private ScreenImage Load(string path)
    {
        if (_templates.TryGetValue(path, out var template)) return template;
        template = ImageCodec.DecodePng(File.ReadAllBytes(path));
        if (!Enumerable.Range(0, template.Width * template.Height).Any(i => template.Bgra[i * 4 + 3] >= 128))
            throw new InvalidOperationException($"模板没有有效像素：{Path.GetFileName(path)}");
        _templates[path] = template;
        return template;
    }

    /// <summary>通过共享自动化会话截屏，保留连接错误与截图规格保护。</summary>
    private async Task<ScreenImage> CaptureAsync(string serial, CancellationToken ct)
    {
        var screen = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
        if (screen.Width != 1920 || screen.Height != 1080)
            throw new InvalidOperationException("自动编制仅支持 1920×1080 横屏，已停止。");
        return screen;
    }

    /// <summary>准备页等待最多约 15 秒，不借助任意全局点击来制造页面成功。</summary>
    private async Task<(ScreenImage Screen, TemplateMatchResult Match)> WaitForAsync(
        string serial, string path, Roi roi, string name, CancellationToken ct)
    {
        for (var poll = 0; poll < _maxPolls; poll++)
        {
            var screen = await CaptureAsync(serial, ct);
            var match = Match(screen, path, roi, ct);
            if (match is not null) return (screen, match);
            await _delay(GameTiming.RecognitionIntervalMs, ct);
        }
        throw new InvalidOperationException($"未识别到{name}，已停止。");
    }

    /// <summary>日志与界面共用部队显示名称。</summary>
    private static string TeamName(FormationTeam team) => team == FormationTeam.Series ? "系列部队" : "自由部队";
    /// <summary>报告实际检测特征，不把未命中描述成异常。</summary>
    private static string IssueName(FormationIssue issue) => issue == FormationIssue.EmptySlot ? "单位或驾驶员空槽" : "不可出击标记";
}

using GGEA.App.Localization;
using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>完成仅表示已走完结算；返回主界面/主要关卡时明确要求重入作品，选关页则原地继续。</summary>
public sealed record AutoProgressBattleResult(bool Completed, string Message, bool NeedsWorkNavigation = false,
    bool ResumeRequested = false);

/// <summary>推图准备、出击前空槽检查与编制恢复桥接；复用原流程处理弹窗、取消、下载及战斗。</summary>
public sealed class AutoProgressBattleService
{
    private readonly AutomationService _automation;
    private readonly AutoFormationService _formation;
    private readonly string _preparationTemplate;
    private readonly string? _directSortieTemplate;
    private readonly string? _watchTemplate;
    private readonly string? _executeTemplate;
    private readonly StageEntryButtonRecognizer _entryButtons = new();
    private static readonly Roi EntryRoi = new(1470, 754, 450, 326);
    // 观看与 GUEST 出击确认共用底部 320 像素区域，不改变其他流程的底部通用按钮区域。
    private static readonly Roi ExecuteRoi = new(0, 760, 1920, 320);
    // 三个布尔参数分别为允许空位、允许地形不适和本关是否从观看进入；状态仅随本关传递。
    private readonly Func<WorkStageKind, bool, bool, bool, CancellationToken, Task<SortieFlowResult>> _executeSortie;

    /// <summary>战斗入口以可等待函数注入，测试可覆盖弹窗重入与有限重试而不运行真实战斗。</summary>
    public AutoProgressBattleService(AutomationService automation, AutoFormationService formation,
        string preparationTemplate, Func<WorkStageKind, bool, bool, CancellationToken, Task<SortieFlowResult>> executeSortie,
        string? directSortieTemplate = null, string? watchTemplate = null, string? executeTemplate = null)
        : this(automation, formation, preparationTemplate,
            (kind, empty, terrain, watch, token) => executeSortie(kind, empty, terrain, token),
            directSortieTemplate, watchTemplate, executeTemplate) { }

    /// <summary>将实际观看入口传给本关流程，避免普通出击误触发跳过后的执行确认。</summary>
    public AutoProgressBattleService(AutomationService automation, AutoFormationService formation,
        string preparationTemplate, Func<WorkStageKind, bool, bool, bool, CancellationToken, Task<SortieFlowResult>> executeSortie,
        string? directSortieTemplate = null, string? watchTemplate = null, string? executeTemplate = null)
    {
        _automation = automation;
        _formation = formation;
        _preparationTemplate = preparationTemplate;
        _executeSortie = executeSortie;
        _directSortieTemplate = directSortieTemplate;
        _watchTemplate = watchTemplate;
        _executeTemplate = executeTemplate;
    }

    /// <summary>在出击前检查当前策略所需素材，不能等到已经出击后才发现无法取消或编制。</summary>
    public void Validate(AutoProgressSettings settings)
    {
        var template = ImageCodec.DecodePng(File.ReadAllBytes(_preparationTemplate));
        if (template.Width > 450 || template.Height > 326)
            throw new InvalidOperationException(L.Text("出击准备模板超出指定的右下角识别区域。"));
        if (_directSortieTemplate is not null)
        {
            var direct = ImageCodec.DecodePng(File.ReadAllBytes(_directSortieTemplate));
            if (direct.Width > 450 || direct.Height > 326)
                throw new InvalidOperationException(L.Text("直接出击模板超出指定的右下角识别区域。"));
        }
        _formation.Validate(settings.Snapshot());
        if ((_watchTemplate is null) != (_executeTemplate is null))
            throw new InvalidOperationException(L.Text("观看入口必须同时提供观看和执行按钮素材。"));
        if (_watchTemplate is not null)
        {
            _entryButtons.Validate(_preparationTemplate, EntryRoi);
            if (_directSortieTemplate is not null) _entryButtons.Validate(_directSortieTemplate, EntryRoi);
            _entryButtons.Validate(_watchTemplate, EntryRoi);
            _entryButtons.Validate(_executeTemplate!, ExecuteRoi);
        }
    }

    /// <summary>编号可未知；以当前右下角真实准备、出击或观看按钮授权入口，不使用节点坐标点击。</summary>
    public async Task<AutoProgressBattleResult> RunAsync(string serial, AutoProgressSettings settings, WorkStageKind kind,
        Action<string> report, CancellationToken ct, bool preparationAlreadyOpened = false, AutoFormationSession? initialFormation = null)
    {
        settings = settings.Snapshot();
        var wait = new WaitOptions { Roi = EntryRoi, Threshold = 0.90,
            TimeoutMs = GameTiming.RecognitionTimeoutMs, PollIntervalMs = GameTiming.RecognitionIntervalMs };
        var directEntry = false;
        var watchEntry = false;
        // 强敌的挑战按钮已进入准备页；仍由 PrepareForSortieAsync 复核准备页和编制。
        if (!preparationAlreadyOpened)
        {
            var stableEntry = new StableStageEntry();
            report(L.Text("正在确认本关入口…"));
            var entry = await _automation.WaitForImageAsync(serial, L.Text("出击准备、出击或观看入口"), wait, ct,
                matchTarget: async (screen, token) =>
                {
                    directEntry = false;
                    watchEntry = false;
                    var preparation = await MatchEntryAsync(screen, _preparationTemplate, wait, token);
                    var direct = _directSortieTemplate is null ? null
                        : await MatchEntryAsync(screen, _directSortieTemplate, wait, token);
                    if (preparation is not null && direct is not null)
                    {
                        // 灰度匹配可能让同一按钮同时命中两张近似素材；仅允许位置一致且分差明确的最佳入口。
                        // 不同位置出现两个按钮或分数接近时保持未知，不能误把进入准备页视为已经出击。
                        if (Math.Abs(preparation.CenterX - direct.CenterX) > 20 ||
                            Math.Abs(preparation.CenterY - direct.CenterY) > 20 ||
                            Math.Abs(preparation.Confidence - direct.Confidence) < 0.03)
                            return stableEntry.Observe(null, "unknown");
                        if (preparation.Confidence > direct.Confidence) return stableEntry.Observe(preparation, "preparation");
                    }
                    directEntry = direct is not null;
                    if ((direct ?? preparation) is { } preferred)
                        return stableEntry.Observe(preferred, directEntry ? "direct" : "preparation");
                    // 两个常规入口均未命中后才检测观看；常规入口歧义也不能改成观看点击。
                    var watch = _watchTemplate is null ? null : await MatchEntryAsync(screen, _watchTemplate, wait, token);
                    watchEntry = watch is not null;
                    return stableEntry.Observe(watch, "watch");
                }, nextPollIntervalMs: () => stableEntry.HasCandidate ? GameTiming.ConfirmationIntervalMs : wait.PollIntervalMs);
            if (entry is null) return new(false, L.Text("关卡入口尚未就绪或画面仍在变化，已停止本关，请检查游戏画面。"));
            report(watchEntry ? L.Text("已识别观看入口，点击观看并等待底部执行按钮…")
                : directEntry ? L.Text("已识别关卡直接出击入口，点击出击并检查执行确认…") : L.Text("已识别出击准备入口，进入准备页检查部队…"));
            await _automation.TapAsync(serial, entry.CenterX, entry.CenterY, ct);
            // GUEST 入口同样先弹出执行确认；仅点击出击不能交给战斗循环，否则会停在确认弹窗。
            // 无确认模板的旧调用方保留原注入契约，产品入口始终同时提供观看与执行模板。
            if (watchEntry || directEntry && _executeTemplate is not null)
            {
                var executeWait = wait with { Roi = ExecuteRoi, TimeoutMs = GameTiming.ShortRecognitionTimeoutMs };
                var entryName = watchEntry ? L.Text("观看") : L.Text("直接出击");
                var execute = await _automation.WaitForImageAsync(serial, L.Format($"{entryName}后的执行按钮"), executeWait, ct,
                    matchTarget: (screen, token) => MatchEntryAsync(screen, _executeTemplate!, executeWait, token));
                if (execute is null) return new(false, L.Format($"{entryName}后未识别到底部 320 像素区域的执行按钮，已停止本关。"));
                report(L.Text("已识别底部执行按钮，点击后检查下载并继续关卡流程…"));
                await _automation.TapAsync(serial, execute.CenterX, execute.CenterY, ct);
            }
            if (directEntry || watchEntry)
            {
                // GUEST 等直接出击关卡没有玩家编制页，不点击部队标签，也不尝试修改预设阵容。
                var directResult = await _executeSortie(kind,
                    settings.EmptySlotAction == FormationIssueAction.ContinueSortie,
                    settings.TerrainAction == FormationIssueAction.ContinueSortie, watchEntry, ct);
                return ObservedResult(directResult) ?? new(false,
                    L.Format($"{(watchEntry ? L.Text("观看") : L.Text("直接出击"))}流程已停止：{directResult}。"));
            }
        }
        // 历史首关会话由作品流程持有，跨导航恢复保留两队确认记录；当前选择须重新确认。
        var session = initialFormation ?? new AutoFormationSession();
        session.CurrentTeam = null;
        if (initialFormation is not null)
            await _formation.InitializeBothTeamsAsync(serial, settings, session, report, ct);
        var preparationRecoveries = 0;
        // 每个问题每支部队至多编制一次，最多四次修复及第五次出击，不会无限重试。
        for (var attempt = 0; attempt <= 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // 两队皆空时游戏不一定弹出空位警告；检查与编制必须发生在出击点击之前。
            var preflight = await _formation.PrepareForSortieAsync(serial, settings, session, report, ct);
            if (preflight == FormationPreflightResult.NoUnits)
                return new(false, AutoFormationService.NoUnitsMessage);
            var sortie = await _formation.WaitForSortieAsync(serial, ct);
            report(L.Text("已确认出击前编制检查与准备页，点击出击并按设置处理后续提示…"));
            await _automation.TapAsync(serial, sortie.CenterX, sortie.CenterY, ct);
            // 只在本关确认过自动编制后回退为允许空位出击；不改写保存设置，也不改变地形策略。
            var allowEmptySlots = settings.EmptySlotAction == FormationIssueAction.ContinueSortie || session.Adjusted.Count > 0;
            var result = await _executeSortie(
                kind,
                allowEmptySlots,
                settings.TerrainAction == FormationIssueAction.ContinueSortie, false, ct);
            if (ObservedResult(result) is { } observed) return observed;
            if (result == SortieFlowResult.ProgressResumePreparation)
            {
                if (++preparationRecoveries > 3 || attempt == 4)
                    return new(false, L.Text("准备页恢复后出击仍未生效，停止重复尝试，保留当前画面。"));
                // 恢复可能改变当前显示的部队；保留已调整记录，但必须重新确认选择及最低单位条件。
                session.CurrentTeam = null;
                report(L.Text("卡顿恢复已确认出击准备页，重新检查本关部队后继续出击…"));
                continue;
            }
            FormationIssue? issue = result switch
            {
                SortieFlowResult.EmptySlotCancelled when settings.EmptySlotAction == FormationIssueAction.AutoForm => FormationIssue.EmptySlot,
                SortieFlowResult.TerrainCancelled when settings.TerrainAction == FormationIssueAction.AutoForm => FormationIssue.Terrain,
                _ => null,
            };
            if (issue is null) return new(false, L.Format($"出击流程已停止：{result}。"));
            // 已传入允许空位仍被出击流程取消，属于流程无法继续，不能反复编制或把取消伪报为完成。
            if (issue == FormationIssue.EmptySlot && session.Adjusted.Count > 0)
                return new(false, L.Text("已允许编制后的空位依旧出击，但出击流程仍返回空位取消，已停止本关；请检查当前画面。"));
            report(issue == FormationIssue.EmptySlot ? L.Text("已取消空位出击提示，检查获准部队…") : L.Text("已取消地形适性提示，检查获准部队…"));
            if (attempt == 4)
                return new(false, L.Text("自动编制未能消除出击提示或未找到可调整目标，已停止；请检查部队配置。"));
            var formation = await _formation.AdjustAsync(serial, issue.Value, settings, session, report, ct);
            if (formation == AutoFormationResult.NoTarget)
                return new(false, L.Text("自动编制未能消除出击提示或未找到可调整目标，已停止；请检查部队配置。"));
        }
        return new(false, L.Text("编制重试次数已达上限。"));
    }

    /// <summary>观看启用时复核固定文字，旧的双入口注入保持兼容；识别在工作线程执行，点击仍由主流程串行负责。</summary>
    private Task<TemplateMatchResult?> MatchEntryAsync(ScreenImage screen, string path, WaitOptions wait, CancellationToken ct)
        => _watchTemplate is null ? _automation.MatchImageAsync(screen, path, wait, ct)
            : Task.Run(() => _entryButtons.Match(screen, path, wait.Roi!.Value, wait.Threshold, ct), ct);

    /// <summary>各类入口复用原结算规则；点击按钮或跳过编制都不能代表本关已完成。</summary>
    private static AutoProgressBattleResult? ObservedResult(SortieFlowResult result)
        => result == SortieFlowResult.Stalled
            ? new(false, L.Text("连续两次 2 分钟检查仍无正常流程进展，已停止自动推图并保留当前画面。"))
            : result == SortieFlowResult.ProgressStageCompleted
                ? new(true, L.Text("本关推进完成，已确认返回当前作品选关页。"))
                : result == SortieFlowResult.ProgressWorkNavigationRequired
                    ? new(true, L.Text("本关推进完成，已确认返回主界面或主要关卡，需要重入当前作品。"), NeedsWorkNavigation: true)
                    : result is SortieFlowResult.ProgressResumeStage or SortieFlowResult.ProgressResumeWork
                        ? new(false, L.Text("卡顿恢复已确认导航页面，重新读取当前作品的真实进度。"),
                            NeedsWorkNavigation: result == SortieFlowResult.ProgressResumeWork, ResumeRequested: true)
                        : null;
}

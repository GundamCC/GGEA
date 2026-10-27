using GGEA.App.Localization;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.UI;

namespace GGEA.App.Tasks;

/// <summary>AP 消耗略过流程结果；局部失败可完成任务链，只有中断结果才停止每日队列。</summary>
public sealed record ApConsumptionResult(bool Success, bool IsInterrupted, string Message)
{
    public static ApConsumptionResult Ok(string message) => new(true, false, message);
    public static ApConsumptionResult CompletedWithFailures(string message) => new(false, false, message);
    public static ApConsumptionResult Fail(string message) => new(false, true, message);
}

/// <summary>
/// AP 消耗执行服务：按作品计划进入关卡页，把勾选的 HARD 关卡按倒序略过。
/// 只复用独立的 StageService（导航）与 StageSkipService（略过），自身不包含 UI 逻辑。
/// </summary>
public sealed class ApConsumptionService
{
    private readonly StageService _stageService;
    private readonly StageSkipService _skipService;
    private readonly IGgeaLog _log;

    public ApConsumptionService(
        StageService stageService,
        StageSkipService skipService,
        IGgeaLog log)
    {
        _stageService = stageService;
        _skipService = skipService;
        _log = log;
    }

    /// <summary>兼容独立调用：把普通计划包装为一次性任务链后执行。</summary>
    public Task<ApConsumptionResult> ExecuteAsync(
        string serial,
        IReadOnlyList<ApConsumptionWorkItem> plan,
        UiLayout mainLayout,
        UiLayout stageLayout,
        StageWorkPositionsData positions,
        IReadOnlyList<HardStageEntry> works,
        CancellationToken ct = default)
        => ExecuteAsync(
            serial,
            new ApConsumptionTaskChain(serial, plan),
            mainLayout,
            stageLayout,
            positions,
            works,
            ct);

    /// <summary>
    /// 从任务链当前游标逐个作品执行略过；同一作品内按 HARD 编号倒序。
    /// 可恢复的选关失败会记入当前链并返回主要关卡画布，随后继续下一作品。
    /// </summary>
    public async Task<ApConsumptionResult> ExecuteAsync(
        string serial,
        ApConsumptionTaskChain chain,
        UiLayout mainLayout,
        UiLayout stageLayout,
        StageWorkPositionsData positions,
        IReadOnlyList<HardStageEntry> works,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (!string.Equals(chain.DeviceSerial, serial, StringComparison.Ordinal))
        {
            return ApConsumptionResult.Fail(L.Text("AP 消耗任务链与当前设备不一致"));
        }

        ct.ThrowIfCancellationRequested();
        chain.ClearInterruption();

        // 业务中断统一保留原因；最终汇总不能覆盖成只有“成功 0、失败 0、未执行 N”。
        ApConsumptionResult Interrupt(string message)
        {
            chain.MarkInterrupted(message);
            _log.Warn($"AP消耗中断：{message}");
            return ApConsumptionResult.Fail(message);
        }

        // 每次调用都由主界面开始；即使游标位于计划中间，恢复的首部作品也必须走完整入口导航。
        var firstWorkInThisRun = true;
        while (chain.CurrentItem is { } item)
        {
            ct.ThrowIfCancellationRequested();
            var index = chain.CurrentIndex;
            var hardNames = string.Join(", ", item.SelectedHardStages.Select(stage => $"Hard{stage}"));
            _log.Info($"AP消耗 {index + 1}/{chain.Count}: {item.Work}（{hardNames}）");

            var stageResult = firstWorkInThisRun
                ? await _stageService.EnterWorkPageAsync(
                    serial,
                    mainLayout,
                    stageLayout,
                    positions,
                    works,
                    item.WorkId,
                    ct: ct)
                : await _stageService.SwitchWorkAsync(
                    serial,
                    stageLayout,
                    positions,
                    works,
                    item.WorkId,
                    ct);
            firstWorkInThisRun = false;
            if (!stageResult.Success)
            {
                // 未解锁是已确认的业务状态，不是识别失败。Logo 导航可能直接从详情页
                // 开始，尚无画布偏移；下一作品会重新识图，不能因旧坐标不可靠而中断队列。
                if (stageResult.Failure == StageNavigationFailure.WorkStageLocked)
                {
                    _log.Warn($"{item.Work} 尚未解锁，跳过并继续下一个作品");
                    chain.MarkCurrentLocked();
                    continue;
                }

                // 无法确认 HARD 仍要求可靠画布；不得将未知页面当作未解锁。
                if (stageResult.Failure == StageNavigationFailure.TitleNotRecognized &&
                    _stageService.Context.PageState == StagePageState.MainStageCanvas &&
                    _stageService.Context.IsReliable)
                {
                    _log.Warn($"{item.Work} 当前关卡无法确认，跳过并继续下一个作品");
                    chain.MarkCurrentCompleted();
                    continue;
                }

                return Interrupt(L.Format($"进入 {item.Work} 失败: {stageResult.Message}"));
            }

            var work = works.FirstOrDefault(entry => entry.Id == item.WorkId);
            if (work is null)
            {
                return Interrupt(L.Format($"作品配置缺失: id={item.WorkId}"));
            }

            if (_stageService.Context.CurrentHardIndex is not int currentHardIndex)
            {
                return Interrupt(L.Format($"无法确定 {item.Work} 当前 HARD 关卡"));
            }

            var targets = item.SelectedHardStages
                .Where(target => StageSlotPlanner.ShouldExecute(
                    work.NormalCount,
                    currentHardIndex,
                    target,
                    work.SideStories))
                .OrderByDescending(target => target)
                .ToList();
            if (targets.Count == 0)
            {
                _log.Warn($"{item.Work} 没有可执行的已解锁 HARD 关卡，跳过");
                chain.MarkCurrentCompleted();
                continue;
            }

            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                _log.Info($"{item.Work}: 选中 HARD {target}");

                var selectResult = await _stageService.SelectHardStageAsync(
                    serial,
                    stageLayout,
                    works,
                    item.WorkId,
                    target,
                    ct);
                if (!selectResult.Success)
                {
                    var failureMessage = L.Format($"{item.Work} HARD {target} 选中失败: {selectResult.Message}");
                    var returnResult = await _stageService.ReturnCurrentWorkToCanvasAsync(
                        serial,
                        stageLayout,
                        positions,
                        item.WorkId,
                        ct);
                    if (!returnResult.Success)
                    {
                        return Interrupt(
                            L.Format($"{failureMessage}；返回主要关卡画布失败: {returnResult.Message}"));
                    }

                    chain.MarkCurrentFailed(failureMessage);
                    _log.Warn($"{failureMessage}；已返回主要关卡画布并继续下一个作品");
                    break;
                }

                var skipResult = await _skipService.SkipCurrentAsync(serial, stageLayout, ct);
                if (!skipResult.Success)
                {
                    if (skipResult.IsApInsufficient)
                    {
                        return Interrupt(
                            L.Format($"{item.Work} AP不足，消耗AP任务结束"));
                    }

                    if (skipResult.IsUnavailable)
                    {
                        _log.Warn(
                            $"{item.Work} HARD {target} 略过不可用（次数上限），继续处理下一个任务");
                        continue;
                    }

                    return Interrupt(
                        L.Format($"{item.Work} HARD {target} 略过失败: {skipResult.Message}"));
                }

                // SKIP RESULT 只证明结算页出现；还须确认已经回到同一 HARD 关卡，
                // 才能抵御网络延迟或页面动画造成的“已完成”假象。
                var confirmResult = await _stageService.ConfirmCurrentHardStageAsync(
                    serial,
                    stageLayout,
                    work,
                    target,
                    ct);
                if (!confirmResult.Success)
                {
                    return Interrupt(
                        L.Format($"{item.Work} HARD {target} 略过后状态确认失败: {confirmResult.Message}"));
                }

                _log.Info($"{item.Work} HARD {target} 略过成功");
            }

            // 选关失败时已记录失败并推进游标；成功完成全部目标时才在此记录成功。
            if (chain.CurrentItem == item)
            {
                chain.MarkCurrentCompleted();
            }
        }

        var summary = chain.GetExecutionSummary();
        return chain.FailureCount == 0
            ? ApConsumptionResult.Ok(summary)
            : ApConsumptionResult.CompletedWithFailures(summary);
    }
}

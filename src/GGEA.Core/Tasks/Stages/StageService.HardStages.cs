using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class StageService
{
    /// <summary>
    /// 进入作品关卡页后优先识别当前 HARD STAGE 标题。
    /// 普通用户通常已通关作品，游戏会自动落在最后一个 HARD；标题命中时可快速确认。
    /// 标题未命中时改为具体关卡识别并向调用方报告，不再擅自返回或点击其他节点。
    /// </summary>
    private async Task<(bool Success, int? HardIndex, string Message)> ReadCurrentHardStageAsync(
        string serial,
        UiLayout stageLayout,
        HardStageEntry work,
        CancellationToken ct)
    {
        await ClearStageContinuationsAsync(serial, ct);
        if (_titleRecognizer is null)
        {
            return (true, null, "未启用标题识别");
        }

        var title = stageLayout.Require("stageTitle", "关卡布局缺少 stageTitle 锚点");
        // 作品详情页已经由“选择”按钮确认；标题连续未命中时不必等待完整 15 秒，
        // 及时切换到用户校准过的左侧关卡轨道识别流程。
        var titleWaitMs = _nodeRecognizer is null
            ? _options.TitleWaitTimeoutMs
            : Math.Min(_options.TitleWaitTimeoutMs, _options.TitleNodeFallbackWaitMs);
        var deadline = Environment.TickCount64 + Math.Max(0, titleWaitMs);
        StageTitleMatch? match = null;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await ClearStageContinuationsAsync(serial, ct);
            match = await _titleRecognizer.RecognizeAsync(serial, title.Roi, ct: ct);
            if (match is not null)
            {
                if (_endAligned)
                {
                    await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
                    var next = await _titleRecognizer.RecognizeAsync(serial, title.Roi, ct: ct);
                    if (next?.HardIndex != match.HardIndex) { match = null; continue; }
                }
                break;
            }

            await Task.Delay(Math.Max(0, _options.TitlePollIntervalMs), ct);
        }

        if (match is not null && match.HardIndex is >= 1 && match.HardIndex <= work.HardCount)
        {
            return (true, match.HardIndex, $"识别到 HARD STAGE {match.HardIndex}");
        }

        if (_progressReader is not null)
        {
            var node = await _progressReader.ReadSelectedNodeAsync(serial, work, ct);
            if (node is
                {
                    Kind: WorkStageKind.Hard,
                    Number: int hardIndex,
                    Position: not null,
                    // 只有已通过 HARD 才可能进入略过流程；未通过节点即使标签误识别也必须停止，
                    // 防止把普通关卡当成 HARD 后继续消耗 AP。
                    State: WorkStageNodeState.Completed or WorkStageNodeState.Cleared,
                } && hardIndex is >= 1 and <= 5 && hardIndex <= work.HardCount)
            {
                _log.Info($"标题识别失败，已由左侧关卡轨道确认 HARD {hardIndex}: {node.Message}");
                return (true, hardIndex, $"左侧关卡轨道识别确认 HARD {hardIndex}");
            }

            Context.Invalidate();
            return node is null
                ? (false, null, "未识别到 HARD STAGE 标题，且无法确认当前具体关卡；已停止自动操作")
                : (false, null, $"未识别到 HARD STAGE 标题；{node.Message}。已停止自动操作，请确认通关进度");
        }

        if (match is null)
        {
            return (false, null, "未识别到 HARD STAGE 标题，无法确认当前具体关卡");
        }

        return (false, null, $"识别到无效 HARD STAGE {match.HardIndex}，作品仅配置 HARD 1~{work.HardCount}");
    }

    /// <summary>关卡识别授权关闭完整底部继续文字；复用会话冷却和安全规则，未知帧、略过按钮均不点击。</summary>
    private async Task ClearStageContinuationsAsync(string serial, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, _options.TitleWaitTimeoutMs);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var png = await _automation.ScreenshotAsync(serial, ct);
            var result = await _automation.GlobalScreens.HandleContinuationAsync(serial, png, ct,
                waitingMessage: "确认当前作品关卡", allowSkip: false);
            if (result == GlobalScreenResult.None) return;
            // 动作发送或冷却阻塞都不能交给节点识别器；必须用新截图确认遮罩已经消失。
            if (Environment.TickCount64 >= deadline)
            {
                var fresh = await _automation.ScreenshotAsync(serial, ct);
                if (!await _automation.GlobalScreens.IsTapToNextVisibleAsync(fresh, ct)) return;
                Context.Invalidate();
                throw new InvalidOperationException("关卡继续遮罩未在限定时间内关闭，停止当前关卡导航；请检查游戏画面。");
            }
            await GameTiming.DelayIfPositiveAsync(Math.Max(1, _options.TitlePollIntervalMs), ct);
        }
    }

    /// <summary>
    /// 根据五槽位映射点击指定 HARD 关卡。
    /// 进入作品时已经识别过当前 HARD，后续切换不再重复识别标题，直接进入略过流程。
    /// 目标必须位于当前视口的 5 个槽位内。
    /// </summary>
    public async Task<StageNavigationResult> SelectHardStageAsync(
        string serial,
        UiLayout stageLayout,
        IReadOnlyList<HardStageEntry> works,
        int workId,
        int targetHardIndex,
        CancellationToken ct = default)
    {
        var work = works.FirstOrDefault(item => item.Id == workId);
        if (work is null)
        {
            return StageNavigationResult.Fail(
                StageNavigationFailure.WorkNotConfigured,
                $"作品未配置: id={workId}");
        }

        // 只有 1 个 HARD 关卡的作品（如 3+1 关卡）无法用五槽位映射，
        // 进入后自动选中的就是唯一 HARD 关，直接确认即可。
        if (work.HardCount == 1)
        {
            if (targetHardIndex != 1)
            {
                return StageNavigationResult.Fail(
                    StageNavigationFailure.InvalidContext,
                    "作品仅一个 HARD 关卡，目标无效");
            }

            await ClearStageContinuationsAsync(serial, ct);
            if (Context.CurrentHardIndex is not int singleHardIndex)
            {
                var singleTitle = await ReadCurrentHardStageAsync(serial, stageLayout, work, ct);
                if (!singleTitle.Success || singleTitle.HardIndex != 1)
                {
                    return StageNavigationResult.Fail(
                        StageNavigationFailure.TitleNotRecognized,
                        singleTitle.Message);
                }

                singleHardIndex = singleTitle.HardIndex.Value;
            }

            if (singleHardIndex != 1)
            {
                return StageNavigationResult.Fail(
                    StageNavigationFailure.InvalidContext,
                    $"当前 HARD {singleHardIndex}，与唯一 HARD 1 不一致");
            }

            Context.SetWorkStageList(serial, Context.Mode, workId, 1);
            _log.Info("作品仅 1 个 HARD 关卡，无需槽位切换，直接使用当前选中状态");
            return StageNavigationResult.Ok("已确认 HARD 1");
        }

        int currentHardIndex = Context.CurrentHardIndex ?? 0;
        if (_endAligned)
        {
            // 单次手势可能只移动数个节点；每次选择均复核实际编号，拖动本身不能授权消耗。
            await _automation.SwipeAsync(serial, 900, 540, 50, 540, 320, ct);
            await _automation.SwipeAsync(serial, 900, 540, 50, 540, 320, ct);
            await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);
            currentHardIndex = work.HardCount;
        }
        else if (currentHardIndex == 0)
        {
            var titleResult = await ReadCurrentHardStageAsync(serial, stageLayout, work, ct);
            if (!titleResult.Success || titleResult.HardIndex is not int readHardIndex)
            {
                return StageNavigationResult.Fail(
                    StageNavigationFailure.TitleNotRecognized,
                    titleResult.Message);
            }

            currentHardIndex = readHardIndex;
        }

        var tapResult = await TapHardStageSlotAsync(
            serial,
            stageLayout,
            work,
            currentHardIndex,
            targetHardIndex,
            ct);
        if (!tapResult.Success)
        {
            return tapResult;
        }

        // 点击后必须重新读取标题，不能只凭内存推断已切换成功。
        if (_titleRecognizer is not null)
        {
            var confirmed = await ReadCurrentHardStageAsync(serial, stageLayout, work, ct);
            if (confirmed.Success && confirmed.HardIndex == targetHardIndex)
            {
                Context.SetWorkStageList(serial, Context.Mode, workId, targetHardIndex);
                return StageNavigationResult.Ok($"已选中 HARD {targetHardIndex}");
            }

            // 右上角标题与点击前完全相同时，通常是入场动画吞掉了第一次点击。
            // 此时用左侧黄色选中节点重新确认真实位置，再按识别结果计算槽位重试一次。
            if (confirmed.Success &&
                (_endAligned || confirmed.HardIndex == currentHardIndex) &&
                (_endAligned || confirmed.Message.StartsWith("识别到 HARD STAGE", StringComparison.Ordinal)) &&
                _progressReader is not null)
            {
                var node = await _progressReader.ReadSelectedNodeAsync(serial, work, ct);
                // 末端活动槽位的位置不随当前选中项变化；两帧标题一致时，即使呼吸光圈变暗也可重选。
                int? observedHard = node is { Kind: WorkStageKind.Hard, Position: not null, HasPassed: true }
                    ? node.Number : _endAligned ? confirmed.HardIndex : null;
                if (observedHard is int recognizedHardIndex && recognizedHardIndex >= 1 && recognizedHardIndex <= work.HardCount)
                {
                    _log.Warn($"点击后复核为 HARD {recognizedHardIndex}，重新点击 HARD {targetHardIndex}");
                    var retryTap = await TapHardStageSlotAsync(
                        serial,
                        stageLayout,
                        work,
                        _endAligned ? work.HardCount : recognizedHardIndex,
                        targetHardIndex,
                        ct);
                    if (!retryTap.Success)
                    {
                        Context.SetWorkStageList(serial, Context.Mode, workId, recognizedHardIndex);
                        return retryTap;
                    }

                    confirmed = await ReadCurrentHardStageAsync(serial, stageLayout, work, ct);
                    if (confirmed.Success && confirmed.HardIndex == targetHardIndex)
                    {
                        Context.SetWorkStageList(serial, Context.Mode, workId, targetHardIndex);
                        return StageNavigationResult.Ok($"重试后已选中 HARD {targetHardIndex}");
                    }
                }
            }

            // 保留仍位于作品关卡页的可靠上下文，调用方可点击左上角返回主要关卡画布。
            var actualHardIndex = confirmed.Success && confirmed.HardIndex is int confirmedHardIndex
                ? confirmedHardIndex
                : currentHardIndex;
            if (_endAligned && !confirmed.Success) Context.Invalidate();
            else Context.SetWorkStageList(serial, Context.Mode, workId, actualHardIndex);
            return StageNavigationResult.Fail(
                StageNavigationFailure.TitleNotRecognized,
                confirmed.Success
                    ? $"点击后仍为 HARD {confirmed.HardIndex}，预期 HARD {targetHardIndex}"
                    : confirmed.Message);
        }

        Context.SetWorkStageList(serial, Context.Mode, workId, targetHardIndex);
        return StageNavigationResult.Ok($"已选中 HARD {targetHardIndex}");
    }

    internal async Task SelectBeforeHardAsync(string serial, UiLayout layout, HardStageEntry work, CancellationToken ct)
    {
        var start = StageSlotPlanner.GetViewportStart(work.NormalCount, work.HardCount, work.HardCount, work.SideStories);
        var previous = StageSlotPlanner.GetOverallIndex(work.NormalCount, 1, work.SideStories) - 1;
        if (!StageSlotPlanner.TryGetVisibleSlot(start, previous, out int slotIndex))
            throw new InvalidOperationException("HARD 1 前一关不在当前视口中。");
        var slot = layout.Require($"stageSlot{slotIndex}", "活动布局缺少前一关的槽位");
        await ClearStageContinuationsAsync(serial, ct);
        await _automation.TapAsync(serial, slot.CenterX, slot.CenterY, ct);
        await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);
        await _automation.SwipeAsync(serial, 180, 540, 585, 540, 400, ct);
        await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);
        await ConfirmBeforeHardAsync(serial, work, ct);
    }

    internal async Task ConfirmBeforeHardAsync(string serial, HardStageEntry work, CancellationToken ct)
    {
        int previous = StageSlotPlanner.GetOverallIndex(work.NormalCount, 1, work.SideStories) - 1;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var node = await _progressReader!.ReadSelectedNodeAsync(serial, work, ct);
            if (node is { HasPassed: true } && node.Position?.OverallIndex == previous) return;
            await GameTiming.DelayIfPositiveAsync(GameTiming.ConfirmationIntervalMs, ct);
        }
        throw new InvalidOperationException("未确认 HARD 1 前一关的选中编号与通关状态。");
    }

    /// <summary>根据当前 HARD 位置计算目标槽位并点击；用于首次选择和动画阻挡后的单次重试。</summary>
    private async Task<StageNavigationResult> TapHardStageSlotAsync(
        string serial,
        UiLayout stageLayout,
        HardStageEntry work,
        int currentHardIndex,
        int targetHardIndex,
        CancellationToken ct)
    {
        UiAnchor slot;
        string slotDescription;
        if (work.HardCount == 5 && targetHardIndex == 1)
        {
            // 五个 HARD 的作品会把 HARD 1 固定显示在屏幕左边缘，不能按当前关卡推算普通槽位。
            slot = stageLayout.Require("fiveHardStage1",
                "关卡布局缺少 fiveHardStage1 锚点");
            slotDescription = "五 HARD 专用位置";
        }
        else
        {
            if (!StageSlotPlanner.TryGetSlotIndex(
                    work.NormalCount,
                    work.HardCount,
                    currentHardIndex,
                    targetHardIndex,
                    out var slotIndex,
                    work.SideStories))
            {
                return StageNavigationResult.Fail(
                    StageNavigationFailure.InvalidContext,
                    $"目标 HARD {targetHardIndex} 不在当前五槽位视口内");
            }

            slot = stageLayout.Require($"stageSlot{slotIndex}",
                $"关卡布局缺少 stageSlot{slotIndex} 锚点");
            slotDescription = $"槽位 {slotIndex}";
        }

        await ClearStageContinuationsAsync(serial, ct);
        _log.Info($"点击 HARD {targetHardIndex} {slotDescription}: ({slot.CenterX},{slot.CenterY})");
        await _automation.TapAsync(serial, slot.CenterX, slot.CenterY, ct);
        await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);
        return StageNavigationResult.Ok($"已点击 HARD {targetHardIndex}");
    }

    /// <summary>
    /// 略过结算返回关卡页后再次确认仍处于预期 HARD。
    /// 这一步防止网络延迟、入场动画或漏点导致流程误以为略过已经完成。
    /// </summary>
    public async Task<StageNavigationResult> ConfirmCurrentHardStageAsync(
        string serial,
        UiLayout stageLayout,
        HardStageEntry work,
        int expectedHardIndex,
        CancellationToken ct = default)
    {
        var confirmed = await ReadCurrentHardStageAsync(serial, stageLayout, work, ct);
        if (!confirmed.Success || confirmed.HardIndex != expectedHardIndex)
        {
            Context.Invalidate();
            return StageNavigationResult.Fail(
                StageNavigationFailure.TitleNotRecognized,
                confirmed.Success
                    ? $"略过后关卡为 HARD {confirmed.HardIndex}，预期 HARD {expectedHardIndex}"
                    : confirmed.Message);
        }

        Context.SetWorkStageList(serial, Context.Mode, work.Id, expectedHardIndex);
        return StageNavigationResult.Ok($"已确认仍处于 HARD {expectedHardIndex}");
    }
}

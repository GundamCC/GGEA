using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>主线与历史共用的原页节点推进；页面几何及身份校验由注入的识别器负责，不重新导航作品。</summary>
public sealed class StageProgressReader(AutomationService automation, IStageNodeRecognizer nodes,
    IGgeaLog log, StageNavigationOptions options, Func<string, CancellationToken, Task> clearContinuations)
{
    /// <summary>原页读取进度；保留旧已通关选中节点时通过 NEXT 选关，再复核实际选中结果。</summary>
    public async Task<StageNodeRecognition> ReadCurrentWorkForProgressAsync(string serial,
        HardStageEntry work, CancellationToken ct)
    {
        // 本读取器不保存 AP 导航上下文；历史适配器在每张节点截图上额外复核作品身份。
        if (nodes is null) throw new InvalidOperationException("推图节点识别未配置。");
        if (nodes is StageNodeRecognizer recognizer) recognizer.ValidateForProgress();
        // 编号是辅助观察，单次两帧识别失败后交由真实出击入口处理，不能在这里反复等待并终止队列。
        var node = await ReadSelectedNodeAsync(serial, work, ct);
        if (node is not null && (node.Position is not null || node.HasPassed || node.State == WorkStageNodeState.Locked))
        {
            // 只有确证仍选着已通关节点才需要 NEXT；观察缺失不触发选关操作。
            if (node.HasPassed &&
                !(node.Kind == WorkStageKind.Hard && node.Number == work.HardCount))
                return await SelectNextForProgressAsync(serial, work, node, ct, requireConfirmation: node.Position is null);
            return node;
        }
        // 游戏负责自动选中下一关。观察失败不能触发 NEXT 重选，更不能阻止后续识别实际出击按钮。
        if (nodes is StageNodeRecognizer failedRecognizer)
            await failedRecognizer.SaveLastFailureAsync(serial);
        log.Info("当前关卡编号未识别，沿用游戏当前选择，继续检查出击入口。");
        return StageNodeRecognition.Unidentified;
    }

    /// <summary>队列已确认本关结算后仍选着同一关时，重新确认当前编号，再以真实 NEXT 推进；不靠 CLEAR 字样触发。</summary>
    public async Task<StageNodeRecognition> AdvanceAfterBattleAsync(string serial, HardStageEntry work,
        StageNodeRecognition completed, CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();
        var current = await ReadSelectedNodeAsync(serial, work, ct);
        if (current?.Position is null || completed.Position is null)
            throw new InvalidOperationException("结算后未能复核当前关卡，停止重复出击，不猜测下一关。");
        // 游戏可能在读取间隙完成自动选关；此时保留其真实选择，不能额外跳过一关。
        if (current.Position != completed.Position || current.State == WorkStageNodeState.Locked ||
            (current.Kind == WorkStageKind.Hard && current.Number == work.HardCount)) return current;
        return await SelectNextForProgressAsync(serial, work, current, ct, requireConfirmation: true);
    }

    /// <summary>已通关状态或本轮结算证据触发两帧 NEXT 选关；同关恢复必须复核新编号，避免识别缺失引起重复出击。</summary>
    private async Task<StageNodeRecognition> SelectNextForProgressAsync(string serial, HardStageEntry work,
        StageNodeRecognition current, CancellationToken ct, bool requireConfirmation = false)
    {
        if (nodes is not IStageNextNodeRecognizer nextRecognizer) return current;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // 公共截图入口校验有效图像；标记出现也不能绕过新帧复核与取消，网络诊断由异常入口负责。
            var firstScreen = await automation.CaptureScreenAsync(serial, ct);
            var first = await Task.Run(() => nextRecognizer.RecognizeNext(firstScreen, work, ct), ct);
            if (first is not null)
            {
                await DelayAsync(GameTiming.ConfirmationIntervalMs, ct);
                var nextScreen = await automation.CaptureScreenAsync(serial, ct);
                var next = await Task.Run(() => nextRecognizer.RecognizeNext(nextScreen, work, ct), ct);
                if (next is not null && first.Position == next.Position && next.Position != current.Position &&
                    Math.Abs(first.MarkerRegion.CenterX - next.MarkerRegion.CenterX) <= 8 &&
                    Math.Abs(first.MarkerRegion.CenterY - next.MarkerRegion.CenterY) <= 12 &&
                    Math.Abs(first.NumberRegion.CenterX - next.NumberRegion.CenterX) <= 8 &&
                    Math.Abs(first.NumberRegion.CenterY - next.NumberRegion.CenterY) <= 12 &&
                    WorkStageSequencePlanner.TryGetPosition(work.NormalCount, work.SideStories, work.HardCount,
                        next.Position.Kind, next.Position.Number, out var expected) && expected == next.Position)
                {
                    ct.ThrowIfCancellationRequested();
                    var reason = requireConfirmation ? $"结算后仍选中 {current.Kind} {current.Number}" : $"当前 {current.Kind} {current.Number} 已通过";
                    log.Info($"{reason}，识别到 NEXT {next.Position.Kind} {next.Position.Number}，原页点击编号 ({next.CenterX},{next.CenterY})。");
                    await automation.TapAsync(serial, next.CenterX, next.CenterY, ct);
                    await DelayAsync(options.PageSettleMs, ct);
                    StageNodeRecognition? lastSelected = null;
                    for (var confirmation = 0; confirmation < 5; confirmation++)
                    {
                        var selected = await ReadSelectedNodeAsync(serial, work, ct);
                        lastSelected = selected;
                        if (selected?.Position == next.Position && selected.State == WorkStageNodeState.Incomplete)
                            return selected;
                        await DelayAsync(options.TitlePollIntervalMs, ct);
                    }
                    if (nodes is StageNodeRecognizer failed)
                        await failed.SaveLastFailureAsync(serial);
                    if (lastSelected?.Position is not null)
                        throw new InvalidOperationException("点击 NEXT 后确认仍为已完成、锁定或其他关卡，停止重复出击。");
                    if (requireConfirmation)
                        throw new InvalidOperationException("点击 NEXT 后未能复核新的关卡编号，停止重复出击。");
                    log.Info("NEXT 点击后的编号暂未确认，继续检查游戏当前出击入口。");
                    return StageNodeRecognition.Unidentified;
                }
                log.Info("NEXT 标记或目标编号尚未稳定，等待重新识别。");
            }
            await DelayAsync(options.TitlePollIntervalMs, ct);
        }
        log.Info("当前页未找到可确认的 NEXT 目标，不执行恢复点击。");
        return current;
    }

    /// <summary>共享继续遮罩处理后读取节点，避免动画或奖励遮挡授权选关。</summary>
    private async Task<StageNodeRecognition?> ReadSelectedNodeAsync(string serial, HardStageEntry work, CancellationToken ct)
    {
        await clearContinuations(serial, ct);
        return await nodes.RecognizeSelectedAsync(serial, work, ct);
    }
    /// <summary>保留既有非正数延时配置的语义，不把零延时测试变成异常。 </summary>
    private static Task DelayAsync(int milliseconds, CancellationToken ct)
        => milliseconds <= 0 ? Task.CompletedTask : Task.Delay(milliseconds, ct);
}

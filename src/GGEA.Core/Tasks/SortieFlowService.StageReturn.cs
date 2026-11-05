using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    /// <summary>
    /// HARD 推图借用刷本结算，但退出点击不等于选关页已加载；实际入口或可信选中节点出现后交回队列，标题不是唯一门槛。
    /// 普通刷本仍保留原返回语义；失败结果原样传递，不补点、不猜测下一关，也不提前计为通关。
    /// </summary>
    public async Task<SortieFlowResult> ConfirmHardProgressReturnAsync(string serial, SortieFlowResult result,
        string titleTemplate, CancellationToken ct = default, int timeoutMs = 60000, int pollIntervalMs = GameTiming.RecognitionIntervalMs,
        TimeProvider? timeProvider = null,
        Func<ScreenImage, CancellationToken, bool>? confirmSelectedNode = null,
        Func<ScreenImage, CancellationToken, Task<bool>>? confirmPreparation = null)
    {
        ct.ThrowIfCancellationRequested();
        if (result != SortieFlowResult.AllRoundsCompleted) return result;
        var title = new SelectStageTitleRecognizer(titleTemplate);
        var clock = timeProvider ?? TimeProvider.System;
        var started = clock.GetTimestamp();
        var preparationFrames = 0;
        _log.Info("HARD 结算已发送返回点击，等待实际选择关卡页，确认后再读取下一关。");
        while (clock.GetElapsedTime(started).TotalMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            // 公共截图入口保留网络保护；规格检查与推图主循环一致，过渡画面不交给编号识别器。
            var screen = await _automation.CaptureScreenAsync(serial, ct);
            if (screen.Width != 1920 || screen.Height != 1080)
                throw new InvalidOperationException("自动推图仅支持 1920×1080 横屏，已停止。");
            if (await HandleTerminalStateAsync(serial, screen, ct) is { } terminal) return terminal;
            ct.ThrowIfCancellationRequested();
            // 与 NORMAL 相同，准备页专属证据优先；连续新帧确认后交回编制会话，不计为通关。
            if (confirmPreparation is not null && await confirmPreparation(screen, ct))
            {
                if (++preparationFrames >= 2) return SortieFlowResult.ProgressResumePreparation;
                await Task.Delay(Math.Clamp(pollIntervalMs, 1, GameTiming.ConfirmationIntervalMs), ct);
                continue;
            }
            preparationFrames = 0;
            if (IsProgressStageReady(screen, ct, confirmSelectedNode))
            {
                _log.Info(title.Recognize(screen, ct)
                    ? "HARD 结算后已确认选择关卡页，开始读取当前真实节点。"
                    : "HARD 结算后标题暂未识别，已通过实际入口或选中关卡确认返回，开始读取真实节点。");
                return SortieFlowResult.ProgressStageCompleted;
            }
            // 结算后的功能解放等继续页也属于推图；沿用同一冷却和完整文字门槛，点击不代表返回成功。
            if (await HandleSettlementContinuationAsync(serial, screen, ct, forAutoProgress: true)) continue;
            await Task.Delay(Math.Max(1, pollIntervalMs), ct);
        }
        ct.ThrowIfCancellationRequested();
        _log.Warn($"HARD 结算后等待选择关卡页超时（{timeoutMs}ms），停止本次推图；尚未确认返回，不计为完成。");
        return SortieFlowResult.ProgressTimeout;
    }
}

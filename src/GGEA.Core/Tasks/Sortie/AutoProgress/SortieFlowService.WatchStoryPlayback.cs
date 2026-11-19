using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    /// <summary>观看关卡的中央点击、SKIP 与二次确认共用单关轮询；每个动作结束后交回主循环重新截图。</summary>
    private sealed class WatchStoryPlayback(SortieFlowService flow, string serial, TimeProvider clock,
        int actionDelayMs, int pollIntervalMs, int skipWaitTimeoutMs)
    {
        private long _lastCycle = clock.GetTimestamp();
        private bool _checkSkip;
        private long? _executeDeadline;

        public bool AwaitingConfirmation => _executeDeadline.HasValue;

        public void ResetCycle()
        {
            _executeDeadline = null;
            _checkSkip = false;
            _lastCycle = clock.GetTimestamp();
        }

        // 返回 true 只说明已点击执行；主循环仍需确认实际返回页。
        public async Task<bool> ConfirmSkipAsync(ScreenImage screen,
            Func<TemplateMatchResult, string, SortiePage, Task> click, CancellationToken ct)
        {
            var execute = flow._progressButtons.Match(screen, WatchExecuteTemplate, WatchSkipExecuteRoi,
                StageSortieTemplateThreshold, ct);
            if (execute is not null)
            {
                await click(execute, "观看跳过后的执行按钮", SortiePage.Skip);
                ResetCycle();
                return true;
            }
            if (Environment.TickCount64 >= _executeDeadline)
            {
                flow._log.Info("观看跳过后暂未识别到底部执行按钮，结束本轮等待，下一轮继续检查。");
                ResetCycle();
            }
            await Task.Delay(Math.Max(1, pollIntervalMs), ct);
            return false;
        }

        // 中央点击只用于唤出菜单；仅在后续截图实际命中 SKIP 时才开启确认等待。
        public async Task<bool> AdvanceAsync(ScreenImage screen, ProgressTemplateMatcher vision, string skipTemplate,
            Func<TemplateMatchResult, string, SortiePage, Task> click, CancellationToken ct)
        {
            var skip = _checkSkip ? vision.Match(screen, skipTemplate, ProgressSkipRoi, StageSortieTemplateThreshold, ct) : null;
            if (skip is not null)
            {
                await click(skip, "SKIP，略过观看剧情", SortiePage.Skip);
                _executeDeadline = Environment.TickCount64 + Math.Max(0, skipWaitTimeoutMs);
                _checkSkip = false;
                return true;
            }
            if (clock.GetElapsedTime(_lastCycle) >= TimeSpan.FromSeconds(15))
            {
                await flow._automation.TapAsync(serial, screen.Width / 2, screen.Height / 2, ct);
                _lastCycle = clock.GetTimestamp();
                _checkSkip = true;
                flow._log.Info("观看关卡十五秒检查：已点击画面中央，重新截图识别右上角 SKIP 与返回入口。");
                await Task.Delay(Math.Max(1, actionDelayMs), ct);
                return false;
            }
            await Task.Delay(Math.Max(1, pollIntervalMs), ct);
            return false;
        }
    }
}

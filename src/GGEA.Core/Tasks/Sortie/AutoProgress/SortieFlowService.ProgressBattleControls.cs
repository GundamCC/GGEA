using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    // 点击间隔与重试次数属于本次出击，退出关卡时随轮询一起释放。
    private sealed class ProgressBattleControls(SortieFlowService flow, string serial,
        SortieStallWatchdog watchdog, int actionDelayMs, int pollIntervalMs)
    {
        private long _nextAutoClick;
        private long _nextSpeedClick;
        private long? _unknownAutoSince;
        public int AutoToggleAttempts { get; set; }

        // 返回 true 表示本帧已处理或正在等候复核，调用方应重新截图。
        public async Task<bool> TryConfigureAsync(ScreenImage screen, AutoButtonMatch? auto, bool menuVisible, CancellationToken ct)
        {
            if (auto?.State == AutoButtonState.All)
            {
                AutoToggleAttempts = 0;
                // 每次常规轮询的健康证据都有效，避免两分钟检查恰逢动画隐藏控件时误停长战斗。
                // 只续期无进展检测，不改变本关总截止时间，也不提供通关证据。
                watchdog.ConfirmProgress();
            }
            if (auto?.State == AutoButtonState.Unknown)
            {
                if (_unknownAutoSince is null)
                {
                    _unknownAutoSince = Environment.TickCount64;
                    flow._log.Warn("自动推图已识别 AUTO 字形但状态证据不足，暂不切换，等待重新确认。");
                }
            }
            else _unknownAutoSince = null;
            if (auto is not null && (auto.State is AutoButtonState.Close or AutoButtonState.Enemy) &&
                Environment.TickCount64 >= _nextAutoClick)
            {
                // 敌军→关闭→全部自动最多两次；额外一次用于未生效点击，仍失败则明确停止。
                if (AutoToggleAttempts >= 3)
                {
                    await Task.Delay(Math.Max(1, pollIntervalMs), ct);
                    return true; // 等两分钟恢复检测后才允许新的有限重试，仍不点击未知 AUTO 状态。
                }
                flow.SaveAutoToggleEvidence(screen, auto);
                await flow._automation.TapAsync(serial, AutoButtonCenter.X, AutoButtonCenter.Y, ct);
                watchdog.Observe(SortiePage.Battle);
                AutoToggleAttempts++;
                flow._log.Info($"自动推图识别 AUTO 状态为 {auto.State}（字形 {auto.Match.Confidence:P1}，区域 {auto.Match.Region}），切换一次并在下一帧复查。");
                _nextAutoClick = Environment.TickCount64 + Math.Max(1, actionDelayMs);
                await Task.Delay(Math.Max(1, actionDelayMs), ct);
                return true;
            }
            if (!menuVisible && Environment.TickCount64 >= _nextSpeedClick)
            {
                // 双箭头本身确认按钮身份，不再因 AUTO 暂时未识别而漏设速度；MENU 显示时不切换。
                var speed = flow.RecognizeSpeed(screen, ct);
                if (speed?.State == SpeedButtonState.Off)
                {
                    await flow._automation.TapAsync(serial, SpeedButtonCenter.X, SpeedButtonCenter.Y, ct);
                    flow._speedVerificationSerial = serial;
                    flow._log.Info("自动推图颜色确认二倍速关闭，已点击，等待后续画面确认开启。");
                    _nextSpeedClick = Environment.TickCount64 + Math.Max(1, actionDelayMs);
                    await Task.Delay(Math.Max(1, actionDelayMs), ct);
                    return true;
                }
            }
            return false;
        }
    }
}

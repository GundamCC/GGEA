using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>供独立阶段调用的有界战斗等待；真实运行入口由 Session 文件持有持续恢复状态。</summary>
public sealed partial class MasterLeagueService
{
    private const int BattleObservationRounds = 600;

    /// <summary>同帧并行收集的只读证据，动作仍由阶段状态机按安全、结算、下载、挑战/SKIP 顺序裁决。</summary>
    private sealed record BattleObservation(ScreenImage Screen, MasterLeagueBattleFrame Battle,
        TemplateMatchResult? DownloadHint, int? ChallengeRemaining);

    /// <summary>截图只取一次；战斗、下载和准备次数同时检查。网络及更新弹窗只在会话停滞后诊断。</summary>
    private async Task<BattleObservation> CaptureBattleObservationAsync(string serial, IMasterLeagueVision vision,
        bool preparing, CancellationToken ct, bool retryOnFailure = true)
    {
        // 模板字典初始化在并行观察之前完成；各检查只读共享素材与截图。
        vision.Validate();
        var captured = await _automation.CaptureAndObserveAsync(serial, async (screen, token) =>
        {
            if (screen.Width != 1920 || screen.Height != 1080)
                return new BattleObservation(screen, new(false, null, null), null, null);
            var battle = Task.Run(() => vision.ReadBattle(screen, token), token);
            var download = _automation.MatchImageAsync(screen, _downloadHintPath,
                new WaitOptions { Roi = SortieFlowService.DownloadHintRoi(screen), Threshold = .90,
                    AllowGlobalContinuation = false }, token);
            var remaining = preparing ? Task.Run(() => vision.ReadChallengeRemaining(screen, token), token)
                : Task.FromResult<int?>(null);
            await Task.WhenAll(battle, download, remaining);
            return new BattleObservation(screen, await battle, await download, await remaining);
        }, ct, retryOnFailure);
        return captured.Observation;
    }

    /// <summary>每轮在同一帧同时检查结果与 SKIP，结算优先；没有 SKIP 也能直接完成战斗阶段。</summary>
    internal async Task<MasterLeagueResult> WaitForBattleResultAsync(string serial, IMasterLeagueVision vision, CancellationToken ct)
    {
        var skipSent = false;
        var downloadSent = false;
        for (var poll = 0; poll < BattleObservationRounds; poll++)
        {
            var frame = await CaptureBattleObservationAsync(serial, vision, false, ct);
            var screen = frame.Screen;
            if (screen.Width != 1920 || screen.Height != 1080)
                throw new InvalidOperationException("大师联盟需要 1920×1080 横屏画面，已停止点击。");
            var observed = frame.Battle;
            if (observed.ResultVisible && observed.ReturnButton is not null)
                return new(true, "已通过 OCR 确认战斗结算及返回按钮。");
            // 挑战后可能先弹出单位数据下载；与战斗共用当前帧，不能先固定等待下载而漏掉短战斗。
            var downloadHint = frame.DownloadHint;
            if (!observed.ResultVisible && downloadHint is not null)
            {
                var buttons = await Task.Run(() => _downloadDialog.Recognize(screen), ct);
                if (!_allowDownload)
                {
                    if (buttons.Cancel is not null)
                        await _automation.TapAsync(serial, buttons.Cancel.CenterX, buttons.Cancel.CenterY, ct);
                    _log.Warn("大师联盟挑战后检测到下载弹窗，未允许下载游戏数据，已停止。");
                    return new(false, "未允许下载游戏数据，大师联盟流程已停止。");
                }
                if (!downloadSent && buttons.Download is not null)
                {
                    await _automation.TapAsync(serial, buttons.Download.CenterX, buttons.Download.CenterY, ct);
                    downloadSent = true;
                    _log.Info("大师联盟挑战后检测到下载弹窗，已点击下载，继续等待战斗或结算。");
                }
                if (poll < BattleObservationRounds - 1) await _delay(1000, ct);
                continue;
            }
            if (!observed.ResultVisible && !skipSent && observed.Skip is not null)
            {
                // 旧帧坐标曾误点结算页；实际输入前立即重取一帧并再次同时识别结果和 SKIP。
                frame = await CaptureBattleObservationAsync(serial, vision, false, ct);
                screen = frame.Screen;
                if (screen.Width != 1920 || screen.Height != 1080)
                    throw new InvalidOperationException("大师联盟需要 1920×1080 横屏画面，已停止点击。");
                observed = frame.Battle;
                if (observed.ResultVisible && observed.ReturnButton is not null)
                    return new(true, "战斗已自行结束，已确认结算，未点击 SKIP。");
                if (!observed.ResultVisible && frame.DownloadHint is null && observed.Skip is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    await _automation.TapAsync(serial, observed.Skip.CenterX, observed.Skip.CenterY, ct);
                    skipSent = true;
                    _log.Info("已在新帧确认并点击 SKIP，继续同时观察结算；本场不重复点击 SKIP。");
                }
            }
            if (poll < BattleObservationRounds - 1) await _delay(1000, ct);
        }
        return Fail("十分钟内未确认大师联盟结算，已停止，不重复挑战或点击 SKIP。");
    }

}

using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    /// <summary>刷本入口阶段决定能否补点上轮再次出击；结算阶段绝不能把恢复当作新一轮开始。</summary>
    private enum FarmRecoveryStage { NextBattle, Battle, Reward, Result }

    /// <summary>每轮共用恢复计时与设置快照，跨继续/奖励等待不重置失败预算，不影响其他任务。</summary>
    private sealed class FarmRecoverySession(
        bool allowDownload, bool allowEmpty, bool allowTerrain, string? sortieTemplate, TimeProvider? time,
        bool forAutoProgress = false)
    {
        internal SortieStallWatchdog Watchdog { get; } = new(time);
        internal bool AllowDownload { get; } = allowDownload;
        internal bool AllowEmpty { get; } = allowEmpty;
        internal bool AllowTerrain { get; } = allowTerrain;
        internal bool ForAutoProgress { get; } = forAutoProgress;
        internal string SortieTemplate { get; } = sortieTemplate ?? ConfigService.ResolvePath(new AppConfig().SortieTemplate);
        private AutoButtonRecognizer? _auto;

        /// <summary>按需缓存 AUTO 素材供战斗健康观察与恢复共用，不把任意灰底判断成关闭。</summary>
        internal AutoButtonRecognizer Auto(TemplateMatcher matcher)
        {
            var paths = AutoProgressFlowTemplates.FromDefaults();
            return _auto ??= new(matcher, paths.AutoAll, paths.AutoEnemy, paths.AutoClose);
        }
    }

    /// <summary>只允许调用方重新识别并补点继续；没有任何恢复分支可直接返回“本轮完成”。</summary>
    private sealed record FarmRecoveryResult(bool Acted = false, bool RetryContinue = false, SortieFlowResult? Terminal = null);

    /// <summary>刷本的两分钟恢复入口；停滞后先诊断错误弹窗再恢复，点击后必须重新截图。</summary>
    private async Task<FarmRecoveryResult> RecoverFarmIfStalledAsync(string serial, ScreenImage screen,
        FarmRecoverySession recovery, FarmRecoveryStage stage, CancellationToken ct)
    {
        // 战斗/加载期间只做低频 AUTO 检查；看不到控件不能视为关闭，也不能缩短正常长战斗总时限。
        if (stage == FarmRecoveryStage.Battle || stage == FarmRecoveryStage.NextBattle &&
            !TryRecognizeResultPageBeforeContinue(screen,
                Match(screen, _continueTemplatePath, ContinueButtonRoi(screen), ContinueTemplateThreshold), out _, out _))
        {
            if (!recovery.Watchdog.TryInspect()) return new();
            _log.Info("自动刷本连续 2 分钟无阶段进展，检查 AUTO 是否为蓝色全部自动。");
            var configured = await EnsureAllAutoAsync(serial, screen, recovery.Auto(_matcher),
                GGEA.App.Automation.GameTiming.RecognitionIntervalMs, ct);
            if (configured.Confirmed) recovery.Watchdog.ConfirmProgress();
            return new(Acted: configured.Acted, Terminal: configured.Terminal);
        }
        if (!recovery.Watchdog.TryInspect()) return new();
        _log.Info("自动刷本连续 2 分钟无阶段进展，重新检测当前画面以恢复运行；保持本轮与剩余次数。");
        var currentContinue = Match(screen, _continueTemplatePath, ContinueButtonRoi(screen), ContinueTemplateThreshold);
        var resultPage = TryRecognizeResultPageBeforeContinue(screen, currentContinue, out var again, out _);
        var terrain = MatchIfConfigured(screen, _terrainTemplatePath, TerrainHintRoi(screen), StageSortieTemplateThreshold);
        var empty = MatchEmptySlotDialog(screen);
        var download = MatchIfConfigured(screen, _sortieDownloadTemplatePath, DownloadHintRoi(screen), StageSortieTemplateThreshold);
        var tap = await MatchTapToNextAsync(screen, ct, recovery.ForAutoProgress);
        var auto = recovery.Auto(_matcher).Recognize(screen, AutoToggleRoi, ct);
        var page = terrain is not null ? SortiePage.Terrain : empty is not null ? SortiePage.EmptySlots :
            download is not null ? SortiePage.Download : resultPage ? SortiePage.Result :
            currentContinue is not null ? SortiePage.Continue : tap is not null ? SortiePage.TapToNext :
            auto is not null ? SortiePage.Battle : SortiePage.Unknown;
        recovery.Watchdog.ObserveDuringInspection(page);
        if (page == SortiePage.Battle && auto!.State == AutoButtonState.All)
        {
            recovery.Watchdog.ConfirmProgress();
            _log.Info("恢复检测确认刷本战斗仍为全部自动，保持当前战斗并等待真实结算。");
            return new();
        }
        if (recovery.Watchdog.Attempts >= SortieStallWatchdog.MaximumAttempts)
        {
            _log.Warn("自动刷本连续两次 2 分钟检查仍无正常流程进展，已停止当前任务并保留画面，不再点击或计为完成。");
            return new(Terminal: SortieFlowResult.Stalled);
        }
        if (terrain is not null)
        {
            var result = await HandleTerrainDialogAsync(serial, screen, recovery.AllowTerrain, ct);
            return result switch
            {
                TerrainHandlingResult.Cancelled => new(Terminal: SortieFlowResult.TerrainCancelled),
                TerrainHandlingResult.Timeout => new(Terminal: SortieFlowResult.TerrainTimeout),
                _ => new(Acted: true),
            };
        }
        if (empty is not null)
        {
            var result = await HandleEmptySlotDialogAsync(serial, screen, recovery.AllowEmpty, ct);
            return result switch
            {
                EmptySlotHandlingResult.Cancelled => new(Terminal: SortieFlowResult.EmptySlotCancelled),
                EmptySlotHandlingResult.Timeout => new(Terminal: SortieFlowResult.EmptySlotTimeout),
                _ => new(Acted: true),
            };
        }
        if (download is not null)
        {
            var dialog = _downloadDialogService.Recognize(screen);
            var button = recovery.AllowDownload ? dialog.Download : dialog.Cancel;
            if (button is null) return new();
            await _automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
            _log.Info(recovery.AllowDownload ? "恢复已重新识别并点击下载，继续等待本轮加载。" : "恢复遵守未允许下载设置，取消并停止刷本。");
            return recovery.AllowDownload ? new(Acted: true) : new(Terminal: SortieFlowResult.DownloadCancelled);
        }
        if (resultPage && stage == FarmRecoveryStage.NextBattle)
        {
            // 此页属于已计数的上一轮。补点后仍必须等待新开场/战斗，绝不再处理同页结算或递减次数。
            await _automation.TapAsync(serial, again!.CenterX, again.CenterY, ct);
            _log.Info("恢复确认仍停在上一轮结算页，补点再次出击；等待新战斗证据，不重复计数。");
            return new(Acted: true);
        }
        if (page == SortiePage.Battle && auto!.State is AutoButtonState.Close or AutoButtonState.Enemy)
        {
            var configured = await EnsureAllAutoAsync(serial, screen, recovery.Auto(_matcher),
                GGEA.App.Automation.GameTiming.RecognitionIntervalMs, ct);
            if (configured.Confirmed) recovery.Watchdog.ConfirmProgress();
            return new(Acted: configured.Acted, Terminal: configured.Terminal);
        }
        if (page == SortiePage.Continue && stage is FarmRecoveryStage.Reward or FarmRecoveryStage.Result)
        {
            _log.Info("恢复确认仍为继续页，允许按新截图补点一次；奖励与双按钮结算门禁保持不变。");
            return new(RetryContinue: true);
        }
        _log.Info($"刷本恢复观察到{RecoveryPageName(page)}，继续有界等待；证据不足时不执行额外点击。");
        return new();
    }

    /// <summary>结算途中出现准备页表示阶段已经偏离；不能空等旧奖励，也不能推断完成并重新出击。</summary>
    private bool StopForUnexpectedFarmPreparation(ScreenImage screen, FarmRecoverySession recovery, CancellationToken ct)
    {
        if (recovery.ForAutoProgress || !IsFarmPreparation(screen, recovery, ct)) return false;
        _log.Warn("自动刷本结算尚未确认，但已识别到出击准备页；停止本次刷本并保留画面，不计完成、不重新出击。");
        return true;
    }

    /// <summary>准备页恢复必须同时命中带 AP 的出击按钮及自动编制按钮，排除普通选关入口。</summary>
    private bool IsFarmPreparation(ScreenImage screen, FarmRecoverySession recovery, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 结算轮询只需排查右下准备页，不扫描整条底栏；先验证较小的自动编制按钮，
        // 再用独立的橙色出击按钮确认，避免每帧扩大搜索拖慢正常结算。
        return Match(screen, ConfigService.ResolvePath("resources/templates/stage/sortie/button_auto-form.png"),
                new Roi(1000, 905, 450, 175), StageSortieTemplateThreshold) is not null &&
            Match(screen, recovery.SortieTemplate, new Roi(1440, 905, 480, 175), StageSortieTemplateThreshold) is not null;
    }
}

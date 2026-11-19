using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    /// <summary>恢复观察只描述当前证据；完成、重新导航和允许点击仍由持有本关状态的主循环决定。</summary>
    private sealed record ProgressRecoveryObservation(SortiePage Page, bool HealthyBattle = false);

    /// <summary>两分钟无进展后重新按画面判断所处阶段，不相信旧的“等待返回页”状态，也不操作未知页面。</summary>
    private async Task<ProgressRecoveryObservation> InspectProgressRecoveryAsync(
        ScreenImage screen, AutoProgressFlowTemplates templates, ProgressTemplateMatcher vision,
        AutoButtonRecognizer autoRecognizer, bool hasReturnEvidence,
        Func<CancellationToken, Task<bool>>? confirmHome,
        Func<ScreenImage, CancellationToken, Task<bool>>? confirmPreparation, CancellationToken ct,
        Func<ScreenImage, CancellationToken, bool>? confirmSelectedNode)
    {
        TemplateMatchResult? Find(string path, Roi roi)
            => vision.Match(screen, path, roi, StageSortieTemplateThreshold, ct);

        // 弹窗先于其背后的选关标题；恢复不能越过下载、地形、空位与系列奖励的处理策略。
        if (MatchIfConfigured(screen, _terrainTemplatePath, TerrainHintRoi(screen), StageSortieTemplateThreshold) is not null)
            return new(SortiePage.Terrain);
        if (MatchEmptySlotDialog(screen) is not null)
            return new(SortiePage.EmptySlots);
        if (MatchIfConfigured(screen, _sortieDownloadTemplatePath, DownloadHintRoi(screen), StageSortieTemplateThreshold) is not null)
            return new(SortiePage.Download);
        if (_progressClose.Match(screen, templates.SeriesCompleteClose, RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, screen.Width, screen.Height), StageSortieTemplateThreshold, ct) is not null &&
            (hasReturnEvidence || Find(templates.SeriesCompleteTitle, SeriesCompleteTitleRoi) is not null))
            return new(SortiePage.Reward);
        if (FindProgressOk(screen, ct) is not null) return new(SortiePage.StoryOk);
        // 准备页的两项专属证据优先于导航标题和通用按钮，不能再交回选关节点读取。
        if (confirmPreparation is not null && await confirmPreparation(screen, ct)) return new(SortiePage.Preparation);
        if (IsProgressStageReady(screen, ct, confirmSelectedNode)) return new(SortiePage.Stage);
        if (Find(templates.MainStageTitle, WorkLogoVision.MainStageTitleRoi) is not null)
            return new(SortiePage.MainStage);

        var story = _progressButtons.Match(screen, templates.Story, ContinueButtonRoi(screen), StageSortieTemplateThreshold, ct);
        var next = _progressButtons.Match(screen, _continueTemplatePath, ContinueButtonRoi(screen), ContinueTemplateThreshold, ct);
        var continuation = story is not null && (next is null || story.Confidence >= next.Confidence) ? story : next;
        if (TryRecognizeResultPageBeforeContinue(screen, continuation, out _, out _)) return new(SortiePage.Result);
        if (continuation is not null) return new(ReferenceEquals(continuation, story) ? SortiePage.Story : SortiePage.Continue);
        if (Match(screen, _playerLevelUpTemplatePath, PlayerLevelUpRoi(screen)) is not null) return new(SortiePage.LevelUp);
        if (Find(templates.Skip, ProgressSkipRoi) is not null) return new(SortiePage.Skip);
        if (await MatchTapToNextAsync(screen, ct, forAutoProgress: true) is not null) return new(SortiePage.TapToNext);
        var auto = autoRecognizer.Recognize(screen, AutoToggleRoi, ct);
        if (auto is not null) return new(SortiePage.Battle, auto.State == AutoButtonState.All);
        if (Find(templates.Menu, ProgressMenuRoi) is not null) return new(SortiePage.Menu);
        if (confirmHome is not null && await confirmHome(ct)) return new(SortiePage.Home);
        ct.ThrowIfCancellationRequested();
        return new(SortiePage.Unknown);
    }

    /// <summary>日志面向使用者说明恢复依据，不把重新识别或重试称为通关。</summary>
    private static string RecoveryPageName(SortiePage page) => page switch
    {
        SortiePage.Stage => "选择关卡页", SortiePage.MainStage => "主要关卡页",
        SortiePage.Home => "主界面", SortiePage.Preparation => "出击准备页",
        SortiePage.StoryOk => "底部 OK 奖励确认",
        SortiePage.Reward => "系列奖励包", SortiePage.Story => "故事按钮",
        SortiePage.Continue => "继续按钮", SortiePage.Result => "战斗结算页",
        SortiePage.Menu => "剧情 MENU", SortiePage.Skip => "剧情 SKIP",
        SortiePage.Battle => "战斗画面", SortiePage.Download => "下载提示",
        SortiePage.Terrain => "地形提示", SortiePage.EmptySlots => "空位提示",
        SortiePage.LevelUp => "升级提示", SortiePage.TapToNext => "TAP TO NEXT（奖励机体或剧情继续）",
        _ => "未知画面",
    };
}

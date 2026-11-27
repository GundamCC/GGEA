using GGEA.App.Automation;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    /// <summary>
    /// 确认战斗结算完成页：左侧“再次出击”与右侧“选择关卡”必须在同一帧同时命中。
    /// 单独按钮不足以表示页面状态，双模板可排除继续按钮与选择关卡按钮外框相似造成的误判。
    /// </summary>
    private bool TryRecognizeResultPage(
        ScreenImage screen,
        out TemplateMatchResult? again,
        out TemplateMatchResult? selectStage, bool forProgress = false)
    {
        // 兼容旧配置：未提供新模板时不做终局页识别，但也不能把空路径交给模板加载器。
        if (string.IsNullOrWhiteSpace(_selectStageTemplatePath))
        {
            again = null;
            selectStage = null;
            return false;
        }

        again = Match(screen, _againTemplatePath, AgainButtonRoi(screen), StageSortieTemplateThreshold);
        selectStage = Match(screen, _selectStageTemplatePath, SelectStageButtonRoi(screen), StageSortieTemplateThreshold);
        // HARD 奖励页的“再次出击”下方会显示剩余次数。先确认右侧确实是“选择关卡”，
        // 再按左侧固定文字和蓝色可用状态补认，避免把相似的“继续”外框放行。
        if (forProgress && again is null && selectStage is not null &&
            _progressButtons.MatchBlueLabel(screen, _selectStageTemplatePath, SelectStageButtonRoi(screen),
                StageSortieTemplateThreshold, default) is { } confirmedSelect)
        {
            again = _progressButtons.MatchBlueLabel(screen, _againTemplatePath, AgainButtonRoi(screen),
                StageSortieTemplateThreshold, default);
            if (again is not null) selectStage = confirmedSelect;
        }
        return again is not null && selectStage is not null;
    }

    /// <summary>使用同帧双按钮确认结算页，避免继续模板因按钮外框相似覆盖结算状态。</summary>
    private bool TryRecognizeResultPageBeforeContinue(
        ScreenImage screen,
        TemplateMatchResult? continueMatch,
        out TemplateMatchResult? again,
        out TemplateMatchResult? selectStage, bool forProgress = false)
    {
        if (!TryRecognizeResultPage(screen, out again, out selectStage, forProgress)) return false;
        // 左侧相似图案不能把明确的右侧“继续”变成终局；同分时仍保留双按钮布局证据。
        // 只比较同一右侧按钮，避免其他位置的继续候选否决真实的选择关卡按钮。
        var sameButton = continueMatch is not null &&
            Math.Abs(continueMatch.CenterX - selectStage!.CenterX) <= selectStage.Width / 4 &&
            Math.Abs(continueMatch.CenterY - selectStage.CenterY) <= selectStage.Height / 4;
        return !sameButton || continueMatch!.Confidence - selectStage!.Confidence < ResultPageContinueConfidenceMargin;
    }

    /// <summary>选择关卡模板明显强于继续模板时，避免把终局页按钮当作继续按钮点击。</summary>
    private static bool IsCurrentContinueCandidate(
        TemplateMatchResult? continueMatch,
        TemplateMatchResult? selectStageMatch)
        => continueMatch is not null
            && (selectStageMatch is null
                || selectStageMatch.Confidence - continueMatch.Confidence < ResultPageContinueConfidenceMargin);

    /// <summary>下载已发生在本轮出击后；只有明确的继续按钮或双按钮终局证据才允许转入结算。</summary>
    private bool IsSettlementPage(ScreenImage screen, bool forProgress = false)
    {
        var currentContinue = Match(screen, _continueTemplatePath, ContinueButtonRoi(screen), ContinueTemplateThreshold);
        var resultPage = TryRecognizeResultPageBeforeContinue(screen, currentContinue, out _, out var selectStage, forProgress);
        return resultPage || currentContinue is not null && IsCurrentContinueCandidate(currentContinue, selectStage);
    }

    /// <summary>“继续”保留水平偏移空间，纵向限定底部 175 像素，排除上方同框选关按钮。</summary>
    private static Roi ContinueButtonRoi(ScreenImage screen) =>
        new(
            Math.Max(0, screen.Width - 768),
            Math.Max(0, screen.Height - 175),
            Math.Min(768, screen.Width),
            Math.Min(175, screen.Height));

    /// <summary>“选择关卡”仍限制在右下按钮位，避免与扩大后的继续区域交叉误判。</summary>
    private static Roi SelectStageButtonRoi(ScreenImage screen) =>
        new(
            screen.Width - 450,
            screen.Height - 175,
            450,
            175);

    /// <summary>“再次出击”按钮识别区域：右侧 435~825 像素、底部 175 像素。</summary>
    private static Roi AgainButtonRoi(ScreenImage screen) =>
        new(
            screen.Width - 825,
            screen.Height - 175,
            390,
            175);

    /// <summary>刷本失败提示识别区域：屏幕顶部 150 像素。</summary>
    private static Roi FailureRoi(ScreenImage screen) =>
        new(0, 0, screen.Width, 150);

    /// <summary>空位提示共用 600×500 区域，完整容纳日文两行说明，不截断出击确认句。</summary>
    internal static Roi EmptySlotHintRoi(ScreenImage screen) =>
        new(
            (screen.Width - 600) / 2,
            0,
            600,
            500);

    /// <summary>地形提示横向覆盖对话框，容纳日文完整两行说明；纵向范围不变，排除下方可变机体列表。</summary>
    private static Roi TerrainHintRoi(ScreenImage screen) =>
        new(
            (screen.Width - 1040) / 2,
            0,
            1040,
            420);

    /// <summary>
    /// 下载提示横向覆盖整个对话框；日文完整说明句宽于旧 666 像素区域。
    /// 保留原纵向范围和阈值，包含说明文字但不依赖下方可变下载大小。
    /// </summary>
    internal static Roi DownloadHintRoi(ScreenImage screen) =>
        new(
            (screen.Width - Math.Min(1080, screen.Width)) / 2,
            Math.Min(60, Math.Max(0, screen.Height - 420)),
            Math.Min(1080, screen.Width),
            420);

    /// <summary>AP 不足提示识别区域：画面正中上方 400×550。</summary>
    private static Roi NoApHintRoi(ScreenImage screen) =>
        new(
            (screen.Width - 400) / 2,
            0,
            400,
            550);

    /// <summary>玩家升级提示识别区域：画面正中上方 640×315。</summary>
    private static Roi PlayerLevelUpRoi(ScreenImage screen) =>
        new(
            (screen.Width - 640) / 2,
            0,
            640,
            315);

    /// <summary>奖励图标识别区域：画面顶部中间 230×120。</summary>
    private static Roi RewardRoi(ScreenImage screen) =>
        new(
            (screen.Width - 230) / 2,
            0,
            230,
            120);

    /// <summary>对话框按钮识别区域：画面正中下方 825×300（下载、空位出击与 AP 不足对话框共用）。</summary>
    private static Roi CenterBottomDialogRoi(ScreenImage screen) =>
        new(
            (screen.Width - 825) / 2,
            screen.Height - 300,
            825,
            300);

    /// <summary>结算等待复用会话级全局继续策略；终止检查后调用，遮罩命中或冷却中均丢弃旧帧。</summary>
    private async Task<bool> HandleSettlementContinuationAsync(string serial, ScreenImage screen, CancellationToken ct,
        bool forAutoProgress = false, bool sortieOpening = false)
        => await _automation.GlobalScreens.HandleContinuationAsync(serial,
            ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height), ct, allowSkip: false,
            tapRegion: forAutoProgress ? RecognitionRegion.ProgressTapToNext : RecognitionRegion.BottomButtons,
            sortieOpening: sortieOpening)
            != GlobalScreenResult.None;

    /// <summary>首次、再次与下载后入口共用底部 OCR；只读匹配不接管取消、安全检查或开场确认。</summary>
    private Task<TemplateMatchResult?> MatchTapToNextAsync(ScreenImage screen, CancellationToken ct, bool forAutoProgress = false)
        => Task.Run(() => _automation.TapToNext.Recognize(screen, ct,
            forAutoProgress ? RecognitionRegion.ProgressTapToNext : RecognitionRegion.BottomButtons, sortieOpening: !forAutoProgress), ct);

    /// <summary>提示可能与剧情灰色背景相似；同帧确认左右按钮后才进入空位处理，避免跨帧借用其他弹窗。</summary>
    internal TemplateMatchResult? MatchEmptySlotDialog(ScreenImage screen)
    {
        var hint = Match(screen, _emptySlotHintTemplatePath, EmptySlotHintRoi(screen));
        if (hint is null) return null;
        var roi = CenterBottomDialogRoi(screen);
        var cancel = Match(screen, _emptySlotCancelTemplatePath, roi);
        // 下载与出击共用蓝色外框；同时复核中央出击文字，不能由外框为另一种确认授权。
        var sortie = _progressButtons.Match(screen, _emptySlotSortieTemplatePath, roi, _threshold, default);
        return cancel is not null && sortie is not null && cancel.CenterX < screen.Width / 2 &&
            sortie.CenterX > screen.Width / 2 ? hint : null;
    }

    /// <summary>在指定区域匹配模板；模板加载失败返回 null。</summary>
    private TemplateMatchResult? Match(ScreenImage screen, string templatePath, Roi roi)
        => Match(screen, templatePath, roi, _threshold);

    /// <summary>匹配可选模板；旧配置缺少新增模板时安全跳过，避免反复记录空路径加载错误。</summary>
    private TemplateMatchResult? MatchIfConfigured(ScreenImage screen, string templatePath, Roi roi, double threshold)
        => string.IsNullOrWhiteSpace(templatePath) ? null : Match(screen, templatePath, roi, threshold);

    /// <summary>在指定区域按指定阈值匹配模板；模板加载失败返回 null。</summary>
    private TemplateMatchResult? Match(ScreenImage screen, string templatePath, Roi roi, double threshold)
    {
        try
        {
            return TemplateFallback.MatchImage(templatePath, screen, roi, threshold, _matcher, _templateImages.Load);
        }
        catch (Exception ex)
        {
            _log.Warn($"出击流程模板不可用，本次无法判断对应目标: {templatePath}: {ex.Message}");
            return null;
        }
    }
}

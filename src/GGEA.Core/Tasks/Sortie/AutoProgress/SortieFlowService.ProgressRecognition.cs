using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class SortieFlowService
{
    private static readonly string[] ProgressEntryButtons = ["button_sortie_preparations", "button_sortie", "watch"];

    // 结算按钮必须核对中央文字；共用外框不能把出击准备认成故事或继续。
    private readonly StageEntryButtonRecognizer _progressButtons = new();
    // 通用关闭的外框随弹窗背景变化；中央纹理识别仍使用推图原有阈值与状态门禁。
    private readonly DialogCloseRecognizer _progressClose = new();
    private static readonly Roi ProgressEntryRoi = new(1470, 754, 450, 326);

    /// <summary>选关专属入口或可信选中节点各自足以证明返回；标题仅辅助，不能挡住真实内容或放行黑屏过渡。</summary>
    private bool IsProgressStageReady(ScreenImage screen, CancellationToken ct,
        Func<ScreenImage, CancellationToken, bool>? confirmSelectedNode = null)
    {
        foreach (var name in ProgressEntryButtons)
            if (_progressButtons.Match(screen, ConfigService.ResolvePath($"resources/templates/stage/sortie/{name}.png"),
                ProgressEntryRoi, StageSortieTemplateThreshold, ct) is not null) return true;
        return confirmSelectedNode?.Invoke(screen, ct) == true;
    }
    // 观看关卡跳过后的二次确认只在底部 300 像素内查找，不复用入口的 320 像素范围。
    private static readonly Roi WatchSkipExecuteRoi = new(0, 780, 1920, 300);
    private static string WatchExecuteTemplate => ConfigService.ResolvePath("resources/templates/dialog/execute.png");
    private static string ProgressOkTemplate => ConfigService.ResolvePath("resources/templates/dialog/button_ok.png");

    /// <summary>正常略过与静止巡查共用底部 OK 的文字和完整按钮校验，不把共用蓝色外框当作确认。</summary>
    private TemplateMatchResult? FindProgressOk(ScreenImage screen, CancellationToken ct)
        => _progressButtons.Match(screen, ProgressOkTemplate,
            RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, screen.Width, screen.Height),
            RecognitionRegions.BottomButtonThreshold, ct)
        // 故事奖励使用同款略小按钮；原尺寸未命中后只补试实帧验证的 98%，阈值与同位置双重校验不变。
        ?? _progressButtons.Match(screen, ProgressOkTemplate,
            RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, screen.Width, screen.Height),
            RecognitionRegions.BottomButtonThreshold, ct, templateScale: .98);

    private static readonly Roi ProgressMenuRoi = new(1595, 0, 325, 100);
    // 标准 1920×1080 画面的右上四分之一；正常轮询与超时恢复共用此区域。
    private static readonly Roi ProgressSkipRoi = new(960, 0, 960, 540);
    // 系列完成奖励包正文标题位于画面上部居中，不使用普通页面的左上角标题区域。
    private static readonly Roi SeriesCompleteTitleRoi = new(536, 100, 850, 150);

    /// <summary>在任何导航或出击之前验证新增素材，缺失、尺寸越界或全透明图均不能静默跳过。</summary>
    public void ValidateAutoProgressFlow(AutoProgressFlowTemplates templates)
    {
        _progressButtons.Validate(ProgressOkTemplate, RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, 1920, 1080));
        // 返回页新增的内容确认素材也在出击前检查，不能走完战斗才发现文件缺失。
        foreach (var name in ProgressEntryButtons)
            _progressButtons.Validate(ConfigService.ResolvePath($"resources/templates/stage/sortie/{name}.png"), ProgressEntryRoi);
        foreach (var (path, width, height) in new[] {
            (templates.Menu, 325, 100), (templates.Skip, ProgressSkipRoi.Width, ProgressSkipRoi.Height), (templates.Story, 768, 250),
            (templates.SelectStageTitle, 1000, 120),
            (templates.AutoClose, 200, 115), (templates.AutoAll, 200, 115), (templates.AutoEnemy, 200, 115),
            (templates.SeriesCompleteClose, 1920, 200), (templates.MainStageTitle, 737, 104),
            (templates.SeriesCompleteTitle, 850, 150) })
        {
            var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
            if (image.Width > width || image.Height > height ||
                !Enumerable.Range(0, image.Width * image.Height).Any(i => image.Bgra[i * 4 + 3] >= 128))
                throw new InvalidOperationException($"自动推图模板尺寸或有效像素不符合识别区域：{Path.GetFileName(path)}");
        }
    }

    private async Task<ScreenImage> CaptureProgressScreenAsync(string serial, CancellationToken ct)
    {
        var screen = await _automation.CaptureScreenAsync(serial, ct);
        if (screen.Width != 1920 || screen.Height != 1080)
            throw new InvalidOperationException("自动推图仅支持 1920×1080 横屏，已停止。");
        return screen;
    }
}

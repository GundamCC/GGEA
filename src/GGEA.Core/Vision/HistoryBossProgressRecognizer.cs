using System.IO;
using GGEA.App.Config;
using GGEA.App.Tasks;

namespace GGEA.App.Vision;

/// <summary>卡片按难度及同帧上下位置定位；完成状态只由对应区域的 CLEAR / COMPLETE 决定。</summary>
public sealed record HistoryBossCard(FierceEnemyDifficulty Difficulty, FierceEnemyProgressState State,
    Roi Label, TemplateMatchResult? Challenge)
{
    // 标识上方 65px 是完成标记，下方 260px 包含整个挑战按钮；列表有效范围为 128..954。
    public const int MinimumLabelY = 193;
    public const int MaximumLabelY = 694;
    public bool IsFullyVisible => Label.Y is >= MinimumLabelY and <= MaximumLabelY;
}

/// <summary>历史 BOSS 卡片复用难度 OCR 与挑战素材，每张卡片独立读取对应区域的完成标记。</summary>
public sealed class HistoryBossProgressRecognizer(OnnxTextRecognizer ocr)
{
    private readonly FierceEnemyDifficultyRecognizer _labels = new(ocr, new(1035, 128, 125, 826));
    private readonly TemplateMatcher _matcher = new();
    private readonly Dictionary<string, ScreenImage> _images = [];
    private const string Clear = "resources/templates/stage/progress_clear.png";
    private const string Complete = "resources/templates/stage/progress_complete.png";

    /// <summary>战后返回需同帧确认 BOSS 标题、目标作品和可读卡片；只交回列表，不据此判定本关通关。</summary>
    public bool ConfirmReturn(ScreenImage screen, IHistoryStageVision vision, int workId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var page = vision.Inspect(screen, workId, ct);
        if (page.Page != HistoryPageKind.Boss || page.DetailWorkId != workId) return false;
        return Read(screen, ct).Any(card => card.IsFullyVisible && card.State != FierceEnemyProgressState.Unknown);
    }

    /// <summary>进入流程前校验 OCR 和亮暗按钮、完成标记，不能将素材缺失解释为未通关。</summary>
    public void Validate()
    {
        ocr.Validate();
        foreach (var path in new[] { Clear, Complete, FierceEnemyProgressRecognizer.ChallengeTemplate,
            FierceEnemyProgressRecognizer.DarkChallengeTemplate }) Load(ConfigService.ResolvePath(path));
    }

    /// <summary>完整标签保留用于滚动定位；状态区或按钮被裁切时只能返回未知，不能授权挑战。</summary>
    public IReadOnlyList<HistoryBossCard> Read(ScreenImage screen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (screen.Width != 1920 || screen.Height != 1080)
            throw new InvalidOperationException("历史 BOSS 识别仅支持 1920×1080。");
        var cards = new List<HistoryBossCard>();
        foreach (var difficulty in Enum.GetValues<FierceEnemyDifficulty>())
        foreach (var label in _labels.FindAll(screen, difficulty, ct))
        {
            if (label.Y < HistoryBossCard.MinimumLabelY || label.Y > HistoryBossCard.MaximumLabelY)
            {
                cards.Add(new(difficulty, FierceEnemyProgressState.Unknown, label, null));
                continue;
            }
            var badge = new Roi(1650, label.Y - 65, 210, 55);
            var button = new Roi(1550, label.Y + 160, 290, 100);
            var passed = Match(Clear, badge) is not null || Match(Complete, badge) is not null;
            // 完成标记已经足够判断通过；只有未完成卡片才检查按钮能否挑战。
            var dark = passed ? null : Match(FierceEnemyProgressRecognizer.DarkChallengeTemplate, button);
            var bright = passed ? null : Match(FierceEnemyProgressRecognizer.ChallengeTemplate, button);
            var state = passed ? FierceEnemyProgressState.Passed : dark is not null ? FierceEnemyProgressState.Locked :
                bright is not null ? FierceEnemyProgressState.Available : FierceEnemyProgressState.Unknown;
            cards.Add(new(difficulty, state, label, state == FierceEnemyProgressState.Available ? bright : null));
        }
        return cards.OrderBy(card => card.Label.Y).ToArray();

        TemplateMatchResult? Match(string path, Roi area)
        {
            return TemplateFallback.Match(path, candidate =>
            {
                var image = Load(candidate);
                return _matcher.Match(screen.Bgra, screen.Width, screen.Height, image.Bgra,
                    image.Width, image.Height, .92, area, ct: ct);
            }, ct);
        }
    }

    /// <summary>本地缓存只含模板；卡片状态只属于本次截图。</summary>
    private ScreenImage Load(string path)
    {
        if (!_images.TryGetValue(path, out var image))
            _images[path] = image = ImageCodec.DecodePng(File.ReadAllBytes(path));
        return image;
    }

}

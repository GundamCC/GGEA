using GGEA.App.Config;
using GGEA.App.Tasks;

namespace GGEA.App.Vision;

/// <summary>只有完成标记区和挑战按钮完整可见才允许出击；未知、裁切和锁定都不授权战斗。</summary>
public enum FierceEnemyProgressState { Unknown, Locked, Available, Passed }

/// <summary>难度与同帧挑战按钮绑定，禁止把另一卡片按钮当成目标入口。</summary>
public sealed record FierceEnemyProgressObservation(FierceEnemyDifficulty Difficulty,
    FierceEnemyProgressState State, TemplateMatchResult? Challenge);

/// <summary>OCR 定位难度，真实素材识别完成标记及挑战亮暗态；不处理 CHALLENGE 难度。</summary>
public sealed class FierceEnemyProgressRecognizer(OnnxTextRecognizer ocr)
{
    public const string ChallengeTemplate = "resources/templates/stage/event/challenge\u200c.png";
    public const string DarkChallengeTemplate = "resources/templates/stage/event/challenge\u200c_disabled.png";
    private readonly TemplateMatcher _matcher = new();
    private readonly Dictionary<string, ScreenImage> _images = new();

    /// <summary>在导航前检查全部资源及 OCR，避免出击后才暴露缺失。</summary>
    public void Validate()
    {
        ocr.Validate();
        foreach (var path in new[] { ChallengeTemplate, DarkChallengeTemplate,
            "resources/templates/stage/progress_clear.png", "resources/templates/stage/progress_complete.png" })
            Load(ConfigService.ResolvePath(path));
    }

    /// <summary>标记优先于亮态；标记区裁切时只允许判断锁定，不能据此推断未通关。</summary>
    public IReadOnlyList<FierceEnemyProgressObservation> Read(ScreenImage screen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (screen.Width != 1920 || screen.Height != 1080)
            throw new InvalidOperationException("强敌推图仅支持 1920×1080 横屏。");
        var observations = new List<FierceEnemyProgressObservation>();
        foreach (var difficulty in Enum.GetValues<FierceEnemyDifficulty>())
        {
            var label = new FierceEnemyDifficultyRecognizer(ocr).Find(screen, difficulty, ct);
            if (label is not { } bounds || bounds.Y + 260 > 950) continue;
            var badgeVisible = bounds.Y - 65 >= 168;
            var badge = new Roi(1650, Math.Max(168, bounds.Y - 65), 200, 55);
            var buttonArea = new Roi(1550, bounds.Y + 160, 285, 100);
            var passed = badgeVisible && (Match(screen, "resources/templates/stage/progress_clear.png", badge, ct) is not null ||
                Match(screen, "resources/templates/stage/progress_complete.png", badge, ct) is not null);
            var dark = Match(screen, DarkChallengeTemplate, buttonArea, ct);
            var bright = Match(screen, ChallengeTemplate, buttonArea, ct);
            var state = passed ? FierceEnemyProgressState.Passed : dark is not null ? FierceEnemyProgressState.Locked
                : bright is not null && badgeVisible ? FierceEnemyProgressState.Available : FierceEnemyProgressState.Unknown;
            observations.Add(new(difficulty, state, state == FierceEnemyProgressState.Available ? bright : null));
        }
        return observations;
    }

    /// <summary>缓存任务期间的静态素材，不缓存截图或按钮位置。</summary>
    private ScreenImage Load(string path)
    {
        if (!_images.TryGetValue(path, out var image))
            _images[path] = image = ImageCodec.DecodePng(System.IO.File.ReadAllBytes(path));
        return image;
    }

    /// <summary>模板比较原始灰度亮度，暗态优先，防止同字形的锁定按钮被点击。</summary>
    private TemplateMatchResult? Match(ScreenImage screen, string path, Roi area, CancellationToken ct)
    {
        return TemplateFallback.Match(path, candidate =>
            {
                var image = Load(candidate);
                return _matcher.Match(screen.Bgra, screen.Width, screen.Height, image.Bgra,
                    image.Width, image.Height, .92, area, ct: ct);
            }, ct);
    }
}

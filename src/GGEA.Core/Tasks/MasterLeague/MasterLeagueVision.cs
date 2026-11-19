using System.IO;
using System.Text.Json;
using GGEA.App.Config;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>每轮同时观察战斗结束与可选 SKIP；两者都出现时由流程优先处理结算。</summary>
public sealed record MasterLeagueBattleFrame(bool ResultVisible, TemplateMatchResult? Skip, TemplateMatchResult? ReturnButton);

/// <summary>变更确认页的同帧证据，确认前校验舍弃积分和剩余次数。</summary>
public sealed record MasterLeagueRefreshDialog(int CurrentPoints, int Remaining, TemplateMatchResult ConfirmButton);

/// <summary>只读识别边界；真实实现复用同一帧，测试替换观察结果而不绕开流程的点击/取消保护。</summary>
public interface IMasterLeagueVision
{
    void Validate();
    TemplateMatchResult? FindStageBoostInfo(ScreenImage screen, CancellationToken ct);
    MasterLeagueOpponentPage? ReadOpponents(ScreenImage screen, CancellationToken ct);
    MasterLeagueBattleFrame ReadBattle(ScreenImage screen, CancellationToken ct);
    MasterLeagueRefreshDialog? ReadRefreshDialog(ScreenImage screen, CancellationToken ct);
    TemplateMatchResult? FindRefresh(ScreenImage screen, int opponent, CancellationToken ct);
    TemplateMatchResult? FindBattleButton(ScreenImage screen, CancellationToken ct);
    int? ReadChallengeRemaining(ScreenImage screen, CancellationToken ct);
}

/// <summary>联盟专属 OCR 和模板组合；不拥有模型，不点击，不把未知数值当作零。</summary>
public sealed class MasterLeagueVision : IMasterLeagueVision
{
    public const string LayoutPath = "resources/ui_layout/master_league_opponents.json";
    public const string StageBoostInfoPath = "resources/templates/stage/pvp/title_stage_boost_info.png";
    public const string SkipPath = "resources/templates/stage/pvp/button_skip.png";
    private const string Root = "resources/templates/stage/pvp/";
    private readonly OnnxTextRecognizer _ocr;
    private readonly MasterLeagueNumberRecognizer _numbers;
    private readonly MasterLeagueResultRecognizer _result;
    private readonly TemplateMatcher _matcher = new();
    private readonly Dictionary<string, ScreenImage> _templates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _templateMasks = new(StringComparer.Ordinal);
    private UiLayout? _layout;
    private byte[]? _skipMask;

    /// <summary>共享窗口 OCR，避免每轮创建或释放推理会话。</summary>
    public MasterLeagueVision(OnnxTextRecognizer ocr)
    { _ocr = ocr; _numbers = new(ocr); _result = new(ocr); }

    /// <summary>消费挑战/刷新次数前检查全部依赖；布局缺失必须失败，不能退回猜测坐标。</summary>
    public void Validate()
    {
        if (_layout is not null) return;
        _ocr.Validate();
        var layout = JsonSerializer.Deserialize<UiLayout>(File.ReadAllText(ConfigService.ResolvePath(LayoutPath)))
            ?? throw new InvalidOperationException("大师联盟识别布局不可用。");
        if (layout.ScreenWidth != 1920 || layout.ScreenHeight != 1080)
            throw new InvalidOperationException("大师联盟布局必须为 1920×1080。");
        var required = new List<string> { "opponentSelectionTitle", "battleButton", "refreshCount", "stageBoostInfoTitle" };
        for (var i = 1; i <= 5; i++) required.AddRange([$"opponent{i}Points", $"opponent{i}Result", $"opponent{i}Refresh"]);
        foreach (var name in required)
        {
            var anchor = layout.Find(name);
            if (anchor is null || anchor.Width <= 0 || anchor.Height <= 0 || anchor.X < 0 || anchor.Y < 0 ||
                anchor.X + anchor.Width > 1920 || anchor.Y + anchor.Height > 1080)
                throw new InvalidOperationException($"大师联盟布局区域无效：{name}");
        }
        foreach (var path in new[] { MasterLeagueService.SelectOpponentTitleTemplate, Root + "button_battle.png",
            Root + "result_victory.png", Root + "result_defeat.png", Root + "button_refresh_opponent.png",
            Root + "button_refresh_opponent_selected.png", Root + "title_change_opponent.png", Root + "button_change_opponent.png",
            MasterLeagueService.BattlePreparationTitleTemplate, MasterLeagueService.ResultSelectOpponentTemplate, SkipPath, StageBoostInfoPath })
            _templates[path] = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(path)));
        _skipMask = CreateSkipMask(_templates[SkipPath]);
        foreach (var path in new[] { MasterLeagueService.SelectOpponentTitleTemplate, MasterLeagueService.BattlePreparationTitleTemplate })
            _templateMasks[path] = CreateWhiteTextMask(_templates[path]);
        _templateMasks[Root + "result_victory.png"] = CreateVictoryTextMask(_templates[Root + "result_victory.png"]);
        _layout = layout;
    }

    /// <summary>同时确认选敌页与信息弹层标题；标题中心是实机验证过的关闭位置，不读取可变增幅标签。</summary>
    public TemplateMatchResult? FindStageBoostInfo(ScreenImage screen, CancellationToken ct)
    {
        Validate();
        if (!Standard(screen) || Match(screen, MasterLeagueService.SelectOpponentTitleTemplate,
            Region("opponentSelectionTitle"), .90, ct) is null) return null;
        return Match(screen, StageBoostInfoPath, Region("stageBoostInfoTitle"), .90, ct);
    }

    /// <summary>同帧读取选敌所需信息；已挑战者的具体积分可未知，未挑战者不可将未知当零分。</summary>
    public MasterLeagueOpponentPage? ReadOpponents(ScreenImage screen, CancellationToken ct)
    {
        Validate();
        if (!Standard(screen) || FindStageBoostInfo(screen, ct) is not null || Match(screen, MasterLeagueService.SelectOpponentTitleTemplate,
            Region("opponentSelectionTitle"), .90, ct) is null) return null;
        // 完成判断只需页面身份与五个明确胜利；不能被对战按钮动画、无关数字或刷新次数误读挡住。
        var opponents = new List<MasterLeagueOpponent>();
        for (var i = 1; i <= 5; i++)
        {
            var roi = Region($"opponent{i}Result");
            opponents.Add(new(null, Match(screen, Root + "result_victory.png", roi, .90, ct) is not null,
                Match(screen, Root + "result_defeat.png", roi, .90, ct) is not null));
        }
        if (opponents.All(o => o.Victory && !o.Defeat)) return new(null, opponents);
        // 继续选敌/刷新仍遵守原有按钮与数字门禁；缺少胜利标记不能通过本地挑战记录推断成功。
        if (FindBattleButton(screen, ct) is null) return null;
        var remaining = _numbers.Read(screen, Region("refreshCount"), ct: ct);
        for (var i = 0; i < opponents.Count; i++)
            opponents[i] = opponents[i] with { Points = _numbers.Read(screen, Region($"opponent{i + 1}Points"), ct: ct) };
        var page = new MasterLeagueOpponentPage(remaining, opponents);
        return MasterLeagueProgress.IsConsistent(page) ? page : null;
    }

    /// <summary>同帧始终检查结算和 SKIP；SKIP 使用白字及邻接轮廓遮罩，排除半透明大背景。</summary>
    public MasterLeagueBattleFrame ReadBattle(ScreenImage screen, CancellationToken ct)
    {
        Validate();
        if (!Standard(screen)) return new(false, null, null);
        var result = _result.IsResult(screen, ct);
        var skip = _templates[SkipPath];
        var match = _matcher.Match(screen.Bgra, screen.Width, screen.Height, skip.Bgra, skip.Width, skip.Height,
            .95, new Roi(1405, 5, 210, 100), _skipMask, ct);
        return new(result, match, result ? Match(screen, MasterLeagueService.ResultSelectOpponentTemplate,
            new Roi(1495, 905, 410, 160), .85, ct) : null);
    }

    /// <summary>确认变更对象现有积分及次数；浅色面板只读取深色数字，不读取背景。</summary>
    public MasterLeagueRefreshDialog? ReadRefreshDialog(ScreenImage screen, CancellationToken ct)
    {
        Validate();
        if (!Standard(screen) || Match(screen, Root + "title_change_opponent.png", new Roi(800, 45, 320, 90), .90, ct) is null)
            return null;
        var button = Match(screen, Root + "button_change_opponent.png", new Roi(940, 910, 410, 160), .85, ct);
        var points = _numbers.Read(screen, new Roi(700, 645, 170, 70), ct: ct);
        var remaining = _numbers.Read(screen, new Roi(1295, 820, 35, 45), darkInk: true, ct: ct);
        return button is not null && points is not null && remaining is >= 0 and <= 5
            ? new(points.Value, remaining.Value, button) : null;
    }

    /// <summary>只在指定对手列的按钮区域匹配两个显示状态。</summary>
    public TemplateMatchResult? FindRefresh(ScreenImage screen, int opponent, CancellationToken ct)
    {
        Validate();
        if (!Standard(screen) || opponent is < 0 or > 4) return null;
        var roi = Region($"opponent{opponent + 1}Refresh");
        return Match(screen, Root + "button_refresh_opponent.png", roi, .90, ct)
            ?? Match(screen, Root + "button_refresh_opponent_selected.png", roi, .90, ct);
    }

    /// <summary>只观察按钮；调用方必须在同帧确认对手表。</summary>
    public TemplateMatchResult? FindBattleButton(ScreenImage screen, CancellationToken ct)
    { Validate(); return Standard(screen) ? Match(screen, Root + "button_battle.png", Region("battleButton"), .85, ct) : null; }

    /// <summary>挑战次数与变更次数是不同资源；准备页次数未知或零次不能开始对战。</summary>
    public int? ReadChallengeRemaining(ScreenImage screen, CancellationToken ct)
    {
        Validate();
        if (!Standard(screen) || Match(screen, MasterLeagueService.BattlePreparationTitleTemplate,
            new Roi(200, 0, 320, 100), .90, ct) is null) return null;
        var count = _numbers.Read(screen, new Roi(1684, 1000, 28, 40), ct: ct);
        return count is >= 0 and <= 5 ? count : null;
    }

    /// <summary>布局已在 Validate 完整检查；缺项不能回退为整屏搜索。</summary>
    private Roi Region(string name) => _layout!.Find(name)!.Roi;

    /// <summary>复用初始化时解码的模板，避免新帧判断到点击之间发生文件读取延迟。</summary>
    private TemplateMatchResult? Match(ScreenImage screen, string path, Roi roi, double threshold, CancellationToken ct)
    {
        var template = _templates[path];
        return _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra, template.Width, template.Height,
            threshold, roi, _templateMasks.GetValueOrDefault(path), ct);
    }

    /// <summary>固定布局只适用于标准横屏，不能将缩放画面的坐标直接送往设备。</summary>
    private static bool Standard(ScreenImage screen) => screen.Width == 1920 && screen.Height == 1080;

    /// <summary>标题背后会透出对手立绘；只比较白色文字及一像素轮廓，保留原匹配阈值。</summary>
    internal static byte[] CreateWhiteTextMask(ScreenImage image)
    {
        var mask = new byte[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var p = (y * image.Width + x) * 4;
                var minimum = Math.Min(image.Bgra[p], Math.Min(image.Bgra[p + 1], image.Bgra[p + 2]));
                var maximum = Math.Max(image.Bgra[p], Math.Max(image.Bgra[p + 1], image.Bgra[p + 2]));
                if (minimum < 225 || maximum - minimum > 30) continue;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (x + dx >= 0 && x + dx < image.Width && y + dy >= 0 && y + dy < image.Height)
                            mask[(y + dy) * image.Width + x + dx] = 255;
            }
        if (mask.Count(value => value != 0) < 200) throw new InvalidOperationException("大师联盟标题文字遮罩不可用。");
        return mask;
    }

    /// <summary>胜利标记外围混入角色背景；仅保留浅金色字形及一像素暗轮廓，仍按原阈值验证完整字形。</summary>
    private static byte[] CreateVictoryTextMask(ScreenImage image)
    {
        var mask = new byte[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var p = (y * image.Width + x) * 4;
                if (image.Bgra[p + 2] < 200 || image.Bgra[p + 1] < 180 || image.Bgra[p] < 100) continue;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (x + dx >= 0 && x + dx < image.Width && y + dy >= 0 && y + dy < image.Height)
                            mask[(y + dy) * image.Width + x + dx] = 255;
            }
        if (mask.Count(value => value != 0) < 200) throw new InvalidOperationException("大师联盟胜利字形遮罩不可用。");
        return mask;
    }

    /// <summary>保留原始用户素材；运行时仅选择字形和两像素轮廓，不覆盖图片或降低全局匹配门槛。</summary>
    private static byte[] CreateSkipMask(ScreenImage image)
    {
        if (image.Width != 184 || image.Height != 83)
            throw new InvalidOperationException("大师联盟 SKIP 素材尺寸变化，需重新验证文字遮罩。");
        var mask = new byte[image.Width * image.Height];
        for (var y = 18; y < 65; y++)
            for (var x = 48; x < 137; x++)
            {
                var p = (y * image.Width + x) * 4;
                if (image.Bgra[p] < 225 || image.Bgra[p + 1] < 225 || image.Bgra[p + 2] < 225) continue;
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++) mask[(y + dy) * image.Width + x + dx] = 255;
            }
        if (mask.Count(value => value != 0) < 200) throw new InvalidOperationException("大师联盟 SKIP 字形遮罩不可用。");
        return mask;
    }
}

using System.IO;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>日服推图原帧回归，保留入口文字校验及 AUTO 颜色证据门禁。</summary>
public sealed class JapaneseAutoProgressTests : IDisposable
{
    private readonly ResourceResolver _resources = new(AppContext.BaseDirectory, "ja");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ggea-ja-progress-" + Guid.NewGuid().ToString("N"));

    /// <summary>独立采集帧识别出击准备，不能被相同外框的故事入口替代。</summary>
    [Fact]
    public void Preparation_RecognizesJapaneseText_RejectsStory()
    {
        var screen = Frame("entry");
        var matcher = new StageEntryButtonRecognizer();
        var area = new Roi(1470, 754, 450, 326);
        var match = matcher.Match(screen, Resource("button_sortie_preparations"), area, .9, default);
        Assert.NotNull(match);
        Assert.InRange(match.CenterX, 1650, 1700);
        Assert.InRange(match.CenterY, 870, 920);
        Assert.Null(matcher.Match(screen, Resource("story"), area, .9, default));
    }

    /// <summary>结算故事按钮不能被继续或准备入口的蓝色外框抢占。</summary>
    [Fact]
    public void Story_RecognizesJapaneseText_RejectsOtherBlueButtons()
    {
        var screen = Frame("story");
        var matcher = new StageEntryButtonRecognizer();
        var area = new Roi(1152, 830, 768, 250);
        var story = matcher.Match(screen, Resource("story"), area, .9, default);
        Assert.NotNull(story);
        Assert.InRange(story.CenterX, 1640, 1700);
        Assert.InRange(story.CenterY, 950, 1030);
        Assert.Null(matcher.Match(screen, Resource("continue"), area, .9, default));
        Assert.Null(matcher.Match(screen, Resource("button_sortie_preparations"), area, .9, default));
    }

    /// <summary>剧情战斗的青色 AUTO 使用独立帧验证；不能把选关页面当作自动状态。</summary>
    [Fact]
    public void AutoAll_IndependentFrame_RequiresVisibleButton()
    {
        var recognizer = new AutoButtonRecognizer(new TemplateMatcher(), Resource("auto_all", common: true), Resource("auto_enemy", common: true), Resource("auto_close", common: true));
        var area = new Roi(1400, 0, 200, 115);
        Assert.Equal(AutoButtonState.All, recognizer.Recognize(Frame("auto-all"), area)?.State);
        // 此帧仍有清晰青色底与 AUTO 字形；通用素材可确认开启，不能把旧语言模板未命中当作状态真值。
        Assert.Equal(AutoButtonState.All, recognizer.Recognize(Frame("auto-uncertain"), area)?.State);
        Assert.Null(recognizer.Recognize(Frame("entry"), area));
    }

    /// <summary>复制到测试独占目录，避免生产全局语言回退重写已解析的日文路径。</summary>
    private string Resource(string name, bool common = false)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name + ".png");
        // AUTO 是语言无关的字形，明确验证通用素材，避免旧输出目录的日文覆盖文件改变测试含义。
        if (!File.Exists(path)) File.Copy(common
            ? Path.Combine(AppContext.BaseDirectory, "resources", "common", "templates", "stage", "sortie", name + ".png")
            : _resources.Resolve($"resources/templates/stage/sortie/{name}.png"), path);
        return path;
    }

    /// <summary>只回收当前测试创建的临时素材副本。</summary>
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    /// <summary>测试使用实机原帧，不将模板粘贴回画面制造命中。</summary>
    private static ScreenImage Frame(string name) => ImageCodec.DecodePng(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sortie", $"ja-progress-{name}.png")));
}

using System.IO;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>历史关卡日文素材的独立实机帧验证；局部截图仅补黑边恢复原坐标，不拼贴模板制造命中。</summary>
public sealed class JapaneseHistoryTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("ggea-ja-history-");

    /// <summary>复制本次语言素材与通用数值、锁图标，避免测试修改全局语言或跨测试竞争。</summary>
    public JapaneseHistoryTests()
    {
        var resources = new ResourceResolver(AppContext.BaseDirectory, "ja");
        foreach (var name in new[] { "history_stage", "history_stage_nodes", "history_go", "history_progress_label", "history_stage_100", "history_boss_100", "history_boss_lock" })
            File.Copy(resources.Resolve($"resources/templates/stage/{name}.png"), Path.Combine(_temp.FullName, name + ".png"));
    }

    /// <summary>列表、总览和节点页的独立帧均能识别，且不能将主要关卡标题视为历史关卡。</summary>
    [Fact]
    public void Title_RecognizesIndependentList_RejectsMainStage()
    {
        var title = new SelectStageTitleRecognizer(Path.Combine(_temp.FullName, "history_stage.png"));
        Assert.True(title.Recognize(Frame("title-list", 0, 0), default));
        Assert.True(title.Recognize(Frame("title-overview", 0, 0), default));
        Assert.False(title.Recognize(Frame("title-negative", 0, 0), default));
        var nodes = new SelectStageTitleRecognizer(Path.Combine(_temp.FullName, "history_stage_nodes.png"));
        Assert.True(nodes.Recognize(Frame("title-nodes", 0, 0), default));
        Assert.False(nodes.Recognize(Frame("title-negative", 0, 0), default));
    }

    /// <summary>日文前往关卡素材仍在既有按钮区域识别；总览不能被误认为作品列表。</summary>
    [Fact]
    public void Go_RecognizesIndependentButton_RejectsOverview()
    {
        var images = new TemplateImages(Path.Combine(_temp.FullName, "history_go.png"));
        var matcher = new TemplateMatcher();
        var roi = new Roi(1490, 830, 380, 130);
        Assert.NotNull(images.Match(matcher, Frame("go", 1490, 830), .9, roi, default));
        Assert.Null(images.Match(matcher, Frame("progress", 1040, 220), .9, roi, default));
    }

    /// <summary>日文攻略度结合通用数值和锁图标，应确认 0% 未完成及 BOSS 锁定，不能误报完成。</summary>
    [Fact]
    public void Overview_RecognizesIncompleteAndLocked_WithCommonSymbols()
    {
        using var vision = new HistoryProgressVision(_temp.FullName);
        var frame = Frame("progress", 1040, 220);
        Assert.True(vision.IsOverview(frame, default));
        Assert.Equal(new HistoryProgressObservation(HistoryCompletion.Incomplete, HistoryCompletion.Locked), vision.Inspect(frame, default));
        Assert.False(vision.IsOverview(Frame("title-list", 0, 0), default));
    }

    /// <summary>普通版停止时的实际日服总览，完整保留宽字形 100%；裁掉首位不能继续认作完成。</summary>
    [Fact]
    public void Overview_WideJapaneseHundred_RequiresAllDigits()
    {
        using var vision = new HistoryProgressVision(_temp.FullName);
        var frame = Frame("stage-complete", 1040, 220);
        Assert.Equal(new HistoryProgressObservation(HistoryCompletion.Complete, HistoryCompletion.Incomplete), vision.Inspect(frame, default));
        for (var y = 650; y < 686; y++) Array.Clear(frame.Bgra, (y * 1920 + 1305) * 4, 25 * 4);
        Assert.NotEqual(HistoryCompletion.Complete, vision.Inspect(frame, default).Stage);
    }

    /// <summary>只回收测试自身创建的临时素材。</summary>
    public void Dispose() => _temp.Delete(true);

    /// <summary>保留实机裁剪像素及位置，未采集区域补黑，原始整帧保存在采集目录。</summary>
    private static ScreenImage Frame(string name, int x, int y)
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Navigation", $"ja-history-{name}.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        for (var row = 0; row < crop.Height; row++)
            Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, pixels, ((y + row) * 1920 + x) * 4, crop.Width * 4);
        return ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080));
    }
}

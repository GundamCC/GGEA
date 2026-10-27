using System.IO;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>攻略度真实截图回放；合成修改只用于缺损和状态组合的回归，不作为实机证据。</summary>
public sealed class HistoryProgressTests
{
    /// <summary>缺百分号、错误文字、低置信度及未经模板确认的 100% 均不得授权推进。</summary>
    [Theory]
    [InlineData("100%", .99)]
    [InlineData("27", .99)]
    [InlineData("O%", .99)]
    [InlineData("27%", .80)]
    public void OcrUncertainPercent_RemainsUnknown(string text, double confidence)
        => Assert.Equal(HistoryCompletion.Unknown,
            HistoryProgressVision.ClassifyPercent(new(text, confidence, confidence)));

    private static HistoryProgressVision Vision() => new(Path.Combine(AppContext.BaseDirectory, "resources/common/templates/stage"));

    /// <summary>完整页为双 100%；用户提供的未解锁页为关卡未完成且 BOSS 锁定。</summary>
    [Theory]
    [InlineData("complete", HistoryCompletion.Complete, HistoryCompletion.Complete, HistoryProgressStep.Finished)]
    [InlineData("locked", HistoryCompletion.Incomplete, HistoryCompletion.Locked, HistoryProgressStep.Stage)]
    [InlineData("xastray", HistoryCompletion.Complete, HistoryCompletion.Complete, HistoryProgressStep.Finished)]
    public void ActualCards_RecognizeProgress(string name, HistoryCompletion stage, HistoryCompletion boss, HistoryProgressStep step)
    {
        using var vision = Vision();
        var observed = vision.Inspect(Frame(name), default);
        Assert.Equal(stage, observed.Stage);
        Assert.Equal(boss, observed.Boss);
        Assert.Equal(step, HistoryProgressDecision.Decide(observed));
    }

    /// <summary>用真实 27% 数字替换 BOSS 数字，验证已解锁但未完成时的第二阶段计划。</summary>
    [Fact]
    public void UnlockedIncompleteBoss_SelectsBossAfterStageComplete()
    {
        var screen = Frame("complete"); var source = Frame("locked");
        Copy(source, HistoryProgressVision.StageRegion, screen.Bgra, 1920, HistoryProgressVision.BossRegion.X, HistoryProgressVision.BossRegion.Y);
        using var vision = Vision();
        var observed = vision.Inspect(screen, default);
        Assert.Equal(HistoryCompletion.Complete, observed.Stage);
        Assert.Equal(HistoryCompletion.Incomplete, observed.Boss);
        Assert.Equal(HistoryProgressStep.Boss, HistoryProgressDecision.Decide(observed));
    }

    /// <summary>数字区域被遮挡不能解释为未达 100%，也不能因此开始推图。</summary>
    [Fact]
    public void MissingDigits_StayUnknown()
    {
        var screen = Frame("complete");
        for (var y = 650; y < 686; y++) Array.Clear(screen.Bgra, (y * 1920 + 1315) * 4, 100 * 4);
        using var vision = Vision();
        var observed = vision.Inspect(screen, default);
        Assert.Equal(HistoryCompletion.Unknown, observed.Stage);
        Assert.Equal(HistoryProgressStep.Unknown, HistoryProgressDecision.Decide(observed));
        Assert.Throws<OperationCanceledException>(() => vision.Inspect(screen, new(true)));
    }

    /// <summary>100% 但锁未消失时不点 BOSS；任何未知关卡进度都不能跳过到 BOSS。</summary>
    [Theory]
    [InlineData(HistoryCompletion.Complete, HistoryCompletion.Locked)]
    [InlineData(HistoryCompletion.Unknown, HistoryCompletion.Incomplete)]
    [InlineData(HistoryCompletion.Complete, HistoryCompletion.Unknown)]
    public void UncertainOrLockedProgress_DoesNotAuthorizeNextPart(HistoryCompletion stage, HistoryCompletion boss)
        => Assert.Equal(HistoryProgressStep.Unknown, HistoryProgressDecision.Decide(new(stage, boss)));

    /// <summary>将实机卡片裁剪放回原始位置，黑底区域不参与数字识别。</summary>
    private static ScreenImage Frame(string name)
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation", $"history-progress-{name}.png")));
        var pixels = new byte[1920 * 1080 * 4];
        Copy(crop, new(0, 0, crop.Width, crop.Height), pixels, 1920, 1040, 220);
        return ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080));
    }

    /// <summary>逐行复制保留原始像素，不缩放测试素材。</summary>
    private static void Copy(ScreenImage source, Roi roi, byte[] target, int width, int x, int y)
    {
        for (var row = 0; row < roi.Height; row++)
            Buffer.BlockCopy(source.Bgra, ((roi.Y + row) * source.Width + roi.X) * 4,
                target, ((y + row) * width + x) * 4, roi.Width * 4);
    }
}

using System.IO;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>通用 OK 素材兼容实际故事奖励按钮；原尺寸和校准尺寸均保留双重校验。</summary>
public sealed class CommonOkButtonTests
{
    /// <summary>实帧的小尺寸按钮使用同一素材的 98% 内存版本，保持原有 90% 门槛。</summary>
    [Fact]
    public void CommonOk_RecognizesRealRewardWithBoundedScale()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "resources/common/templates/dialog/button_ok.png");
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Sortie/watch-reward-ok-mumu5556.png")));
        var recognizer = new StageEntryButtonRecognizer();
        var region = RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, screen.Width, screen.Height);
        Assert.Null(recognizer.Match(screen, path, region, .90, default));
        var hit = recognizer.Match(screen, path, region, .90, default, templateScale: .98);
        Assert.NotNull(hit);
        Assert.True(hit.Confidence >= .90);
        Assert.InRange(hit.CenterX, 950, 970);
        Assert.InRange(hit.CenterY, 985, 1005);
    }
}
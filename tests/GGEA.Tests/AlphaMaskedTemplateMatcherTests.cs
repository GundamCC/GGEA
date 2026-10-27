using System.IO;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>透明背景不参与评分，但完整笔画、有效纹理、区域与取消仍是必要条件。</summary>
public sealed class AlphaMaskedTemplateMatcherTests
{
    private static ScreenImage Title => ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory, "resources/common/templates/stage/history_stage.png")));

    /// <summary>只改变模板透明位置对应的画面，匹配结果应保持不变；缺字则不得命中。</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(255, false)]
    [InlineData(255, true)]
    public void Match_IgnoresBackgroundButRequiresCompleteGlyphs(byte background, bool missingCharacter)
    {
        var title = Title;
        var pixels = TestImageFactory.CreateBgra(title.Width, title.Height, background);
        for (var i = 0; i < title.Width * title.Height; i++)
            if (title.Bgra[i * 4 + 3] >= 128)
            {
                Buffer.BlockCopy(title.Bgra, i * 4, pixels, i * 4, 4);
                if (missingCharacter && i % title.Width > title.Width * 3 / 4)
                    pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = 0;
            }
        var screen = ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, title.Width, title.Height));
        var match = new AlphaMaskedTemplateMatcher(title).Match(screen, new(0, 0, title.Width, title.Height), .90);
        Assert.Equal(!missingCharacter, match is not null);
    }

    /// <summary>纯色窗口不能依靠笔画覆盖率伪装标题，区域不足与取消也必须停止。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void Match_RejectsUniformFramesAndHonorsBoundsAndCancellation(byte value)
    {
        var title = Title;
        var matcher = new AlphaMaskedTemplateMatcher(title);
        var screen = ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1000, 120, value), 1000, 120));
        Assert.Null(matcher.Match(screen, new(0, 0, 1000, 120), .90));
        Assert.Null(matcher.Match(title, new(0, 0, title.Width - 1, title.Height), .90));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => matcher.Match(screen, new(0, 0, 1000, 120), .90, cancellation.Token));
    }
}

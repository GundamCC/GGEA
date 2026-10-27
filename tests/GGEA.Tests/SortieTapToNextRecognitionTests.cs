using System.IO;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>使用离线模型和实机文字回归，验证底部全宽、完整文字和误点击边界。</summary>
public sealed class SortieTapToNextRecognitionTests : IDisposable
{
    private readonly OnnxTextRecognizer _ocr = new();
    private OnnxTapToNextRecognizer Recognizer => new(_ocr, new TestLog());
    public void Dispose() => _ocr.Dispose();

    /// <summary>奖励机体的白色模型粘连 NEXT 笔画；正常略过与推图区域都必须识别完整文字。</summary>
    [Theory]
    [InlineData(RecognitionRegion.BottomButtons)]
    [InlineData(RecognitionRegion.ProgressTapToNext)]
    public async Task FierceUnitReward_ModelOverlappingText_IsRecognized(RecognitionRegion region)
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Sortie/fierce-unit-reward-tap-mumu16448.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        Buffer.BlockCopy(crop.Bgra, 0, pixels, 780 * 1920 * 4, crop.Bgra.Length);
        var match = await Recognizer.RecognizeAsync(ImageCodec.EncodePng(pixels, 1920, 1080), region: region);
        Assert.NotNull(match);
        Assert.InRange(match.CenterX, 950, 970);
        Assert.InRange(match.CenterY, 1020, 1035);
        Assert.True(match.Confidence >= OnnxTapToNextRecognizer.AverageThreshold);
    }

    /// <summary>MuMu 功能解放真实文字在底部 200 像素之外；只扩大推图专用区域，其他任务维持原范围。</summary>
    [Fact]
    public async Task FeatureUnlock_ProgressRegionRecognizesWithoutChangingDefault()
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Sortie/feature-unlock-tap-mumu5558.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        Buffer.BlockCopy(crop.Bgra, 0, pixels, 780 * 1920 * 4, crop.Bgra.Length);
        var png = ImageCodec.EncodePng(pixels, 1920, 1080);
        Assert.Null(await Recognizer.RecognizeAsync(png));
        var match = await Recognizer.RecognizeAsync(png, region: RecognitionRegion.ProgressTapToNext);
        Assert.NotNull(match);
        Assert.InRange(match.CenterX, 940, 980);
        Assert.InRange(match.CenterY, 840, 880);
    }

    /// <summary>扩大后的上边界仍拒绝区域外和被截断的文字，不放宽完整行约束。</summary>
    [Theory]
    [InlineData(700)]
    [InlineData(760)]
    public async Task ProgressRegion_OutsideOrClippedText_IsRejected(int y)
        => Assert.Null(await Recognizer.RecognizeAsync(SortieTapToNextSamples.CreateFrame(y: y),
            region: RecognitionRegion.ProgressTapToNext));

    /// <summary>背景与横向位置变化时，仍读取完整文字并点击字形范围的中心。</summary>
    [Theory]
    [InlineData(-1, 100)]
    [InlineData(80, 724)]
    [InlineData(160, 1300)]
    public async Task RealText_OnDifferentBackgrounds_MatchesCorrectLocation(int background, int x)
    {
        var match = await Recognizer.RecognizeAsync(SortieTapToNextSamples.CreateFrame(background, x: x));
        Assert.NotNull(match);
        Assert.InRange(match.CenterX, x + 214, x + 218);
        Assert.InRange(match.CenterY, 968, 974);
        Assert.True(match.Confidence >= .90);
    }

    /// <summary>没有文字的深浅背景都不得点击。</summary>
    [Theory]
    [InlineData(20)]
    [InlineData(160)]
    [InlineData(255)]
    public async Task NoText_RejectsFlatBackground(byte value)
        => Assert.Null(await Recognizer.RecognizeAsync(ImageCodec.EncodePng(
            TestImageFactory.CreateBgra(1920, 1080, value), 1920, 1080)));

    /// <summary>区域外或被底部区域上缘截断的字形都不能授权继续。</summary>
    [Theory]
    [InlineData(700)]
    [InlineData(860)]
    public async Task TextOutsideSortieRegion_IsNotRecognized(int y)
        => Assert.Null(await Recognizer.RecognizeAsync(SortieTapToNextSamples.CreateFrame(y: y)));

    /// <summary>两个合格提示存在歧义时不能挑一个点击。</summary>
    [Fact]
    public async Task MultipleCompleteLines_AreRejected()
    {
        var first = ImageCodec.DecodePng(SortieTapToNextSamples.CreateFrame(x: 100));
        var second = ImageCodec.DecodePng(SortieTapToNextSamples.CreateFrame(x: 1300));
        for (var y = 880; y < 1080; y++)
            Buffer.BlockCopy(second.Bgra, (y * 1920 + 1200) * 4, first.Bgra, (y * 1920 + 1200) * 4, 720 * 4);
        Assert.Null(await Recognizer.RecognizeAsync(ImageCodec.EncodePng(first.Bgra, 1920, 1080)));
    }

    /// <summary>旧文件虽然叫 TAP TO NEXT，实际内容是机体名称，不能授权继续。</summary>
    [Fact]
    public async Task IncorrectlyNamedAsset_IsRejected()
    {
        var asset = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath("resources/templates/entry/tap_to_next.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var row = 0; row < asset.Height; row++)
            Buffer.BlockCopy(asset.Bgra, row * asset.Width * 4, pixels, ((940 + row) * 1920 + 800) * 4, asset.Width * 4);
        Assert.Null(await Recognizer.RecognizeAsync(ImageCodec.EncodePng(pixels, 1920, 1080)));
    }

    /// <summary>取消的观察不能产生识别结果。</summary>
    [Fact]
    public async Task CanceledObservation_DoesNotRecognize()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Recognizer.RecognizeAsync(
            SortieTapToNextSamples.CreateFrame(), new CancellationToken(true)));

    /// <summary>重建自动化会话不能释放借用模型；独立会话则负责释放自己的原生推理资源。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomationSession_DisposesOnlyOwnedOcr(bool borrowed)
    {
        var frame = SortieTapToNextSamples.CreateFrame();
        using var automation = new GGEA.App.Automation.AutomationService(new FakeAdbService(frame),
            new TemplateMatcher(), new TestLog(), textOcr: borrowed ? _ocr : null);
        Assert.NotNull(await automation.TapToNext.RecognizeAsync(frame));
        automation.Dispose();
        if (borrowed) Assert.NotNull(await Recognizer.RecognizeAsync(frame));
        else Assert.Throws<ObjectDisposedException>(() => automation.TapToNext.Validate());
    }

    /// <summary>只容忍空白和外侧短横线，不通过关键词、字母替换或低分猜测。</summary>
    [Theory]
    [InlineData("- TAP TO NEXT -", .99, .99, true)]
    [InlineData("taptonext", .99, .99, true)]
    [InlineData("TAP T0 NEXT", .99, .99, false)]
    [InlineData("NEXT", .99, .99, false)]
    [InlineData("TAP TO NEXT EXTRA", .99, .99, false)]
    [InlineData("TAP TO NEXT", .89, .99, false)]
    [InlineData("TAP TO NEXT", .99, .84, false)]
    public void CompleteTextAndConfidence_AreRequired(string text, double average, double minimum, bool expected)
        => Assert.Equal(expected, OnnxTapToNextRecognizer.IsAccepted(new(text, average, minimum)));
}

/// <summary>原始用户裁剪和显式标记的背景模拟辅助，不依赖真实模拟器或外部图像服务。</summary>
internal static class SortieTapToNextSamples
{
    public static string TemplatePath => ConfigService.ResolvePath("resources/templates/stage/sortie/tap_to_next.png");

    /// <summary>在指定位置放置裁剪；非负背景参数仅用于合成亮度压力，不宣称该位置来自全屏截图。</summary>
    public static byte[] CreateFrame(int background = -1, int x = 724, int y = 940)
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "tap-to-next-background.png")));
        if (background >= 0)
        {
            for (var pixel = 0; pixel < crop.Bgra.Length; pixel += 4)
            {
                // 由原画面暗底约 34、亮字约 242 估算覆盖率，只调整底色和字缘混合像素。
                var brightness = Math.Max(crop.Bgra[pixel], Math.Max(crop.Bgra[pixel + 1], crop.Bgra[pixel + 2]));
                var coverage = Math.Clamp((brightness - 34) / 208.0, 0, 1);
                for (var channel = 0; channel < 3; channel++)
                    crop.Bgra[pixel + channel] = (byte)Math.Clamp(
                        crop.Bgra[pixel + channel] + (background - 34) * (1 - coverage), 0, 255);
            }
        }
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var row = 0; row < crop.Height; row++)
            Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, pixels,
                ((y + row) * 1920 + x) * 4, crop.Width * 4);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }
}

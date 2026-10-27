using System.Diagnostics;
using System.IO;
using GGEA.App.Config;
using GGEA.App.Vision;
using Xunit.Abstractions;

namespace GGEA.Tests;

/// <summary>验证推图加速仍遵守原灰度评分、透明遮罩与阈值，并以同一实机截图比较搜索耗时。</summary>
public sealed class ProgressTemplateMatcherTests(ITestOutputHelper output)
{
    /// <summary>固定灰度差有明确分数；透明背景不计分，低于阈值及区域外目标不能授权。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactScoreAlphaAndThreshold_MatchExistingRules(bool transparent)
    {
        var directory = Directory.CreateTempSubdirectory("ggea-progress-match-");
        try
        {
            var pixels = TestImageFactory.CreateBgra(32, 24, 0);
            var random = new Random(814);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)random.Next(40, 210);
                if (transparent && i / 4 % 3 == 0) pixels[i + 3] = 0;
            }
            var path = Path.Combine(directory.FullName, "target.png");
            File.WriteAllBytes(path, ImageCodec.EncodePng(pixels, 32, 24));
            var frame = TestImageFactory.CreateBgra(128, 96, 5);
            for (var y = 0; y < 24; y++)
                for (var x = 0; x < 32; x++)
                {
                    var source = (y * 32 + x) * 4;
                    if (pixels[source + 3] == 0) continue;
                    var target = ((y + 36) * 128 + x + 48) * 4;
                    frame[target] = frame[target + 1] = frame[target + 2] = (byte)(pixels[source] + 12);
                }
            var screen = ImageCodec.DecodePng(ImageCodec.EncodePng(frame, 128, 96));
            var area = new Roi(8, 6, 90, 70);
            using var accelerated = new ProgressTemplateMatcher();
            var actual = accelerated.Match(screen, path, area, .90, default);
            var expected = new TemplateMatcher().Match(screen.Bgra, 128, 96, pixels, 32, 24, .90, area);
            Assert.NotNull(actual);
            Assert.NotNull(expected);
            Assert.Equal(expected.Region, actual.Region);
            Assert.Equal(expected.Confidence, actual.Confidence, 10);
            Assert.Equal(1 - 12.0 / 255, actual.Confidence, 10);
            Assert.Null(accelerated.Match(screen, path, area, .96, default));
            Assert.Null(accelerated.Match(screen, path, new Roi(0, 0, 32, 24), .90, default));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => accelerated.Match(screen, path, area, .90, cancellation.Token));
            accelerated.Dispose();
            Assert.Throws<ObjectDisposedException>(() => accelerated.Match(screen, path, area, .90, default));
        }
        finally { directory.Delete(recursive: true); }
    }

    /// <summary>同一 1920×1080 实机画面的常用大区未命中也要快速返回；度量不以脆弱的硬件时间断言代替行为验证。</summary>
    [Fact]
    public void RealFrame_CompareMatchingCostAndAcceptance()
    {
        var original = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Shop", "free_confirm.png")));
        string Resolve(string name) => ConfigService.ResolvePath($"resources/templates/stage/sortie/{name}.png");
        var cases = new[] {
            (Path: Resolve("menu"), Area: new Roi(1595, 0, 325, 100)),
            (Path: Resolve("skip"), Area: new Roi(960, 0, 960, 540)),
            (Path: Resolve("story"), Area: new Roi(1440, 810, 480, 270)),
            (Path: Resolve("title_select_stage"), Area: new Roi(0, 0, 1000, 120)),
            (Path: ConfigService.ResolvePath("resources/templates/common/close.png"), Area: new Roi(0, 880, 1920, 200)),
            (Path: ConfigService.ResolvePath("resources/templates/stage/series_complete_title.png"), Area: new Roi(536, 100, 850, 150)) };
        var images = cases.Select(item => ImageCodec.DecodePng(File.ReadAllBytes(item.Path))).ToArray();
        var frames = Enumerable.Range(0, 3).Select(_ => new ScreenImage {
            Bitmap = original.Bitmap, Bgra = (byte[])original.Bgra.Clone(), Width = original.Width, Height = original.Height }).ToArray();
        var reference = new TemplateMatcher();
        using var accelerated = new ProgressTemplateMatcher();
        // 预热排除首次 DLL 装载，实际计时仍为每张新帧重新转换灰度，而不是复用上一帧结果。
        accelerated.Match(original, cases[0].Path, cases[0].Area, .90, default);
        reference.Match(original.Bgra, original.Width, original.Height, images[0].Bgra,
            images[0].Width, images[0].Height, .90, cases[0].Area);
        var expected = new TemplateMatchResult?[frames.Length, cases.Length];
        var timer = Stopwatch.StartNew();
        for (var f = 0; f < frames.Length; f++)
            for (var i = 0; i < cases.Length; i++)
                expected[f, i] = reference.Match(frames[f].Bgra, frames[f].Width, frames[f].Height,
                    images[i].Bgra, images[i].Width, images[i].Height, .90, cases[i].Area);
        var before = timer.Elapsed.TotalMilliseconds / frames.Length;
        timer.Restart();
        for (var f = 0; f < frames.Length; f++)
            for (var i = 0; i < cases.Length; i++)
            {
                var actual = accelerated.Match(frames[f], cases[i].Path, cases[i].Area, .90, default);
                Assert.Equal(expected[f, i] is null, actual is null);
                if (actual is not null) Assert.True(actual.Confidence >= .90);
            }
        var after = timer.Elapsed.TotalMilliseconds / frames.Length;
        output.WriteLine($"Six ROI searches per new frame: C# {before:F1} ms; native SSD {after:F1} ms; reduction {100 * (1 - after / before):F1}%. Excludes ADB/OCR/game rendering.");
    }
}

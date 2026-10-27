using System.IO;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 主要关卡作品识别器测试：识别当前作品与可见作品列表。
/// </summary>
public class StageWorkRecognizerTests
{
    [Fact]
    public async Task RecognizeAsync_VisibleWorks_ReturnsCurrentBest()
    {
        // 屏幕上两张作品卡片：A 在中心附近（高置信度），B 在边缘（较低置信度）
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 300, 200, 800, 400, 200); // A
        TestImageFactory.PasteBlock(screen, 1920, 300, 200, 1300, 700, 150); // B

        using var temp = new TempDirectory();
        var dir = Path.Combine(temp.Path, "stage");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "work_a.png"), ImageCodec.EncodePng(TestImageFactory.CreateBgra(300, 200, 200), 300, 200));
        File.WriteAllBytes(Path.Combine(dir, "work_b.png"), ImageCodec.EncodePng(TestImageFactory.CreateBgra(300, 200, 150), 300, 200));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var recognizer = new StageWorkRecognizer(fakeAdb, new TemplateMatcher(), new TestLog(), dir);

        var matches = await recognizer.RecognizeAsync("serial", threshold: 0.9);

        Assert.Equal(2, matches.Count);
        var current = matches.Single(m => m.IsCurrent);
        Assert.Equal("work_a", current.Name);
        Assert.Equal(950, current.CenterX);
        Assert.Equal(500, current.CenterY);
    }

    [Fact]
    public async Task RecognizeAsync_NoMatch_ReturnsEmpty()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);

        using var temp = new TempDirectory();
        var dir = Path.Combine(temp.Path, "stage");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "work_a.png"), ImageCodec.EncodePng(TestImageFactory.CreateBgra(300, 200, 200), 300, 200));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var recognizer = new StageWorkRecognizer(fakeAdb, new TemplateMatcher(), new TestLog(), dir);

        var matches = await recognizer.RecognizeAsync("serial", threshold: 0.9);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task FindAsync_TargetInExpectedRoi_ReturnsOnlyRequestedWork()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 300, 200, 800, 300, 180);

        using var temp = new TempDirectory();
        var dir = Path.Combine(temp.Path, "stage");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(
            Path.Combine(dir, "work_a.png"),
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(300, 200, 180), 300, 200));

        var fakeAdb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var recognizer = new StageWorkRecognizer(fakeAdb, new TemplateMatcher(), new TestLog(), dir);

        var match = await recognizer.FindAsync(
            "serial",
            "work_a",
            new Roi(650, 200, 700, 500),
            threshold: 0.9);

        Assert.NotNull(match);
        Assert.Equal("work_a", match!.Name);
        Assert.Equal(950, match.CenterX);
        Assert.Equal(400, match.CenterY);
    }

    /// <summary>测试用临时目录，自动清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }
    }
}

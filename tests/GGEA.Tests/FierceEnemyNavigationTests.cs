using System.IO;
using System.IO.Compression;
using System.Text.Json;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.Tests;

/// <summary>新 Boss 选中光效的真实局部回放、导航边界及失败原帧取证，不连接模拟器。</summary>
public sealed class FierceEnemyNavigationTests
{
    [Theory]
    [InlineData("flash")]
    [InlineData("stable")]
    public void DarkHome_OverlappingHypothesesAreOneUnit(string state)
    {
        using var unit = Unit(24);
        var observation = unit.ObserveHomeSelection(ImageCodec.DecodePng(Home(state)), default);
        Assert.Equal(new Roi(320, 558, 204, 204), observation.Match);
        Assert.Contains(observation.Candidates, c => c.Decision == "matched" && c.Inliers >= 12);
        using var wrong = Unit(1);
        Assert.Null(wrong.FindHomeSelection(ImageCodec.DecodePng(Home(state))));
    }

    /// <summary>两个蓝圈出现相同立绘仍须拒绝，不能因去除同簇伪歧义而任取一处。</summary>
    [Fact]
    public void DarkHome_TwoCirclesRemainAmbiguous()
    {
        var frame = ImageCodec.DecodePng(Home("stable"));
        var patch = new byte[204 * 204 * 4];
        for (var y = 0; y < 204; y++)
            Buffer.BlockCopy(frame.Bgra, ((558 + y) * 1920 + 320) * 4, patch, y * 204 * 4, 204 * 4);
        Paste(frame.Bgra, new ScreenImage { Width = 204, Height = 204, Bgra = patch }, 1396, 558);
        using var unit = Unit(24);
        Assert.Null(unit.FindHomeSelection(frame));
    }

    /// <summary>首页首次动画遮挡时重读同页；恢复后只点目标蓝圈和关卡选择，不进入历史。</summary>
    [Fact]
    public async Task Navigate_HomeTransientMiss_RetriesAndConfirmsDetail()
    {
        var ready = Home("flash");
        var initial = Home(null);
        var detail = Detail();
        var adb = new FakeAdbService(initial);
        adb.ScreenshotProvider = () => adb.Taps.Count >= 2 ? detail : adb.ScreenshotCount <= 1 ? initial : ready;
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
        using var unit = Unit(24);
        var navigation = new FierceEnemyNavigationService(automation, log, TapToNextOcrSamples.SharedOcr,
            timeoutMs: 2000, pollIntervalMs: 1);
        Assert.True(await navigation.NavigateAsync("serial", unit, default));
        Assert.Equal(2, adb.Taps.Count);
        Assert.Equal((422, 660), adb.Taps[0]);
        Assert.InRange(adb.Taps[1].X, 1496, 1855);
        Assert.Empty(adb.Swipes);
    }

    /// <summary>历史未就绪不滑动；列表穷尽不点单位，两种失败都保存本轮首页及最后帧。</summary>
    [Theory]
    [InlineData(false, "history-not-ready", 0)]
    [InlineData(true, "history-unit-not-found", 10)]
    public async Task Navigate_FailedHistory_PreservesOriginalEvidence(bool historyReady, string stage, int swipes)
    {
        using var temp = new TempDirectory();
        var home = Home("stable");
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        if (historyReady) Paste(pixels, LoadTemplate(FierceEnemyNavigationService.HistoryTitleTemplate), 220, 18);
        var history = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(home);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? home : history;
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
        using var unit = Unit(1);
        var navigation = new FierceEnemyNavigationService(automation, log, TapToNextOcrSamples.SharedOcr,
            new RecognitionEvidence(temp.Path, log), timeoutMs: 250, pollIntervalMs: 1);
        Assert.False(await navigation.NavigateAsync("serial", unit, default));
        Assert.Equal(new[] { (1575, 415) }, adb.Taps);
        Assert.Equal(swipes, adb.Swipes.Count);
        using var zip = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(temp.Path, "*.zip")));
        using var json = JsonDocument.Parse(zip.GetEntry("diagnostic.json")!.Open());
        var context = json.RootElement.GetProperty("context");
        Assert.Equal(stage, context.GetProperty("stage").GetString());
        Assert.Equal(4, context.GetProperty("home").GetProperty("Candidates").GetArrayLength());
        Assert.Equal(ImageCodec.DecodePng(history).Bgra, ReadFrame(zip, "frame.png").Bgra);
        Assert.Equal(ImageCodec.DecodePng(home).Bgra, ReadFrame(zip, "home.png").Bgra);
        Assert.NotNull(zip.GetEntry("unit-primary.png"));
        Assert.Contains(log.Entries, e => e.Message.Contains("识图失败证据已保存"));
    }

    [Fact]
    public async Task Navigate_Cancellation_DoesNotWriteFailureOrContinue()
    {
        using var temp = new TempDirectory();
        var adb = new FakeAdbService(Home(null));
        using var cts = new CancellationTokenSource();
        adb.BeforeScreenshotAsync = (_, _) => { cts.Cancel(); return Task.CompletedTask; };
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
        using var unit = Unit(24);
        var navigation = new FierceEnemyNavigationService(automation, log, TapToNextOcrSamples.SharedOcr,
            new RecognitionEvidence(temp.Path, log), timeoutMs: 100, pollIntervalMs: 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation.NavigateAsync("serial", unit, cts.Token));
        Assert.Empty(adb.Taps);
        Assert.Empty(adb.Swipes);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    private static FierceEnemyUnitRecognizer Unit(int id) => new(FierceEnemyUnit.Load().Single(u => u.Id == id).TemplatePath);
    private static ScreenImage LoadTemplate(string path) => ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(path)));
    private static byte[] Home(string? state)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        if (state is not null)
            Paste(pixels, ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                $"Fixtures/Navigation/fierce-dark-home-{state}.png"))), 280, 520);
        Paste(pixels, LoadTemplate(FierceEnemyNavigationService.SelectStageTemplate), 1496, 832);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }

    /// <summary>组合现有单位素材与真实难度列，仅验证导航交接，不伪称完整实机画面。</summary>
    private static byte[] Detail()
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        using var source = Cv2.ImRead(FierceEnemyUnit.Load().Single(u => u.Id == 24).TemplatePath, ImreadModes.Unchanged);
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(), .6, .6);
        Paste(pixels, ImageCodec.DecodePng(resized.ToBytes(".png")), 180, 230);
        Paste(pixels, ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/fierce-difficulty-column.png"))), 966, 168);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }

    private static void Paste(byte[] pixels, ScreenImage patch, int x, int y)
    {
        for (var row = 0; row < patch.Height; row++)
            Buffer.BlockCopy(patch.Bgra, row * patch.Width * 4, pixels, ((y + row) * 1920 + x) * 4, patch.Width * 4);
    }

    private static ScreenImage ReadFrame(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return ImageCodec.DecodePng(bytes.ToArray());
    }

    /// <summary>仅清理由本用例创建的唯一临时目录。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-fierce-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

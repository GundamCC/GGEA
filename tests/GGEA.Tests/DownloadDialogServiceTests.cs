using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 下载界面服务测试：允许下载时点击下载、禁止下载时点击取消、无下载界面时不点击。
/// </summary>
public class DownloadDialogServiceTests
{
    private static (string Download, string Cancel) CreateTemplateFiles(TempDirectory temp)
    {
        var download = Path.Combine(temp.Path, "download.png");
        var cancel = Path.Combine(temp.Path, "cancel.png");
        File.WriteAllBytes(download, ImageCodec.EncodePng(TestImageFactory.CreateBgra(362, 127, 200), 362, 127));
        File.WriteAllBytes(cancel, ImageCodec.EncodePng(TestImageFactory.CreateBgra(200, 100, 90), 200, 100));
        return (download, cancel);
    }

    /// <summary>构造 1920x1080 下载界面截图：下载/取消按钮位于屏幕下方 30%。</summary>
    private static byte[] CreateDialogScreen()
    {
        var screen = TestImageFactory.CreateBgra(1920, 1080, 20);
        TestImageFactory.PasteBlock(screen, 1920, 362, 127, 700, 800, 200);  // 下载按钮
        TestImageFactory.PasteBlock(screen, 1920, 200, 100, 1150, 800, 90);  // 取消按钮
        return ImageCodec.EncodePng(screen, 1920, 1080);
    }

    private static DownloadDialogService CreateService(FakeAdbService adb, string download, string cancel)
        => new(
            new AutomationService(adb, new TemplateMatcher(), new TestLog()),
            new TemplateMatcher(),
            new TestLog(),
            download,
            cancel,
            threshold: 0.85);

    [Fact]
    public async Task HandleIfAppearsAsync_AllowDownload_TapsDownloadButton()
    {
        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var adb = new FakeAdbService(CreateDialogScreen());
        var service = CreateService(adb, files.Download, files.Cancel);

        var action = await service.HandleIfAppearsAsync(
            "serial", allowDownload: true, waitTimeoutMs: 2000, pollIntervalMs: 10);

        Assert.Equal(DownloadDialogAction.DownloadClicked, action);
        var tap = Assert.Single(adb.Taps);
        Assert.Equal(700 + 362 / 2, tap.X);
        Assert.Equal(800 + 127 / 2, tap.Y);
    }

    [Fact]
    public async Task HandleIfAppearsAsync_NotAllowed_TapsCancelButton()
    {
        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var adb = new FakeAdbService(CreateDialogScreen());
        var service = CreateService(adb, files.Download, files.Cancel);

        var action = await service.HandleIfAppearsAsync(
            "serial", allowDownload: false, waitTimeoutMs: 2000, pollIntervalMs: 10);

        Assert.Equal(DownloadDialogAction.CancelClicked, action);
        var tap = Assert.Single(adb.Taps);
        Assert.Equal(1150 + 200 / 2, tap.X);
        Assert.Equal(800 + 100 / 2, tap.Y);
    }

    [Fact]
    public async Task HandleIfAppearsAsync_NoDialog_ReturnsNotVisibleWithoutTapping()
    {
        using var temp = new TempDirectory();
        var files = CreateTemplateFiles(temp);
        var adb = new FakeAdbService(
            ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var service = CreateService(adb, files.Download, files.Cancel);

        var action = await service.HandleIfAppearsAsync(
            "serial", allowDownload: true, waitTimeoutMs: 100, pollIntervalMs: 10);

        Assert.Equal(DownloadDialogAction.NotVisible, action);
        Assert.Empty(adb.Taps);
    }

    /// <summary>可选按钮模板不可用时保留未识别结果与 WARN，不能提前报告任务链失败或点击。</summary>
    [Fact]
    public void Recognize_UnavailableTemplates_WarnsWithoutTapping()
    {
        using var temp = new TempDirectory();
        var adb = new FakeAdbService(CreateDialogScreen());
        var log = new TestLog();
        var service = new DownloadDialogService(new AutomationService(adb, new TemplateMatcher(), log),
            new TemplateMatcher(), log, Path.Combine(temp.Path, "missing.png"),
            Path.Combine(temp.Path, "missing-cancel.png"), 0.85);

        var result = service.Recognize(ImageCodec.DecodePng(CreateDialogScreen()));

        Assert.Null(result.Download);
        Assert.Null(result.Cancel);
        Assert.Empty(adb.Taps);
        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, entry => Assert.Equal("WARN", entry.Level));
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

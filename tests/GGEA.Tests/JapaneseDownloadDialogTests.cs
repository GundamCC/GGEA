using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>日服下载确认的独立原帧回放，验证标题、按钮和允许下载设置。</summary>
public sealed class JapaneseDownloadDialogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ggea-ja-download-" + Guid.NewGuid().ToString("N"));

    /// <summary>只回收本测试创建的独占素材副本。</summary>
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    /// <summary>只向 FakeAdb 发出输入，不下载真实游戏数据；禁止下载时仍只点击左侧取消。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualDialog_RespectsDownloadPermission(bool allowed)
    {
        Directory.CreateDirectory(_directory);
        var resolver = new ResourceResolver(AppContext.BaseDirectory, "ja");
        // 使用独占物理文件，防止并行测试的当前语言重新解析素材。
        string Copy(string relative, string name)
        {
            var path = Path.Combine(_directory, name);
            File.Copy(resolver.Resolve("resources/templates/" + relative), path);
            return path;
        }
        var download = Copy("dialog/download.png", "button.png");
        var cancel = Copy("dialog/cancel.png", "cancel.png");
        var hint = ImageCodec.DecodePng(File.ReadAllBytes(Copy("stage/sortie/download.png", "hint.png")));
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Sortie/ja-download-dialog.png"));
        var screen = ImageCodec.DecodePng(bytes);
        var matcher = new TemplateMatcher();
        // 完整说明句超过旧区域；复用生产区域，保留 0.90 阈值且不包含可变下载大小。
        var roi = SortieFlowService.DownloadHintRoi(screen);
        Assert.True(hint.Width > 666);
        Assert.Equal(1080, roi.Width);
        Assert.NotNull(matcher.Match(screen.Bgra, 1920, 1080, hint.Bgra, hint.Width, hint.Height, .90, roi));
        var other = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Sortie/ja-empty-warning.png")));
        Assert.Null(matcher.Match(other.Bgra, 1920, 1080, hint.Bgra, hint.Width, hint.Height, .90, roi));
        var adb = new FakeAdbService(bytes);
        var log = new TestLog();
        using var automation = new AutomationService(adb, matcher, log);
        var service = new DownloadDialogService(automation, matcher, log, download, cancel, .85);
        var buttons = service.Recognize(screen);
        Assert.NotNull(buttons.Download);
        Assert.NotNull(buttons.Cancel);
        // 导航不能只凭共用按钮外框授权下载；空位出击确认即使同色也必须拒绝。
        Assert.NotNull(service.RecognizeForNavigation(screen).Download);
        Assert.Null(service.RecognizeForNavigation(other).Download);
        // 全按钮保留共用外框；业务必须先匹配完整下载提示，不能仅凭蓝色按钮判定下载页面。
        var result = await service.HandleIfAppearsAsync("replay", allowed, waitTimeoutMs: 2000);
        Assert.Equal(allowed ? DownloadDialogAction.DownloadClicked : DownloadDialogAction.CancelClicked, result);
        var tap = Assert.Single(adb.Taps);
        Assert.InRange(tap.X, allowed ? 1100 : 700, allowed ? 1200 : 850);
        Assert.InRange(tap.Y, 800, 900);
    }
}

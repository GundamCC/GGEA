using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.Tests;

/// <summary>工厂只执行主界面恢复、共享开发导航及铁球点击，未命中、异常和取消不得继续制造。</summary>
public sealed class BallFactoryServiceTests
{
    /// <summary>只清理本测试创建的唯一临时目录。</summary>
    private sealed class TempDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ggea-ball-");
        public string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(true);
    }

    /// <summary>创建定位阶段请求；故意留空后续开发确认素材，证明不会提前执行制造。</summary>
    private static DevelopRequest Request(string roadmap) => new()
    {
        MainLayout = new UiLayout { Anchors = [new() { Name = "navDevelop", X = 700, Y = 950, Width = 40, Height = 40 }] },
        DevelopLayout = new UiLayout { Anchors = [new() { Name = "firstRoadmap", X = 500, Y = 250, Width = 200, Height = 100 }] },
        RoadmapFirstTemplatePath = roadmap,
        MsTemplatePath = "daily-custom-unit.png", MsLargeTemplatePath = "daily-custom-large.png",
        DevelopAllTemplatePath = "", DevelopAllDisabledTemplatePath = "", ExecuteDevelopTemplatePath = "",
        ExecuteConfirmTemplatePath = "", ShowcaseTapToNextTemplatePath = "", BackButtonTemplatePath = "",
        DevelopTitleTemplatePath = "", StepDelayMs = 1, NetworkSettleDelayMs = 1, MaxDragsPerDirection = 1,
    };

    /// <summary>主界面失败不进入开发；成功后只替换目标，不改动原请求或共享识别参数。</summary>
    [Theory]
    [InlineData(false, true, BallFactoryLocateResult.MainScreenUnavailable)]
    [InlineData(true, false, BallFactoryLocateResult.TargetNotFound)]
    [InlineData(true, true, BallFactoryLocateResult.TargetSelected)]
    public async Task Locate_UsesSharedStage_AndStopsAtSelection(bool home, bool found, BallFactoryLocateResult expected)
    {
        var calls = new List<string>();
        var develop = new DevelopProbe(calls, found);
        var factory = new BallFactoryService(develop, (serial, ct) =>
        {
            Assert.Equal("selected-device", serial);
            calls.Add("home");
            return Task.FromResult(home);
        });
        var request = Request(ConfigService.ResolvePath(BallFactoryService.UnitTemplate));
        Assert.Equal(expected, await factory.LocateAsync("selected-device", request, _ => { }));
        Assert.Equal(home ? new[] { "home", "select" } : new[] { "home" }, calls);
        if (home)
        {
            Assert.Equal(ConfigService.ResolvePath(BallFactoryService.UnitTemplate), develop.Request!.MsTemplatePath);
            Assert.Empty(develop.Request.MsLargeTemplatePath);
            Assert.Equal(request with { MsTemplatePath = develop.Request.MsTemplatePath, MsLargeTemplatePath = "" }, develop.Request);
            Assert.Equal("daily-custom-unit.png", request.MsTemplatePath);
        }
    }

    /// <summary>取消、缺失素材和异常保留原结果；没有主界面确认就不会发起目标搜索。</summary>
    [Theory]
    [InlineData("pre-cancel")]
    [InlineData("home-cancel")]
    [InlineData("missing")]
    [InlineData("exception")]
    public async Task Locate_FailureBoundaries_DoNotContinue(string scenario)
    {
        var calls = new List<string>();
        var develop = new DevelopProbe(calls, true);
        using var cancel = new CancellationTokenSource();
        if (scenario == "pre-cancel") cancel.Cancel();
        var factory = new BallFactoryService(develop, (_, _) =>
        {
            if (scenario == "exception") throw new IOException("capture failed");
            cancel.Cancel();
            return Task.FromResult(true);
        });
        var request = Request(scenario == "missing" ? Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png") :
            ConfigService.ResolvePath(BallFactoryService.UnitTemplate));
        var action = () => factory.LocateAsync("selected-device", request, _ => { }, cancel.Token);
        if (scenario == "missing") await Assert.ThrowsAsync<FileNotFoundException>(action);
        else if (scenario == "exception") await Assert.ThrowsAsync<IOException>(action);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        Assert.Empty(calls);
    }

    /// <summary>将用户铁球素材合成到测试画布，走生产导航与两帧识别；两档比例均只点击开发、作品和铁球。</summary>
    [Theory]
    [InlineData(1.3)]
    [InlineData(.832)]
    public async Task Locate_RealBallArtwork_UsesProductionSearchAndStops(double scale)
    {
        using var temp = new TempDirectory();
        var roadmap = Path.Combine(temp.Path, "roadmap.png");
        File.WriteAllBytes(roadmap, ImageCodec.EncodePng(TestImageFactory.CreateBgra(200, 100, 200), 200, 100));
        using var source = Cv2.ImRead(ConfigService.ResolvePath(BallFactoryService.UnitTemplate), ImreadModes.Unchanged);
        using var scaled = new Mat();
        Cv2.Resize(source, scaled, new Size((int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale)));
        using var color = new Mat();
        using var alpha = new Mat();
        Cv2.CvtColor(scaled, color, ColorConversionCodes.BGRA2BGR);
        Cv2.ExtractChannel(scaled, alpha, 3);
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(20, 20, 20));
        using (var region = new Mat(screen, new Rect(800, 400, scaled.Width, scaled.Height))) color.CopyTo(region, alpha);
        using (var region = new Mat(screen, new Rect(500, 250, 200, 100))) region.SetTo(new Scalar(200, 200, 200));
        var adb = new FakeAdbService(screen.ToBytes(".png"));
        var log = new TestLog();
        IDevelopService develop = new DevelopService(new AutomationService(adb, new TemplateMatcher(), log), log, true);
        var factory = new BallFactoryService(develop, (_, _) => Task.FromResult(true));
        Assert.Equal(BallFactoryLocateResult.TargetSelected, await factory.LocateAsync("selected-device", Request(roadmap), _ => { }));
        Assert.Equal(3, adb.Taps.Count);
        Assert.Equal((720, 970), adb.Taps[0]);
        Assert.Equal((600, 300), adb.Taps[1]);
        Assert.InRange(adb.Taps[2].X, 800, 800 + scaled.Width);
        Assert.InRange(adb.Taps[2].Y, 400, 400 + scaled.Height);
        Assert.True(adb.ScreenshotCount >= 3);
        Assert.Empty(adb.Swipes);
    }

    /// <summary>识别右下角按钮不发出点击；同样素材位于区域外时不得命中。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BulkButton_RespectsRoiAndNeverClicks(bool inside)
    {
        var path = ConfigService.ResolvePath(new AppConfig().DevelopAllTemplate);
        using var template = Cv2.ImRead(path);
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using (var region = new Mat(screen, new Rect(inside ? 1600 : 100, 935, template.Width, template.Height)))
            template.CopyTo(region);
        var adb = new FakeAdbService(screen.ToBytes(".png"));
        var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog());
        using var cancellation = new CancellationTokenSource();
        if (inside)
            Assert.NotNull(await BallFactoryService.WaitForBulkDevelopmentAsync(automation, "selected-device", path));
        else
        {
            cancellation.CancelAfter(1000);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BallFactoryService.WaitForBulkDevelopmentAsync(automation, "selected-device", path, cancellation.Token));
        }
        Assert.Empty(adb.Taps);
    }

    /// <summary>精确迁移官方旧按钮路径，保留用户自定义同名文件。</summary>
    [Theory]
    [InlineData("relative")]
    [InlineData("windows")]
    [InlineData("absolute")]
    [InlineData("custom")]
    public void BulkButton_MigratesOnlyOfficialOldPath(string kind)
    {
        using var temp = new TempDirectory();
        var old = "resources/templates/develop/button_develop_all.png";
        var path = kind switch
        {
            "windows" => old.Replace('/', '\\'),
            "absolute" => ConfigService.ResolvePath(old),
            "custom" => "custom/button_develop_all.png",
            _ => old,
        };
        var file = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { DevelopAllTemplate = path }));
        var config = new ConfigService(new TestLog()).Load(file);
        Assert.Equal(kind == "custom" ? path : new AppConfig().DevelopAllTemplate, config.DevelopAllTemplate);
    }

    /// <summary>仅记录共享选择入口；调用完整开发即失败，避免工厂越过已定义边界。</summary>
    private sealed class DevelopProbe(List<string> calls, bool found) : IDevelopService
    {
        public DevelopRequest? Request { get; private set; }
        public Task<TemplateMatchResult?> FindAndTapAsync(string serial, DevelopRequest request, CancellationToken ct = default)
        {
            Assert.Equal("selected-device", serial);
            ct.ThrowIfCancellationRequested();
            Request = request;
            calls.Add("select");
            return Task.FromResult(found ? new TemplateMatchResult(new Roi(800, 400, 100, 100), .99) : null);
        }
        public Task<DevelopResult> ConfirmAsync(string serial, DevelopRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("定位阶段不得确认开发");

        public Task<DevelopResult> ExecuteAsync(string serial, DevelopRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("不得执行完整开发");
    }
}

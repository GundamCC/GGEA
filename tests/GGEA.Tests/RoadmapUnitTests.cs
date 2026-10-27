using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;
using GGEA.App.Logging;
using OpenCvSharp;

namespace GGEA.Tests;

/// <summary>真实雷电和蓝叠立绘、透明模板与手势驱动地图共同覆盖识别和安全边界。</summary>
public sealed class RoadmapUnitTests
{
    /// <summary>只清理本测试创建的唯一目录。</summary>
    private sealed class TempDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ggea-roadmap-");
        public string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(true);
    }

    /// <summary>读取随测试分发的资源，避免依赖开发者机器路径。</summary>
    private static byte[] Template(string name = "ogk_g0070u00100") =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, $"resources/common/templates/develop/dev_unit/{name}.png"));

    /// <summary>将真实画面裁剪缩放后置于空画布，模拟进入路线图时保留的不同缩放比例。</summary>
    private static byte[] Frame(string name, double scale = 1)
    {
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, $"Fixtures/Roadmap/{name}.png"));
        using var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(), scale, scale, InterpolationFlags.Linear);
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(20, 20, 20));
        using var region = new Mat(screen, new Rect(700, 300, resized.Width, resized.Height));
        resized.CopyTo(region);
        return screen.ToBytes(".png");
    }

    /// <summary>真实立绘在大、小和中间比例都必须命中；非目标吉姆在相同比例不得命中。</summary>
    [Theory]
    [InlineData("target", 1.0)]
    [InlineData("after", 1.0)]
    [InlineData("after", 0.45)]
    [InlineData("after", 0.70)]
    public void ActualArtwork_AcrossScales_RejectsSimilarUnit(string fixture, double scale)
    {
        var recognizer = new RoadmapUnitRecognizer();
        var match = recognizer.Match(Frame(fixture, scale), Template(), 0.86);
        Assert.NotNull(match);
        Assert.InRange(match.CenterX, 700, 1140);
        Assert.InRange(match.CenterY, 300, 705);
        Assert.Null(recognizer.Match(Frame("before", scale), Template(), 0.86));
    }

    /// <summary>真实蓝叠小比例存在斜切，不能用把雷电大图缩小的合成样本替代。</summary>
    [Theory]
    [InlineData("ogk_g0070u00100")]
    public void BlueStacksActualSmall_CustomScalePathRecognizesNativeArtwork(string name)
    {
        var recognizer = new RoadmapUnitRecognizer();
        var match = recognizer.Match(Frame("bluestacks_small"), Template(name), 0.86);
        Assert.NotNull(match);
        Assert.InRange(match.CenterX, 780, 825);
        Assert.InRange(match.CenterY, 350, 420);
        Assert.Null(recognizer.Match(Frame("bluestacks_other_gundam"), Template(name), 0.86));
    }

    /// <summary>透明模板保留原尺寸、头部与躯干，外围光圈和星级不能重新进入匹配。</summary>
    [Theory]
    [InlineData("ogk_g0070u00100", 204, 180, 100, 50)]
    public void Templates_KeepArtworkAndRemoveBackground(string name, int width, int height, int x, int y)
    {
        var image = ImageCodec.DecodePng(Template(name));
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        // 原生 PNG 左上角带 Alpha=1 的导出残留；验证真实源值和最终遮罩，不能把它当作有效立绘。
        Assert.Equal(1, image.Bgra[3]);
        Assert.Equal(0, image.Bgra[((height - 1) * width + width - 1) * 4 + 3]);
        var prepared = new RoadmapUnitTemplate(Template(name));
        foreach (var variant in prepared.Preferred.Values)
        {
            using var mask = Cv2.ImDecode(variant.Mask, ImreadModes.Grayscale);
            Assert.Equal(0, mask.At<byte>(0, 0));
            Assert.Equal(0, mask.At<byte>(mask.Height - 1, mask.Width - 1));
            Assert.True(Cv2.CountNonZero(mask) > 32);
        }
        Assert.Equal(255, image.Bgra[(y * width + x) * 4 + 3]);
        var visible = Enumerable.Range(0, width * height).Count(i => image.Bgra[i * 4 + 3] == 255);
        Assert.InRange((double)visible / (width * height), 0.2, 0.5);
    }

    /// <summary>透明背景不能产生无穷高分；两个相同单位或过高阈值必须安全拒绝。</summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("ambiguous")]
    [InlineData("strict")]
    public void MaskedMatching_RejectsUncertainResults(string scenario)
    {
        var png = scenario == "dark"
            ? ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 0), 1920, 1080)
            : Frame("bluestacks_small");
        if (scenario == "ambiguous")
        {
            using var screen = Cv2.ImDecode(png, ImreadModes.Color);
            using var first = new Mat(screen, new Rect(700, 300, 190, 165));
            using var second = new Mat(screen, new Rect(1150, 300, 190, 165));
            first.CopyTo(second);
            png = screen.ToBytes(".png");
        }
        Assert.Null(new RoadmapUnitRecognizer().Match(png, Template("ogk_g0070u00100"),
            scenario == "strict" ? 0.99 : 0.86));
    }

    /// <summary>缩图提出候选后，原图精查仍应覆盖 ROI 四边附近的真实小比例立绘。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewRefinement_FindsArtworkNearRoiEdges(bool bottomRight)
    {
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures/Roadmap/bluestacks_small.png"));
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(20, 20, 20));
        var x = bottomRight ? 1880 - crop.Width : 360;
        var y = bottomRight ? 940 - crop.Height : 240;
        using var region = new Mat(screen, new Rect(x, y, crop.Width, crop.Height));
        crop.CopyTo(region);
        var match = new RoadmapUnitRecognizer().Match(screen.ToBytes(".png"), Template("ogk_g0070u00100"), .86,
            new Roi(360, 240, 1520, 700));
        Assert.NotNull(match);
        Assert.InRange(match.CenterX, x + 85, x + 115);
        Assert.InRange(match.CenterY, y + 60, y + 95);
    }

    /// <summary>原生素材仍支持自定义原比例识别，不依赖已经移除的截图素材。</summary>
    [Theory]
    [InlineData("ogk_g0070u00100")]
    public async Task NativeTemplate_CustomScalePathRemainsUsable(string name)
    {
        var template = ImageCodec.DecodePng(Template(name));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var y = 0; y < template.Height; y++)
            Buffer.BlockCopy(template.Bgra, y * template.Width * 4, pixels,
                ((300 + y) * 1920 + 700) * 4, template.Width * 4);
        var fake = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var automation = new AutomationService(fake, new TemplateMatcher(), new TestLog());
        var path = Path.Combine(AppContext.BaseDirectory, $"resources/common/templates/develop/dev_unit/{name}.png");
        var match = await automation.FindRoadmapImageAsync("ld", path, new WaitOptions { Threshold = 0.92 });
        Assert.NotNull(match);
        Assert.Equal(700 + template.Width / 2, match.CenterX);
        Assert.Empty(fake.Taps);
        Assert.Empty(fake.PinchInDurations);
    }

    /// <summary>原生素材只按两档固定比例识别真实 MuMu 大小画面和光圈变化。</summary>
    [Theory]
    [InlineData("mumu_large", 265, 234)]
    [InlineData("mumu_small", 170, 150)]
    [InlineData("mumu_small_glow", 170, 150)]
    public void NativeArtwork_RecognizesExactlyTwoDefaultScales(string fixture, int width, int height)
    {
        var prepared = new RoadmapUnitTemplate(Template("ogk_g0070u00100"));
        var match = new RoadmapUnitRecognizer().Match(Frame(fixture), prepared, .86, fixedScales: true);
        Assert.NotNull(match);
        Assert.Equal(width, match.Width);
        Assert.Equal(height, match.Height);
        Assert.InRange(match.CenterX, 700, 1020);
        Assert.InRange(match.CenterY, 300, 580);
    }

    /// <summary>MuMu 首次进入的真实小单位须命中，普通高达及重复目标仍拒绝。</summary>
    [Fact]
    public void NativeArtwork_InitialMuMuSmall_RecognizesAndRejectsUnsafeCandidates()
    {
        var prepared = new RoadmapUnitTemplate(Template());
        var recognizer = new RoadmapUnitRecognizer();
        var png = Frame("mumu_initial_small");
        var match = recognizer.Match(png, prepared, .86, fixedScales: true);
        Assert.NotNull(match);
        Assert.Equal(149, match.Width);
        Assert.Equal(131, match.Height);
        Assert.InRange(match.CenterX, 795, 810);
        Assert.InRange(match.CenterY, 380, 395);
        Assert.Null(recognizer.Match(Frame("mumu_initial_other"), prepared, .86, fixedScales: true));
        using var screen = Cv2.ImDecode(png, ImreadModes.Color);
        using var first = new Mat(screen, new Rect(700, 300, 200, 190));
        using var second = new Mat(screen, new Rect(1150, 300, 200, 190));
        first.CopyTo(second);
        Assert.Null(recognizer.Match(screen.ToBytes(".png"), prepared, .86, fixedScales: true));
    }

    /// <summary>原生模板替换不能放宽非目标、重复目标、严格阈值及取消的安全边界。</summary>
    [Theory]
    [InlineData("before")]
    [InlineData("bluestacks_other_gundam")]
    [InlineData("ambiguous")]
    [InlineData("cross_scale_ambiguous")]
    [InlineData("unsupported_scale")]
    [InlineData("dark")]
    [InlineData("strict")]
    [InlineData("cancel")]
    public void NativeArtwork_RejectsUnsafeCandidates(string scenario)
    {
        var png = Frame(scenario is "before" or "bluestacks_other_gundam" ? scenario : "mumu_small");
        if (scenario == "ambiguous")
        {
            using var screen = Cv2.ImDecode(png, ImreadModes.Color);
            using var first = new Mat(screen, new Rect(700, 300, 245, 210));
            using var second = new Mat(screen, new Rect(1150, 300, 245, 210));
            first.CopyTo(second);
            png = screen.ToBytes(".png");
        }
        if (scenario == "cross_scale_ambiguous")
        {
            using var screen = Cv2.ImDecode(png, ImreadModes.Color);
            using var original = Cv2.ImDecode(Template("ogk_g0070u00100"), ImreadModes.Unchanged);
            using var color = new Mat();
            using var alpha = new Mat();
            Cv2.CvtColor(original, color, ColorConversionCodes.BGRA2BGR);
            Cv2.ExtractChannel(original, alpha, 3);
            screen.SetTo(new Scalar(20, 20, 20));
            foreach (var item in new[] { (X: 700, W: 170, H: 150), (X: 1150, W: 265, H: 234) })
            {
                using var resized = new Mat();
                using var mask = new Mat();
                Cv2.Resize(color, resized, new Size(item.W, item.H));
                Cv2.Resize(alpha, mask, resized.Size());
                using var region = new Mat(screen, new Rect(item.X, 300, item.W, item.H));
                resized.CopyTo(region, mask);
            }
            png = screen.ToBytes(".png");
        }
        if (scenario == "unsupported_scale") png = Frame("after", .7);
        if (scenario == "dark") png = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 0), 1920, 1080);
        var prepared = new RoadmapUnitTemplate(Template("ogk_g0070u00100"));
        var recognizer = new RoadmapUnitRecognizer();
        if (scenario == "cancel")
            Assert.Throws<OperationCanceledException>(() => recognizer.Match(png, prepared, .86, ct: new CancellationToken(true), fixedScales: true));
        else Assert.Null(recognizer.Match(png, prepared, scenario == "strict" ? .99 : .86, fixedScales: true));
    }

    /// <summary>真实入口使用单一原图连续确认两帧；找到后不继续拖动，也不发送点击或缩放。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeArtwork_DefaultSearchConfirmsTwoRealFrames(bool upgradeLegacyConfig)
    {
        var fake = new FakeAdbService(Frame("mumu_small"));
        fake.EnqueueScreenshots(Frame("mumu_small"), Frame("mumu_small_glow"));
        var automation = new AutomationService(fake, new TemplateMatcher(), new TestLog());
        var config = new GGEA.App.Config.AppConfig();
        if (upgradeLegacyConfig)
        {
            using var temp = new TempDirectory();
            var configPath = Path.Combine(temp.Path, "old.json");
            // 只配过旧小图且关闭固定比例的用户也必须完整迁移，之后识别不再读取旧 PNG。
            File.WriteAllText(configPath, """{"developMsTemplate":"resources/templates/develop/dev_prototype_gundam.png","developMsFixedScales":false}""");
            config = new GGEA.App.Config.ConfigService(new TestLog()).Load(configPath);
        }
        var service = new DevelopService(automation, new TestLog(), config.DevelopMsFixedScales);
        var path = Path.Combine(AppContext.BaseDirectory, config.DevelopMsTemplate);
        var match = await service.FindOnRoadmapAsync("mumu", path, config.DevelopMsLargeTemplate);
        Assert.NotNull(match);
        Assert.Equal(2, fake.ScreenshotCount);
        Assert.Empty(fake.Taps);
        Assert.Empty(fake.Swipes);
        Assert.Empty(fake.PinchInDurations);
    }

    /// <summary>逐行扫描应覆盖四边之间的内部节点，并检查预算内最后一次拖动的新画面。</summary>
    [Fact]
    public async Task Search_SerpentineFindsInteriorNode_WithoutTappingOrZooming()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "unit.png");
        File.WriteAllBytes(path, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 200), 100, 100));
        var fake = new FakeAdbService([]);
        fake.ScreenshotProvider = () =>
        {
            var x = 2; var y = 2;
            foreach (var swipe in fake.Swipes)
            {
                x = Math.Clamp(x + Math.Sign(swipe.X1 - swipe.X2), 0, 2);
                y = Math.Clamp(y + Math.Sign(swipe.Y1 - swipe.Y2), 0, 2);
            }
            var pixels = TestImageFactory.CreateBgra(1920, 1080, (byte)((x + y) % 2 * 80));
            if (x == 1 && y == 1) TestImageFactory.PasteBlock(pixels, 1920, 100, 100, 900, 450, 200);
            return ImageCodec.EncodePng(pixels, 1920, 1080);
        };
        var service = new DevelopService(new AutomationService(fake, new TemplateMatcher(), new TestLog()), new TestLog());
        var match = await service.FindOnRoadmapAsync("ld", path, "", 3, 1);
        Assert.NotNull(match);
        Assert.Equal(950, match.CenterX);
        Assert.Contains(fake.Swipes, s => s.X2 > s.X1);
        Assert.Contains(fake.Swipes, s => s.X2 < s.X1);
        Assert.Contains(fake.Swipes, s => s.Y2 > s.Y1);
        Assert.Contains(fake.Swipes, s => s.Y2 < s.Y1);
        Assert.Empty(fake.Taps);
        Assert.Empty(fake.PinchInDurations);
    }

    /// <summary>尺寸不符、取消或标题消失时不得发送任何拖动或点击。</summary>
    [Theory]
    [InlineData("size")]
    [InlineData("title")]
    [InlineData("cancel")]
    public async Task InvalidContext_StopsWithoutInput(string failure)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "title.png");
        File.WriteAllBytes(path, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 50, 200), 100, 50));
        var width = failure == "size" ? 1280 : 1920;
        var fake = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(width, 1080, 20), width, 1080));
        var service = new DevelopService(new AutomationService(fake, new TemplateMatcher(), new TestLog()), new TestLog());
        var action = () => service.FindOnRoadmapAsync("ld", path, "", titleTemplatePath: path,
            ct: new CancellationToken(failure == "cancel"));
        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        else await Assert.ThrowsAsync<InvalidOperationException>(action);
        Assert.Empty(fake.Swipes);
        Assert.Empty(fake.Taps);
    }

    /// <summary>节点稀疏的画面仍需检测到位移，不能把大量空白当作到边界。</summary>
    [Fact]
    public void SparseNodeMovement_IsNotBounce()
    {
        var before = TestImageFactory.CreateBgra(1920, 1080, 20);
        var after = (byte[])before.Clone();
        TestImageFactory.PasteBlock(before, 1920, 140, 140, 600, 450, 220);
        TestImageFactory.PasteBlock(after, 1920, 140, 140, 1200, 450, 220);
        Assert.False(DevelopService.IsNearlySame(ImageCodec.EncodePng(before, 1920, 1080),
            ImageCodec.EncodePng(after, 1920, 1080)));
    }

    /// <summary>最后一次允许的拖动出现目标时必须识别，不能先耗尽循环再退出。</summary>
    [Fact]
    public async Task LastAllowedDrag_IsInspected()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "unit.png");
        File.WriteAllBytes(path, ImageCodec.EncodePng(TestImageFactory.CreateBgra(100, 100, 200), 100, 100));
        var fake = new FakeAdbService([]);
        fake.ScreenshotProvider = () =>
        {
            var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
            if (fake.Swipes.Count > 0) TestImageFactory.PasteBlock(pixels, 1920, 100, 100, 900, 450, 200);
            return ImageCodec.EncodePng(pixels, 1920, 1080);
        };
        var service = new DevelopService(new AutomationService(fake, new TemplateMatcher(), new TestLog()), new TestLog());
        Assert.NotNull(await service.FindOnRoadmapAsync("ld", path, "", 1, 100));
        Assert.Single(fake.Swipes);
        Assert.Empty(fake.Taps);
    }

    /// <summary>不同未知尺寸和起点均完整逐行扫描；归位撞边也检索目标，一次拖动只截一帧。</summary>
    [Theory]
    [InlineData(1, 1, 0, 0)]
    [InlineData(3, 2, 1, 1)]
    [InlineData(2, 3, 1, 2)]
    public async Task UnknownCanvas_ScansEveryCellOnceAfterHoming(int columns, int rows, int startX, int startY)
    {
        using var temp = new TempDirectory();
        var unit = Path.Combine(temp.Path, "unit.png");
        var title = Path.Combine(temp.Path, "title.png");
        File.WriteAllBytes(unit, ImageCodec.EncodePng(TestImageFactory.CreateBgra(40, 30, 250), 40, 30));
        File.WriteAllBytes(title, ImageCodec.EncodePng(TestImageFactory.CreateBgra(40, 30, 200), 40, 30));
        var fake = new FakeAdbService([]);
        var positions = new List<(int X, int Y)>();
        // 模拟器只按手势移动并夹紧边界；搜索服务不知道画布的尺寸和起点。
        (int X, int Y) Position()
        {
            var x = startX; var y = startY;
            foreach (var swipe in fake.Swipes)
            {
                x = Math.Clamp(x + Math.Sign(swipe.X1 - swipe.X2), 0, columns - 1);
                y = Math.Clamp(y + Math.Sign(swipe.Y1 - swipe.Y2), 0, rows - 1);
            }
            return (x, y);
        }
        fake.ScreenshotProvider = () =>
        {
            var (x, y) = Position();
            var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
            TestImageFactory.PasteBlock(pixels, 1920, 190, 170, 500 + x * 250, 350 + y * 150, 100);
            TestImageFactory.PasteBlock(pixels, 1920, 40, 30, 230, 30, 200);
            return ImageCodec.EncodePng(pixels, 1920, 1080);
        };
        var log = new ScanLog(() => positions.Add(Position()));
        var service = new DevelopService(new AutomationService(fake, new TemplateMatcher(), log), log);
        Assert.Null(await service.FindOnRoadmapAsync("test", unit, unit, 4, 100, titleTemplatePath: title));
        var expected = Enumerable.Range(0, rows).SelectMany(y => Enumerable.Range(0, columns)
            .Select(x => (X: y % 2 == 0 ? x : columns - 1 - x, Y: y))).ToList();
        // 纵向归位到顶边的新画面也必须检索，不能用它与前一帧相似就跳过。
        if (startX != 0 && startY != 0) expected.Insert(0, (startX, 0));
        if (startX != 0 || startY != 0) expected.Insert(0, (startX, startY));
        Assert.Equal(expected, positions);
        Assert.Equal(fake.Swipes.Count + 1, fake.ScreenshotCount);
        Assert.Empty(fake.Taps);
        Assert.Empty(fake.PinchInDurations);
    }

    /// <summary>归位也必须逐帧检查标题；省略单位匹配不能省略页面和取消保护。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Homing_StopsWhenPageDisappearsOrCancelled(bool cancel)
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var unit = Path.Combine(temp.Path, "unit.png");
        var title = Path.Combine(temp.Path, "title.png");
        File.WriteAllBytes(unit, ImageCodec.EncodePng(TestImageFactory.CreateBgra(40, 30, 250), 40, 30));
        File.WriteAllBytes(title, ImageCodec.EncodePng(TestImageFactory.CreateBgra(40, 30, 200), 40, 30));
        var fake = new FakeAdbService([]);
        fake.ScreenshotProvider = () =>
        {
            var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
            if (fake.Swipes.Count == 0) TestImageFactory.PasteBlock(pixels, 1920, 40, 30, 230, 30, 200);
            else if (cancel) cancellation.Cancel();
            return ImageCodec.EncodePng(pixels, 1920, 1080);
        };
        var service = new DevelopService(new AutomationService(fake, new TemplateMatcher(), new TestLog()), new TestLog());
        var action = () => service.FindOnRoadmapAsync("test", unit, "", titleTemplatePath: title, ct: cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        else await Assert.ThrowsAsync<InvalidOperationException>(action);
        Assert.Single(fake.Swipes);
        Assert.Empty(fake.Taps);
    }

    /// <summary>记录真实识别调用时的位置，而不是仅验证手势列表。</summary>
    private sealed class ScanLog(Action inspected) : IGgeaLog
    {
        public event EventHandler<LogEntry>? Logged { add { } remove { } }
        public void Info(string message) { if (message.StartsWith("路线图单帧检索耗时")) inspected(); }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }
}

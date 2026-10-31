using System.IO;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.Tests;

/// <summary>真实 WebP 素材与小尺寸实机裁剪样本验证，不依赖运行中的模拟器。</summary>
public sealed class WorkLogoVisionTests
{
    /// <summary>雷电失败帧和独立重采帧均保留真实渲染；画布修正不能取消 .80 门槛或跳过详情身份确认。</summary>
    [Theory]
    [InlineData("victory_canvas_failure.png", 1208)]
    [InlineData("victory_canvas_live.png", 1225)]
    public void ActualVictoryCanvas_RecognizesOpaqueCore(string fixture, int expectedX)
    {
        var (vision, works, _) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Fixture(fixture), 1080, 450);
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 20), works);
        var match = Assert.Single(observation.Works, w => w.WorkId == 20);
        Assert.True(match.Confidence >= .80, observation.Diagnostic);
        Assert.InRange(match.X, expectedX - 3, expectedX + 3);
        Assert.InRange(match.Y, 531, 537);
        Assert.False(observation.TargetTitleVisible);
        Assert.Null(observation.SelectButton);
    }

    /// <summary>V 的不透明主体只用于画布；详情页仍以原光晕标题及选择按钮共同确认。</summary>
    [Fact]
    public void ActualVictoryTitle_PreservesOverviewVerification()
    {
        var (vision, works, resources) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Fixture("victory_title.png"), 123, 120);
        Paste(pixels, Path.Combine(resources, "templates/stage/select.png"), 1494, 831);
        var png = ImageCodec.EncodePng(pixels, 1920, 1080);
        Assert.True(vision.Inspect(png, works.Single(w => w.Id == 20), works).TargetTitleVisible);
        Assert.False(vision.Inspect(png, works.Single(w => w.Id == 7), works).TargetTitleVisible);
    }

    /// <summary>真实 F91 标题及相同亮色背景不能被 V 的核心遮罩误识别为目标。</summary>
    [Fact]
    public void ActualOtherWork_DoesNotAuthorizeVictoryCanvas()
    {
        var (vision, works, _) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Fixture("f91_title.png"), 1080, 450);
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 20), works);
        Assert.DoesNotContain(observation.Works, w => w.WorkId == 20);
    }

    /// <summary>实机 Recollection 标题不可因共用 SEED 主体而被同位置竞争拒绝。</summary>
    [Fact]
    public void ActualSeedRecollectionCanvas_RecognizesDistinctWork()
    {
        var (vision, works, _) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Fixture("seed_recollection_canvas.png"), 320, 175);
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 14), works);
        Assert.True(observation.Works.Any(w => w.WorkId == 14), observation.Diagnostic);
        Assert.DoesNotContain(observation.Works, w => w.WorkId == 13);
        Assert.False(observation.TargetTitleVisible);
    }

    /// <summary>真实画面副标题被遮挡后必须恢复歧义拒绝，不能只靠 SEED 主体点击 Recollection。</summary>
    [Fact]
    public void ActualSeedRecollectionCanvas_WithoutSubtitleRejectsRecollection()
    {
        var (vision, works, _) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Fixture("seed_recollection_canvas.png"), 320, 175);
        for (var y = 249; y < 276; y++)
            for (var x = 497; x < 608; x++)
                for (var channel = 0; channel < 3; channel++) pixels[(y * 1920 + x) * 4 + channel] = 20;
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 14), works);
        Assert.DoesNotContain(observation.Works, w => w.WorkId == 14);
    }

    /// <summary>同族真实素材构造详情页，确认区分两部作品且不借用选择按钮授权错误作品。</summary>
    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    public void SeedOverview_RequiresCorrectSubtitle(int displayedWork)
    {
        var (vision, works, resources) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        var work = works.Single(w => w.Id == displayedWork);
        PasteRenderedLogo(pixels, ConfigService.ResolvePath(Path.Combine(resources, "templates/stage/main_stage", work.Screenshot)), 160, 140);
        Paste(pixels, Path.Combine(resources, "templates/stage/select.png"), 1494, 831);
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 14), works);
        Assert.Equal(displayedWork == 14, observation.TargetTitleVisible);
        Assert.NotNull(observation.SelectButton);
    }

    /// <summary>测试专用黑底透明合成，使用原素材生成详情标题，不依赖模拟器或新识别素材。</summary>
    private static void PasteRenderedLogo(byte[] pixels, string path, int x, int y)
    {
        using var original = Cv2.ImRead(path, ImreadModes.Unchanged);
        var height = original.Height;
        var width = original.Width;
        using var composited = new Mat(height, width, MatType.CV_8UC4);
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
            {
                var pixel = original.At<Vec4b>(row, col);
                composited.Set(row, col, new Vec4b((byte)(pixel.Item0 * pixel.Item3 / 255),
                    (byte)(pixel.Item1 * pixel.Item3 / 255), (byte)(pixel.Item2 * pixel.Item3 / 255), 255));
            }
        using var scaled = new Mat();
        Cv2.Resize(composited, scaled, new Size((int)Math.Round(original.Width * .445),
            (int)Math.Round(original.Height * .445)), 0, 0, InterpolationFlags.Lanczos4);
        var image = ImageCodec.DecodePng(scaled.ToBytes(".png"));
        for (var row = 0; row < image.Height; row++)
            Buffer.BlockCopy(image.Bgra, row * image.Width * 4, pixels,
                ((y + row) * 1920 + x) * 4, image.Width * 4);
    }

    /// <summary>F91/W 未解锁实机按钮带活动标牌及不同背景，仍须识别暗态且不得授权点击。</summary>
    [Theory]
    [InlineData("f91_closed_select.png")]
    [InlineData("wing_closed_select.png")]
    public void ActualClosedSelect_IsRecognized(string fixture)
    {
        var (vision, works, _) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Fixture(fixture), 1484, 816);
        Paste(pixels, Fixture("f91_title.png"), 123, 120);
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 7), works);
        Assert.True(observation.SelectClosed);
        Assert.Null(observation.SelectButton);
        Assert.True(observation.TargetTitleVisible);
    }

    /// <summary>原始无标牌暗态模板仍可识别，亮态按钮不得同时判为未解锁。</summary>
    [Theory]
    [InlineData("select_disabled.png", true)]
    [InlineData("select.png", false)]
    public void SelectButton_PreservesEnabledAndClosedDistinction(string template, bool closed)
    {
        var (vision, works, resources) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Path.Combine(resources, "templates/stage", template), 1494, 831);
        Paste(pixels, Fixture("hathaway_title.png"), 123, 120);
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 6), works);
        Assert.Equal(closed, observation.SelectClosed);
        Assert.Equal(!closed, observation.SelectButton is not null);
    }

    /// <summary>只有活动标牌而没有按钮主体时不能把相似暗色背景判为未解锁。</summary>
    [Fact]
    public void ActivityBadgeWithoutButton_RejectsClosedSignal()
    {
        var (vision, works, _) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Fixture("f91_closed_select.png"), 1484, 816);
        // 保留顶部标牌片段，清除下方按钮文字与边框，模拟画布上的干扰。
        var blank = TestImageFactory.CreateBgra(436, 105, 20);
        for (var row = 0; row < 105; row++)
            Buffer.BlockCopy(blank, row * 436 * 4, pixels, ((855 + row) * 1920 + 1484) * 4, 436 * 4);
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 7), works);
        Assert.False(observation.SelectClosed);
        Assert.Null(observation.SelectButton);
    }

    /// <summary>挑战画布虽有主要关卡标题和相同 Logo，也必须走错误页面恢复。</summary>
    [Fact]
    public void ChallengeStoryEntry_RejectsCanvasAndOverviewSignals()
    {
        var (vision, works, resources) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Path.Combine(resources, "templates/stage/challenge_story.png"), 48, 838);
        Paste(pixels, Path.Combine(resources, "templates/stage/main_stage.png"), 210, 20);
        Paste(pixels, Fixture("hathaway_title.png"), 123, 120);
        Paste(pixels, Path.Combine(resources, "templates/stage/select.png"), 1494, 831);
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 6), works);
        Assert.False(observation.MainStageVisible);
        Assert.False(observation.TargetTitleVisible);
        Assert.Null(observation.SelectButton);
        Assert.Empty(observation.Works);
        Assert.Contains("挑战地图", observation.Diagnostic);
    }

    /// <summary>半透明光晕的详情标题必须命中，同时标题移出 ROI 后不得沿用旧结果。</summary>
    [Theory]
    [InlineData(123, 120, true)]
    [InlineData(800, 300, false)]
    public void ActualHathawayTitle_RespectsOverviewRoi(int x, int y, bool expected)
    {
        var (vision, works, resources) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Fixture("hathaway_title.png"), x, y);
        Paste(pixels, Path.Combine(resources, "templates/stage/select.png"), 1494, 831);
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 6), works);
        Assert.Equal(expected, observation.TargetTitleVisible);
        Assert.NotNull(observation.SelectButton);
    }

    /// <summary>同一详情标题不能证明另一个作品已进入，即使选择按钮确实存在。</summary>
    [Fact]
    public void ActualHathawayTitle_DoesNotAuthorizeAnotherWork()
    {
        var (vision, works, resources) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Fixture("hathaway_title.png"), 123, 120);
        Paste(pixels, Path.Combine(resources, "templates/stage/select.png"), 1494, 831);
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 1), works);
        Assert.False(observation.TargetTitleVisible);
        Assert.NotNull(observation.SelectButton);
    }

    /// <summary>初代浅色 G 的实机样本覆盖 OpenCV 插值与弱对比前景的回归。</summary>
    [Theory]
    [InlineData("initial_canvas_logo.png")]
    [InlineData("initial_canvas_bright.png")]
    [InlineData("initial_canvas_20261028.png")]
    public void ActualInitialLogo_FindsCanvasCoordinates(string fixture)
    {
        var (vision, works, _) = CreateVision();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, Fixture(fixture), 709, 471);
        var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), works.Single(w => w.Id == 1), works);
        Assert.True(observation.Works.Any(work => work.WorkId == 1), observation.Diagnostic);
        var match = Assert.Single(observation.Works, work => work.WorkId == 1);
        Assert.InRange(match.X, 777, 787);
        Assert.InRange(match.Y, 528, 538);
        Assert.False(observation.TargetTitleVisible);
        Assert.Null(observation.SelectButton);
    }

    /// <summary>正确 WebP 可独立识别平滑渲染；错误 WebP 与错误校准图不能授权点击。</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void InitialLogo_ValidatesWebpIndependentlyOfCalibration(bool unrelatedCalibration, bool correctWebp)
    {
        var (_, works, resources) = CreateVision();
        var target = works.Single(work => work.Id == 1);
        var directory = Path.Combine(Path.GetTempPath(), "ggea-logo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(correctWebp ? ConfigService.ResolvePath(Path.Combine(resources, "templates/stage/main_stage", target.Screenshot)) : Fixture("seed_recollection_canvas.png"),
                Path.Combine(directory, target.Screenshot));
            if (unrelatedCalibration)
                File.Copy(Fixture("seed_recollection_canvas.png"), Path.Combine(directory, "initial_canvas.png"));
            var vision = new WorkLogoVision(new FakeAdbService([]), new TemplateMatcher(), directory,
                Path.Combine(directory, "main_stage.png"), new StageNavigationOptions());
            var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
            Paste(pixels, Fixture("initial_canvas_20261028.png"), 709, 471);
            var observation = vision.Inspect(ImageCodec.EncodePng(pixels, 1920, 1080), target, [target]);
            if (correctWebp)
            {
                var match = Assert.Single(observation.Works);
                Assert.Equal(1, match.WorkId);
                Assert.True(match.Confidence >= .78);
                Assert.InRange(match.X, 777, 787);
                Assert.InRange(match.Y, 528, 538);
            }
            else Assert.Empty(observation.Works);
            Assert.False(observation.TargetTitleVisible);
            Assert.Null(observation.SelectButton);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>素材缺失的原因进入普通诊断，不伪装成有可点击的作品。</summary>
    [Fact]
    public void MissingLogo_ReportsWorkIdWithoutAuthorizingClick()
    {
        var (vision, _, _) = CreateVision();
        var target = new HardStageEntry { Id = 999, Screenshot = "missing-test-logo.webp" };
        var png = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080);
        var observation = vision.Inspect(png, target, [target]);
        Assert.Contains("缺失素材作品=[999]", observation.Diagnostic);
        Assert.Empty(observation.Works);
        Assert.False(observation.TargetTitleVisible);
    }

    /// <summary>纯色错误页面不能因 NCC 零方差产生 NaN/Infinity 假命中。</summary>
    [Fact]
    public void BlankFrame_RejectsAllSignals()
    {
        var (vision, works, _) = CreateVision();
        var png = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080);
        var observation = vision.Inspect(png, works[0], works);
        Assert.False(observation.TargetTitleVisible);
        Assert.False(observation.MainStageVisible);
        Assert.False(observation.SelectClosed);
        Assert.Null(observation.SelectButton);
        Assert.Empty(observation.Works);
    }

    /// <summary>生产 ROI 只用于校准分辨率，不能把其他尺寸截图上的像素直接当作点击坐标。</summary>
    [Fact]
    public void UnexpectedResolution_StopsBeforeMatching()
    {
        var (vision, works, _) = CreateVision();
        var png = ImageCodec.EncodePng(TestImageFactory.CreateBgra(960, 540, 20), 960, 540);
        Assert.Throws<InvalidOperationException>(() => vision.Inspect(png, works[0], works));
    }

    /// <summary>创建使用构建分发素材的真实识别器。</summary>
    private static (WorkLogoVision Vision, IReadOnlyList<HardStageEntry> Works, string Resources) CreateVision()
    {
        var resources = Path.Combine(AppContext.BaseDirectory, "resources");
        var log = new TestLog();
        var works = new HardStagesService(log).Load(ConfigService.ResolvePath(Path.Combine(resources, "hard_stages.json"))).HardStages;
        var options = new StageNavigationOptions
        {
            WorkSelectTemplatePath = ConfigService.ResolvePath(Path.Combine(resources, "templates/stage/select.png")),
            WorkSelectClosedTemplatePath = ConfigService.ResolvePath(Path.Combine(resources, "templates/stage/select_disabled.png")),
        };
        return (new WorkLogoVision(new FakeAdbService([]), new TemplateMatcher(),
            ConfigService.ResolvePath(Path.Combine(resources, "templates/stage/main_stage")), ConfigService.ResolvePath(Path.Combine(resources, "templates/stage/main_stage.png")), options), works, resources);
    }

    /// <summary>实机裁剪样本以稳定的相对路径随测试构建分发。</summary>
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "WorkLogo", name);

    /// <summary>把独立采集的裁剪图放入测试画面，用于验证位置和页面信号组合。</summary>
    private static void Paste(byte[] screen, string path, int x, int y)
    {
        var image = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(path)));
        for (var row = 0; row < image.Height; row++)
            Buffer.BlockCopy(image.Bgra, row * image.Width * 4, screen,
                ((y + row) * 1920 + x) * 4, image.Width * 4);
    }
}

using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.Tests;

/// <summary>强敌素材裁切识别与独立返回确认的核心边界。</summary>
public sealed class FierceEnemySkipTests
{
    /// <summary>裁切、缩放后的立绘仍依据素材识别；错误单位与空白画面不得命中。</summary>
    [Fact]
    public void UnitMatcher_AcceptsCroppedPortraitRejectsWrongUnit()
    {
        var units = FierceEnemyUnit.Load();
        var red = units.Single(u => u.Id == 1);
        using var original = Cv2.ImRead(red.TemplatePath, ImreadModes.Unchanged);
        using var resized = new Mat();
        Cv2.Resize(original, resized, new Size(), .6, .6);
        using var crop = new Mat(resized, new Rect(40, 40, resized.Width - 80, resized.Height - 80));
        using var canvas = new Mat(1080, 1920, MatType.CV_8UC4, new Scalar(30, 30, 30, 255));
        using (var target = new Mat(canvas, new Rect(180, 230, crop.Width, crop.Height))) crop.CopyTo(target);
        var screen = ImageCodec.DecodePng(canvas.ToBytes(".png"));
        using var recognizer = new FierceEnemyUnitRecognizer(red.TemplatePath);
        var region = new Roi(80, 180, 1760, 750);
        Assert.NotNull(recognizer.Find(screen, region));
        using var wrong = new FierceEnemyUnitRecognizer(units.Single(u => u.Id == 2).TemplatePath);
        Assert.Null(wrong.Find(screen, region));
        canvas.SetTo(new Scalar(30, 30, 30, 255));
        Assert.Null(recognizer.Find(ImageCodec.DecodePng(canvas.ToBytes(".png")), region));
    }

    /// <summary>真实首页同时有中央大图和四个缩略图，每个单位只能返回所属蓝圈。</summary>
    [Theory]
    [InlineData(23, 422, 660)]
    [InlineData(22, 750, 802)]
    [InlineData(21, 1168, 802)]
    [InlineData(20, 1498, 660)]
    public void HomeSelection_RealFrame_OnlyMatchesBlueCircles(int id, int x, int y)
    {
        var frame = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/fierce-home-circles.png")));
        using var recognizer = new FierceEnemyUnitRecognizer(FierceEnemyUnit.Load().Single(u => u.Id == id).TemplatePath);
        var match = recognizer.FindHomeSelection(frame);
        Assert.NotNull(match);
        Assert.Equal(x, match.Value.CenterX);
        Assert.Equal(y, match.Value.CenterY);
    }

    /// <summary>移除四个候选圆后，仍然存在的中央大立绘不能授权点击。</summary>
    [Fact]
    public void HomeSelection_CentralPortraitAlone_IsRejected()
    {
        var frame = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/fierce-home-circles.png")));
        using var image = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        foreach (var center in new[] { new Point(422, 660), new Point(750, 802), new Point(1168, 802), new Point(1498, 660) })
            Cv2.Circle(image, center, 105, new Scalar(20, 20, 20, 255), -1);
        var masked = ImageCodec.DecodePng(image.ToBytes(".png"));
        using var recognizer = new FierceEnemyUnitRecognizer(FierceEnemyUnit.Load().Single(u => u.Id == 23).TemplatePath);
        Assert.NotNull(recognizer.Find(masked, new Roi(540, 150, 780, 650)));
        Assert.Null(recognizer.FindHomeSelection(masked));
    }

    /// <summary>历史页实际反馈：两列独立提取可识别骰子高达与被底部裁切的红色高达。</summary>
    [Theory]
    [InlineData(2, 1030, 1290, 650, 822)]
    [InlineData(1, 140, 390, 838, 950)]
    public void HistorySelection_RealFrame_FindsReportedUnits(int id, int left, int right, int top, int bottom)
    {
        var screen = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/fierce-history-bottom.png")));
        using var unit = new FierceEnemyUnitRecognizer(FierceEnemyUnit.Load().Single(u => u.Id == id).TemplatePath);
        var match = unit.FindHistorySelection(screen);
        Assert.NotNull(match);
        Assert.InRange(match.Value.CenterX, left, right);
        Assert.InRange(match.Value.CenterY, top, bottom);
    }

    /// <summary>未解锁的 EXPERT 暗字仍可定位，随后必须判为不可略过，不点击、不继续向下查找。</summary>
    [Fact]
    public async Task LockedExpert_RecognizesLabelAndUnavailableSkip()
    {
        var panel = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/fierce-locked-expert-panel.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var y = 0; y < panel.Height; y++)
            Array.Copy(panel.Bgra, y * panel.Width * 4, pixels, ((168 + y) * 1920 + 966) * 4, panel.Width * 4);
        var png = ImageCodec.EncodePng(pixels, 1920, 1080);
        var screen = ImageCodec.DecodePng(png);
        var label = new FierceEnemyDifficultyRecognizer(TapToNextOcrSamples.SharedOcr).Find(screen, FierceEnemyDifficulty.EXPERT);
        Assert.NotNull(label);
        Assert.InRange(label.Value.Y, 560, 580);
        var region = FierceEnemyDifficultyRecognizer.GetSkipRegion(label.Value);
        Assert.NotNull(region);
        var adb = new FakeAdbService(png);
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new FierceEnemySkipService(automation, log, TapToNextOcrSamples.SharedOcr,
            _ => throw new InvalidOperationException("锁定难度不能执行略过"));
        var result = await service.FindSkipButtonAsync(screen, region.Value);
        Assert.True(result.IsUnavailable);
        Assert.Null(result.Button);
        Assert.Empty(adb.Taps);
        Assert.Empty(adb.Swipes);
    }

    /// <summary>真实亮/暗按钮素材分别判定，暗态不返回可点击目标且不产生 ADB 点击。</summary>
    [Theory]
    [InlineData("button_skip_disabled.png", true)]
    [InlineData("button_skip.png", false)]
    public async Task SkipButton_DarkIsUnavailable(string file, bool dark)
    {
        var template = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "resources/common/templates/stage/event", file)));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var y = 0; y < template.Height; y++)
            Array.Copy(template.Bgra, y * template.Width * 4, pixels, ((746 + y) * 1920 + 1290) * 4, template.Width * 4);
        var screen = ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080));
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new FierceEnemySkipService(automation, log, null!, _ => throw new InvalidOperationException());
        var result = await service.FindSkipButtonAsync(screen, new Roi(1280, 727, 280, 100));
        Assert.Equal(dark, result.IsUnavailable);
        Assert.Equal(!dark, result.Button is not null);
        Assert.Empty(adb.Taps);
    }

    /// <summary>“最新”无需立绘：首页只点击关卡选择，空白左侧详情仍可进入难度流程。</summary>
    [Fact]
    public async Task Latest_NavigatesDirectlyWithoutUnitPortrait()
    {
        var home = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/fierce-home-circles.png"));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        var column = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/fierce-difficulty-column.png")));
        for (var y = 0; y < column.Height; y++)
            Array.Copy(column.Bgra, y * column.Width * 4, pixels, ((168 + y) * 1920 + 966) * 4, column.Width * 4);
        var detail = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(home);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? home : detail;
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: TapToNextOcrSamples.SharedOcr);
        var service = new FierceEnemySkipService(automation, log, TapToNextOcrSamples.SharedOcr,
            _ => throw new InvalidOperationException("导航不应启动略过"));
        Assert.True(await service.NavigateAsync("serial", null, CancellationToken.None));
        var tap = Assert.Single(adb.Taps);
        Assert.InRange(tap.X, 1496, 1856);
        Assert.InRange(tap.Y, 834, 954);
        Assert.Empty(adb.Swipes);
        var screen = ImageCodec.DecodePng(detail);
        Assert.True(service.IsDetail(screen, null, CancellationToken.None));
        using var explicitUnit = new FierceEnemyUnitRecognizer(FierceEnemyUnit.Load()[0].TemplatePath);
        Assert.False(service.IsDetail(screen, explicitUnit, CancellationToken.None));
        Assert.False(service.IsDetail(ImageCodec.DecodePng(home), null, CancellationToken.None));
    }

    /// <summary>向下浏览未命中后必须反向向上找一次；暗态终止当前任务，始终未命中也不得无限滚动。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DifficultySearch_ReversesOnceAfterDownwardMiss(bool showExpertAbove)
    {
        var blank = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080);
        var panel = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/fierce-locked-expert-panel.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var y = 0; y < panel.Height; y++)
            Array.Copy(panel.Bgra, y * panel.Width * 4, pixels, ((168 + y) * 1920 + 966) * 4, panel.Width * 4);
        var top = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(blank);
        adb.ScreenshotProvider = () => showExpertAbove && adb.Swipes.Count == 2 ? top : blank;
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new FierceEnemySkipService(automation, log, TapToNextOcrSamples.SharedOcr,
            _ => throw new InvalidOperationException("暗态与未知画面不允许执行略过"));
        var result = await service.SkipDetailAsync("serial", new(),
            new(FierceEnemyUnit.Latest, FierceEnemyDifficulty.EXPERT), null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(showExpertAbove, result.IsUnavailable);
        Assert.Equal(new[] { (1500, 870, 1500, 340, 650), (1500, 340, 1500, 870, 650) }, adb.Swipes);
        Assert.Equal(3, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>底部裁切按钮不得直接点击；挑战难度不在可选枚举中。</summary>
    [Fact]
    public void Difficulty_RequiresVisibleButtonAndExcludesChallenge()
    {
        Assert.NotNull(FierceEnemyDifficultyRecognizer.GetSkipRegion(new(1030, 567, 90, 26)));
        Assert.Null(FierceEnemyDifficultyRecognizer.GetSkipRegion(new(1030, 904, 75, 26)));
        Assert.Equal(new[] { "EXPERT", "HARD", "NORMAL" }, Enum.GetNames<FierceEnemyDifficulty>());
    }

    /// <summary>活动返回证据缺失必须失败，不能因没有次数上限模板而直接报成功。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReturnConfirmation_RequiresActivityEvidence(bool confirmed)
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new StageSkipService(automation, log, new() {
            PostSkipConfirmation = (_, _) => confirmed, PostSkipTimeoutMs = 30 });
        Assert.Equal(confirmed, await service.ConfirmPostSkipAsync("serial", new()));
        Assert.Empty(adb.Taps);
    }
}

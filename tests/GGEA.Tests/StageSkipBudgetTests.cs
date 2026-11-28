using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.Tests;

public sealed class StageSkipBudgetTests
{
    [Fact]
    public void SortieTitlePortraitAndProgressIdentifyTheSameOrder()
    {
        var frame = new ScreenImage { Width = 1920, Height = 1080, Bgra = TestImageFactory.CreateBgra(1920, 1080, 0) };
        Paste(frame, Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/sortie_shakti_order.png"), 0, 0);
        using var ocr = new OnnxTextRecognizer();
        var label = new TemplateImages("resources/templates/base/character_requests/title_sortie_request.png");
        var title = label.Match(new TemplateMatcher(), frame, .96, new(1340, 225, 300, 80), default);
        Assert.NotNull(title);
        var order = new CharacterRequestCatalog(ocr).ReadSortie(frame, title.Region, default);
        Assert.Equal(new SortieOrder(new("1175003401", "请求出击2", "1750", 2), 0), order);
        Assert.Null(DevelopmentRequestIndex.FindSortie("1175003401", 6));
    }

    [Fact]
    public void CaptureOrderIsBoundToQuessSeriesAndActualProgress()
    {
        var frame = new ScreenImage { Width = 1920, Height = 1080, Bgra = TestImageFactory.CreateBgra(1920, 1080, 0) };
        Paste(frame, Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/capture_quess_order.png"), 0, 0);
        using var ocr = new OnnxTextRecognizer();
        var label = new TemplateImages("resources/templates/base/character_requests/title_capture_unit.png");
        var title = label.Match(new TemplateMatcher(), frame, .96, new(1340, 225, 300, 80), default);
        Assert.NotNull(title);
        var catalog = new CharacterRequestCatalog(ocr);
        Assert.Equal(new SortieOrder(new("1110000701", "夺取单位3", "1100", 3), 2),
            catalog.ReadSortie(frame, title.Region, default, capture: true));
        Assert.Null(catalog.ReadSortie(frame, title.Region, default));
        Paste(frame, Path.Combine(AppContext.BaseDirectory, "Fixtures/Base/capture_report.png"), 0, 0);
        Assert.Equal(3, catalog.ReadSortie(frame, title.Region, default, capture: true)?.Progress);
        Assert.NotNull(new TemplateImages("resources/templates/base/character_requests/button_report.png")
            .Match(new TemplateMatcher(), frame, .93, new(1540, 800, 170, 110)));
    }

    [Theory]
    [InlineData("two", 3, 2)]
    [InlineData("one", 3, 1)]
    [InlineData("zero", 1, 0)]
    public void RewardReadsOnlyGreenCaptureCards(string sample, int skips, int captured)
    {
        var frame = new ScreenImage { Width = 1920, Height = 1080, Bgra = TestImageFactory.CreateBgra(1920, 1080, 0) };
        Paste(frame, Path.Combine(AppContext.BaseDirectory, $"Fixtures/Base/capture_reward_{sample}.png"), 0, 0);
        using var ocr = new OnnxTextRecognizer();
        var budget = new CaptureRequestBudget(RequestProductionAttempt.RefreshPeriod);
        Assert.Equal(captured, budget.ReadRewards(frame, skips, ocr, default));
        Assert.Null(budget.ReadRewards(frame, skips + 1, ocr, default));
        if (captured > 0)
        {
            // 保留绿底，遮挡卡片标签；未知卡片不能当作零掉落继续消费。
            for (int y = 368; y < 400; y++) Array.Clear(frame.Bgra, (1920 * y + 130) * 4, 67 * 4);
            Assert.Null(budget.ReadRewards(frame, skips, ocr, default));
        }
    }

    [Fact]
    public async Task CaptureHardThree_ReturnsTwoUnitsForThreeSkips()
    {
        byte[] Frame(string sample)
        {
            var frame = new ScreenImage { Width = 1920, Height = 1080, Bgra = TestImageFactory.CreateBgra(1920, 1080, 0) };
            Paste(frame, Path.Combine(AppContext.BaseDirectory, $"Fixtures/Base/capture_{sample}.png"), 0, 0);
            return ImageCodec.EncodePng(frame.Bgra, frame.Width, frame.Height);
        }
        var frames = new[] { "hard3", "preview", "reward_two", "returned" }.Select(Frame).ToArray();
        var adb = new FakeAdbService(frames[0]);
        adb.ScreenshotProvider = () => frames[Math.Min(adb.Taps.Count, frames.Length - 1)];
        using var ocr = new OnnxTextRecognizer();
        var matcher = new TemplateMatcher();
        var log = new TestLog();
        using var auto = new AutomationService(adb, matcher, log, textOcr: ocr);
        using var numbers = new OnnxStageNumberRecognizer(ocr);
        using var nodes = new StageNodeRecognizer(adb, matcher, log,
            ConfigService.ResolvePath("resources/templates/stage/node_states"), numberRecognizer: numbers);
        var nav = new StageService(auto, new FakeStageWorkRecognizer(), log, nodeRecognizer: nodes);
        var skip = new StageSkipService(auto, log, new()
        {
            SkipResultTemplatePath = ConfigService.ResolvePath("resources/templates/stage/skip/skip_result.png"),
            SkipCountLimitTemplatePath = ConfigService.ResolvePath("resources/templates/stage/skip/count_limit.png"),
            SettleAfterOkMs = 0, SkipResultTimeoutMs = 3500, LateResultRecoveryTimeoutMs = 0
        });
        var layout = new UiLayoutService(log).Load(ConfigService.ResolvePath("resources/ui_layout/stage.json"));
        var work = new HardStageEntry { Id = 4, NormalCount = 5, HardCount = 3, Screenshot = "logo_l_series_1100.webp" };
        var task = new SortieRequestService(auto, nav, skip, layout, [work], ocr, log);
        var budget = new CaptureRequestBudget(RequestProductionAttempt.RefreshPeriod);
        budget.Reserve(60);
        var order = new SortieOrder(new("1110000701", "夺取单位3", "1100", 3), 0);
        Assert.Equal(2, await task.RunAsync("replay", order, default, budget));
        Assert.Equal(0, budget.RemainingAp);
        Assert.Equal(4, adb.Taps.Count); // 略过、执行、结果 OK、返回委托。
    }

    [Fact]
    public async Task ExhaustedHardContinuesLeftAcrossNormalStagesWithOneBudget()
    {
        byte[] Frame(string sample, string? rail = null)
        {
            var frame = new ScreenImage { Width = 1920, Height = 1080, Bgra = TestImageFactory.CreateBgra(1920, 1080, 0) };
            Paste(frame, Path.Combine(AppContext.BaseDirectory, $"Fixtures/Base/capture_{sample}.png"), 0, 0);
            if (rail is not null) Paste(frame, Path.Combine(AppContext.BaseDirectory, $"Fixtures/StageNodes/{rail}.png"), 0, 600);
            return ImageCodec.EncodePng(frame.Bgra, frame.Width, frame.Height);
        }
        // 首帧及第二帧右侧均为次数上限；切换到第三个节点才有可用次数。
        var frames = new[] { Frame("returned", "normal11-mumu5558"), Frame("returned", "normal10-mumu5556"),
            Frame("hard3"), Frame("preview"), Frame("reward_two"), Frame("returned") };
        var adb = new FakeAdbService(frames[0]);
        adb.ScreenshotProvider = () => frames[Math.Min(adb.Taps.Count, frames.Length - 1)];
        using var ocr = new OnnxTextRecognizer();
        var log = new TestLog();
        using var auto = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var nodes = new FakeStageNodeRecognizer();
        StageNodeRecognition At(int x, WorkStageKind kind, int n, int index) =>
            new(x, 840, kind, n, WorkStageNodeState.Completed, new(kind, n, index), "");
        nodes.EnqueueResults(At(450, WorkStageKind.Hard, 1, 11), At(553, WorkStageKind.Boss, 10, 10),
            At(360, WorkStageKind.Normal, 9, 9));
        var nav = new StageService(auto, new FakeStageWorkRecognizer(), log, nodeRecognizer: nodes);
        var skip = new StageSkipService(auto, log, new()
        {
            SkipResultTemplatePath = ConfigService.ResolvePath("resources/templates/stage/skip/skip_result.png"),
            SkipCountLimitTemplatePath = ConfigService.ResolvePath("resources/templates/stage/skip/count_limit.png"),
            SettleAfterOkMs = 0, SkipResultTimeoutMs = 3500, LateResultRecoveryTimeoutMs = 0
        });
        var layout = new UiLayoutService(log).Load(ConfigService.ResolvePath("resources/ui_layout/stage.json"));
        var work = new HardStageEntry { Id = 4, NormalCount = 10, HardCount = 3, Screenshot = "logo_l_series_1100.webp" };
        var task = new SortieRequestService(auto, nav, skip, layout, [work], ocr, log);
        var budget = new CaptureRequestBudget(RequestProductionAttempt.RefreshPeriod, 120);
        budget.Reserve(90);
        Assert.Equal(2, await task.RunAsync("replay", new(new("1110000701", "夺取单位3", "1100", 3), 0), default, budget));
        Assert.Equal(0, budget.RemainingAp);
        Assert.Equal(6, adb.Taps.Count);
        Assert.InRange(adb.Taps[0].X, 244, 278);
        Assert.InRange(adb.Taps[1].X, 340, 382);
        Assert.Equal((1674, 710), adb.Taps[2]);
    }

    [Fact]
    public void CaptureStopsBeforeSpendingPastLimitOrRefresh()
    {
        var budget = new CaptureRequestBudget(RequestProductionAttempt.RefreshPeriod);
        budget.Reserve(60);
        Assert.Throws<InvalidOperationException>(() => budget.Reserve(31));
        Assert.Equal(30, budget.RemainingAp);
        budget.Reserve(30);
        Assert.Throws<InvalidOperationException>(() => budget.Reserve(10));
        Assert.Throws<InvalidOperationException>(() => new CaptureRequestBudget(-1).Reserve(10));
    }

    [Theory]
    [InlineData(20, 10, 3, 5, 2)]
    [InlineData(9, 10, 3, 1, 0)]
    [InlineData(174, 10, 3, 2, 2)]
    [InlineData(174, 10, 1, 5, 1)]
    public void UsesSmallestLimit(int ap, int cost, int remaining, int requested, int expected) =>
        Assert.Equal(expected, new StageSkipBudget.Stage(ap, cost, remaining).CountFor(requested));

    [Fact]
    public void JapaneseHardReadsApAboveNaturalCap()
    {
        using var ocr = new OnnxTextRecognizer();
        var budget = new StageSkipBudget(ocr, new ResourceResolver(AppContext.BaseDirectory, "ja"));
        var frame = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/ApConsumption/ja-zeta-hard2-ready.png")));
        Assert.Equal(new StageSkipBudget.Stage(760, 10, 3), budget.ReadStage(frame, true, default));
    }

    // 使用实机控件裁图，只替换体力数字来重现 20 AP 和不足一次的情形。
    [Theory]
    [InlineData(20, false)]
    [InlineData(9, false)]
    [InlineData(20, true)]
    public async Task DailyHard_ConfirmsAffordableQuantityBeforeExecution(int ap, bool wrongPreview)
    {
        string Resource(string relative) => ConfigService.ResolvePath("resources/templates/" + relative);
        string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures/ApConsumption/sortie_" + name + ".png");
        ScreenImage Frame() => new() { Width = 1920, Height = 1080, Bgra = TestImageFactory.CreateBgra(1920, 1080, 0) };
        var stage = Frame();
        Paste(stage, Fixture("landed"), 0, 0);
        Draw(stage, new(1711, 31, 126, 32), $"{ap}/164", true);
        var original = Frame();
        Paste(original, Fixture("dialog"), 561, 206);
        var two = Frame();
        Paste(two, Fixture("two"), 561, 206);
        Draw(two, new(911, 590, 133, 47), ap.ToString(), false);
        // 从三位数切换到一位数时，游戏将红色箭头向右移动两个数字槽。
        using (var mat = Mat.FromPixelData(two.Height, two.Width, MatType.CV_8UC4, two.Bgra))
        {
            using var oldArrow = mat[new Rect(1347, 599, 19, 36)];
            using var glyph = oldArrow.Clone();
            using var cleared = mat[new Rect(1347, 596, 99, 40)];
            cleared.SetTo(Scalar.All(190));
            using var shifted = mat[new Rect(1391, 599, 19, 36)];
            glyph.CopyTo(shifted);
        }
        Draw(two, new(1412, 590, 34, 47), wrongPreview ? "9" : "0", false);
        var result = Frame();
        Paste(result, Resource("stage/skip/skip_result.png"), 820, 30);
        var returned = Frame();
        Paste(returned, Fixture("landed"), 0, 0);
        Paste(returned, Fixture("remaining_one"), 1674, 631);
        Draw(returned, new(1711, 31, 126, 32), "0/164", true);
        var frames = new[] { stage, original, two, result, returned }
            .Select(f => ImageCodec.EncodePng(f.Bgra, f.Width, f.Height)).ToArray();
        var adb = new FakeAdbService(frames[0]);
        adb.ScreenshotProvider = () => frames[Math.Min(adb.Taps.Count, frames.Length - 1)];
        var log = new TestLog();
        using var auto = new AutomationService(adb, new TemplateMatcher(), log);
        var skip = new StageSkipService(auto, log, new()
        {
            SkipResultTemplatePath = Resource("stage/skip/skip_result.png"),
            SkipCountLimitTemplatePath = Resource("stage/skip/count_limit.png"),
            SkipResultTimeoutMs = 2500,
            PostSkipTimeoutMs = 2500,
            SettleAfterOkMs = 0
        });
        var layout = new UiLayoutService(log).Load(ConfigService.ResolvePath("resources/ui_layout/stage.json"));
        if (ap == 20)
        {
            var numbers = new StageSkipBudget(auto.GlobalScreens.TextOcr, auto.Resources);
            Assert.Equal(20, numbers.OriginalAp(two, default));
            Assert.Equal(wrongPreview ? 9 : 0, numbers.ProjectedAp(two, default));
        }
        var batch = await skip.SkipDailyHardAsync("replay", layout, default);
        if (ap < 10)
        {
            Assert.True(batch.IsApInsufficient, batch.Message);
            Assert.Empty(adb.Taps);
        }
        else if (wrongPreview)
        {
            Assert.False(batch.Success);
            Assert.Equal(new[] { (1674, 710), (759, 477) }, adb.Taps);
        }
        else
        {
            Assert.True(batch.Success, batch.Message);
            Assert.Equal(2, batch.ExecutedCount);
            Assert.Equal(0, batch.RemainingAp);
            Assert.Equal(4, adb.Taps.Count);
            Assert.Equal((759, 477), adb.Taps[1]);
        }
    }

    private static void Paste(ScreenImage target, string file, int x, int y)
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(file));
        for (var row = 0; row < crop.Height; row++)
            Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, target.Bgra,
                ((y + row) * target.Width + x) * 4, crop.Width * 4);
    }

    private static void Draw(ScreenImage frame, Roi area, string digits, bool light)
    {
        using var mat = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var region = mat[new Rect(area.X, area.Y, area.Width, area.Height)];
        region.SetTo(light ? Scalar.All(25) : Scalar.All(190));
        if (digits == "0" && !light)
        {
            // 数字 0 使用游戏字体。HersheySimplex 的 0 与字母 O 几乎相同。
            var hud = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures/ApConsumption/sortie_landed.png")));
            for (int y = 0; y < 27; y++)
                for (int x = 0; x < 20; x++)
                {
                    var ink = hud.Bgra[((698 + y) * hud.Width + 1436 + x) * 4 + 1];
                    var value = (byte)(190 - ink * 135 / 255);
                    region.Set(9 + y, 5 + x, new Vec4b(value, value, value, 255));
                }
            return;
        }
        int width = Cv2.GetTextSize(digits, HersheyFonts.HersheySimplex, .86, 2, out _).Width;
        Cv2.PutText(region, digits, new Point(Math.Max(1, area.Width - width - 5), area.Height - 5), HersheyFonts.HersheySimplex,
            .86, light ? Scalar.White : Scalar.Black, 2, LineTypes.AntiAlias);
    }
}

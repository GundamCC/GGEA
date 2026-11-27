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

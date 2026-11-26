using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>用采集到的卡片和券不足弹窗回放。只允许点击稀有卡略过和取消。</summary>
public sealed class LimitedEventBossTests
{
    private const string Assets = "resources/templates/stage/event/limited_time_event/";
    private static byte[] Frame(params (string Path, int X, int Y)[] parts)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 15);
        foreach (var (path, x, y) in parts)
        {
            var image = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(path)));
            for (int row = 0; row < image.Height; row++)
                Buffer.BlockCopy(image.Bgra, row * image.Width * 4, pixels,
                    ((y + row) * 1920 + x) * 4, image.Width * 4);
        }
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }

    [Fact]
    public async Task RareExpertPrecedesNormalAndClosesInsufficientDialog()
    {
        var story = Frame((Assets + "story.png", 210, 3));
        var boss = Frame((Assets + "boss.png", 219, 8), ("Fixtures/Navigation/event-card.png", 975, 120));
        var dialog = Frame(("Fixtures/Navigation/event-dialog.png", 423, 165));
        var log = new TestLog();
        var device = new FakeAdbService(story);
        device.ScreenshotProvider = () => device.Taps.Count switch { 0 => story, 2 => dialog, _ => boss };
        using var automation = new AutomationService(device, new TemplateMatcher(), log);
        using var ocr = new OnnxTextRecognizer();
        await new LimitedEventBossService(automation, ocr, log, "serial").RunAsync(FierceEnemyDifficulty.NORMAL, default);
        Assert.Equal(3, device.Taps.Count);
        Assert.InRange(device.Taps[1].X, 1390, 1470);
        Assert.InRange(device.Taps[1].Y, 400, 460);
        Assert.InRange(device.Taps[2].X, 710, 825);
    }

    /// <summary>常驻最大次数、结算后出现稀有、稀有只执行一次，直到关闭不足弹窗。</summary>
    [Fact]
    public async Task EachResultRechecksRareAndOnlyRegularSelectsMaximum()
    {
        var story = Frame((Assets + "story.png", 210, 3));
        var regular = Frame((Assets + "boss.png", 219, 8), ("Fixtures/Navigation/event-regular-card.png", 975, 135));
        var rare = Frame((Assets + "boss.png", 219, 8), ("Fixtures/Navigation/event-card.png", 975, 120));
        var insufficient = Frame(("Fixtures/Navigation/event-dialog.png", 423, 165));
        byte[] Dialog(bool maxSelected) => Frame((Assets + "ticket_dialog.png", 740, 312),
            ("resources/templates/dialog/cancel.png", 588, 790),
            ("resources/templates/dialog/execute.png", 966, 787),
            ("resources/templates/develop/button/set_max_button" + (maxSelected ? "_disabled" : "") + ".png", 1257, 431));
        var initial = Dialog(false);
        var maximum = Dialog(true);
        byte[][] screens = [story, regular, initial, maximum, rare, maximum, regular, insufficient, regular];
        var device = new FakeAdbService(story);
        device.ScreenshotProvider = () => screens[device.Taps.Count];
        using var ocr = new OnnxTextRecognizer();
        var log = new TestLog();
        using var automation = new AutomationService(device, new TemplateMatcher(), log);
        await new LimitedEventBossService(automation, ocr, log, "serial").RunAsync(FierceEnemyDifficulty.EXPERT, default);
        Assert.Equal(8, device.Taps.Count);
        Assert.InRange(device.Taps[2].X, 1280, 1360);
        Assert.InRange(device.Taps[3].X, 1100, 1190);
        Assert.InRange(device.Taps[5].X, 1100, 1190);
        Assert.InRange(device.Taps[7].X, 710, 825);
    }

    [Fact]
    public void RegularDifficultyIsReadableAtEventCardScale()
    {
        var frame = ImageCodec.DecodePng(Frame(("Fixtures/Navigation/event-card.png", 975, 120)));
        using var ocr = new OnnxTextRecognizer();
        var labels = new FierceEnemyDifficultyRecognizer(ocr, new(1035, 128, 125, 776));
        Assert.NotNull(labels.Find(frame, FierceEnemyDifficulty.EXPERT));
        Assert.Null(labels.Find(frame, FierceEnemyDifficulty.HARD));
    }
}

using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>实机红蓝背景裁剪验证 OCR 与同帧返回门禁，不连接模拟器。</summary>
public sealed class MasterLeagueResultTests
{
    /// <summary>标题、按钮同时出现才返回；返回后重新确认对手页，不沿用旧结算帧。</summary>
    [Theory]
    [InlineData("win", true)]
    [InlineData("lose", true)]
    [InlineData("win", false)]
    [InlineData(null, true)]
    public async Task Return_RequiresTitleAndButton(string? outcome, bool button)
    {
        using var ocr = new OnnxTextRecognizer();
        var resultFrame = Frame(outcome, button);
        var destination = Frame(null, false, true);
        var adb = new FakeAdbService(resultFrame);
        adb.ScreenshotProvider = () => adb.Taps.Count > 0 ? destination : resultFrame;
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new MasterLeagueService(automation, log, (_, ct) =>
        { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var result = await service.ReturnFromBattleResultAsync("serial", new(ocr));
        var expected = outcome is not null && button;
        Assert.Equal(expected, result.Success);
        Assert.Equal(expected ? new[] { (1701, 989) } : Array.Empty<(int, int)>(), adb.Taps);
        Assert.Equal(expected ? 3 : 1, adb.ScreenshotCount);
    }

    /// <summary>仅接受完整文字与可靠置信度，不能把相似词或部分词当成结算页。</summary>
    [Theory]
    [InlineData(" battle score ", .99, .98, true)]
    [InlineData("SCORE", .99, .98, false)]
    [InlineData("BATTLE SC0RE", .99, .98, false)]
    [InlineData("BATTLE SCORE", .94, .93, false)]
    [InlineData("BATTLE SCORE", .99, .89, false)]
    public void Acceptance_RequiresCompleteReliableText(string text, double average, double minimum, bool expected)
        => Assert.Equal(expected, MasterLeagueResultRecognizer.IsAccepted(new(text, average, minimum)));

    /// <summary>连续晋级和报酬页允许继续；提示一直存在也只能点击三次，不能宣称已返回。</summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(4, false)]
    public async Task Return_HandlesAtMostThreeContinuationPages(int pages, bool expected)
    {
        using var ocr = new OnnxTextRecognizer();
        var resultFrame = Frame("win", true);
        var nextFrame = Frame(null, false, tapToNext: true);
        var destination = Frame(null, false, true);
        var adb = new FakeAdbService(resultFrame);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? resultFrame
            : adb.Taps.Count <= pages ? nextFrame : destination;
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) =>
        { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var result = await service.ReturnFromBattleResultAsync("serial", new(ocr));
        Assert.Equal(expected, result.Success);
        Assert.Equal(1 + Math.Min(pages, 3), adb.Taps.Count);
        Assert.Equal((1701, 989), adb.Taps[0]);
        Assert.All(adb.Taps.Skip(1), tap => Assert.InRange(tap.Item2, 900, 1079));
    }

    /// <summary>首次继续提示缺席、过渡背景标题出现时不能提前退出；两页之间也允许加载空白。</summary>
    [Fact]
    public async Task Return_WaitsForDelayedPromotionAndReward_ThenCompleteOpponentPage()
    {
        using var ocr = new OnnxTextRecognizer();
        var resultFrame = Frame("win", true);
        var transition = Frame(null, false, true);
        var nextFrame = Frame(null, false, tapToNext: true);
        var adb = new FakeAdbService(resultFrame);
        var observations = 0;
        adb.ScreenshotProvider = () =>
        {
            if (adb.Taps.Count == 0) return resultFrame;
            observations++;
            return observations is 4 or 8 ? nextFrame : transition;
        };
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, textOcr: ocr);
        var service = new MasterLeagueService(automation, log, (_, ct) =>
        { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var result = await service.ReturnFromBattleResultAsync("serial", new(ocr), default,
            (_, _) => observations >= 9);
        Assert.True(result.Success, result.Message);
        Assert.Equal(3, adb.Taps.Count);
        Assert.Equal(10, observations);
    }

    /// <summary>将原始标题裁剪放回固定区域，其他部分仅组合必要模板，不保留玩家资料。</summary>
    private static byte[] Frame(string? outcome, bool button, bool destination = false, bool tapToNext = false)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        void Paste(string path, int x, int y)
        {
            var crop = ImageCodec.DecodePng(File.ReadAllBytes(path));
            for (var row = 0; row < crop.Height; row++)
                Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, pixels,
                    ((y + row) * 1920 + x) * 4, crop.Width * 4);
        }
        if (outcome is not null) Paste(Path.Combine(AppContext.BaseDirectory,
            $"Fixtures/Navigation/master-league-score-{outcome}.png"), 720, 275);
        if (button) Paste(ConfigService.ResolvePath(MasterLeagueService.ResultSelectOpponentTemplate), 1520, 928);
        if (destination)
        {
            Paste(ConfigService.ResolvePath(MasterLeagueService.SelectOpponentTitleTemplate), 218, 20);
            Paste(ConfigService.ResolvePath("resources/templates/stage/pvp/button_battle.png"), 1466, 833);
        }
        if (tapToNext) TapToNextOcrSamples.Paste(pixels, 1920, 360, 80, 780, 940);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }
}

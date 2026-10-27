using System.IO;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>实机区域回放验证数值隔离和半透明 SKIP，复合图仅包含必要识别区域。</summary>
public sealed class MasterLeagueVisionTests
{
    /// <summary>全胜不依赖无关按钮与数字；缺少任何一列胜利或页面标题时仍不能直接确认完成。</summary>
    [Fact]
    public void Opponents_AllVictory_IsIndependentOfActionControls()
    {
        using var ocr = new OnnxTextRecognizer();
        var vision = new MasterLeagueVision(ocr);
        var pixels = Fixture("all-victory").Bgra.ToArray();
        void Clear(int x, int y, int width, int height)
        {
            for (var row = y; row < y + height; row++) Array.Clear(pixels, (row * 1920 + x) * 4, width * 4);
        }
        ScreenImage Screen() => ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080));
        Clear(1445, 810, 400, 160);
        Clear(1100, 28, 30, 46);
        Clear(0, 716, 1445, 66);
        var page = vision.ReadOpponents(Screen(), default);
        Assert.NotNull(page);
        Assert.All(page.Opponents, opponent => { Assert.True(opponent.Victory); Assert.Null(opponent.Points); });
        Assert.Null(page.RefreshRemaining);
        // 四胜不能利用该快捷路径；原有选敌门禁也不能被缺失按钮绕过。
        var complete = pixels.ToArray();
        Clear(1138, 570, 106, 70);
        Assert.Null(vision.ReadOpponents(Screen(), default));
        pixels = complete;
        Clear(200, 0, 440, 100);
        Assert.Null(vision.ReadOpponents(Screen(), default));
    }

    /// <summary>青铜已有积分及银级全零都必须完整读取，不能把 O、Pt 或评级当作数字。</summary>
    [Theory]
    [InlineData("bronze")]
    [InlineData("silver")]
    [InlineData("gold")]
    [InlineData("master")]
    public void Opponents_RealRegions_ReadCompletePage(string name)
    {
        using var ocr = new OnnxTextRecognizer();
        var vision = new MasterLeagueVision(ocr);
        var page = vision.ReadOpponents(Fixture($"opponents-{name}"), default);
        Assert.NotNull(page);
        Assert.Equal(5, page.RefreshRemaining);
        Assert.Equal(name == "bronze" ? new[] { 3664, 0, 0, 0, 117 }
            : name == "gold" ? new[] { 15522, 19702, 22041, 0, 0 } : new[] { 0, 0, 0, 0, 0 },
            page.Opponents.Select(o => o.Points!.Value));
        if (name == "bronze") Assert.True(page.Opponents[4].Defeat);
        else if (name is "silver" or "master") Assert.All(page.Opponents, o => Assert.True(o.Unchallenged));
    }

    /// <summary>白金截图已胜利者的分数被区域裁切，也应继续选择后面的零分对手。</summary>
    [Fact]
    public void Opponents_CompletedScoreUnknown_DoesNotBlockNextOpponent()
    {
        using var ocr = new OnnxTextRecognizer();
        var page = new MasterLeagueVision(ocr).ReadOpponents(Fixture("opponents-platinum"), default);
        Assert.NotNull(page);
        Assert.True(page.Opponents[0].Victory);
        Assert.Null(page.Opponents[0].Points);
        Assert.Equal(2, MasterLeagueProgress.Choose(page, false).Index);
        var known = page with { Opponents = new[] { page.Opponents[0] with { Points = 22988 } }.Concat(page.Opponents.Skip(1)).ToArray() };
        Assert.True(page.SameAs(known));
        var unknown = page with { Opponents = new[] { new MasterLeagueOpponent(null, false, false) }.Concat(page.Opponents.Skip(1)).ToArray() };
        Assert.True(MasterLeagueProgress.IsConsistent(unknown));
        Assert.Equal(2, MasterLeagueProgress.Choose(unknown, false).Index);
    }

    /// <summary>整个右侧积分区缺失也不影响选敌；大师最高纪录和其他段位门槛都不再读取。</summary>
    [Theory]
    [InlineData("master")]
    [InlineData("bronze")]
    public void OpponentReading_IgnoresAllRightPanelScores(string league)
    {
        using var ocr = new OnnxTextRecognizer();
        var original = Fixture($"opponents-{league}");
        var pixels = original.Bgra.ToArray();
        for (var y = 100; y < 800; y++) Array.Clear(pixels, (y * 1920 + 1445) * 4, 410 * 4);
        var screen = ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080));
        var page = new MasterLeagueVision(ocr).ReadOpponents(screen, default);
        Assert.NotNull(page);
        Assert.Equal(2, MasterLeagueProgress.Choose(page, false).Index);
    }

    /// <summary>两种动态背景使用同一用户 SKIP 素材与遮罩，结算页不得出现假 SKIP。</summary>
    [Theory]
    [InlineData("skip-a", true)]
    [InlineData("skip-b", true)]
    [InlineData("opponents-silver", false)]
    public void Skip_UsesTextContourMask(string fixture, bool expected)
    {
        using var ocr = new OnnxTextRecognizer();
        var result = new MasterLeagueVision(ocr).ReadBattle(Fixture(fixture), default);
        Assert.Equal(expected, result.Skip is not null);
        Assert.False(result.ResultVisible);
        if (result.Skip is not null) Assert.Equal((1510, 53), (result.Skip.CenterX, result.Skip.CenterY));
    }

    /// <summary>挑战次数来自准备页，变更次数来自确认弹窗，不共用同一个计数器。</summary>
    [Fact]
    public void Counts_UseTheirOwnPages()
    {
        using var ocr = new OnnxTextRecognizer();
        var vision = new MasterLeagueVision(ocr);
        Assert.Equal(5, vision.ReadChallengeRemaining(Fixture("preparation"), default));
        var dialog = vision.ReadRefreshDialog(Fixture("change"), default);
        Assert.NotNull(dialog);
        Assert.Equal(0, dialog.CurrentPoints);
        Assert.Equal(5, dialog.Remaining);
        Assert.Equal((1148, 995), (dialog.ConfirmButton.CenterX, dialog.ConfirmButton.CenterY));
    }

    /// <summary>信息弹层必须同时具备选敌页和自身标题；普通列表不能触发关闭，弹层也不能当作可用列表。</summary>
    [Fact]
    public void StageBoostInfo_RequiresBothTitles_AndBlocksOpponentReading()
    {
        using var ocr = new OnnxTextRecognizer();
        var vision = new MasterLeagueVision(ocr);
        var screen = Fixture("stage-boost-info");
        var info = vision.FindStageBoostInfo(screen, default);
        Assert.NotNull(info);
        Assert.Equal((960, 441), (info.CenterX, info.CenterY));
        Assert.Null(vision.ReadOpponents(screen, default));
        Assert.Null(vision.FindStageBoostInfo(Fixture("opponents-bronze"), default));
        var pixels = screen.Bgra.ToArray();
        Array.Clear(pixels, 0, 1920 * 100 * 4);
        Assert.Null(vision.FindStageBoostInfo(ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080)), default));
    }

    /// <summary>五种角色背景、末列选中光效与列间像素偏移不能遮蔽全胜标记；没有标记的普通列表不能误判胜利。</summary>
    [Fact]
    public void Opponents_AllVictory_RecognizesEveryColumnWithoutScoreDependency()
    {
        using var ocr = new OnnxTextRecognizer();
        var vision = new MasterLeagueVision(ocr);
        var page = vision.ReadOpponents(Fixture("all-victory"), default);
        Assert.NotNull(page);
        Assert.All(page.Opponents, o => { Assert.True(o.Victory); Assert.False(o.Defeat); });
        foreach (var fixture in new[] { "opponents-silver", "opponents-master" })
        {
            var unplayed = vision.ReadOpponents(Fixture(fixture), default);
            Assert.NotNull(unplayed);
            Assert.All(unplayed.Opponents, o => Assert.False(o.Victory));
        }
        var mixed = vision.ReadOpponents(Fixture("opponents-bronze"), default);
        Assert.NotNull(mixed);
        Assert.True(mixed.Opponents[4].Defeat);
        Assert.False(mixed.Opponents[4].Victory);
    }

    /// <summary>从测试输出读取原像素区域复合图，不依赖本机 ADB、日志或原始截图路径。</summary>
    internal static ScreenImage Fixture(string name) => ImageCodec.DecodePng(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, $"Fixtures/Navigation/master-league-{name}.png")));
}

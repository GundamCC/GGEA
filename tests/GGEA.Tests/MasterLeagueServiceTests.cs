using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>使用实际标题和按钮素材验证联盟导航、十五次点击边界及取消；不连接模拟器。</summary>
public sealed class MasterLeagueServiceTests
{
    /// <summary>只在准备标题出现后按指定坐标挑战一次，缺失时有限等待且不点击。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChallengePreparation_RequiresTitle(bool titlePresent)
    {
        var adb = new FakeAdbService(titlePresent
            ? Frame((MasterLeagueService.BattlePreparationTitleTemplate, 218, 20)) : Frame());
        var result = await Service(adb, []).ChallengePreparedBattleAsync("serial");
        Assert.Equal(titlePresent, result.Success);
        Assert.Equal(titlePresent ? new[] { (1675, 1000) } : Array.Empty<(int, int)>(), adb.Taps);
        Assert.Equal(titlePresent ? 1 : 20, adb.ScreenshotCount);
    }

    /// <summary>准备页识别前取消不能发出挑战点击。</summary>
    [Fact]
    public async Task ChallengePreparation_Cancelled_DoesNotTap()
    {
        var adb = new FakeAdbService(Frame((MasterLeagueService.BattlePreparationTitleTemplate, 218, 20)));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(adb, []).ChallengePreparedBattleAsync("serial", cancel.Token));
        Assert.Empty(adb.Taps);
    }

    /// <summary>未勾选编制时，重新确认标题后直接选择对手，不能触发清屏或编制点击。</summary>
    [Fact]
    public async Task TitlePresent_WithoutFormation_SelectsOpponentOnly()
    {
        var adb = new FakeAdbService(LeagueFrame());
        var delays = new List<int>();
        var result = await Service(adb, delays).ConfirmAndEnterFormationAsync("serial", false);
        Assert.True(result.Success);
        Assert.Equal(new[] { (1646, 893) }, adb.Taps);
        Assert.Empty(delays);
        Assert.Equal(2, adb.ScreenshotCount);
    }

    /// <summary>标题缺失最多十五次中心点击，第十次起逐次复查，每次等待 1.2 秒，失败不能循环。</summary>
    [Fact]
    public async Task TitleMissing_TapsFifteenTimesThenStops()
    {
        var adb = new FakeAdbService(Frame());
        var delays = new List<int>();
        var result = await Service(adb, delays).ConfirmAndEnterFormationAsync("serial", true);
        Assert.False(result.Success);
        Assert.Equal(Enumerable.Repeat((960, 540), 15), adb.Taps);
        Assert.Equal(Enumerable.Repeat(1200, 15), delays);
        Assert.Equal(7, adb.ScreenshotCount);
    }

    /// <summary>标题在第十至十五次出现时立即停止中心点击，不补齐剩余次数。</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public async Task TitleAppears_StopsClearingImmediately(int afterTaps)
    {
        var blank = Frame();
        var league = LeagueFrame();
        var adb = new FakeAdbService(blank);
        adb.ScreenshotProvider = () => adb.Taps.Count >= afterTaps ? league : blank;
        var delays = new List<int>();
        var result = await Service(adb, delays).ConfirmAndEnterFormationAsync("serial", false);
        Assert.True(result.Success);
        Assert.Equal(Enumerable.Repeat((960, 540), afterTaps).Append((1646, 893)), adb.Taps);
        Assert.Equal(Enumerable.Repeat(1200, afterTaps), delays);
        Assert.Equal(afterTaps - 7, adb.ScreenshotCount);
    }

    /// <summary>十次点击后才复查；进入编制后复用推图弹窗，确定编制并确认返回联盟。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormationEnabled_ConfirmsDestination(bool needsClearing)
    {
        var blank = Frame();
        var league = LeagueFrame(withButton: true);
        var formation = FormationFrame();
        var dialog = DialogFrame();
        var adb = new FakeAdbService(blank);
        adb.ScreenshotProvider = () => adb.Taps.Contains((1674, 991)) ? league
            : adb.Taps.Contains((962, 961)) ? formation
            : adb.Taps.Contains((1278, 1011)) ? dialog
            : adb.Taps.Contains((1262, 893)) ? formation
            : !needsClearing || adb.Taps.Count >= 10 ? league : blank;
        var delays = new List<int>();
        var result = await Service(adb, delays).ConfirmAndEnterFormationAsync("serial", true);
        Assert.True(result.Success, result.Message);
        Assert.Equal(needsClearing ? 15 : 5, adb.Taps.Count);
        Assert.Equal(new[] { (1262, 893), (1278, 1011), (962, 961), (1674, 991), (1646, 893) }, adb.Taps.TakeLast(5));
        Assert.Equal(needsClearing ? 10 : 0, delays.Count(ms => ms == 1200));
        Assert.Contains(GameTiming.RecognitionIntervalMs, delays);
        Assert.Contains("已确认大师联盟界面", result.Message);
        Assert.DoesNotContain((145, 445), adb.Taps); // 大师联盟不切换推图的系列/自由部队。
    }

    /// <summary>对战编制标题缺失时，不能仅凭自动编制按钮修改阵容。</summary>
    [Fact]
    public async Task AutoFormCurrentPage_MissingPageTitle_DoesNotTap()
    {
        var adb = new FakeAdbService(Frame(("resources/templates/stage/sortie/button_auto-form.png", 1150, 975)));
        var result = await Service(adb, []).AutoFormCurrentPageAsync("serial");
        Assert.False(result.Success);
        Assert.Empty(adb.Taps);
    }

    /// <summary>共用弹窗标题未出现时，即使确定按钮可见也不能确认编制。</summary>
    [Fact]
    public async Task AutoFormCurrentPage_MissingDialogTitle_DoesNotConfirm()
    {
        var formation = FormationFrame();
        var noTitle = Frame(("resources/templates/common/button_confirm.png", 780, 900));
        var adb = new FakeAdbService(formation);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? formation : noTitle;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(adb, []).AutoFormCurrentPageAsync("serial"));
        Assert.Equal(new[] { (1278, 1011) }, adb.Taps);
    }

    /// <summary>在弹窗确认前取消必须保留现状，不能补点确定编制或继续对战。</summary>
    [Fact]
    public async Task AutoFormCurrentPage_CancelBeforeDialogConfirmation_Stops()
    {
        using var cancel = new CancellationTokenSource();
        var adb = new FakeAdbService(FormationFrame());
        adb.BeforeScreenshotAsync = (_, ct) =>
        {
            if (adb.Taps.Count > 0) cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(adb, []).AutoFormCurrentPageAsync("serial", cancel.Token));
        Assert.Equal(new[] { (1278, 1011) }, adb.Taps);
    }

    /// <summary>标题已确认但按钮不存在时，不得使用预设坐标代替识别结果。</summary>
    [Fact]
    public async Task FormationButtonMissing_StopsWithoutTap()
    {
        var adb = new FakeAdbService(LeagueFrame());
        var result = await Service(adb, []).ConfirmAndEnterFormationAsync("serial", true);
        Assert.False(result.Success);
        Assert.Empty(adb.Taps);
    }

    /// <summary>中心点击期间取消，立即终止剩余点击与后续标题复查。</summary>
    [Fact]
    public async Task CancelDuringClearing_StopsImmediately()
    {
        using var cancel = new CancellationTokenSource();
        var adb = new FakeAdbService(Frame());
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new MasterLeagueService(automation, log, (_, ct) =>
        {
            if (adb.Taps.Count == 3) cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ConfirmAndEnterFormationAsync("serial", false, cancel.Token));
        Assert.Equal(3, adb.Taps.Count);
        Assert.Equal(1, adb.ScreenshotCount);
    }

    /// <summary>不兼容分辨率必须在首次中心点击前停止。</summary>
    [Fact]
    public async Task InvalidScreenSize_DoesNotTap()
    {
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(960, 540, 20), 960, 540));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(adb, []).ConfirmAndEnterFormationAsync("serial", false));
        Assert.Empty(adb.Taps);
    }

    /// <summary>真实关卡入口识别通过后，必须使用用户指定的联盟坐标。</summary>
    [Fact]
    public async Task OpenFromHome_UsesStageThenRequestedLeagueEntry()
    {
        var stage = TestImageFactory.CreateStageMenuPng();
        var league = LeagueFrame();
        var adb = new FakeAdbService(stage);
        adb.ScreenshotProvider = () => adb.Taps.Contains((1668, 400)) ? league : stage;
        var result = await Service(adb, []).OpenFromHomeAsync("serial", new UiLayout
        {
            Anchors = [new() { Name = "navStage", X = 900, Y = 900, Width = 200, Height = 100 }],
        }, false);
        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { (1000, 950), (1668, 400), (1646, 893) }, adb.Taps);
    }

    /// <summary>空位提示优先于背景，确认一次后重新截图识别联盟，最后选择对手。</summary>
    [Fact]
    public async Task ReturnWithEmptySlots_ConfirmsDialogThenSelectsOpponent()
    {
        var hint = Frame((MasterLeagueService.EmptyFormationHintTemplate, 806, 170),
            (MasterLeagueService.ConfirmEmptyFormationTemplate, 968, 935),
            (MasterLeagueService.TitleTemplate, 218, 20),
            (MasterLeagueService.SelectOpponentTemplate, 1466, 833));
        var league = LeagueFrame();
        var adb = new FakeAdbService(hint);
        adb.ScreenshotProvider = () => adb.Taps.Contains((1148, 995)) ? league : hint;
        var result = await Service(adb, []).WaitForLeagueAndSelectOpponentAsync("serial", true, default);
        Assert.True(result.Success);
        Assert.Equal(new[] { (1148, 995), (1646, 893) }, adb.Taps);
        Assert.Equal(2, adb.ScreenshotCount);
    }

    /// <summary>提示未消失时不能重复确认或点击背景按钮，有限观察后报告失败。</summary>
    [Fact]
    public async Task EmptyDialogPersists_ConfirmsOnlyOnceThenStops()
    {
        var adb = new FakeAdbService(Frame((MasterLeagueService.EmptyFormationHintTemplate, 806, 170),
            (MasterLeagueService.ConfirmEmptyFormationTemplate, 968, 935)));
        var result = await Service(adb, []).WaitForLeagueAndSelectOpponentAsync("serial", true, default);
        Assert.False(result.Success);
        Assert.Equal(new[] { (1148, 995) }, adb.Taps);
        Assert.Equal(20, adb.ScreenshotCount);
    }

    /// <summary>提示与确认按钮、联盟标题与对手按钮都必须同帧；孤立按钮不能授权点击。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnMissingEvidence_DoesNotTap(bool hintWithoutButton)
    {
        var adb = new FakeAdbService(hintWithoutButton
            ? Frame((MasterLeagueService.EmptyFormationHintTemplate, 806, 170))
            : Frame((MasterLeagueService.ConfirmEmptyFormationTemplate, 968, 935),
                (MasterLeagueService.SelectOpponentTemplate, 1466, 833)));
        var result = await Service(adb, []).WaitForLeagueAndSelectOpponentAsync("serial", true, default);
        Assert.False(result.Success);
        Assert.Empty(adb.Taps);
    }

    /// <summary>空位确认后取消，不得补点选择对手；保留游戏当前画面。</summary>
    [Fact]
    public async Task CancelAfterEmptyConfirmation_DoesNotSelectOpponent()
    {
        using var cancel = new CancellationTokenSource();
        var adb = new FakeAdbService(Frame((MasterLeagueService.EmptyFormationHintTemplate, 806, 170),
            (MasterLeagueService.ConfirmEmptyFormationTemplate, 968, 935)));
        var log = new TestLog();
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var service = new MasterLeagueService(automation, log, (_, ct) =>
        { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.WaitForLeagueAndSelectOpponentAsync("serial", true, cancel.Token));
        Assert.Equal(new[] { (1148, 995) }, adb.Taps);
    }

    /// <summary>记录业务要求的延时参数，同时复用真实截图和匹配实现。</summary>
    private static MasterLeagueService Service(FakeAdbService adb, List<int> delays)
    {
        var log = new TestLog();
        return new MasterLeagueService(new AutomationService(adb, new TemplateMatcher(), log), log, (ms, ct) =>
        { ct.ThrowIfCancellationRequested(); delays.Add(ms); return Task.CompletedTask; });
    }

    /// <summary>构造大师联盟同帧证据，避免把编制按钮与标题拆成不同时间的截图。</summary>
    private static byte[] LeagueFrame(bool withButton = false) => withButton
        ? Frame((MasterLeagueService.TitleTemplate, 218, 20), (MasterLeagueService.FormationButtonTemplate, 1082, 833),
            (MasterLeagueService.SelectOpponentTemplate, 1466, 833))
        : Frame((MasterLeagueService.TitleTemplate, 218, 20), (MasterLeagueService.SelectOpponentTemplate, 1466, 833));

    /// <summary>对战编制页同时包含页面标题、自动编制和确定编制按钮。</summary>
    private static byte[] FormationFrame() => Frame(
        (MasterLeagueService.FormationTitleTemplate, 218, 20),
        ("resources/templates/stage/sortie/button_auto-form.png", 1150, 975),
        (MasterLeagueService.ConfirmFormationTemplate, 1494, 931));

    /// <summary>沿用自动推图弹窗素材，而不是为大师联盟复制另一套确认模板。</summary>
    private static byte[] DialogFrame() => Frame(
        ("resources/templates/stage/sortie/auto-form.png", 800, 0),
        ("resources/templates/common/button_confirm.png", 780, 900));

    /// <summary>把随包真实素材贴到标准画面，验证资源解析、ROI 与匹配中心，不保存整张私人截图。</summary>
    private static byte[] Frame(params (string Path, int X, int Y)[] templates)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        foreach (var (path, x, y) in templates)
        {
            var image = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(path)));
            for (var row = 0; row < image.Height; row++)
                Buffer.BlockCopy(image.Bgra, row * image.Width * 4, pixels,
                    ((y + row) * 1920 + x) * 4, image.Width * 4);
        }
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }
}

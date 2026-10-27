using System.IO;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>历史导航的真实 Logo 回放与确定性状态机检查；补黑边仅用于保留原始裁剪位置。</summary>
public sealed class HistoryStageNavigationTests
{
    private static readonly string StageDirectory = Path.Combine(AppContext.BaseDirectory, "resources/common/templates/stage");
    private static HistoryStageCatalog Catalog() => HistoryStageCatalog.Load(
        Path.Combine(AppContext.BaseDirectory, "resources/common/history_stages.json"), Path.Combine(StageDirectory, "history_stage"));

    /// <summary>两个相似组的实机预览及左下 Logo 均确认正确作品，竞争作品不能借共用主标题通过。</summary>
    [Theory]
    [InlineData("bandit-selected", 9, 1, false)]
    [InlineData("bandit-stage", 9, 1, true)]
    [InlineData("xastray-selected", 7, 2, false)]
    [InlineData("xastray-stage", 7, 2, true)]
    [InlineData("eight-node", 8, 4, true)]
    public void ActualLogo_ConfirmsIdentityAndRejectsSimilarWork(string fixture, int expected, int rival, bool detail)
    {
        using var vision = new HistoryStageVision(Catalog(), StageDirectory);
        var region = detail ? new Roi(100, 700, 550, 230) : HistoryStageVision.SelectedRegion;
        if (fixture == "eight-node") region = HistoryStageVision.DetailRegion;
        var screen = Frame(fixture, region, !detail);
        var state = vision.Inspect(screen, rival, default);
        Assert.True(state.HistoryTitle);
        Assert.Equal(expected, detail ? state.DetailWorkId : state.SelectedWorkId);
        Assert.NotEqual(rival, detail ? state.DetailWorkId : state.SelectedWorkId);
        if (detail) { Assert.Null(state.SelectedWorkId); Assert.Null(state.Go); }
        else { Assert.Null(state.DetailWorkId); Assert.NotNull(state.Go); }
    }

    /// <summary>列表十个素材可定位为候选；候选定位本身不能确认所选作品或最终关卡身份。</summary>
    [Theory]
    [InlineData("view0", new[] { 6, 7, 8, 9, 10 })]
    [InlineData("view1", new[] { 1, 2, 3, 4, 5 })]
    public void ActualList_AllTenCandidatesStayWithinLeft365(string fixture, int[] ids)
    {
        using var vision = new HistoryStageVision(Catalog(), StageDirectory);
        var screen = Frame(fixture, HistoryStageVision.ListRegion, true);
        foreach (var id in ids)
        {
            var state = vision.Inspect(screen, id, default);
            Assert.True(state.Candidate is not null, $"未定位历史作品 ID {id}，样本 {fixture}");
            var bounds = Assert.IsType<Roi>(state.Candidate);
            Assert.InRange(bounds.X, 0, 364);
            Assert.InRange(bounds.X + bounds.Width, 1, 365);
            Assert.InRange(bounds.Y, 120, 969);
            Assert.Null(state.SelectedWorkId);
            Assert.Null(state.DetailWorkId);
        }
    }

    /// <summary>即使作品正确，标题缺失仍不可授权；已取消和非标准截图也不能返回候选。</summary>
    [Fact]
    public void TitleSizeAndCancellation_AreRequired()
    {
        using var vision = new HistoryStageVision(Catalog(), StageDirectory);
        var screen = Frame("bandit-stage", new(100, 700, 550, 230), false, title: false);
        Assert.Null(vision.Inspect(screen, 9, default).DetailWorkId);
        Assert.Throws<OperationCanceledException>(() => vision.Inspect(screen, 9, new CancellationToken(true)));
        var small = ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(800, 600, 0), 800, 600));
        Assert.Throws<InvalidOperationException>(() => vision.Inspect(small, 9, default));
    }

    /// <summary>所选作品、按钮和进入后的 Logo 都需两帧；成功路径只点击一次“前往关卡”。</summary>
    [Fact]
    public async Task ConfirmedSelectionAndDetail_CompletesWithoutBattleClicks()
    {
        var test = new Scenario(Selected(9), Selected(9), Detail(9), Detail(9));
        Assert.True((await test.Run()).Success);
        Assert.Equal(new[] { (1675, 895) }, test.Taps);
        Assert.Equal(0, test.Scrolls);
    }

    /// <summary>主要关卡入口也需两帧，只有历史标题与可用按钮到达后才进行作品复核。</summary>
    [Fact]
    public async Task MainStageEntry_RequiresFreshHistoryList()
    {
        var entry = new HistoryStageObservation(false, new(new(410, 835, 103, 52), .99), null, null, null, null);
        var test = new Scenario(entry, entry, Selected(9), Selected(9), Detail(9), Detail(9));
        Assert.True((await test.Run()).Success);
        Assert.Equal(new[] { (461, 861), (1675, 895) }, test.Taps);
    }

    /// <summary>NEW 遮住入口时，主要关卡双帧证据授权固定点，随后仍按作品确认流程继续。</summary>
    [Fact]
    public async Task MainStageEntry_MissingTemplate_UsesFixedPointThenContinues()
    {
        var main = new HistoryStageObservation(false, null, null, null, null, null) { MainStageTitle = true };
        var test = new Scenario(main, main, Selected(9), Selected(9), Detail(9), Detail(9));
        Assert.True((await test.Run()).Success);
        Assert.Equal(new[] { (551, 872), (1675, 895) }, test.Taps);
        Assert.Equal(0, test.Scrolls);
    }

    /// <summary>主要标题证据独立于入口返回；入口区域完全被遮挡时仍保留标题，空白页不提供授权。</summary>
    [Fact]
    public void MainStageTitleEvidence_DoesNotRequireEntryTemplate()
    {
        using var vision = new HistoryStageVision(Catalog(), StageDirectory);
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        var empty = ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080));
        Assert.False(vision.Inspect(empty, 9, default).MainStageTitle);
        var title = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(StageDirectory, "main_stage.png")));
        for (var row = 0; row < title.Height; row++)
            Buffer.BlockCopy(title.Bgra, row * title.Width * 4, pixels, ((16 + row) * 1920 + 219) * 4, title.Width * 4);
        var state = vision.Inspect(ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080)), 9, default);
        Assert.True(state.MainStageTitle);
        Assert.Null(state.Entry);
        Assert.False(state.HistoryTitle);
    }

    /// <summary>标题丢失时不补偿；补偿未进入历史列表时只点一次，不盲点作品或循环恢复。</summary>
    [Fact]
    public async Task MainStageEntry_FallbackRequiresTitleAndVerifiedDestination()
    {
        var main = new HistoryStageObservation(false, null, null, null, null, null) { MainStageTitle = true };
        var lost = new Scenario(main, main with { MainStageTitle = false });
        Assert.False((await lost.Run()).Success);
        Assert.Empty(lost.Taps);
        var stuck = new Scenario(main);
        Assert.False((await stuck.Run()).Success);
        Assert.Equal(new[] { (551, 872) }, stuck.Taps);
        Assert.Equal(0, stuck.Scrolls);
        using var cts = new CancellationTokenSource();
        var cancelled = new Scenario(main) { BeforeObservation = n => { if (n == 2) cts.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Run(cts.Token));
        Assert.Empty(cancelled.Taps);
    }

    /// <summary>不同作品的节点页最多返回两层，每次都复核 Logo 与返回箭头；不从未知页盲目后退。</summary>
    [Fact]
    public async Task OtherWorkNode_ReturnsTwoVerifiedLevelsThenSelectsTarget()
    {
        var other = Detail(8) with { Back = new(new(80, 27, 84, 43), .99) };
        var test = new Scenario(other, other, other, other, Selected(9), Selected(9), Detail(9), Detail(9));
        Assert.True((await test.Run()).Success);
        Assert.Equal(new[] { (122, 48), (122, 48), (1675, 895) }, test.Taps);
    }

    /// <summary>中心作品误识别、按钮消失或关卡页复核错误都不得误报成功或继续出击。</summary>
    [Fact]
    public async Task WrongOrMissingEvidence_StopsWithoutFurtherActions()
    {
        var wrong = new Scenario(Selected(9), Selected(9), Detail(1));
        Assert.False((await wrong.Run()).Success);
        Assert.Single(wrong.Taps);
        var unknown = new Scenario(new HistoryStageObservation(false, null, null, null, null, null));
        Assert.False((await unknown.Run()).Success);
        Assert.Empty(unknown.Taps);
        Assert.Equal(0, unknown.Scrolls);
        var changed = new Scenario(Selected(9), new(true, null, null, null, null, null));
        Assert.False((await changed.Run()).Success);
        Assert.Empty(changed.Taps);
        Assert.Equal(0, changed.Scrolls);
    }

    /// <summary>列表候选只授权选中；选中后是另一作品时，整个有限搜索不得点击“前往关卡”。</summary>
    [Fact]
    public async Task SimilarCandidate_RequiresPostSelectionIdentity()
    {
        var candidate = Selected(1) with { Candidate = new Roi(190, 330, 140, 50) };
        var test = new Scenario(candidate, candidate, Selected(1));
        Assert.False((await test.Run()).Success);
        Assert.Equal(new[] { (260, 355) }, test.Taps);
        Assert.InRange(test.Scrolls, 1, 2);
    }

    /// <summary>第二帧到达时取消必须撤销已经取得的第一帧授权。</summary>
    [Fact]
    public async Task CancellationBeforeClick_RevokesAuthorization()
    {
        using var cts = new CancellationTokenSource();
        var test = new Scenario(Selected(9), Selected(9)) { BeforeObservation = n => { if (n == 2) cts.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Run(cts.Token));
        Assert.Empty(test.Taps);
    }

    /// <summary>缺失的竞争素材、重复编号和相似组引用必须在操作设备前失败。</summary>
    [Fact]
    public void Catalog_InvalidReferencesFailClosed()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{\"hardStages\":[{\"id\":1,\"work\":\"x\",\"screenshot\":\"../main_stage.png\"}]}");
            Assert.Throws<InvalidDataException>(() => HistoryStageCatalog.Load(file, Path.Combine(StageDirectory, "history_stage")));
            var data = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "resources/common/history_stages.json"));
            File.WriteAllText(file, data.Replace("[1, 9]", "[1, 999]"));
            Assert.Throws<InvalidDataException>(() => HistoryStageCatalog.Load(file, Path.Combine(StageDirectory, "history_stage")));
        }
        finally { File.Delete(file); }
    }

    /// <summary>原像素裁剪按采集坐标补回黑底，其他区域不合成任何身份或交互状态。</summary>
    private static ScreenImage Frame(string fixture, Roi roi, bool go, bool title = true)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        Paste(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/history-" + fixture + ".png"), roi.X, roi.Y);
        if (title) Paste(Path.Combine(StageDirectory, "history_stage.png"), 219, 16);
        if (go) Paste(Path.Combine(StageDirectory, "history_go.png"), 1603, 875);
        return ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080));
        void Paste(string path, int x, int y)
        {
            var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
            for (var row = 0; row < image.Height; row++)
                Buffer.BlockCopy(image.Bgra, row * image.Width * 4, pixels, ((y + row) * 1920 + x) * 4, image.Width * 4);
        }
    }
    /// <summary>计划要求两帧一致；进度改变时仍可完成导航，但不能把上一帧的 100% 当成最终计划。</summary>
    [Fact]
    public async Task ProgressDecision_RequiresTwoMatchingFrames()
    {
        var complete = Detail(9) with { Progress = new(HistoryCompletion.Complete, HistoryCompletion.Complete) };
        var incomplete = Detail(9) with { Progress = new(HistoryCompletion.Incomplete, HistoryCompletion.Locked) };
        var stable = new Scenario(complete, complete);
        Assert.Contains("无需推图", (await stable.Run()).Message);
        Assert.Empty(stable.Taps);
        var changing = new Scenario(complete, incomplete);
        Assert.Contains("暂不决定推图", (await changing.Run()).Message);
        Assert.Empty(changing.Taps);
    }

    private static HistoryStageObservation Selected(int id) => new(true, null, new(new(1603, 875, 144, 40), .99), null, id, null)
        { Page = HistoryPageKind.List };
    private static HistoryStageObservation Detail(int id) => new(true, null, null, null, null, id) { Page = HistoryPageKind.Overview };

    /// <summary>仅记录观察序列与动作；延时不耗时，帧用尽后维持最后状态用于验证有限停止。</summary>
    private sealed class Scenario(params HistoryStageObservation[] states)
    {
        private int _observations;
        public Action<int>? BeforeObservation { get; init; }
        public List<(int X, int Y)> Taps { get; } = [];
        public int Scrolls { get; private set; }
        public Task<StageNavigationResult> Run(CancellationToken ct = default)
        {
            var service = new HistoryStageNavigationService((_, token) =>
                {
                    _observations++;
                    BeforeObservation?.Invoke(_observations);
                    return Task.FromResult(states[Math.Min(_observations - 1, states.Length - 1)]);
                }, (x, y, token) => { token.ThrowIfCancellationRequested(); Taps.Add((x, y)); return Task.CompletedTask; },
                (_, token) => { token.ThrowIfCancellationRequested(); Scrolls++; return Task.CompletedTask; },
                new TestLog(), (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
            return service.OpenAsync(new HardStageEntry { Id = 9, Work = "Bandit Flower" }, 1, ct);
        }
    }
}

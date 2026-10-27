using System.IO;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>用户反馈的准备页错位、黑屏过渡和入口抢点回归；合成帧仅用于离线测试。</summary>
public sealed partial class AutoProgressFlowTests
{
    /// <summary>历史 BOSS 没有普通节点入口；加载帧不能放行，完整列表通过专用确认立即交回队列。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryBossReturn_WaitsForContentThenCompletesWithoutExtraClicks(bool resultPage)
    {
        using var scene = new Scene();
        var directory = ConfigService.ResolvePath("resources/templates/stage");
        var catalog = HistoryStageCatalog.Load(ConfigService.ResolvePath("resources/history_stages.json"),
            Path.Combine(directory, "history_stage"));
        using var vision = new HistoryStageVision(catalog, directory);
        var recognizer = new HistoryBossProgressRecognizer(TapToNextOcrSamples.SharedOcr);
        var pixels = new byte[1920 * 1080 * 4];
        void Paste(string path, int x, int y)
        {
            var part = ImageCodec.DecodePng(File.ReadAllBytes(path));
            for (var row = 0; row < part.Height; row++)
                Buffer.BlockCopy(part.Bgra, row * part.Width * 4, pixels, ((row + y) * 1920 + x) * 4, part.Width * 4);
        }
        Paste(Path.Combine(directory, "history_boss_title.png"), 218, 20);
        var titleOnly = ImageCodec.EncodePng(pixels, 1920, 1080);
        Paste(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/history-boss-logo.png"), 0, 700);
        var withoutCards = ImageCodec.EncodePng(pixels, 1920, 1080);
        Paste(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation/history-boss-top.png"), 1000, 128);
        var ready = ImageCodec.EncodePng(pixels, 1920, 1080);
        Assert.False(recognizer.ConfirmReturn(ImageCodec.DecodePng(titleOnly), vision, 7, default));
        Assert.False(recognizer.ConfirmReturn(ImageCodec.DecodePng(withoutCards), vision, 7, default));
        Assert.False(recognizer.ConfirmReturn(ImageCodec.DecodePng(ready), vision, 2, default));
        Assert.Throws<OperationCanceledException>(() => recognizer.ConfirmReturn(
            ImageCodec.DecodePng(ready), vision, 7, new CancellationToken(true)));
        var reads = 0;
        scene.Adb.ScreenshotProvider = () => scene.Adb.Taps.Count switch
        {
            0 => resultPage ? scene.Frame(("again", 1200, 920), ("select", 1600, 920)) : scene.Frame(("story", 1500, 930)),
            1 when !resultPage => scene.Frame(("skip", 1700, 380)),
            _ => ++reads switch { 1 => titleOnly, 2 => withoutCards, _ => ready }
        };
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run(
            confirmSelectedNode: (screen, token) => recognizer.ConfirmReturn(screen, vision, 7, token)));
        Assert.Equal(3, reads);
        Assert.Equal(resultPage ? 1 : 2, scene.Adb.Taps.Count);
        Assert.DoesNotContain(scene.Log.Entries, e => e.Message.Contains("连续 2 分钟"));
    }

    /// <summary>HARD 返回时准备页证据也优先于残留入口；两张新帧确认后交回原会话，不计完成。</summary>
    [Fact]
    public async Task HardReturn_PreparationEvidence_PrecedesStageEntry()
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("button_sortie_preparations", 1494, 834)));
        Assert.Equal(SortieFlowResult.ProgressResumePreparation,
            await scene.ConfirmHardReturn(new ManualProgressTime(), confirmPreparation: (_, _) => Task.FromResult(true)));
        Assert.Equal(2, scene.Adb.ScreenshotCount);
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>抹去标题后，各个真实入口仍可独立确认返回；不会点击入口来伪造通关。</summary>
    [Theory]
    [InlineData("button_sortie_preparations")]
    [InlineData("button_sortie")]
    [InlineData("watch")]
    public async Task EndingStory_EntryWithoutTitle_IsEnough(string entry)
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("skip", 1700, 380)),
            scene.Frame((entry, 1494, 834)));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(2, scene.Adb.Taps.Count);
    }

    /// <summary>真实选中光圈和完整编号也能独立确认页面，不依赖标题或右侧入口。</summary>
    [Fact]
    public async Task EndingStory_SelectedNodeWithoutTitleOrEntry_IsEnough()
    {
        using var scene = new Scene();
        var frame = ImageCodec.DecodePng(ReturnFixture("select-stage-zz-event-mumu.png"));
        Array.Clear(frame.Bgra, 0, 120 * frame.Width * 4);
        for (var y = 0; y < frame.Height; y++) Array.Clear(frame.Bgra, (y * frame.Width + 1015) * 4, (frame.Width - 1015) * 4);
        using var number = new OnnxStageNumberRecognizer(TapToNextOcrSamples.SharedOcr);
        using var nodes = new StageNodeRecognizer(scene.Adb, new TemplateMatcher(), scene.Log,
            ConfigService.ResolvePath("resources/templates/stage/node_states"), numberRecognizer: number);
        Assert.True(nodes.HasSelectedNode(frame));
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("skip", 1700, 380)),
            ImageCodec.EncodePng(frame.Bgra, frame.Width, frame.Height));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run(confirmSelectedNode: nodes.HasSelectedNode));
        Assert.Equal(2, scene.Adb.Taps.Count);
        // 同一画面去掉台座光圈后，独立数字不能冒充当前选中关卡。
        Array.Clear(frame.Bgra, 800 * frame.Width * 4, (frame.Height - 800) * frame.Width * 4);
        Assert.False(nodes.HasSelectedNode(frame));
    }

    /// <summary>1.1.6.0 实机卡住的 ZZ 活动蓝色出击准备页，必须接续战后剧情返回。</summary>
    [Fact]
    public async Task EndingStory_RealBlueEntry_ReturnsWithoutWatchdog()
    {
        using var scene = new Scene();
        var frame = ImageCodec.DecodePng(ReturnFixture("select-stage-zz-event-mumu.png"));
        Assert.True(new SelectStageTitleRecognizer(ConfigService.ResolvePath("resources/templates/stage/sortie/title_select_stage.png")).Recognize(frame, default), "选关标题未命中");
        Assert.NotNull(new StageEntryButtonRecognizer().Match(frame,
            ConfigService.ResolvePath("resources/templates/stage/sortie/button_sortie_preparations.png"), new Roi(1470, 754, 450, 326), .90, default));
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("menu", 1700, 10)),
            scene.Frame(("skip", 1700, 380)), ReturnFixture("select-stage-zz-event-mumu.png"));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(3, scene.Adb.Taps.Count);
        Assert.DoesNotContain(scene.Log.Entries, e => e.Message.Contains("连续 2 分钟"));
    }

    /// <summary>战后故事可接续多段剧情；每次 SKIP 后都检测返回页，不依赖两分钟恢复。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndingStory_SkipReturnsImmediatelyOrContinuesNextStory(bool anotherStory)
    {
        using var scene = new Scene();
        var frames = new List<byte[]> { scene.Frame(("story", 1500, 930)),
            scene.Frame(("menu", 1700, 10)), scene.Frame(("skip", 1700, 380)) };
        if (anotherStory)
        {
            frames.Add(scene.Frame(("menu", 1700, 10)));
            frames.Add(scene.Frame(("skip", 1700, 380)));
        }
        frames.Add(ReturnFixture("select-stage-z-mumu5558.png"));
        scene.Steps(frames.ToArray());
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(anotherStory ? 5 : 3, scene.Adb.Taps.Count);
        Assert.DoesNotContain(scene.Log.Entries, e => e.Message.Contains("连续 2 分钟"));
    }

    /// <summary>保留用户准备页页首原始像素，保证准备页不能再被识别为选择关卡。</summary>
    [Theory]
    [InlineData("preparation-space-header.png")]
    [InlineData("preparation-ground-header.png")]
    public void FeedbackPreparationHeader_IsNotStage(string file)
    {
        var frame = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sortie", file)));
        var title = new SelectStageTitleRecognizer(ConfigService.ResolvePath("resources/templates/stage/sortie/title_select_stage.png"));
        Assert.False(title.Recognize(frame, default));
    }

    /// <summary>与故事共框的出击准备入口不能触发剧情点击；完整选关页会直接交回导航。</summary>
    [Fact]
    public async Task FeedbackPreparationEntry_IsNotStoryOrContinue()
    {
        using var scene = new Scene();
        var frame = ImageCodec.DecodePng(scene.Frame(("button_sortie_preparations", 1494, 834)));
        var matcher = new StageEntryButtonRecognizer();
        foreach (var name in new[] { "story", "continue" })
            Assert.Null(matcher.Match(frame, ConfigService.ResolvePath($"resources/templates/stage/sortie/{name}.png"),
                new Roi(1152, 830, 768, 250), .90, default));
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("skip", 1603, 356)), scene.Frame(("title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(2, scene.Adb.Taps.Count);
    }

    /// <summary>标题先出现而按钮尚未加载时不得完成、不得点故事；随后完整选关页才可交接。</summary>
    [Fact]
    public async Task FeedbackLoadingTitle_WaitsForContentWithoutClicking()
    {
        using var scene = new Scene();
        var ready = scene.Frame(("title", 200, 20));
        var loading = ImageCodec.DecodePng(ready);
        Array.Clear(loading.Bgra, 120 * 1920 * 4, loading.Bgra.Length - 120 * 1920 * 4);
        var loadingPng = ImageCodec.EncodePng(loading.Bgra, 1920, 1080);
        var reads = 0;
        scene.Adb.ScreenshotProvider = () => scene.Adb.Taps.Count == 0
            ? scene.Frame(("story", 1500, 930)) : scene.Adb.Taps.Count == 1 ? scene.Frame(("skip", 1603, 356)) : ++reads <= 3 ? loadingPng : ready;
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.True(reads >= 4);
        Assert.Equal(2, scene.Adb.Taps.Count);
    }

    /// <summary>HARD 使用独立结算入口，也不能仅见标题就结束等待并读取黑屏上的节点。</summary>
    [Fact]
    public async Task FeedbackHardLoadingTitle_WaitsForActualEntry()
    {
        using var scene = new Scene();
        var ready = ReturnFixture("select-stage-z-mumu5558.png");
        var loading = ImageCodec.DecodePng(ready);
        Array.Clear(loading.Bgra, 120 * 1920 * 4, loading.Bgra.Length - 120 * 1920 * 4);
        var loadingPng = ImageCodec.EncodePng(loading.Bgra, 1920, 1080);
        var clock = new ManualProgressTime();
        scene.Adb.BeforeScreenshotAsync = (_, _) => { clock.Advance(1); return Task.CompletedTask; };
        scene.Adb.ScreenshotProvider = () => scene.Adb.ScreenshotCount <= 3 ? loadingPng : ready;
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.ConfirmHardReturn(clock));
        Assert.Equal(4, scene.Adb.ScreenshotCount);
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>战后准备页证据优先于残留选关标题，必须重验两帧并交回编制，不再读取节点或计数。</summary>
    [Fact]
    public async Task FeedbackPreparation_AfterSkipResumesWithoutStageCompletion()
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("skip", 1603, 356)), scene.Frame(("title", 200, 20)));
        var checks = 0;
        Assert.Equal(SortieFlowResult.ProgressResumePreparation,
            await scene.Run(confirmPreparation: (_, _) => { if (scene.Adb.Taps.Count < 2) return Task.FromResult(false); checks++; return Task.FromResult(true); }));
        Assert.Equal(2, checks);
        Assert.Equal(2, scene.Adb.Taps.Count);
    }

    /// <summary>原像素页首和按钮裁剪接入真实编制识别器，确保产品回调能确认用户实际准备页。</summary>
    [Fact]
    public async Task FeedbackPreparation_RealButtonsResumeOriginalFormation()
    {
        using var scene = new Scene();
        var frame = ImageCodec.DecodePng(scene.Frame());
        foreach (var (file, x, y) in new[] { ("preparation-ground-header.png", 0, 0), ("preparation-ground-buttons.png", 870, 810) })
        {
            var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sortie", file)));
            for (var row = 0; row < crop.Height; row++)
                Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, frame.Bgra, ((y + row) * 1920 + x) * 4, crop.Width * 4);
        }
        var formation = new AutoFormationService(scene.Automation, new TemplateMatcher(), scene.Log,
            AutoFormationTemplates.FromConfig(new AppConfig()));
        Assert.True(await formation.IsPreparationPageAsync(frame, default));
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("skip", 1603, 356)), ImageCodec.EncodePng(frame.Bgra, 1920, 1080));
        Assert.Equal(SortieFlowResult.ProgressResumePreparation,
            await scene.Run(confirmPreparation: formation.IsPreparationPageAsync));
        Assert.Equal(2, scene.Adb.Taps.Count);
    }

    /// <summary>连续三帧确认就绪；消失、移动、尺寸和类型变化均撤销授权，不再依赖固定秒数。</summary>
    [Fact]
    public void FeedbackEntryAnimation_RequiresContinuousStability()
    {
        var gate = new StableStageEntry();
        var button = new TemplateMatchResult(new Roi(1494, 834, 365, 115), .99);
        Assert.Null(gate.Observe(button, "preparation"));
        Assert.True(gate.HasCandidate);
        Assert.Null(gate.Observe(button, "preparation"));
        Assert.Null(gate.Observe(null, "preparation"));
        Assert.False(gate.HasCandidate);
        Assert.Null(gate.Observe(button, "preparation"));
        var moved = button with { Region = new Roi(1500, 834, 365, 115) };
        Assert.Null(gate.Observe(moved, "preparation"));
        Assert.Null(gate.Observe(moved, "watch"));
        var resized = moved with { Region = new Roi(1500, 834, 367, 115) };
        Assert.Null(gate.Observe(resized, "watch"));
        Assert.Null(gate.Observe(resized, "watch"));
        var fresh = resized with { Confidence = .98 };
        Assert.Same(fresh, gate.Observe(fresh, "watch"));
    }

    /// <summary>每帧微移必须相对首帧累计检查，不能靠逐帧小位移提前点击。</summary>
    [Fact]
    public void EntrySlowDrift_DoesNotCountAsStable()
    {
        var gate = new StableStageEntry();
        for (var x = 1494; x < 1510; x += 2)
            Assert.Null(gate.Observe(new(new Roi(x, 834, 365, 115), .99), "preparation"));
    }
}

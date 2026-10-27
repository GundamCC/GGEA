using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>以真实新增按钮素材模拟剧情、战斗、结算与返回，验证整个关卡生命周期的识别持续性。</summary>
public sealed partial class AutoProgressFlowTests
{
    /// <summary>通用关闭模板必须识别原始系列奖励按钮，保留推图的 90% 阈值。</summary>
    [Fact]
    public void SharedClose_MatchesRealSeriesPopup()
    {
        using var scene = new Scene();
        var matcher = new DialogCloseRecognizer();
        foreach (var png in new[] { scene.SeriesPopupFrame(), scene.DirectSeriesPopupFrame() })
        {
            var screen = ImageCodec.DecodePng(png);
            var match = matcher.Match(screen, ConfigService.ResolvePath("resources/templates/common/close.png"),
                RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, screen.Width, screen.Height), .9, CancellationToken.None);
            Assert.True(match is not null && match.Confidence >= .9, $"关闭候选：{match}");
        }
    }

    /// <summary>MENU 消失后即使 AUTO 缺失，灰色速度也应开启一次；下一帧青色只能确认，不能再次切换。</summary>
    [Fact]
    public async Task SpeedAfterMenu_WithoutAuto_VerifiesOnAndDoesNotToggleAgain()
    {
        using var scene = new Scene();
        scene.Adb.ScreenshotProvider = () => scene.Adb.Taps.Count switch
        {
            0 => scene.Frame(("menu", 1603, 12)),
            1 => scene.Frame(("speed_off", 1630, 14)),
            _ when !scene.Log.Entries.Any(x => x.Message.Contains("颜色确认已开启")) => scene.Frame(("speed_on", 1630, 14)),
            2 => scene.Frame(("story", 1500, 930)),
            3 => scene.Frame(("skip", 1603, 356)),
            _ => scene.Frame(("title", 200, 20)),
        };
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(new[] { scene.Center("menu", 1603, 12), (1712, 50), scene.Center("story", 1500, 930), scene.Center("skip", 1603, 356) }, scene.Adb.Taps);
        Assert.Contains(scene.Log.Entries, x => x.Message.Contains("二倍速点击后复查：颜色确认已开启"));
    }

    /// <summary>MuMu 5558 的 Z 高达标题背景使旧整图匹配失败；原像素补黑边后验证 SKIP 返回与原有门禁。</summary>
    [Theory]
    [InlineData(true, false, SortieFlowResult.ProgressStageCompleted)]
    [InlineData(false, false, SortieFlowResult.ProgressTimeout)]
    [InlineData(true, true, SortieFlowResult.ProgressTimeout)]
    public async Task ZStageTitle_AfterSkipHonorsCompletionGates(bool skip, bool boss, SortieFlowResult expected)
    {
        using var scene = new Scene();
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "select-stage-z-mumu5558.png")));
        var pixels = ImageCodec.DecodePng(scene.Frame(("button_sortie_preparations", 1494, 834))).Bgra;
        for (var y = 0; y < crop.Height; y++)
            Buffer.BlockCopy(crop.Bgra, y * crop.Width * 4, pixels, y * 1920 * 4, crop.Width * 4);
        var frame = ImageCodec.EncodePng(pixels, 1920, 1080);
        using var oldMatcher = new ProgressTemplateMatcher();
        Assert.Null(oldMatcher.Match(ImageCodec.DecodePng(frame),
            ConfigService.ResolvePath("resources/templates/stage/sortie/title_select_stage.png"),
            new Roi(0, 0, 1000, 120), .90, default));
        if (skip) scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("skip", 1603, 356)), frame);
        else scene.Steps(frame);
        Assert.Equal(expected, await scene.Run(timeout: 3000, requireSeriesCompleteClose: boss));
        Assert.Equal(skip ? 2 : 0, scene.Adb.Taps.Count);
    }

    /// <summary>空白与纯白不能冒充标题；取消在任何原生匹配前生效。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void SelectStageTitle_RejectsUniformPage(byte value)
    {
        var recognizer = new SelectStageTitleRecognizer(
            ConfigService.ResolvePath("resources/templates/stage/sortie/title_select_stage.png"));
        var frame = ImageCodec.DecodePng(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1000, 120, value), 1000, 120));
        Assert.False(recognizer.Recognize(frame, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => recognizer.Recognize(frame, cancellation.Token));
    }

    /// <summary>原图遮去最后一个字或将标题移出页首均不能完成，不能仅靠几个相似笔画通过。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectStageTitle_RequiresCompleteTextInHeader(bool outsideHeader)
    {
        var recognizer = new SelectStageTitleRecognizer(
            ConfigService.ResolvePath("resources/templates/stage/sortie/title_select_stage.png"));
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "select-stage-z-mumu5558.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        for (var y = 0; y < crop.Height; y++)
            Buffer.BlockCopy(crop.Bgra, y * crop.Width * 4, pixels,
                ((outsideHeader ? y + 200 : y) * 1920) * 4, (outsideHeader ? crop.Width : 405) * 4);
        Assert.False(recognizer.Recognize(ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080)), default));
    }

    /// <summary>青蓝亮光使绿蓝通道接近时，已开启的 AUTO 仍保持全部自动，继续处理故事。</summary>
    [Fact]
    public async Task AllAutoWithCyanLight_DoesNotToggleOff()
    {
        using var scene = new Scene();
        scene.Steps(scene.CyanAutoFrame(), scene.Frame(("title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(new[] { scene.Center("story", 1500, 930) }, scene.Adb.Taps);
    }

    /// <summary>搜索区下沿的红色相似字形不在实际按钮位置，不能授权点击上方 AUTO。</summary>
    [Fact]
    public async Task EnemyGlyphAwayFromButton_DoesNotAuthorizeFixedTap()
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("auto_enemy", 1425, 48), ("story", 1500, 930)),
            scene.Frame(("title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(new[] { scene.Center("story", 1500, 930) }, scene.Adb.Taps);
    }

    /// <summary>实机关闭按钮的透明边缘混入草地后，仍确认白字暗底并恢复自动，随后继续完成本关。</summary>
    [Fact]
    public async Task OffAutoWithTransparentGrass_RecoversThenContinues()
    {
        using var scene = new Scene();
        scene.Steps(scene.OffAutoFixtureFrame(), scene.Frame(("auto_all", 1425, 10), ("story", 1500, 930)),
            scene.Frame(("title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(new[] { (1508, 50), scene.Center("story", 1500, 930) }, scene.Adb.Taps);
    }

    /// <summary>AUTO 点击未生效时只做有限恢复；连续两轮两分钟无进展必须停止，不能无限追加点击。</summary>
    [Fact]
    public async Task AutoToggleWithoutStateChange_StopsAfterBoundedRetries()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        var off = scene.OffAutoFixtureFrame();
        scene.Adb.ScreenshotProvider = () => off;
        scene.Adb.BeforeScreenshotAsync = (_, _) =>
        {
            if (scene.Adb.Taps.Count == 3 || scene.Adb.Taps.Count == 4) clock.Advance(120);
            return Task.CompletedTask;
        };
        Assert.Equal(SortieFlowResult.Stalled, await scene.Run(recoveryTime: clock));
        // 初次三次尝试；第一轮恢复一次后确认状态未变，不再叠加点击，下一轮检查停止。
        Assert.Equal(Enumerable.Repeat((1508, 50), 4), scene.Adb.Taps);
        Assert.Contains(scene.Log.Entries, entry => entry.Level == "WARN" && entry.Message.Contains("连续两次 2 分钟"));
    }

    /// <summary>开场、战斗中和故事后均可略过并恢复 AUTO；继续/故事相似按钮不能互相抢占。</summary>
    [Fact]
    public async Task StoryRound_KeepsAutoAndSkipUntilStageTitle()
    {
        using var scene = new Scene();
        // 开场 SKIP 不授权结束；同一轮继续处理 AUTO、结算和战后剧情。
        scene.Steps(scene.Frame(("menu", 1700, 10)), scene.Frame(("skip", 1700, 380)),
            scene.Frame(("auto_enemy", 1425, 10)), scene.Frame(("auto_close", 1425, 10)),
            scene.Frame(("continue", 1500, 930)), scene.Frame(("story", 1500, 930)),
            scene.Frame(("menu", 1700, 10)), scene.Frame(("skip", 1700, 380)),
            scene.Frame(("title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(new[] { scene.Center("menu", 1700, 10), scene.Center("skip", 1700, 380),
            (1508, 50), (1508, 50), scene.Center("continue", 1500, 930),
            scene.Center("story", 1500, 930), scene.Center("menu", 1700, 10),
            scene.Center("skip", 1700, 380) }, scene.Adb.Taps);
    }
    /// <summary>无需本轮点击 MENU 即可处理 SKIP；两者同帧出现时只点击 SKIP。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipWithoutPriorMenu_IsClickedBeforeMenu(bool showMenu)
    {
        using var scene = new Scene();
        var first = showMenu ? scene.Frame(("skip", 1603, 356), ("menu", 1700, 10))
            : scene.Frame(("skip", 1603, 356));
        scene.Steps(first, scene.Frame(("story", 1500, 930)), scene.Frame(("title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(new[] { scene.Center("skip", 1603, 356), scene.Center("story", 1500, 930) }, scene.Adb.Taps);
    }

    /// <summary>未点击战后故事时，普通 SKIP 即使随后看到导航页也不能作为通关证据。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipWithoutEndingStory_DoesNotAuthorizeNavigationCompletion(bool mainStage)
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("skip", 1603, 356)),
            mainStage ? scene.Frame(("main_title", 200, 20)) : scene.Frame(("title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressTimeout, await scene.Run(timeout: 1000));
        Assert.Equal(new[] { scene.Center("skip", 1603, 356) }, scene.Adb.Taps);
    }

    /// <summary>点击 SKIP 后仍停在未知页面时不能计数，原有限等待与停止条件仍有效。</summary>
    [Fact]
    public async Task SkipEndsOnUnknownPage_DoesNotComplete()
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("skip", 1603, 356)), scene.Frame());
        Assert.Equal(SortieFlowResult.ProgressTimeout, await scene.Run(timeout: 1000));
        Assert.Equal(new[] { scene.Center("skip", 1603, 356) }, scene.Adb.Taps);
    }

    /// <summary>SKIP 后失败提示与返回标题同帧出现时仍优先停止，不能被返回页确认吞掉。</summary>
    [Fact]
    public async Task SkipThenFailureAndReturnTitle_FailureWins()
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("skip", 1603, 356)), scene.Frame(("failed", 100, 20), ("title", 500, 20)));
        Assert.Equal(SortieFlowResult.FailureDetected, await scene.Run(timeout: 5000));
        Assert.Equal(new[] { scene.Center("skip", 1603, 356), (1670, 980) }, scene.Adb.Taps);
    }

    /// <summary>区域外的 SKIP 不点击，缺少它也不结束关卡；未知页只按本关总预算停止且不反复开关 MENU。</summary>
    [Theory]
    [InlineData(600, 200)]
    [InlineData(1400, 540)]
    public async Task SkipOutsideRequestedRegion_UsesRoundTimeoutWithoutClickingIt(int x, int y)
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("menu", 1700, 10)), scene.Frame(("menu", 1700, 10), ("skip", x, y)));
        Assert.Equal(SortieFlowResult.ProgressTimeout, await scene.Run(timeout: 1000, skipTimeout: 0));
        Assert.Equal(new[] { scene.Center("menu", 1700, 10) }, scene.Adb.Taps);
        Assert.Single(scene.Log.Entries, entry => entry.Level == "INFO" && entry.Message.Contains("结束本次菜单等待"));
    }

    /// <summary>MENU 后关卡自动结算的真实成绩页须继续处理；残留 MENU 命中和局部等待是否到期都不能挡住继续。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    public async Task MenuWithoutSkip_RealStage12ScoreContinuesSettlement(int skipTimeout)
    {
        using var scene = new Scene();
        var scoreFrame = scene.Stage12ScoreFrame(menuVisible: true);
        var screen = ImageCodec.DecodePng(scoreFrame);
        var template = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath("resources/templates/stage/sortie/continue.png")));
        var next = new TemplateMatcher().Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
            template.Width, template.Height, .90, new Roi(1152, 830, 768, 250), ct: CancellationToken.None);
        Assert.NotNull(next);
        Assert.InRange(next.CenterX, 1650, 1690);
        Assert.InRange(next.CenterY, 960, 1000);
        scene.Steps(scene.Frame(("menu", 1700, 10)), scoreFrame,
            scene.Frame(("story", 1500, 930)), scene.Frame(("title", 200, 20)));

        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run(skipTimeout: skipTimeout));
        Assert.Equal(new[] { scene.Center("menu", 1700, 10), (next.CenterX, next.CenterY),
            scene.Center("story", 1500, 930) }, scene.Adb.Taps);
        Assert.Contains(scene.Log.Entries, entry => entry.Message.Contains("继续按钮"));
    }

    /// <summary>未到期的 MENU→SKIP 等待也不能屏蔽 AUTO；MENU 持续存在但没有 SKIP 时仍接续战斗和结算。</summary>
    [Fact]
    public async Task MenuWithoutSkip_PendingWaitDoesNotBlockAutoOrStory()
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("menu", 1700, 10)),
            scene.Frame(("menu", 1700, 10), ("auto_close", 1425, 10)),
            scene.Frame(("menu", 1700, 10), ("auto_all", 1425, 10), ("story", 1500, 930)),
            scene.Frame(("title", 200, 20)));

        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(new[] { scene.Center("menu", 1700, 10), (1508, 50),
            scene.Center("story", 1500, 930) }, scene.Adb.Taps);
    }

    /// <summary>SKIP 未出现且局部等待已结束时取消仍立即传播，不发重复 MENU 或伪报完成。</summary>
    [Fact]
    public async Task MenuWithoutSkip_CancellationStillStopsObservation()
    {
        using var scene = new Scene();
        using var cancellation = new CancellationTokenSource();
        scene.Steps(scene.Frame(("menu", 1700, 10)));
        scene.Adb.BeforeScreenshotAsync = (count, token) =>
        {
            if (count >= 3) cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.Run(skipTimeout: 0, ct: cancellation.Token));
        Assert.Equal(new[] { scene.Center("menu", 1700, 10) }, scene.Adb.Taps);
    }

    /// <summary>菜单被剧情自行收起后，即使略过等待已过期也继续处理 AUTO、再次出现的菜单与故事。</summary>
    [Fact]
    public async Task MenuDisappearsWithoutSkip_ContinuesDetection()
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("menu", 1700, 10)), scene.Frame(("auto_close", 1425, 10)),
            scene.Frame(("menu", 1700, 10)), scene.Frame(("skip", 1603, 356)),
            scene.Frame(("story", 1500, 930)), scene.Frame(("title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run(skipTimeout: 0));
        Assert.Equal(new[] { scene.Center("menu", 1700, 10), (1508, 50),
            scene.Center("menu", 1700, 10), scene.Center("skip", 1603, 356),
            scene.Center("story", 1500, 930) }, scene.Adb.Taps);
        Assert.Contains(scene.Log.Entries, item => item.Message.Contains("继续检测剧情推进后的画面"));
    }

    /// <summary>覆盖右上四分之一区域的新位置与原有位置，识别后仍须确认故事结束及返回页。</summary>
    [Theory]
    [InlineData(960, 0)]
    [InlineData(1400, 380)]
    [InlineData(1600, 380)]
    [InlineData(1603, 356)]
    public async Task SkipInExpandedRightRegion_IsClicked(int x, int y)
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("menu", 1700, 10)), scene.Frame(("skip", x, y)),
            scene.Frame(("story", 1500, 930)), scene.Frame(("title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await scene.Run());
        Assert.Equal(new[] { scene.Center("menu", 1700, 10), scene.Center("skip", x, y),
            scene.Center("story", 1500, 930) }, scene.Adb.Taps);
    }

    /// <summary>系列完成后用底部通用区域关闭奖励包；真实主界面或主要关卡标题确认后才计数。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeriesCompletePopup_ClosesThenConfirmsReturn(bool returnHome)
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.SeriesPopupFrame(),
            returnHome ? scene.SeriesHomeFrame() : scene.Frame(("main_title", 200, 20)));
        var observed = await scene.Run();
        Assert.True(observed == SortieFlowResult.ProgressWorkNavigationRequired,
            $"{observed}: " + string.Join("; ", scene.Log.Entries.Select(entry => entry.Message)));
        Assert.Equal(new[] { scene.Center("story", 1500, 930), (772, 994) }, scene.Adb.Taps);
        Assert.Contains(scene.Log.Entries, entry => entry.Message.Contains("已关闭系列完成提示并确认"));
    }

    /// <summary>最后一个 NORMAL 的结束按钮后可能短暂露出选关页；必须继续取帧关闭奖励包才返回队列。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastNormal_TransientStageTitleBeforePopup_ClosesBeforeReturning(bool selectStage)
    {
        using var scene = new Scene();
        var ending = selectStage ? scene.Frame(("again", 1200, 920), ("select", 1600, 920))
            : scene.Frame(("story", 1500, 930));
        var title = scene.Frame(("title", 200, 20));
        var popup = scene.DirectSeriesPopupFrame();
        var transientFrames = 0;
        scene.Adb.ScreenshotProvider = () => scene.Adb.Taps.Count switch
        {
            0 => ending,
            1 => ++transientFrames <= 2 ? title : popup,
            _ => title,
        };

        var result = await scene.Run(requireSeriesCompleteClose: true);
        Assert.True(result == SortieFlowResult.ProgressStageCompleted,
            $"结果 {result}；点击 {string.Join(",", scene.Adb.Taps)}；日志 {string.Join("；", scene.Log.Entries.Select(entry => entry.Message))}");
        Assert.Equal(new[] { scene.Center(selectStage ? "select" : "story", selectStage ? 1600 : 1500,
            selectStage ? 920 : 930), (772, 994) }, scene.Adb.Taps);
        Assert.True(transientFrames > 2);
    }

    /// <summary>最终 NORMAL 没有关闭奖励包时，即使结束后持续出现选关或主要关卡标题也不能计数。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastNormal_MissingPopup_DoesNotCompleteOrNavigate(bool mainStage)
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("story", 1500, 930)),
            scene.Frame((mainStage ? "main_title" : "title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressTimeout,
            await scene.Run(timeout: 1000, requireSeriesCompleteClose: true));
        Assert.Equal(new[] { scene.Center("story", 1500, 930) }, scene.Adb.Taps);
        Assert.Contains(scene.Log.Entries, entry => entry.Level == "WARN" &&
            entry.Message.Contains("尚未识别并关闭系列完成奖励包"));
    }

    /// <summary>最终 NORMAL 关闭未生效仍有限重试；关闭后的未知页也不能作为推进完成。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastNormal_PopupUnconfirmedOrPersistent_RemainsBounded(bool persistent)
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.DirectSeriesPopupFrame(),
            persistent ? scene.DirectSeriesPopupFrame() : scene.Frame());
        Assert.Equal(SortieFlowResult.ProgressTimeout,
            await scene.Run(timeout: 5000, requireSeriesCompleteClose: true));
        Assert.Equal(persistent ? 4 : 2, scene.Adb.Taps.Count);
        Assert.All(scene.Adb.Taps.Skip(1), tap => Assert.Equal((772, 994), tap));
    }

    /// <summary>普通系列完成可直接跳到奖励包；真实标题与底部关闭同帧命中即可关闭，无需故事按钮先置状态。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeriesPopup_DirectReturn_ClosesThenConfirmsPage(bool returnHome)
    {
        using var scene = new Scene();
        scene.Steps(scene.DirectSeriesPopupFrame(), returnHome ? scene.SeriesHomeFrame() : scene.Frame(("main_title", 200, 20)));
        Assert.Equal(SortieFlowResult.ProgressWorkNavigationRequired, await scene.Run());
        Assert.Equal(new[] { (772, 994) }, scene.Adb.Taps);
    }

    /// <summary>直接返回的标题和关闭必须同时出现；异帧、区域外、只有移动及标题低分都不能授权。</summary>
    [Theory]
    [InlineData("separate")]
    [InlineData("outside")]
    [InlineData("move-only")]
    [InlineData("low-title")]
    public async Task SeriesPopup_DirectReturn_RequiresSameFrameTitleAndBottomClose(string variant)
    {
        using var scene = new Scene();
        var popup = ImageCodec.DecodePng(scene.DirectSeriesPopupFrame());
        if (variant is "separate" or "move-only" or "outside")
            TestImageFactory.PasteColoredBlock(popup.Bgra, popup.Width, 380, 140, 580, 930, 0, 0, 0);
        if (variant == "low-title")
            TestImageFactory.PasteColoredBlock(popup.Bgra, popup.Width, 850, 150, 536, 100, 0, 0, 0);
        if (variant == "outside")
        {
            var outside = ImageCodec.DecodePng(scene.Frame(("series_close", 692, 700)));
            for (var row = 700; row < 790; row++)
                Buffer.BlockCopy(outside.Bgra, row * 1920 * 4, popup.Bgra, row * 1920 * 4, 1920 * 4);
        }
        var frame = ImageCodec.EncodePng(popup.Bgra, popup.Width, popup.Height);
        scene.Steps(frame);
        if (variant == "separate")
            scene.Adb.ScreenshotProvider = () => scene.Adb.ScreenshotCount % 2 == 0 ? frame : scene.SeriesPopupFrame();
        Assert.Equal(SortieFlowResult.ProgressTimeout, await scene.Run(timeout: 1000));
        Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>直接关闭后仍需确认返回页；未知页不计数，持续弹窗最多关闭三次。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeriesPopup_DirectReturn_UnconfirmedOrPersistent_IsBounded(bool persistent)
    {
        using var scene = new Scene();
        scene.Steps(scene.DirectSeriesPopupFrame(), persistent ? scene.DirectSeriesPopupFrame() : scene.Frame());
        Assert.Equal(SortieFlowResult.ProgressTimeout, await scene.Run(timeout: 5000));
        Assert.Equal(persistent ? 3 : 1, scene.Adb.Taps.Count);
        Assert.All(scene.Adb.Taps, tap => Assert.Equal((772, 994), tap));
    }

    /// <summary>区域外关闭、只有移动、尚未结算的弹窗都不能触发关闭或冒充本关完成。</summary>
    [Theory]
    [InlineData("outside")]
    [InlineData("move-only")]
    [InlineData("before-result")]
    public async Task SeriesClose_RequiresBottomRegionAndResultPhase(string variant)
    {
        using var scene = new Scene();
        var popup = variant == "outside" ? scene.Frame(("series_close", 692, 700))
            : scene.SeriesPopupFrame(removeClose: variant == "move-only");
        scene.Steps(variant == "before-result" ? popup : scene.Frame(("story", 1500, 930)), popup);
        Assert.Equal(SortieFlowResult.ProgressTimeout, await scene.Run(timeout: 1000));
        if (variant == "before-result") Assert.Empty(scene.Adb.Taps);
        else Assert.Equal(new[] { scene.Center("story", 1500, 930) }, scene.Adb.Taps);
    }

    /// <summary>关闭后未知页不计为完成；关闭点击未生效时有限重试，避免无限点击。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeriesPopup_UnconfirmedOrPersistent_DoesNotComplete(bool persistent)
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.SeriesPopupFrame(),
            persistent ? scene.SeriesPopupFrame() : scene.Frame());
        Assert.Equal(SortieFlowResult.ProgressTimeout, await scene.Run(timeout: 5000));
        Assert.Equal(persistent ? 4 : 2, scene.Adb.Taps.Count);
        Assert.All(scene.Adb.Taps.Skip(1), tap => Assert.Equal((772, 994), tap));
    }

    /// <summary>选关标题出现在出击前残帧或故事后一直未出现，都不算本关完成；空画面不盲点 AUTO。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionRequiresStoryThenTitle(bool clickStory)
    {
        using var scene = new Scene();
        scene.Steps(clickStory ? scene.Frame(("story", 1500, 930)) : scene.Frame(("title", 200, 20)), scene.Frame());
        Assert.Equal(SortieFlowResult.ProgressTimeout, await scene.Run(timeout: 700));
        if (clickStory) Assert.Equal(new[] { scene.Center("story", 1500, 930) }, scene.Adb.Taps);
        else Assert.Empty(scene.Adb.Taps);
    }

    /// <summary>故事等待期间取消立即传播，不把下一帧选关标题当作已完成。</summary>
    [Fact]
    public async Task CancellationAfterStory_DoesNotComplete()
    {
        using var scene = new Scene();
        using var cancellation = new CancellationTokenSource();
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("title", 200, 20)));
        scene.Adb.BeforeScreenshotAsync = (count, ct) =>
        {
            if (count > 1) cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.Run(ct: cancellation.Token));
        Assert.Equal(new[] { scene.Center("story", 1500, 930) }, scene.Adb.Taps);
    }

    /// <summary>同帧失败优先于菜单等待和故事；MENU 后出现失败也沿用原退出动作与失败返回值。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureBeforeMenuAndStory_StopsRound(bool afterMenu)
    {
        using var scene = new Scene();
        var failure = scene.Frame(("failed", 100, 20), ("menu", 1700, 10), ("story", 1500, 930));
        scene.Steps(afterMenu ? scene.Frame(("menu", 1700, 10)) : failure, failure);
        Assert.Equal(SortieFlowResult.FailureDetected, await scene.Run(skipTimeout: 0));
        Assert.Equal(afterMenu ? new[] { scene.Center("menu", 1700, 10), (1670, 980) } : new[] { (1670, 980) }, scene.Adb.Taps);
    }

    /// <summary>空位/地形取消仍返回原有结果，交给上层编制恢复，不让略过按钮越过出击对话框。</summary>
    [Theory]
    [InlineData("empty", SortieFlowResult.EmptySlotCancelled)]
    [InlineData("terrain", SortieFlowResult.TerrainCancelled)]
    public async Task FormationDialog_StillReturnsCancellation(string hint, SortieFlowResult expected)
    {
        using var scene = new Scene();
        // 空位确认必须保留实际对话框的左右按钮，不能仅凭孤立提示进入跨帧等待。
        var cancelX = hint == "empty" ? 700 : 950;
        scene.Steps(hint == "empty"
            ? scene.Frame((hint, 800, 100), ("cancel", cancelX, 850), ("confirm", 1100, 850), ("menu", 1700, 10))
            : scene.Frame((hint, 800, 100), ("cancel", cancelX, 850), ("menu", 1700, 10)));
        Assert.Equal(expected, await scene.Run(allowFormation: false));
        Assert.Equal(new[] { scene.Center("cancel", cancelX, 850) }, scene.Adb.Taps);
    }

    /// <summary>下载结束回到同一持续识别循环；禁止下载时只取消，不处理背后的菜单。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadPolicy_PreservedBeforeStory(bool allowed)
    {
        using var scene = new Scene();
        scene.Steps(
            scene.Frame(("download_hint", 800, 100), ("download", 600, 850), ("cancel", 1000, 850)),
            scene.Frame(("menu", 1700, 10)), scene.Frame(("skip", 1700, 380)),
            scene.Frame(("story", 1500, 930)), scene.Frame(("title", 200, 20)));
        Assert.Equal(allowed ? SortieFlowResult.ProgressStageCompleted : SortieFlowResult.DownloadCancelled,
            await scene.Run(allowDownload: allowed));
        Assert.Equal(scene.Center(allowed ? "download" : "cancel", allowed ? 600 : 1000, 850), scene.Adb.Taps[0]);
        if (!allowed) Assert.Single(scene.Adb.Taps);
    }

    /// <summary>从界面上下文调用推图时，持续识别必须运行在后台；不能依赖测试没有 WPF 上下文而漏掉卡顿。</summary>
    [Fact]
    public async Task ProgressLoop_DoesNotRunOnCallerSynchronizationContext()
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("story", 1500, 930)), scene.Frame(("title", 200, 20)));
        var caller = new SynchronizationContext();
        var previous = SynchronizationContext.Current;
        var contexts = new List<SynchronizationContext?>();
        scene.Adb.BeforeScreenshotAsync = (_, _) =>
        {
            contexts.Add(SynchronizationContext.Current);
            return Task.CompletedTask;
        };
        Task<SortieFlowResult> task;
        try
        {
            SynchronizationContext.SetSynchronizationContext(caller);
            task = scene.Run();
            Assert.Same(caller, SynchronizationContext.Current);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.Equal(SortieFlowResult.ProgressStageCompleted, await task);
        Assert.NotEmpty(contexts);
        Assert.DoesNotContain(caller, contexts);
    }

    /// <summary>从真实观看/执行入口接入原下载与剧情流程；下载权限、最终返回确认和点击顺序必须保持有效。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WatchExecute_DownloadThenNormalProgress_PreservesPolicy(bool allowed)
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("watch", 1500, 780)), scene.Frame(("execute", 500, 770)),
            scene.Frame(("download_hint", 800, 100), ("download", 600, 850), ("cancel", 1000, 850)),
            scene.Frame(), scene.Frame(("skip", 1603, 356)), scene.Frame(("execute", 500, 800)),
            scene.Frame(("title", 200, 20)));
        // 观看下载后进入观看剧情，沿用十五秒检查及 SKIP/执行确认，不模拟战斗的故事按钮。
        var clock = new ManualProgressTime();
        scene.Adb.BeforeScreenshotAsync = (_, _) =>
        {
            if (allowed && scene.Adb.Taps.Count >= 3 && clock.GetTimestamp() == 0) clock.Advance(15);
            return Task.CompletedTask;
        };
        var result = await scene.RunWatchBattle(allowed, recoveryTime: clock);
        Assert.True(allowed == result.Completed, $"{result.Message}; logs={string.Join(" | ", scene.Log.Entries.Select(entry => entry.Message))}");
        var expected = new List<(int, int)> { scene.Center("watch", 1500, 780), scene.Center("execute", 500, 770),
            scene.Center(allowed ? "download" : "cancel", allowed ? 600 : 1000, 850) };
        if (allowed) expected.AddRange([(960, 540), scene.Center("skip", 1603, 356), scene.Center("execute", 500, 800)]);
        Assert.Equal(expected, scene.Adb.Taps);
    }

    /// <summary>真实观看入口在 SKIP 后额外确认执行，再由新帧选关页确认完成。</summary>
    [Fact]
    public async Task WatchSkip_ExecutesConfirmationBeforeReturning()
    {
        using var scene = new Scene();
        scene.Steps(scene.Frame(("watch", 1500, 780)), scene.Frame(("execute", 500, 770)),
            scene.Frame(), scene.Frame(("skip", 1603, 356)), scene.Frame(("execute", 500, 800)),
            scene.Frame(("title", 200, 20)));
        var clock = new ManualProgressTime();
        scene.Adb.BeforeScreenshotAsync = (_, _) =>
        {
            if (scene.Adb.Taps.Count == 2) clock.Advance(15);
            return Task.CompletedTask;
        };
        Assert.True((await scene.RunWatchBattle(true, recoveryTime: clock)).Completed);
        Assert.Equal(new[] { scene.Center("watch", 1500, 780), scene.Center("execute", 500, 770),
            (960, 540), scene.Center("skip", 1603, 356), scene.Center("execute", 500, 800) }, scene.Adb.Taps);
    }

    /// <summary>非观看入口、区域外或缺失的执行按钮均不能授权二次点击，也不能把选关残帧当完成。</summary>
    [Theory]
    [InlineData(false, 800)]
    [InlineData(true, 600)]
    [InlineData(true, -1)]
    public async Task WatchSkip_RequiresWatchEntryAndBottomExecute(bool watchEntry, int executeY)
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        using var cancellation = new CancellationTokenSource();
        scene.Adb.BeforeScreenshotAsync = (count, ct) =>
        {
            if (count == 1) clock.Advance(15);
            // 让区域外/缺失按钮实际经过数帧识别，再取消；不以一秒机器耗时截断点击链。
            if (count == 6) cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var skip = scene.Frame(("skip", 1603, 356));
        var after = executeY < 0 ? scene.Frame(("title", 200, 20))
            : scene.Frame(("execute", 500, executeY));
        scene.Steps(watchEntry ? [scene.Frame(), skip, after] : [skip, after]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.Run(
            skipTimeout: 0, watchEntry: watchEntry, recoveryTime: clock, ct: cancellation.Token));
        var expected = new List<(int, int)>();
        if (watchEntry) expected.Add((960, 540));
        expected.Add(scene.Center("skip", 1603, 356));
        Assert.Equal(expected, scene.Adb.Taps);
    }

    /// <summary>观看跳过后的执行等待仍优先停止失败关卡，不点击同帧执行。</summary>
    [Fact]
    public async Task WatchSkip_FailurePreemptsExecute()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        scene.Adb.BeforeScreenshotAsync = (count, _) =>
        {
            if (count == 1) clock.Advance(15);
            return Task.CompletedTask;
        };
        scene.Steps(scene.Frame(), scene.Frame(("skip", 1603, 356)),
            scene.Frame(("failed", 100, 20), ("execute", 500, 800)));
        Assert.Equal(SortieFlowResult.FailureDetected, await scene.Run(watchEntry: true, recoveryTime: clock));
        Assert.Equal(new[] { (960, 540), scene.Center("skip", 1603, 356), (1670, 980) }, scene.Adb.Taps);
    }

    /// <summary>首轮与未返回后的下一轮都等待十五秒；返回入口出现后不再点击中央。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchCycle_RepeatsAfterFifteenSecondsUntilReturn(bool resultPage)
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        var skip = scene.Frame(("skip", 1603, 356));
        var execute = scene.Frame(("execute", 500, 800));
        var returned = scene.Frame(("title", 200, 20));
        scene.Steps(scene.Frame(), skip, execute, scene.Frame(),
            resultPage ? scene.Frame(("again", 1200, 920), ("select", 1600, 920)) : returned, returned);
        var secondCycleStarted = false;
        scene.Adb.BeforeScreenshotAsync = (_, _) =>
        {
            if (scene.Adb.Taps.Count == 0)
            {
                clock.Advance(15);
            }
            else if (scene.Adb.Taps.Count == 3)
            {
                // 执行后仍是未知画面，必须等下一轮，不能直接停下或连续点中央。
                secondCycleStarted = true;
                clock.Advance(clock.GetTimestamp() % TimeSpan.FromSeconds(15).Ticks == 0 ? 14 : 1);
            }
            return Task.CompletedTask;
        };
        var result = await scene.Run(watchEntry: true, recoveryTime: clock);
        Assert.True(result == SortieFlowResult.ProgressStageCompleted,
            $"{result}; time={clock.GetTimestamp()}; taps={string.Join(",", scene.Adb.Taps)}; logs={string.Join(" | ", scene.Log.Entries.Select(entry => entry.Message))}");
        Assert.True(secondCycleStarted);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, clock.GetTimestamp());
        var expected = new List<(int, int)> { (960, 540), scene.Center("skip", 1603, 356),
            scene.Center("execute", 500, 800), (960, 540) };
        if (resultPage) expected.Add(scene.Center("select", 1600, 920));
        Assert.Equal(expected, scene.Adb.Taps);
    }

    /// <summary>SKIP 始终缺失时按十五秒继续中央点击；取消能结束等待且不会误报通关。</summary>
    [Fact]
    public async Task WatchCycle_MissingSkipRepeatsAndCancellationStops()
    {
        using var scene = new Scene();
        var clock = new ManualProgressTime();
        using var cancellation = new CancellationTokenSource();
        scene.Steps(scene.Frame());
        scene.Adb.BeforeScreenshotAsync = (_, ct) =>
        {
            if (scene.Adb.Taps.Count == 2) cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            clock.Advance(clock.GetTimestamp() % TimeSpan.FromSeconds(15).Ticks == 0 ? 14 : 1);
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene.Run(watchEntry: true,
            recoveryTime: clock, ct: cancellation.Token));
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, clock.GetTimestamp());
        Assert.Equal(new[] { (960, 540), (960, 540) }, scene.Adb.Taps);
    }

    /// <summary>编号未知仍从真实入口进入，按画面处理 HARD 结算或系列奖励；不依赖猜测关卡类型。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnidentifiedStage_UsesEntryAndObservedSettlement(bool seriesReward)
    {
        using var scene = new Scene();
        if (seriesReward)
            scene.Steps(scene.Frame(("button_sortie", 1500, 850)), scene.Frame(("execute", 500, 770)), scene.DirectSeriesPopupFrame(),
                scene.Frame(("main_title", 200, 20)));
        else
            scene.Steps(scene.Frame(("button_sortie", 1500, 850)), scene.Frame(("execute", 500, 770)),
                scene.Frame(("again", 1200, 920), ("select", 1600, 920)), scene.Frame(("title", 200, 20)));
        var result = await scene.RunWatchBattle(true, WorkStageKind.Unknown);
        Assert.True(result.Completed, result.Message);
        Assert.Equal(seriesReward, result.NeedsWorkNavigation);
        Assert.Equal(new[] { scene.Center("button_sortie", 1500, 850), scene.Center("execute", 500, 770),
            seriesReward ? (772, 994) : scene.Center("select", 1600, 920) }, scene.Adb.Taps);
    }

    /// <summary>测试场景只随点击推进，错误点击或漏点击都会破坏最终坐标序列。</summary>
    private sealed class Scene : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ggea-progress-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string> _paths = new();
        private readonly Dictionary<string, ScreenImage> _images = new();
        private readonly SortieFlowService _service;
        private readonly AutoProgressFlowTemplates _templates;
        private readonly MainScreenDetector _homeDetector;
        public FakeAdbService Adb { get; }
        public AutomationService Automation { get; }
        public TestLog Log { get; } = new();

        /// <summary>新增识别使用原始素材；其余提示使用互不相似的隔离字形，避免单色模板假阳性。</summary>
        public Scene()
        {
            Directory.CreateDirectory(_directory);
            foreach (var name in new[] { "menu", "skip", "story", "auto_close", "auto_all", "auto_enemy", "continue",
                         "watch", "button_sortie", "button_sortie_preparations" })
                _paths[name] = ConfigService.ResolvePath($"resources/templates/stage/sortie/{name}.png");
            _paths["execute"] = ConfigService.ResolvePath("resources/templates/dialog/execute.png");
            _paths["ok"] = ConfigService.ResolvePath("resources/templates/dialog/button_ok.png");
            _paths["title"] = ConfigService.ResolvePath("resources/templates/stage/sortie/title_select_stage.png");
            _paths["series_close"] = ConfigService.ResolvePath("resources/templates/common/close.png");
            _paths["series_title"] = ConfigService.ResolvePath("resources/templates/stage/series_complete_title.png");
            _paths["main_title"] = ConfigService.ResolvePath("resources/templates/stage/main_stage.png");
            _paths["home_active"] = ConfigService.ResolvePath(MainScreenDetector.DefaultOptions.ActivatedTemplatePath);
            _paths["home_inactive"] = ConfigService.ResolvePath(MainScreenDetector.DefaultOptions.InactiveTemplatePath);
            _paths["home_back"] = ConfigService.ResolvePath(MainScreenDetector.DefaultOptions.BackTemplatePath);
            _paths["tap"] = ConfigService.ResolvePath("resources/templates/stage/sortie/tap_to_next.png");
            var seed = 10;
            foreach (var name in new[] { "speed_on", "speed_off", "again", "select", "again_close", "failed", "empty", "confirm", "cancel",
                         "no_ap", "level", "reward", "reward_tap", "terrain", "download_hint", "download" })
            {
                var pixels = TestImageFactory.CreateBgra(64, 48, 0);
                var random = new Random(seed++);
                for (var i = 0; i < pixels.Length; i += 4)
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(random.Next(2) * 255);
                var path = Path.Combine(_directory, name + ".png");
                File.WriteAllBytes(path, ImageCodec.EncodePng(pixels, 64, 48));
                _paths[name] = path;
            }
            _paths["speed_on"] = ConfigService.ResolvePath("resources/templates/stage/sortie/double_speed_on.png");
            _paths["speed_off"] = ConfigService.ResolvePath("resources/templates/stage/sortie/double_speed_close.png");
            foreach (var (name, path) in _paths) _images[name] = ImageCodec.DecodePng(File.ReadAllBytes(path));
            Adb = new FakeAdbService(Frame());
            var matcher = new TemplateMatcher();
            var automation = Automation = new AutomationService(Adb, matcher, Log, textOcr: TapToNextOcrSamples.SharedOcr);
            _homeDetector = new MainScreenDetector(Adb, matcher, Log, MainScreenDetector.DefaultOptions with
            { ActivatedTemplatePath = _paths["home_active"], InactiveTemplatePath = _paths["home_inactive"],
                BackTemplatePath = _paths["home_back"], ConfirmationIntervalMs = 1 });
            var download = new DownloadDialogService(automation, matcher, Log, _paths["download"], _paths["cancel"], .90);
            _service = new SortieFlowService(automation, matcher, Log, download, _paths["tap"], _paths["speed_on"], _paths["speed_off"],
                _paths["continue"], _paths["again"], _paths["select"], _paths["again_close"], _paths["failed"], _paths["empty"],
                _paths["confirm"], _paths["cancel"], _paths["no_ap"], _paths["level"], _paths["reward"], _paths["reward_tap"], .90,
                _paths["terrain"], _paths["download_hint"]);
            _templates = new(_paths["menu"], _paths["skip"], _paths["story"], _paths["title"], _paths["auto_close"], _paths["auto_all"], _paths["auto_enemy"], _paths["series_close"], _paths["main_title"], _paths["series_title"]);
        }

        /// <summary>返回实际模板中心，断言点击没有偏离识别到的按钮。</summary>
        public (int X, int Y) Center(string name, int x, int y)
            => name == "tap" ? (x + 141, y + 28) // OCR 使用白色字形范围，不再使用模板的透明留白。
                : (x + _images[name].Width / 2, y + _images[name].Height / 2);

        /// <summary>以点击数量驱动下一张画面，最后一帧保持不变。</summary>
        public void Steps(params byte[][] frames) => Adb.ScreenshotProvider = () => frames[Math.Min(Adb.Taps.Count, frames.Length - 1)];

        /// <summary>仅缩短等待间隔，保留识别阈值、区域、实际素材和完成门禁。</summary>
        public Task<SortieFlowResult> Run(int timeout = 20000, int skipTimeout = 10000, bool allowFormation = true,
            bool allowDownload = true, CancellationToken ct = default, bool requireSeriesCompleteClose = false,
            TimeProvider? recoveryTime = null,
            Func<ScreenImage, CancellationToken, Task<bool>>? confirmPreparation = null,
            Func<ScreenImage, CancellationToken, bool>? confirmSelectedNode = null, bool watchEntry = false,
            bool detectHome = true)
            => _service.ExecuteForProgressAsync("test", allowDownload, allowFormation, allowFormation, _templates,
                timeoutMs: timeout, pollIntervalMs: 1, actionDelayMs: 1, skipWaitTimeoutMs: skipTimeout,
                confirmHome: detectHome ? token => _homeDetector.IsMainScreenAsync("test", token) : null, ct: ct,
                requireSeriesCompleteClose: requireSeriesCompleteClose,
                recoveryTimeProvider: recoveryTime, confirmPreparation: confirmPreparation, confirmSelectedNode: confirmSelectedNode, watchEntry: watchEntry);

        /// <summary>复用生产返回页门禁，只替换单调时钟和轮询间隔，不缩短生产等待上限。</summary>
        public Task<SortieFlowResult> ConfirmHardReturn(TimeProvider clock, CancellationToken ct = default,
            SortieFlowResult result = SortieFlowResult.AllRoundsCompleted,
            Func<ScreenImage, CancellationToken, Task<bool>>? confirmPreparation = null)
            => _service.ConfirmHardProgressReturnAsync("test", result, _templates.SelectStageTitle,
                ct, pollIntervalMs: 1, timeProvider: clock, confirmPreparation: confirmPreparation);

        /// <summary>沿用原刷本下一轮入口和完整结算，仅替换单调时钟及测试轮询间隔。</summary>
        public Task<SortieFlowResult> RunFarmNext(TimeProvider clock, CancellationToken ct = default, int openingTimeout = 20000)
            => _service.ExecuteNextRoundAsync("test", moreRoundsRemaining: true,
                tapToNextWaitTimeoutMs: openingTimeout, tapToNextPollIntervalMs: 1, tapToNextClickDelayMs: 1,
                continueStartDelayMs: 1, continueClickDelayMs: 1, continuePollIntervalMs: 1,
                continueWaitTimeoutMs: 20000, continueRepeatIntervalMs: 1, againStartDelayMs: 1,
                againPollIntervalMs: 1, againWaitTimeoutMs: 20000, ct: ct, recoveryTimeProvider: clock);

        /// <summary>复用与窗口相同的单关入口及下载处理，保留公共取消与结算判定，不在测试中伪造下载成功。</summary>
        public Task<AutoProgressBattleResult> RunWatchBattle(bool allowDownload, WorkStageKind kind = WorkStageKind.Normal, TimeProvider? recoveryTime = null)
        {
            var formation = new AutoFormationService(Automation, new TemplateMatcher(), Log, AutoFormationTemplates.FromConfig(new AppConfig()));
            var battle = new AutoProgressBattleService(Automation, formation, _paths["button_sortie_preparations"],
                (_, allowEmpty, allowTerrain, watchEntry, token) => _service.ExecuteForProgressAsync("test", allowDownload,
                    allowEmpty, allowTerrain, _templates, timeoutMs: 20000, pollIntervalMs: 1, actionDelayMs: 1,
                    confirmHome: homeToken => _homeDetector.IsMainScreenAsync("test", homeToken), ct: token, watchEntry: watchEntry, recoveryTimeProvider: recoveryTime),
                _paths["button_sortie"], _paths["watch"], _paths["execute"]);
            return battle.RunAsync("test", new(), kind, _ => { }, default);
        }

        /// <summary>合成用于流程回归的页面，只复制素材不生成或改写按钮文字。</summary>
        public byte[] Frame(params (string Name, int X, int Y)[] items)
        {
            // 完整选关测试页必须同时有标题和已加载入口；仅标题的黑屏过渡由专门回归覆盖。
            if (items.Any(item => item.Name == "title"))
                items = [.. items, ("button_sortie_preparations", 1494, 834)];
            var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
            foreach (var item in items)
            {
                var image = _images[item.Name];
                for (var y = 0; y < image.Height; y++)
                    for (var x = 0; x < image.Width; x++)
                    {
                        var source = (y * image.Width + x) * 4;
                        if (image.Bgra[source + 3] < 128) continue;
                        Buffer.BlockCopy(image.Bgra, source, pixels, ((item.Y + y) * 1920 + item.X + x) * 4, 4);
                    }
            }
            return ImageCodec.EncodePng(pixels, 1920, 1080);
        }

        /// <summary>保留 1912×1065 附件的原像素，右/下仅补黑边以满足测试通道规格；叠加 MENU 模拟残留命中。</summary>
        public byte[] Stage12ScoreFrame(bool menuVisible)
        {
            var fixture = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "Sortie", "progress-score-stage12-skip-missing.png")));
            var screen = ImageCodec.DecodePng(Frame());
            for (var row = 0; row < fixture.Height; row++)
                Buffer.BlockCopy(fixture.Bgra, row * fixture.Width * 4, screen.Bgra, row * screen.Width * 4, fixture.Width * 4);
            if (menuVisible)
            {
                var menu = _images["menu"];
                for (var y = 0; y < menu.Height; y++)
                for (var x = 0; x < menu.Width; x++)
                {
                    var source = (y * menu.Width + x) * 4;
                    if (menu.Bgra[source + 3] < 128) continue;
                    Buffer.BlockCopy(menu.Bgra, source, screen.Bgra, ((10 + y) * screen.Width + 1700 + x) * 4, 4);
                }
            }
            return ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height);
        }

        /// <summary>回放系列奖励包的实机底部；只保存按钮区域，不把整个账号画面纳入仓库。</summary>
        public byte[] SeriesPopupFrame(bool removeClose = false)
        {
            var pixels = FixtureFrame("series-complete-bottom-mumu5556.png", 880);
            if (removeClose) TestImageFactory.PasteColoredBlock(pixels.Bgra, pixels.Width, 380, 140, 580, 930, 0, 0, 0);
            return ImageCodec.EncodePng(pixels.Bgra, pixels.Width, pixels.Height);
        }

        /// <summary>拼接当前 V 高达奖励包的实机标题与底部裁剪，其余黑底为测试合成，不冒充完整运行截图。</summary>
        public byte[] DirectSeriesPopupFrame()
        {
            var screen = FixtureFrame("series-complete-bottom-v-mumu5556.png", 880);
            var title = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "Sortie", "series-complete-title-v-mumu5556.png")));
            for (var row = 0; row < title.Height; row++)
                Buffer.BlockCopy(title.Bgra, row * title.Width * 4, screen.Bgra,
                    ((100 + row) * screen.Width + 536) * 4, title.Width * 4);
            return ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height);
        }

        /// <summary>使用关闭后的真实主界面按钮，复用主界面检测器的两帧确认。</summary>
        public byte[] SeriesHomeFrame()
        {
            var pixels = FixtureFrame("series-complete-home-mumu5556.png", 975);
            return ImageCodec.EncodePng(pixels.Bgra, pixels.Width, pixels.Height);
        }

        /// <summary>原像素拼接裁剪证据；其余画面黑底为明确的测试合成页。</summary>
        private ScreenImage FixtureFrame(string name, int y)
        {
            var fixture = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sortie", name)));
            var screen = ImageCodec.DecodePng(Frame());
            for (var row = 0; row < fixture.Height; row++)
                Buffer.BlockCopy(fixture.Bgra, row * fixture.Width * 4, screen.Bgra,
                    ((y + row) * screen.Width) * 4, fixture.Width * 4);
            return screen;
        }

        /// <summary>保留 AUTO 素材字形，仅模拟绿蓝接近的青色亮光；故事按钮使多余 AUTO 点击立即暴露。</summary>
        public byte[] CyanAutoFrame()
        {
            var screen = ImageCodec.DecodePng(Frame(("auto_all", 1425, 10), ("story", 1500, 930)));
            var image = _images["auto_all"];
            for (var y = 10; y < 10 + image.Height; y++)
            for (var x = 1425; x < 1425 + image.Width; x++)
            {
                var offset = (y * screen.Width + x) * 4;
                if (screen.Bgra[offset] > screen.Bgra[offset + 2] + 25)
                    screen.Bgra[offset + 1] = screen.Bgra[offset];
            }
            return ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height);
        }

        /// <summary>只拼入原始实机按钮区域用于回归，不将合成测试页面当作运行截图。</summary>
        public byte[] OffAutoFixtureFrame()
        {
            var fixture = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "Sortie", "auto-off-mumu5556.png")));
            var screen = ImageCodec.DecodePng(Frame());
            for (var y = 0; y < fixture.Height; y++)
                Buffer.BlockCopy(fixture.Bgra, y * fixture.Width * 4, screen.Bgra,
                    (y * screen.Width + 1420) * 4, fixture.Width * 4);
            return ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height);
        }

        /// <summary>拼入奖励机体的原始底部裁剪，保留模型与文字粘连，不伪造完整运行截图。</summary>
        public byte[] UnitRewardFrame()
        {
            var screen = FixtureFrame("fierce-unit-reward-tap-mumu16448.png", 780);
            return ImageCodec.EncodePng(screen.Bgra, screen.Width, screen.Height);
        }

        /// <summary>只清理本用例创建的隔离素材。</summary>
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}

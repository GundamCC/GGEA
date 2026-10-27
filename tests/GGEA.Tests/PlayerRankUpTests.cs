using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>升级遮罩的同帧确认、延迟出现、丢失点击与返回关卡页验证。</summary>
public sealed class PlayerRankUpTests
{
    private static string Template(string name) => Path.Combine(AppContext.BaseDirectory, "resources/common/templates/stage/skip", name);
    private static UiLayout Layout => new() { Anchors = [
        new() { Name = "stageSkip", X = 1600, Y = 700 },
        new() { Name = "skipDialogCancel", X = 770, Y = 850 },
        new() { Name = "skipDialogExecute", X = 1150, Y = 850 },
        new() { Name = "skipResultOk", X = 1676, Y = 988 },
        new() { Name = "skipResultTitle", X = 820, Y = 30, Width = 330, Height = 80 },
        new() { Name = "skipCountLimit", X = 1590, Y = 680, Width = 160, Height = 60 }] };

    /// <summary>完整略过流程在 OK 后才处理升级，并等待次数上限出现后返回成功。</summary>
    [Fact]
    public async Task SkipFlow_HandlesRankUpAfterOk()
    {
        var result = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(result, "skip_result.png", 820, 30);
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 200), 1920, 1080));
        adb.EnqueueScreenshots(ImageCodec.EncodePng(result, 1920, 1080), Screen(true, true), Screen(true, true), Screen(count: true));
        Assert.True((await Service(adb).SkipCurrentAsync("serial", Layout)).Success);
        Assert.Equal(new[] { (1600, 700), (1150, 850), (1676, 988), (960, 973) }, adb.Taps);
    }

    /// <summary>网络延迟及第一次继续点击被吞掉时，重新截图后重试，不提前报告成功。</summary>
    [Fact]
    public async Task DelayedOverlayAndDroppedTap_RechecksUntilStageVisible()
    {
        var adb = new FakeAdbService(Screen());
        adb.EnqueueScreenshots(Screen(true, true), Screen(true, true), Screen(true, true), Screen(count: true));
        Assert.True(await Service(adb).ConfirmPostSkipAsync("serial", Layout));
        Assert.Equal(new[] { (960, 973), (960, 973) }, adb.Taps);
    }

    /// <summary>升级标题和继续提示分属不同帧时不能合并成一次点击授权。</summary>
    [Fact]
    public async Task SeparateSignals_NeverClicks()
    {
        var adb = new FakeAdbService(Screen(title: true));
        adb.EnqueueScreenshots(Screen(next: true));
        Assert.False(await Service(adb, 800).ConfirmPostSkipAsync("serial", Layout));
        Assert.Empty(adb.Taps);
    }

    /// <summary>即使背景误命中次数上限，只要升级标题还在就不能报告返回关卡页。</summary>
    [Fact]
    public async Task TitleWithoutContinue_BlocksCompletion()
    {
        var adb = new FakeAdbService(Screen(title: true));
        adb.EnqueueScreenshots(Screen(title: true, count: true));
        Assert.False(await Service(adb, 200).ConfirmPostSkipAsync("serial", Layout));
        Assert.Empty(adb.Taps);
    }

    /// <summary>没有升级时直接确认原关卡状态，不额外点击屏幕。</summary>
    [Fact]
    public async Task NoRankUp_ConfirmsStageWithoutClick()
    {
        var adb = new FakeAdbService(Screen(count: true));
        Assert.True(await Service(adb).ConfirmPostSkipAsync("serial", Layout));
        Assert.Empty(adb.Taps);
    }

    /// <summary>取消发生在截图前时不得继续关闭弹窗。</summary>
    [Fact]
    public async Task Cancelled_DoesNotClick()
    {
        var adb = new FakeAdbService(Screen(true, true));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(adb).ConfirmPostSkipAsync("serial", Layout, cancel.Token));
        Assert.Empty(adb.Taps);
    }

    /// <summary>正常返回在升级检测延迟内直接结束，升级素材甚至不需要读取。</summary>
    [Fact]
    public async Task NormalStage_DoesNotLoadRankTemplates()
    {
        var adb = new FakeAdbService(Screen(count: true));
        var log = new TestLog();
        var service = new StageSkipService(new AutomationService(adb, new TemplateMatcher(), log), log, new()
        {
            SkipCountLimitTemplatePath = Template("count_limit.png"),
            PlayerRankUpTemplatePath = "absent-title.png",
            PlayerRankUpNextTemplatePath = "absent-next.png",
        });
        Assert.True(await service.ConfirmPostSkipAsync("serial", Layout));
        Assert.Equal(1, adb.ScreenshotCount);
    }

    /// <summary>动画等待期间页面可能已切换，不能点击等待前识别到的继续坐标。</summary>
    [Fact]
    public async Task AnimationWait_UsesFreshFrame()
    {
        var adb = new FakeAdbService(Screen(true, true));
        adb.EnqueueScreenshots(Screen(count: true));
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await Service(adb, animationWait: 200).ConfirmPostSkipAsync("serial", Layout));
        Assert.True(started.ElapsedMilliseconds >= 200);
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>升级检查触发前可以继续确认正常关卡，不会为稀有提示阻塞正常返回。</summary>
    [Fact]
    public async Task CheckDelay_DoesNotScanEarlyOverlay()
    {
        var adb = new FakeAdbService(Screen(true, true));
        adb.EnqueueScreenshots(Screen(count: true));
        Assert.True(await Service(adb, checkDelay: 3000).ConfirmPostSkipAsync("serial", Layout));
        Assert.Empty(adb.Taps);
    }

    /// <summary>使用分发素材及可记录点击的 ADB，避免依赖模拟器。</summary>
    private static StageSkipService Service(FakeAdbService adb, int timeout = 5000, int animationWait = 0, int checkDelay = 0)
        => new(new AutomationService(adb, new TemplateMatcher(), new TestLog()), new TestLog(), new()
        {
            PlayerRankUpTemplatePath = Template("player_rank_up.png"),
            PlayerRankUpNextTemplatePath = Template("rank_up_next.png"),
            SkipCountLimitTemplatePath = Template("count_limit.png"),
            SkipResultTemplatePath = Template("skip_result.png"),
            SkipDialogWaitMs = 0, SettleAfterOkMs = 0, SkipResultTimeoutMs = timeout,
            PlayerRankUpCheckDelayMs = checkDelay, PlayerRankUpAnimationWaitMs = animationWait,
            PlayerRankUpPollIntervalMs = 0,
        });

    /// <summary>独立组合各信号，覆盖不同帧与被遮住的背景按钮。</summary>
    private static byte[] Screen(bool title = false, bool next = false, bool count = false)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        if (title) Paste(pixels, "player_rank_up.png", 680, 235);
        if (next) Paste(pixels, "rank_up_next.png", 815, 950);
        if (count) Paste(pixels, "count_limit.png", 1590, 680);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }

    /// <summary>复制模板到测试画面，不修改生产素材。</summary>
    private static void Paste(byte[] pixels, string name, int x, int y)
    {
        var image = ImageCodec.DecodePng(File.ReadAllBytes(Template(name)));
        for (var row = 0; row < image.Height; row++)
            Array.Copy(image.Bgra, row * image.Width * 4, pixels, ((y + row) * 1920 + x) * 4, image.Width * 4);
    }
}

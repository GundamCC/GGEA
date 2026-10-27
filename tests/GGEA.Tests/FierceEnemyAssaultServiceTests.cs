using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>真实事件左栏回放及标题、区域、取消门禁，不连接模拟器。</summary>
public sealed class FierceEnemyAssaultServiceTests
{
    /// <summary>真实截图裁剪保留原像素，回放主界面到活动的完整点击顺序。</summary>
    [Fact]
    public async Task OpenFromHome_RealEvent_ClicksOnlyExpectedEntries()
    {
        var adb = new FakeAdbService(TestImageFactory.CreateStageMenuPng());
        adb.EnqueueScreenshots(Frame(), Frame());
        var log = new TestLog();
        var service = new FierceEnemyAssaultService(new AutomationService(adb, new TemplateMatcher(), log), log);
        var result = await service.OpenFromHomeAsync("serial", new UiLayout
        { Anchors = [new() { Name = "navStage", X = 900, Y = 900, Width = 200, Height = 100 }] }, 0);
        Assert.True(result.Success, result.Message);
        Assert.Equal(3, adb.Taps.Count);
        Assert.Equal((1000, 950), adb.Taps[0]);
        Assert.Equal((1165, 400), adb.Taps[1]);
        Assert.InRange(adb.Taps[2].X, 150, 360);
        Assert.InRange(adb.Taps[2].Y, 320, 410);
    }

    /// <summary>无标题、标题离开顶部、图标离开左栏时均不得点击。</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SelectFromEvent_MissingTitleOrOutsideRegion_DoesNotTap(bool hideTitle, bool moveEntry)
    {
        var adb = new FakeAdbService(Frame(hideTitle, moveEntry));
        var log = new TestLog();
        var result = await new FierceEnemyAssaultService(new AutomationService(adb, new TemplateMatcher(), log), log,
            timeoutMs: 150, pollIntervalMs: 10).SelectFromEventAsync("serial");
        Assert.False(result.Success);
        Assert.Empty(adb.Taps);
    }

    /// <summary>标题在下一帧消失时，旧帧授权不得继续生效。</summary>
    [Fact]
    public async Task SelectFromEvent_TitleDisappears_DoesNotTap()
    {
        var adb = new FakeAdbService(Frame());
        adb.EnqueueScreenshots(Frame(hideTitle: true));
        var log = new TestLog();
        var result = await new FierceEnemyAssaultService(new AutomationService(adb, new TemplateMatcher(), log), log,
            timeoutMs: 150, pollIntervalMs: 10).SelectFromEventAsync("serial");
        Assert.False(result.Success);
        Assert.Empty(adb.Taps);
    }

    /// <summary>取消和缺失素材必须在导航点击之前终止。</summary>
    [Fact]
    public async Task OpenFromHome_CanceledOrMissingResource_DoesNotTap()
    {
        var adb = new FakeAdbService(Frame());
        var log = new TestLog();
        var service = new FierceEnemyAssaultService(new AutomationService(adb, new TemplateMatcher(), log), log,
            entryPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png"));
        await Assert.ThrowsAnyAsync<IOException>(() => service.OpenFromHomeAsync("serial", new()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.OpenFromHomeAsync("serial", new(), ct: cts.Token));
        Assert.Empty(adb.Taps);
    }

    /// <summary>只在测试帧中遮去标题或平移活动区，以检查空间门禁；不改动原始回放素材。</summary>
    private static byte[] Frame(bool hideTitle = false, bool moveEntry = false)
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/fierce-event-left.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var row = 0; row < crop.Height; row++)
        {
            if (hideTitle && row < 120) continue;
            var x = moveEntry && row >= 120 ? 600 : 0;
            Buffer.BlockCopy(crop.Bgra, row * crop.Width * 4, pixels, (row * 1920 + x) * 4, crop.Width * 4);
        }
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }
}

using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>验证两层独立取消与主界面恢复的顺序，缺失、越界和取消均不允许继续。</summary>
public sealed class DevelopmentResourceRecoveryTests
{
    /// <summary>两次按钮位置刻意不同，防止复用坐标或直接连点。</summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Recovery_RequiresBothFreshMatchesBeforeHome(int missingLayer, bool homeSuccess)
    {
        var blank = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080);
        var frames = new[] { Frame(200, 930), Frame(1100, 940) };
        var adb = new FakeAdbService(blank);
        adb.ScreenshotProvider = () => adb.Taps.Count == missingLayer ? blank : frames[Math.Min(1, adb.Taps.Count)];
        var homeCalls = 0;
        var service = new DevelopmentResourceRecovery(new AutomationService(adb, new TemplateMatcher(), new TestLog()), (_, _) =>
        {
            Assert.Equal(2, adb.Taps.Count);
            homeCalls++;
            return Task.FromResult(homeSuccess);
        });
        Assert.Equal(missingLayer < 0 && homeSuccess, await service.RecoverAsync("device", _ => { }, timeoutMs: 100, settleMs: 1));
        Assert.Equal(missingLayer < 0 ? 2 : missingLayer, adb.Taps.Count);
        Assert.Equal(missingLayer < 0 ? 1 : 0, homeCalls);
        if (adb.Taps.Count == 2)
        {
            Assert.Equal(900, adb.Taps[1].X - adb.Taps[0].X);
            Assert.Equal(10, adb.Taps[1].Y - adb.Taps[0].Y);
            Assert.Equal(2, adb.ScreenshotCount);
        }
    }

    /// <summary>取消令牌或非底部按钮不得触发任何输入或主页恢复。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_OutsideRegionOrCanceled_NeverClicks(bool canceled)
    {
        var adb = new FakeAdbService(Frame(600, 400));
        var service = new DevelopmentResourceRecovery(new AutomationService(adb, new TemplateMatcher(), new TestLog()),
            (_, _) => throw new InvalidOperationException("不得恢复主界面"));
        var run = () => service.RecoverAsync("device", _ => { }, new CancellationToken(canceled), 100, 1);
        if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
        else Assert.False(await run());
        Assert.Empty(adb.Taps);
    }

    /// <summary>第一次点击后用户取消，绝不点击第二层或恢复主界面。</summary>
    [Fact]
    public async Task Recovery_CanceledAfterFirstClick_Stops()
    {
        var adb = new FakeAdbService(Frame(600, 930));
        using var cts = new CancellationTokenSource();
        var service = new DevelopmentResourceRecovery(new AutomationService(adb, new TemplateMatcher(), new TestLog()),
            (_, _) => throw new InvalidOperationException("不得恢复主界面"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecoverAsync("device", _ =>
        { if (adb.Taps.Count == 1) cts.Cancel(); }, cts.Token, 100, 1));
        Assert.Single(adb.Taps);
    }

    /// <summary>仅拷贝真实取消素材到测试画布，不依赖模拟器运行。</summary>
    private static byte[] Frame(int x, int y)
    {
        var template = ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath("resources/templates/common/button_cancel.png")));
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var row = 0; row < template.Height; row++)
            Array.Copy(template.Bgra, row * template.Width * 4, pixels, ((y + row) * 1920 + x) * 4, template.Width * 4);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }
}

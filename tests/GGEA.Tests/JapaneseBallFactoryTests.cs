using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>日服铁球工厂实机原图回归，覆盖强化入口与原有自动解体循环，不修改全局语言。</summary>
public sealed class JapaneseBallFactoryTests
{
    /// <summary>强化页入口必须通过日文标题及按钮识别，不能仅凭导航点击就判定进入解体。</summary>
    [Fact]
    public async Task Entry_RecognizesJapaneseUpgradeAndDismantle()
    {
        var adb = new FakeAdbService(Frame("upgrade"));
        adb.ScreenshotProvider = () => Frame(adb.Taps.Count < 2 ? "upgrade" : "dismantle");
        using var automation = CreateAutomation(adb);

        Assert.True(await new DismantleEntryService(automation).OpenFromHomeAsync("replay", timeoutMs: 2000));
        Assert.Equal(new[] { (415, 1034), (938, 899) }, adb.Taps);
    }

    /// <summary>原图驱动重置、开发选项、解体和结果确认；下一轮必须识别空提示才结束。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalLoop_StopsAtJapaneseEmptyPrompt(bool oneRound)
    {
        var frames = new[] { "dismantle", "auto", "dev-off", "dev-on", "empty", "dismantle" }
            .Distinct().ToDictionary(name => name, Frame);
        foreach (var name in new[] { "selected", "confirm", "result" }) frames[name] = Frame(name);
        var adb = new FakeAdbService(frames["dismantle"]);
        adb.ScreenshotProvider = () =>
        {
            var count = adb.Taps.Count;
            if (oneRound && count is >= 4 and <= 6)
                return frames[count == 4 ? "selected" : count == 5 ? "confirm" : "result"];
            if (oneRound && count >= 7) count -= 7;
            return frames[count switch { 0 => "dismantle", 1 => "auto", 2 => "dev-off", 3 => "dev-on", 4 => "empty", _ => "dismantle" }];
        };
        using var automation = CreateAutomation(adb);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var rounds = await new DismantleService(automation, timeoutMs: 2000, settleMs: 1)
            .RunUntilEmptyAsync("replay", _ => { }, timeout.Token);

        Assert.Equal(oneRound ? 1 : 0, rounds);
        Assert.Equal(oneRound ? 12 : 5, adb.Taps.Count);
        Assert.Equal(oneRound ? 2 : 1, adb.Swipes.Count);
        Assert.Equal((1681, 857), adb.Taps[1]); // 每轮都重置条件，不继承人工的保留数量。
        Assert.Equal((148, 584), adb.Taps[2]); // 开发单位必须从未勾选转为蓝色勾选。
        Assert.InRange(adb.Taps[^1].Y, 780, 920);
        if (oneRound)
        {
            Assert.InRange(adb.Taps[5].X, 1100, 1200);
            Assert.InRange(adb.Taps[6].X, 900, 1000); // 结果 OK 居中，不能重复点击右侧执行。
            Assert.Equal(adb.Taps[1], adb.Taps[8]);
        }
    }

    /// <summary>没有推荐单位必须有明确日文提示，未选中任何单位的普通页面不等于清理完成。</summary>
    [Fact]
    public async Task UnselectedPage_DoesNotPretendToBeEmpty()
    {
        var adb = new FakeAdbService(Frame("dismantle"));
        using var automation = CreateAutomation(adb);
        var service = new DismantleService(automation, timeoutMs: 200, settleMs: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WaitSelectionAsync("replay"));
        Assert.Empty(adb.Taps);
    }

    /// <summary>实例独占日语解析视图；共用的 OK、确定和筛选图标保持生产回退机制。</summary>
    private static AutomationService CreateAutomation(FakeAdbService adb) =>
        new(adb, new TemplateMatcher(), new TestLog(), tapToNextCheckDelayMs: 0,
            resources: new ResourceResolver(AppContext.BaseDirectory, "ja"));

    /// <summary>仅加载真实实机原帧，不把合成截图当作日服验证证据。</summary>
    private static byte[] Frame(string name) => File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Develop", "ja-" + name + ".png"));
}

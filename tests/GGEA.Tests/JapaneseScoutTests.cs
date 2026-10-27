using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>日服招募原帧回归：真实 OCR 结果、返回列表计数及取消后的消费边界。</summary>
public sealed class JapaneseScoutTests
{
    private readonly ResourceResolver _resources = new(AppContext.BaseDirectory, "ja");

    /// <summary>沿用生产完成回调及取消机制，恰好两人、六次点击，不开始第三轮。</summary>
    [Fact]
    public async Task TwoRecruits_CancelAfterConfirmedReturn_PreventsThirdRecruit()
    {
        var frames = new[] { "first", "confirm-first", "result-first", "second", "confirm-second", "result-second", "after-two" }
            .Select(Frame).ToArray();
        var adb = new FakeAdbService(frames[0]);
        adb.ScreenshotProvider = () => frames[Math.Min(adb.Taps.Count, 6)];
        using var automation = Automation(adb);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var completed = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ScoutService(automation)
            .RunAsync("replay", Templates(), Layout(), (count, _) =>
            {
                completed = count;
                if (count == 2) limit.Cancel();
            }, ct: limit.Token));

        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal(2, completed);
        Assert.Equal(6, adb.Taps.Count);
        for (var index = 0; index < 6; index += 3)
        {
            Assert.InRange(adb.Taps[index].X, 1600, 1750);
            Assert.InRange(adb.Taps[index + 1].X, 1100, 1200);
            Assert.InRange(adb.Taps[index + 1].Y, 800, 900);
            Assert.InRange(adb.Taps[index + 2].Y, 1000, 1060);
        }
    }

    /// <summary>其他日文确认框即使含有相似执行按钮，也不能替代 CAPITAL 招募提示授权。</summary>
    [Fact]
    public async Task OtherConfirmation_DoesNotAuthorizeRecruitment()
    {
        var initial = Frame("first");
        var other = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Develop", "ja-confirm.png"));
        var adb = new FakeAdbService(initial);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? initial : other;
        using var automation = Automation(adb);
        var completed = 0;
        var service = new ScoutService(automation, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, 1000);

        await Assert.ThrowsAsync<TimeoutException>(() => service.RunAsync("replay", Templates(), Layout(), (count, _) => completed = count));
        Assert.Equal(0, completed);
        Assert.Single(adb.Taps);
    }

    /// <summary>使用实例语言视图，避免并行测试修改全局资源配置。</summary>
    private AutomationService Automation(FakeAdbService adb) => new(adb, new TemplateMatcher(), new TestLog(),
        textOcr: TapToNextOcrSamples.SharedOcr, resources: _resources);

    /// <summary>与窗口一致的七项素材；未触发的空列表和不足状态继续保留通用回退。</summary>
    private ScoutTemplates Templates() => new(_resources.Resolve(ScoutService.TitleTemplate),
        _resources.Resolve(ScoutService.ButtonTemplate), _resources.Resolve(ScoutService.ExecuteHintTemplate),
        _resources.Resolve(ScoutService.ExecuteTemplate), _resources.Resolve(ScoutService.NextTemplate),
        _resources.Resolve(ScoutService.NoCharactersTemplate), _resources.Resolve(ScoutService.ClosedButtonTemplate));

    /// <summary>标题区域直接使用产品开发布局，避免测试自造更宽松区域。</summary>
    private UiLayout Layout() => new UiLayoutService(new TestLog()).Load(_resources.Resolve("resources/ui_layout/develop.json"));

    /// <summary>独立实机执行原帧，不使用模板粘贴替代日文画面。</summary>
    private static byte[] Frame(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Scout", "ja-" + name + ".png"));
}

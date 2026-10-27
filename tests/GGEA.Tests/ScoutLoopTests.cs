using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>招募循环回归：用实际模板组成阶段画面，验证同帧确认、等待、循环和停止边界。</summary>
public sealed class ScoutLoopTests
{
    private static string PathOf(string path) => GGEA.App.Config.ConfigService.ResolvePath(path);
    private static ScoutTemplates Templates => new(PathOf(ScoutService.TitleTemplate), PathOf(ScoutService.ButtonTemplate),
        PathOf(ScoutService.ExecuteHintTemplate), PathOf(ScoutService.ExecuteTemplate), PathOf(ScoutService.NextTemplate),
        PathOf(ScoutService.NoCharactersTemplate), PathOf(ScoutService.ClosedButtonTemplate));
    private static UiLayout Layout => new UiLayoutService(new TestLog()).Load(PathOf("resources/ui_layout/develop.json"));

    /// <summary>按模板放置位置建立真实尺寸测试帧，不替换产品识别算法。</summary>
    private static byte[] Frame(params (string Path, int X, int Y)[] targets)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        foreach (var (path, x, y) in targets)
        {
            var template = ImageCodec.DecodePng(File.ReadAllBytes(path));
            for (var row = 0; row < template.Height; row++)
                Buffer.BlockCopy(template.Bgra, row * template.Width * 4, pixels,
                    ((y + row) * 1920 + x) * 4, template.Width * 4);
        }
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }

    /// <summary>测试用阶段画面共享同一套模板，点击次数推动状态而非固定截图序号。</summary>
    private sealed class Frames
    {
        public byte[] Home { get; } = Frame((Templates.Title, 218, 16), (Templates.Button, 1495, 832));
        public byte[] Confirm { get; } = Frame((Templates.ExecuteHint, 441, 273), (Templates.Execute, 967, 787));
        public byte[] Next { get; } = Frame((Templates.Next, 807, 999));
        public byte[] Blank { get; } = Frame();
        public byte[] Empty { get; } = Frame((Templates.Title, 218, 16), (Templates.NoCharacters, 63, 119));
    }

    /// <summary>虚拟等待只记录延时要求；生产构造函数始终使用真实 Task.Delay。</summary>
    private static Task ImmediateDelay(int milliseconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>资源不足可发生在首轮或下一轮；暗态命中后不点击、不多计轮数，也不伪报完成。</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task RunAsync_ClosedButtonStopsRecruitment(bool recruitFirst, int expectedRounds)
    {
        var frames = new Frames();
        var closed = Frame((Templates.Title, 218, 16), (Templates.ClosedButton, 1495, 832));
        var adb = new FakeAdbService(closed);
        adb.ScreenshotProvider = () => !recruitFirst ? closed : adb.Taps.Count switch
        {
            0 => frames.Home, 1 => frames.Confirm, 2 => frames.Next, _ => closed
        };
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), ImmediateDelay);
        var completed = -1;
        var messages = new List<string>();
        var result = await service.RunAsync("serial", Templates, Layout, (count, message) =>
        {
            completed = count;
            messages.Add(message);
        });
        Assert.Equal(ScoutResult.InsufficientResources, result);
        Assert.Equal(expectedRounds, completed);
        Assert.Equal(expectedRounds * 3, adb.Taps.Count);
        Assert.Contains("资源不足", messages.Last());
        Assert.DoesNotContain(messages, message => message.Contains("自动招募完成"));
        if (!recruitFirst) Assert.Equal(1, adb.ScreenshotCount);
    }

    /// <summary>暗态按钮也必须受标题和按钮区域保护，不能在其他页面或错误区域判定资源不足。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ClosedButtonRequiresTitleAndRegion(bool outside)
    {
        var screen = outside
            ? Frame((Templates.Title, 218, 16), (Templates.ClosedButton, 900, 400))
            : Frame((Templates.ClosedButton, 1495, 832));
        var adb = new FakeAdbService(screen);
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), ImmediateDelay, 1000);
        await Assert.ThrowsAsync<TimeoutException>(() => service.RunAsync("serial", Templates, Layout, (_, _) => { }));
        Assert.Empty(adb.Taps);
    }

    /// <summary>暗态资源必须在启动前校验，不能招募到一半才发现新模板缺失。</summary>
    [Fact]
    public async Task RunAsync_MissingClosedTemplateFailsBeforeAnyAction()
    {
        var adb = new FakeAdbService([]);
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), ImmediateDelay);
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.RunAsync("serial",
            Templates with { ClosedButton = Path.Combine(Path.GetTempPath(), $"ggea-missing-{Guid.NewGuid():N}.png") },
            Layout, (_, _) => { }));
        Assert.Equal(0, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>初始即为空或完成最后一轮后为空均正常结束，轮数不多计、不发送额外点击。</summary>
    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(false, 0, true)]
    [InlineData(true, 1, true)]
    public async Task RunAsync_EmptyListFinishesSuccessfully(bool recruitFirst, int expectedRounds, bool hasClosedButton)
    {
        var frames = new Frames();
        // 无角色时也可能显示暗态按钮，专属空列表证据应优先决定完成状态。
        var empty = hasClosedButton
            ? Frame((Templates.Title, 218, 16), (Templates.NoCharacters, 63, 119), (Templates.ClosedButton, 1495, 832))
            : frames.Empty;
        var adb = new FakeAdbService(empty);
        adb.ScreenshotProvider = () => !recruitFirst ? empty : adb.Taps.Count switch
        {
            0 => frames.Home, 1 => frames.Confirm, 2 => frames.Next, _ => empty
        };
        var waits = new List<int>();
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), (ms, ct) =>
        {
            waits.Add(ms);
            return ImmediateDelay(ms, ct);
        });
        var completed = -1;
        var lastMessage = "";
        var result = await service.RunAsync("serial", Templates, Layout, (count, message) => { completed = count; lastMessage = message; });
        Assert.Equal(ScoutResult.NoCharacters, result);
        Assert.Equal(expectedRounds, completed);
        Assert.Equal(expectedRounds * 3, adb.Taps.Count);
        Assert.Contains("没有可招募的角色", lastMessage);
        Assert.Contains("自动招募完成", lastMessage);
        Assert.Contains(500, waits);
        if (!recruitFirst) Assert.Equal(2, adb.ScreenshotCount);
    }

    /// <summary>缺失按钮不足以完成；错误区域、标题缺失和空帧都保持原有超时失败语义。</summary>
    [Theory]
    [InlineData("noTitle")]
    [InlineData("outside")]
    [InlineData("noEmpty")]
    public async Task RunAsync_RequiresTitleAndEmptyTemplateInRegion(string mode)
    {
        var screen = mode switch
        {
            "noTitle" => Frame((Templates.NoCharacters, 63, 119)),
            "outside" => Frame((Templates.Title, 218, 16), (Templates.NoCharacters, 950, 119)),
            _ => Frame((Templates.Title, 218, 16))
        };
        var adb = new FakeAdbService(screen);
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), ImmediateDelay, 1000);
        var messages = new List<string>();
        await Assert.ThrowsAsync<TimeoutException>(() => service.RunAsync("serial", Templates, Layout, (_, message) => messages.Add(message)));
        Assert.Empty(adb.Taps);
        Assert.DoesNotContain(messages, m => m.Contains("自动招募完成"));
    }

    /// <summary>首帧空列表随后加载出角色时继续招募，防止瞬时空帧被误判成功。</summary>
    [Fact]
    public async Task RunAsync_TransientEmptyListDoesNotFinish()
    {
        var frames = new Frames();
        var adb = new FakeAdbService(frames.Empty);
        adb.ScreenshotProvider = () => adb.ScreenshotCount == 1 ? frames.Empty : frames.Home;
        using var cts = new CancellationTokenSource();
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), (ms, ct) =>
        {
            if (ms == 1000) cts.Cancel();
            return ImmediateDelay(ms, ct);
        });
        var messages = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync("serial", Templates, Layout,
            (_, message) => messages.Add(message), ct: cts.Token));
        Assert.Single(adb.Taps);
        Assert.DoesNotContain(messages, m => m.Contains("自动招募完成"));
    }

    /// <summary>按钮存在时不得检查空列表而提前完成；保持用户指定的检测顺序。</summary>
    [Fact]
    public async Task RunAsync_ButtonTakesPriorityOverEmptyTemplate()
    {
        var screen = Frame((Templates.Title, 218, 16), (Templates.Button, 1495, 832), (Templates.NoCharacters, 63, 119));
        var adb = new FakeAdbService(screen);
        using var cts = new CancellationTokenSource();
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync("serial", Templates, Layout, (_, _) => { }, ct: cts.Token));
        Assert.Single(adb.Taps);
    }

    /// <summary>两帧确认之间中止不能报告成功，也不能发送任何点击。</summary>
    [Fact]
    public async Task RunAsync_CancelEmptyConfirmationDoesNotFinish()
    {
        var frames = new Frames();
        var adb = new FakeAdbService(frames.Empty);
        using var cts = new CancellationTokenSource();
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var messages = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync("serial", Templates, Layout,
            (_, message) => messages.Add(message), ct: cts.Token));
        Assert.Empty(adb.Taps);
        Assert.DoesNotContain(messages, m => m.Contains("自动招募完成"));
    }

    /// <summary>两轮均按招募→执行→继续顺序点击，保留五秒短等待，执行后直接识别结果。</summary>
    [Fact]
    public async Task RunAsync_TwoRoundsFollowTimingAndConfirmReturn()
    {
        var frames = new Frames();
        var adb = new FakeAdbService(frames.Home);
        adb.ScreenshotProvider = () => (adb.Taps.Count % 3) switch { 0 => frames.Home, 1 => frames.Confirm, _ => frames.Next };
        var events = new List<string>();
        adb.BeforeScreenshotAsync = (_, _) => { events.Add($"capture:{adb.Taps.Count}"); return Task.CompletedTask; };
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), (ms, ct) =>
        {
            events.Add($"delay:{adb.Taps.Count}:{ms}");
            return ImmediateDelay(ms, ct);
        });
        using var cts = new CancellationTokenSource();
        var completed = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync("serial", Templates, Layout, (count, _) =>
        {
            completed = count;
            if (count == 2) cts.Cancel();
        }, ct: cts.Token));
        Assert.Equal(2, completed);
        var scout = ImageCodec.DecodePng(File.ReadAllBytes(Templates.Button));
        var expected = new[] { (1495 + scout.Width / 2, 832 + scout.Height / 2), (1148, 849), (960, 1027) };
        Assert.Equal(expected.Concat(expected), adb.Taps);
        Assert.Equal(new[] { "delay:1:5000", "delay:4:5000" }, events.Where(e => e.StartsWith("delay:")));
        Assert.True(events.IndexOf("delay:1:5000") < events.IndexOf("capture:1"));
        Assert.DoesNotContain("delay:2:6000", events);
        Assert.Equal(8, adb.ScreenshotCount);
    }

    /// <summary>只有提示、只有按钮或交替出现在不同帧时，均不得执行资源消耗点击。</summary>
    [Theory]
    [InlineData("hint")]
    [InlineData("button")]
    [InlineData("alternate")]
    [InlineData("hintOutside")]
    [InlineData("buttonOutside")]
    public async Task RunAsync_ConfirmationRequiresBothInSameFrame(string mode)
    {
        var frames = new Frames();
        var hint = Frame((Templates.ExecuteHint, 441, 273));
        var button = Frame((Templates.Execute, 967, 787));
        var outside = Frame((Templates.ExecuteHint, 441, mode == "hintOutside" ? 450 : 273),
            (Templates.Execute, 967, mode == "buttonOutside" ? 600 : 787));
        var adb = new FakeAdbService(frames.Home);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? frames.Home :
            mode.EndsWith("Outside") ? outside :
            mode == "hint" || (mode == "alternate" && adb.ScreenshotCount % 2 == 0) ? hint : button;
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), ImmediateDelay, 1000);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => service.RunAsync("serial", Templates, Layout, (_, _) => { }));
        Assert.Contains("确认提示与执行按钮", error.Message);
        Assert.Single(adb.Taps);
    }

    /// <summary>结果页未出现或继续后未返回招募页时停止，不能增加完成轮数或启动下一轮。</summary>
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public async Task RunAsync_MissingResultOrReturnStops(bool showNext, int taps)
    {
        var frames = new Frames();
        var adb = new FakeAdbService(frames.Home);
        adb.ScreenshotProvider = () => adb.Taps.Count switch
        {
            0 => frames.Home, 1 => frames.Confirm, 2 when showNext => frames.Next, _ => frames.Blank
        };
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), ImmediateDelay, 1000);
        var completed = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => service.RunAsync("serial", Templates, Layout, (count, _) => completed = count));
        Assert.Equal(taps, adb.Taps.Count);
        Assert.Equal(0, completed);
    }

    /// <summary>短动画等待取消时，不再进入确认截图或执行。</summary>
    [Theory]
    [InlineData(5000, 1)]
    public async Task RunAsync_CancelDuringDelayPreventsNextAction(int stopDelay, int expectedTaps)
    {
        var frames = new Frames();
        var adb = new FakeAdbService(frames.Home);
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? frames.Home : frames.Confirm;
        using var cts = new CancellationTokenSource();
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), (ms, ct) =>
        {
            if (ms == stopDelay) cts.Cancel();
            return ImmediateDelay(ms, ct);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync("serial", Templates, Layout, (_, _) => { }, ct: cts.Token));
        Assert.Equal(expectedTaps, adb.Taps.Count);
        Assert.Equal(expectedTaps, adb.ScreenshotCount);
    }

    /// <summary>最后确认截图异常立即停止，不通过截图重试延续执行授权。</summary>
    [Fact]
    public async Task RunAsync_ConfirmationCaptureFailureDoesNotRetry()
    {
        var frames = new Frames();
        var adb = new FakeAdbService(frames.Home) { ScreenshotFailure = count => count > 1 ? new InvalidOperationException("截图失败") : null };
        var service = new ScoutService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), ImmediateDelay);
        await Assert.ThrowsAsync<ScreenshotCaptureException>(() => service.RunAsync("serial", Templates, Layout, (_, _) => { }));
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Single(adb.Taps);
    }

    /// <summary>后续模板缺失必须在首次点击前发现。</summary>
    [Fact]
    public async Task RunAsync_MissingOcrModelFailsBeforeAnyAction()
    {
        var adb = new FakeAdbService([]);
        using var ocr = new OnnxTextRecognizer(Path.Combine(Path.GetTempPath(), $"ggea-missing-{Guid.NewGuid():N}.onnx"));
        using var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: ocr);
        var service = new ScoutService(automation, ImmediateDelay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync("serial", Templates, Layout, (_, _) => { }));
        Assert.Equal(0, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }
}

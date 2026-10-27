using System.IO;
using OpenCvSharp;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>共享消费性确认阶段只按已识别页面推进，失败或取消后不继续输入。</summary>
public sealed class DevelopConfirmationTests
{
    /// <summary>暗态在底部才代表资源不足；即使同帧存在亮态也禁止消费性点击。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledButton_UsesBottomRegionAndOverridesEnabled(bool inside)
    {
        var request = Request();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        Paste(pixels, request.ExecuteDevelopTemplatePath, 70, 930);
        Paste(pixels, ConfigService.ResolvePath(DevelopExecutionButton.DisabledTemplate), 968, inside ? 934 : 300);
        var png = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(png);
        var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        var result = await DevelopExecutionButton.ObserveAsync(automation, png, request.ExecuteDevelopTemplatePath);
        Assert.Equal(inside ? DevelopButtonState.UnitLimitReached : DevelopButtonState.Ready, result.State);
        if (inside)
        {
            Assert.Equal(DevelopResult.UnitLimitReached,
                await new DevelopService(automation, new TestLog()).ConfirmAsync("device", request));
            Assert.Empty(adb.Taps);
        }
    }

    /// <summary>数量增减只截图一次；最终确认必须读取调整后的新画面，暗态时工厂不开发也不搜索机体。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_RechecksAfterQuantity_StopsWithoutExecuting(bool initiallyDisabled)
    {
        var request = Request();
        var before = TestImageFactory.CreateBgra(1920, 1080, 190);
        Paste(before, initiallyDisabled ? ConfigService.ResolvePath(DevelopExecutionButton.DisabledTemplate) : request.ExecuteDevelopTemplatePath, 968, 934);
        Paste(before, ConfigService.ResolvePath("resources/templates/develop/button/increment_button.png"), 1566, 801);
        var after = Frame(ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(DevelopExecutionButton.DisabledTemplate))), 968, 934);
        var adb = new FakeAdbService(ImageCodec.EncodePng(before, 1920, 1080));
        adb.ScreenshotProvider = () => adb.Taps.Count == 0 ? ImageCodec.EncodePng(before, 1920, 1080) : after;
        var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        await new DevelopmentQuantityService(automation).SetAsync("device", 2, request.ExecuteDevelopTemplatePath, _ => { }, current: 1);
        Assert.Equal(1, adb.ScreenshotCount);
        var factory = new BallFactoryService(new DevelopService(automation, new TestLog()), (_, _) => throw new InvalidOperationException());
        var result = await factory.CompleteBatchAsync(automation, "device", request, _ => { });
        Assert.Equal(DevelopResult.UnitLimitReached, result.State);
        Assert.Null(result.Unit);
        Assert.Equal(2, adb.ScreenshotCount);
        Assert.Single(adb.Taps); // 仅一次增加，不允许执行开发。
        Assert.Equal((1625, 837), adb.Taps[0]);
    }

    /// <summary>仅拷贝原始模板像素，构造按钮位置与前后状态。</summary>
    private static void Paste(byte[] pixels, string path, int x, int y)
    {
        var template = ImageCodec.DecodePng(File.ReadAllBytes(path));
        for (var row = 0; row < template.Height; row++)
            Array.Copy(template.Bgra, row * template.Width * 4, pixels, ((y + row) * 1920 + x) * 4, template.Width * 4);
    }

    /// <summary>金币红字必须与暗态同帧且位于金币行；使用实机数字裁剪，不做 OCR。</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task CoinShortage_RequiresDisabledButtonAndRedInCoinRow(bool disabled, bool red, bool inside)
    {
        var request = Request();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 190);
        Paste(pixels, disabled ? ConfigService.ResolvePath(DevelopExecutionButton.DisabledTemplate) : request.ExecuteDevelopTemplatePath, 968, 934);
        if (red) Paste(pixels, Path.Combine(AppContext.BaseDirectory, "Fixtures", "Develop", "coins-insufficient.png"), 1280, inside ? 660 : 300);
        var png = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(png);
        var automation = new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr);
        var observed = await DevelopExecutionButton.ObserveAsync(automation, png, request.ExecuteDevelopTemplatePath);
        var coinShortage = disabled && red && inside;
        Assert.Equal(coinShortage ? DevelopButtonState.InsufficientCoins : disabled ? DevelopButtonState.UnitLimitReached : DevelopButtonState.Ready, observed.State);
        if (disabled)
        {
            var service = new DevelopService(automation, new TestLog());
            var expected = coinShortage ? DevelopResult.InsufficientCoins : DevelopResult.UnitLimitReached;
            Assert.Equal(expected, await service.ConfirmAsync("device", request));
            var factory = new BallFactoryService(service, (_, _) => throw new InvalidOperationException("不得重新导航"));
            Assert.Equal(expected, (await factory.CompleteBatchAsync(automation, "device", request, _ => { })).State);
        }
        Assert.Empty(adb.Taps);
    }

    /// <summary>失败或成功不带资源不足提示，金币不足与其它资源不足分别保留原因。</summary>
    [Theory]
    [InlineData(DevelopResult.Completed, null)]
    [InlineData(DevelopResult.Failed, null)]
    [InlineData(DevelopResult.Unavailable, null)]
    [InlineData(DevelopResult.InsufficientCoins, "持有金币不足")]
    [InlineData(DevelopResult.UnitLimitReached, "持有单位已达上限")]
    public void ShortageReason_IsOnlyReportedForConfirmedShortage(DevelopResult result, string? reason)
        => Assert.Equal(reason, result.GetBlockedReason());

    /// <summary>确认阶段不依赖导航布局；真实随包素材均在底部区域测试。</summary>
    private static DevelopRequest Request() => new()
    {
        MainLayout = new UiLayout(),
        DevelopLayout = new UiLayout(),
        RoadmapFirstTemplatePath = "",
        MsTemplatePath = "",
        MsLargeTemplatePath = "",
        DevelopAllTemplatePath = "",
        DevelopAllDisabledTemplatePath = "",
        BackButtonTemplatePath = "",
        DevelopTitleTemplatePath = "",
        ExecuteDevelopTemplatePath = ConfigService.ResolvePath(new AppConfig().DevelopExecuteTemplate),
        ExecuteConfirmTemplatePath = ConfigService.ResolvePath(new AppConfig().DevelopExecuteConfirmTemplate),
        ShowcaseTapToNextTemplatePath = ConfigService.ResolvePath("resources/templates/develop/tap_to_next_showcase.png"),
        NetworkSettleDelayMs = 1,
        DevelopWaitTimeoutMs = 100,
    };

    /// <summary>按实际点击切换页面；每个缺失阶段都阻止后续动作，成功仅三次点击，不返回主界面。</summary>
    [Theory]
    [InlineData(-3)]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Confirm_StopsAtEachMissingStage(int missing)
    {
        var request = Request();
        var paths = new[] { request.ExecuteDevelopTemplatePath, request.ExecuteConfirmTemplatePath, request.ShowcaseTapToNextTemplatePath };
        var templates = paths.Select(p => ImageCodec.DecodePng(File.ReadAllBytes(p))).ToArray();
        // 实机扫描光效样本仅约 89.4%，验证正式确认链使用 85% 而非原 90% 阈值。
        if (missing == -2)
            templates[1] = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Develop", "execute-glow.png")));
        // 实机展示背景样本约 85.1%，旧 90% 阈值漏识别，新阈值必须完成共享确认链。
        if (missing == -3)
            templates[2] = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Develop", "showcase-background.png")));
        var blank = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080);
        var frames = templates.Select((t, i) => Frame(t, 80 + i * 500, 900)).ToArray();
        var adb = new FakeAdbService(blank);
        adb.ScreenshotProvider = () => adb.Taps.Count == missing || adb.Taps.Count >= 3 ? blank : frames[adb.Taps.Count];
        IDevelopService develop = new DevelopService(new AutomationService(adb, new TemplateMatcher(), new TestLog(),
            tapToNextTemplatePaths: [request.ShowcaseTapToNextTemplatePath], tapToNextCheckDelayMs: 0, textOcr: TapToNextOcrSamples.SharedOcr), new TestLog());
        Assert.Equal(missing < 0 ? DevelopResult.Completed : DevelopResult.Failed, await develop.ConfirmAsync("device", request));
        Assert.Equal(missing < 0 ? 3 : missing, adb.Taps.Count);
        for (var i = 0; i < adb.Taps.Count; i++)
            Assert.Equal(i == 2 ? (1237, 925) : (80 + i * 500 + templates[i].Width / 2, 900 + templates[i].Height / 2), adb.Taps[i]);
        Assert.Empty(adb.Swipes);
    }

    /// <summary>素材缺失在第一次点击前失败，取消同样不会触发确认输入。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirm_PreflightAndCancellationNeverClick(bool canceled)
    {
        var request = Request() with { ExecuteConfirmTemplatePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png") };
        var adb = new FakeAdbService(Frame(ImageCodec.DecodePng(File.ReadAllBytes(request.ExecuteDevelopTemplatePath)), 964, 933));
        var develop = new DevelopService(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr), new TestLog());
        var action = () => develop.ConfirmAsync("device", request, new CancellationToken(canceled));
        if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
        else await Assert.ThrowsAsync<FileNotFoundException>(action);
        Assert.Empty(adb.Taps);
    }

    /// <summary>确认失败不进入工厂结果搜索；确认成功后才观察居中单位，不点击或返回主页。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_OnlyObservesResultAfterConfirmation(bool confirmed)
    {
        var probe = new ConfirmationProbe(confirmed);
        var adb = new FakeAdbService(ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080));
        var factory = new BallFactoryService(probe, (_, _) => throw new InvalidOperationException("不得重新导航"));
        var result = await factory.CompleteBatchAsync(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr),
            "device", Request(), _ => { });
        Assert.Null(result.Unit);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(confirmed ? 1 : 0, adb.ScreenshotCount);
        Assert.Empty(adb.Taps);
    }

    /// <summary>两档原生比例在中央均能稳定识别；区域外目标不引发拖动或点击。</summary>
    [Theory]
    [InlineData(.832, true)]
    [InlineData(1.3, true)]
    [InlineData(1.3, false)]
    public async Task CenteredUnit_RecognizesBothScalesWithoutInput(double scale, bool centered)
    {
        using var source = Cv2.ImRead(ConfigService.ResolvePath(BallFactoryService.UnitTemplate), ImreadModes.Unchanged);
        using var scaled = new Mat();
        Cv2.Resize(source, scaled, new Size((int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale)));
        using var color = new Mat(); using var alpha = new Mat();
        Cv2.CvtColor(scaled, color, ColorConversionCodes.BGRA2BGR); Cv2.ExtractChannel(scaled, alpha, 3);
        using var screen = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(20, 20, 20));
        using (var region = new Mat(screen, new Rect(centered ? 963 : 1450, 315, scaled.Width, scaled.Height))) color.CopyTo(region, alpha);
        var adb = new FakeAdbService(screen.ToBytes(".png"));
        var match = await BallFactoryService.WaitForCenteredUnitAsync(new AutomationService(adb, new TemplateMatcher(), new TestLog(), textOcr: TapToNextOcrSamples.SharedOcr),
            "device", centered ? 3000 : 100);
        Assert.Equal(centered, match is not null);
        if (centered) Assert.True(adb.ScreenshotCount >= 2);
        Assert.Empty(adb.Taps); Assert.Empty(adb.Swipes);
    }

    /// <summary>隔离工厂编排，任何重复选择或完整每日流程调用均失败。</summary>
    private sealed class ConfirmationProbe(bool confirmed) : IDevelopService
    {
        public int Calls { get; private set; }
        public Task<DevelopResult> ConfirmAsync(string serial, DevelopRequest request, CancellationToken ct = default)
        { Calls++; return Task.FromResult(confirmed ? DevelopResult.Completed : DevelopResult.Failed); }
        public Task<TemplateMatchResult?> FindAndTapAsync(string serial, DevelopRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("不得重新选机体");
        public Task<DevelopResult> ExecuteAsync(string serial, DevelopRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("不得返回主界面");
    }

    /// <summary>按实际模板大小放置，覆盖旧窄区域之外的底部位置。</summary>
    private static byte[] Frame(ScreenImage template, int x, int y)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        for (var row = 0; row < template.Height; row++)
            Array.Copy(template.Bgra, row * template.Width * 4, pixels, ((y + row) * 1920 + x) * 4, template.Width * 4);
        return ImageCodec.EncodePng(pixels, 1920, 1080);
    }
}

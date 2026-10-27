using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>数量计划、区域边界与快速输入回归，不依赖游戏网络或数字 OCR。</summary>
public sealed class DevelopmentQuantityTests
{
    /// <summary>穷举每个起点与终点，用独立广度搜索核对点击次数最优且不越界。</summary>
    [Fact]
    public void Plan_IsShortestForAllQuantities()
    {
        for (var start = 1; start <= 32; start++)
        {
            var distances = Enumerable.Repeat(100, 33).ToArray(); distances[start] = 0;
            var queue = new Queue<int>(); queue.Enqueue(start);
            while (queue.TryDequeue(out var value))
                foreach (var next in new[] { 1, 32, Math.Max(1, value - 1), Math.Min(32, value + 1) })
                    if (distances[next] > distances[value] + 1) { distances[next] = distances[value] + 1; queue.Enqueue(next); }
            for (var target = 1; target <= 32; target++)
            {
                var actions = BallDevelopmentPlan.Adjust(start, target);
                Assert.Equal(distances[target], actions.Count);
                Assert.Equal(target, actions.Aggregate(start, BallDevelopmentPlan.Apply));
            }
        }
        Assert.Equal(9, BallDevelopmentPlan.Adjust(1, 10).Count);
        Assert.Equal(QuantityAction.Maximum, BallDevelopmentPlan.Adjust(1, 24)[0]);
        Assert.Equal(9, BallDevelopmentPlan.Adjust(1, 24).Count);
        Assert.Equal(QuantityAction.Minimum, BallDevelopmentPlan.Adjust(null, 15)[0]);
    }

    /// <summary>批次求和准确，每批最多 32 台，非法目标不能进入执行。</summary>
    [Fact]
    public void Batches_ExactlyCoverTarget()
    {
        for (var gp = 20; gp <= 100000; gp += 20)
        {
            var batches = BallDevelopmentPlan.Batches(gp);
            Assert.Equal(gp / 20, batches.Sum());
            Assert.All(batches, x => Assert.InRange(x, 1, 32));
        }
        Assert.Equal(new[] { 32, 32, 1 }, BallDevelopmentPlan.Batches(1300));
        Assert.Throws<ArgumentOutOfRangeException>(() => BallDevelopmentPlan.Batches(21));
        Assert.Throws<ArgumentOutOfRangeException>(() => BallDevelopmentPlan.Adjust(0, 15));
    }

    /// <summary>真实模板在底部左右均可命中，底部之外不得命中；语义区域覆盖旧窄 ROI。</summary>
    [Theory]
    [InlineData("button_execute.png")]
    [InlineData("button_execute_develop.png")]
    [InlineData("close.png")]
    public async Task BottomButtons_UseSharedFullWidthRegion(string name)
    {
        var relative = name == "close.png" ? "resources/templates/common/" : "resources/templates/develop/button/";
        var path = ConfigService.ResolvePath(relative + name);
        var template = ImageCodec.DecodePng(File.ReadAllBytes(path));
        foreach (var y in new[] { 500, 900 })
        {
            var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
            Paste(pixels, template, 50, y);
            var png = ImageCodec.EncodePng(pixels, 1920, 1080);
            var automation = new AutomationService(new FakeAdbService(png), new TemplateMatcher(), new TestLog());
            var options = new WaitOptions { Region = RecognitionRegion.BottomButtons, Roi = new Roi(1060, 960, 190, 70), Threshold = .95 };
            Assert.Equal(y == 900, await automation.MatchImageAsync(png, path, options) is not null);
            Assert.Equal(y == 900, await automation.FindImageAsync("device", path, options) is not null);
        }
        Assert.Equal(new Roi(0, 880, 1920, 200), RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, 1920, 1080));
    }

    /// <summary>同一帧定位后批量点击，无数字识别或每次截图；取消和页面不符不能继续输入。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SetQuantity_OnlyClicksQuantityButtons(bool cancel, bool wrongPage)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 190);
        var execute = ConfigService.ResolvePath(new AppConfig().DevelopExecuteTemplate);
        if (!wrongPage) Paste(pixels, ImageCodec.DecodePng(File.ReadAllBytes(execute)), 970, 930);
        Paste(pixels, ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath("resources/templates/develop/button/increment_button.png"))), 1566, 801);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var service = new DevelopmentQuantityService(new AutomationService(adb, new TemplateMatcher(), new TestLog()));
        using var cts = new CancellationTokenSource(); if (cancel) cts.Cancel();
        var run = () => service.SetAsync("device", 15, execute, _ => { }, cts.Token, current: 10);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
        else if (wrongPage) await Assert.ThrowsAsync<InvalidOperationException>(run);
        else
        {
            Assert.Equal(15, await run()); Assert.Equal(5, adb.Taps.Count);
            Assert.All(adb.Taps, t => { Assert.InRange(t.X, 1566, 1684); Assert.InRange(t.Y, 801, 873); });
            Assert.Equal(1, adb.ScreenshotCount);
        }
        if (cancel || wrongPage) Assert.Empty(adb.Taps);
    }

    /// <summary>过渡帧未命中时等待下一帧；等待可取消，期间不发送数量或开发点击。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SetQuantity_RetriesTransitionFrameAndAllowsCancellation(bool cancel, bool quantityDelayed)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 190);
        var blank = ImageCodec.EncodePng(pixels, 1920, 1080);
        var execute = ConfigService.ResolvePath(new AppConfig().DevelopExecuteTemplate);
        Paste(pixels, ImageCodec.DecodePng(File.ReadAllBytes(execute)), 970, 930);
        if (quantityDelayed) blank = ImageCodec.EncodePng(pixels, 1920, 1080);
        Paste(pixels, ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath("resources/templates/develop/button/increment_button.png"))), 1566, 801);
        var ready = ImageCodec.EncodePng(pixels, 1920, 1080);
        var adb = new FakeAdbService(blank);
        adb.ScreenshotProvider = () => adb.ScreenshotCount == 1 ? blank : ready;
        var service = new DevelopmentQuantityService(new AutomationService(adb, new TemplateMatcher(), new TestLog()));
        using var cts = new CancellationTokenSource();
        var retries = 0;
        var run = () => service.SetAsync("device", 2, execute, message =>
        {
            if (adb.ScreenshotCount != 1) return;
            Assert.Empty(adb.Taps);
            Assert.Contains(quantityDelayed ? "加号" : "执行开发", message);
            retries++;
            if (cancel) cts.Cancel();
        }, cts.Token, current: 1);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
            Assert.Empty(adb.Taps);
        }
        else
        {
            Assert.Equal(2, await run());
            Assert.Equal(2, adb.ScreenshotCount);
            Assert.Single(adb.Taps);
            Assert.InRange(adb.Taps[0].X, 1566, 1684);
        }
        Assert.Equal(1, retries);
    }

    /// <summary>执行开发已出现但数量按钮持续缺失时，重试到超时并报告具体按钮，全程不点击。</summary>
    [Fact]
    public async Task SetQuantity_MissingQuantityButtonTimesOutWithoutClicks()
    {
        var execute = ConfigService.ResolvePath(new AppConfig().DevelopExecuteTemplate);
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 190);
        Paste(pixels, ImageCodec.DecodePng(File.ReadAllBytes(execute)), 970, 930);
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var service = new DevelopmentQuantityService(new AutomationService(adb, new TemplateMatcher(), new TestLog()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetAsync("device", 2, execute, _ => { }, current: 1));
        Assert.Contains("等待10秒", error.Message);
        Assert.Contains("加号", error.Message);
        Assert.Contains("increment_button.png", error.Message);
        Assert.True(adb.ScreenshotCount > 1);
        Assert.Empty(adb.Taps);
    }
    /// <summary>初始禁用按钮也能定位；切换端点后复用位置，不做逐次截图。</summary>
    [Theory]
    [InlineData(1, 24, 9)]
    [InlineData(32, 15, 15)]
    public async Task SetQuantity_HandlesDisabledInitialButtons(int current, int target, int taps)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 190);
        var execute = ConfigService.ResolvePath(new AppConfig().DevelopExecuteTemplate);
        Paste(pixels, ImageCodec.DecodePng(File.ReadAllBytes(execute)), 964, 933);
        var buttons = new[]{("set_min_button",1087,800), ("decrement_button",1219,803),
            ("increment_button",1566,801), ("set_max_button",1696,801)};
        for (var i = 0; i < 4; i++)
        {
            var disabled = current == 1 ? i < 2 : i >= 2;
            var name = buttons[i].Item1 + (disabled ? "_disabled" : "") + ".png";
            Paste(pixels, ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath("resources/templates/develop/button/" + name))), buttons[i].Item2, buttons[i].Item3);
        }
        var adb = new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080));
        var service = new DevelopmentQuantityService(new AutomationService(adb, new TemplateMatcher(), new TestLog()));
        Assert.Equal(target, await service.SetAsync("device", target, execute, _ => { }, current: current));
        Assert.Equal(taps, adb.Taps.Count);
        Assert.Equal(1, adb.ScreenshotCount);
        Assert.All(adb.Taps, t => Assert.InRange(t.Y, 800, 879));
        Assert.InRange(adb.Taps[0].X, current == 1 ? 1696 : 1087, current == 1 ? 1813 : 1205);
    }

    /// <summary>执行按钮旧路径升级到 button 子目录，自定义路径保持原值。</summary>
    [Theory]
    [InlineData("relative")]
    [InlineData("absolute")]
    [InlineData("windows")]
    [InlineData("custom")]
    public void ExecuteTemplates_MigrateOldConfig(string kind)
    {
        var directory = Directory.CreateTempSubdirectory("ggea-execute-migrate-");
        try
        {
            string Old(string name) => kind switch
            {
                "absolute" => ConfigService.ResolvePath("resources/templates/develop/" + name),
                "windows" => "resources\\templates\\develop\\" + name,
                "custom" => "custom/" + name,
                _ => "resources/templates/develop/" + name,
            };
            var path = Path.Combine(directory.FullName, "config.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                DevelopExecuteTemplate = Old("button_execute_develop.png"),
                DevelopExecuteConfirmTemplate = Old("button_execute.png")
            }));
            var config = new ConfigService(new TestLog()).Load(path);
            Assert.Equal(kind == "custom" ? Old("button_execute_develop.png") : new AppConfig().DevelopExecuteTemplate, config.DevelopExecuteTemplate);
            Assert.Equal(kind == "custom" ? Old("button_execute.png") : new AppConfig().DevelopExecuteConfirmTemplate, config.DevelopExecuteConfirmTemplate);
        }
        finally { directory.Delete(true); }
    }

    /// <summary>合成测试画布，只拷贝素材，不改变素材像素。</summary>
    private static void Paste(byte[] pixels, ScreenImage template, int x, int y)
    {
        for (var row = 0; row < template.Height; row++)
            Array.Copy(template.Bgra, row * template.Width * 4, pixels, ((y + row) * 1920 + x) * 4, template.Width * 4);
    }
}

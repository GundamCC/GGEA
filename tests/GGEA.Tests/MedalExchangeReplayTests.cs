using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>在实机控件裁剪上运行正式流程。设备只接受当前状态允许的点击，测试不依赖截图次数。</summary>
public sealed class MedalExchangeReplayTests
{
    private const string Fixtures = "Fixtures/Navigation/medal-";
    private const string Assets = "resources/templates/stage/event/limited_time_event/";
    private static ScreenImage Load(string path) =>
        ImageCodec.DecodePng(File.ReadAllBytes(ConfigService.ResolvePath(path + ".png")));
    private static void Paste(ScreenImage target, ScreenImage source, int x, int y)
    {
        for (var row = 0; row < source.Height; row++)
            Buffer.BlockCopy(source.Bgra, row * source.Width * 4, target.Bgra,
                ((row + y) * target.Width + x) * 4, source.Width * 4);
    }
    private static byte[] Encode(ScreenImage frame) => ImageCodec.EncodePng(frame.Bgra, frame.Width, frame.Height);
    private static ScreenImage Scene(string name) => Load(Fixtures + "replay-" + name);

    private static byte[] PoolFrame(string key, string general, bool checkedBox)
    {
        var frame = Scene(checkedBox ? "checked" : "status");
        var heading = Load(Assets + "medal_reset_key_rewards");
        Paste(frame, heading, 495, 403);
        Paste(frame, Load(Fixtures + "count-" + key), 1185, 403 + heading.Height / 2 - 19);
        heading = Load(Assets + "medal_general_rewards");
        Paste(frame, heading, 495, 534);
        Paste(frame, Load(Fixtures + "count-" + general), 1185, 534 + heading.Height / 2 - 19);
        return Encode(frame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualControlsProtectExchangeAndReset(bool startFull)
    {
        using var owner = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var state = startFull ? "supply" : "story";
        var supply = Scene("supply");
        if (startFull)
        {
            // 从真实余额的末尾保留单个“2”，模拟余额不足一份。新池复核后应结束。
            var balance = Load(Fixtures + "balance");
            for (int y = 0; y < balance.Height; y++)
            for (int x = 0; x < balance.Width; x++)
                if (x is < 102 or > 114) Array.Clear(balance.Bgra, (y * balance.Width + x) * 4, 3);
            Paste(supply, balance, 1762, 33);
        }
        var story = Scene("supply");
        Paste(story, Load(Assets + "story"), 210, 3);
        var settingsWrong = Scene("settings");
        Paste(settingsWrong, Load(Fixtures + "settings-alternatives"), 1540, 169);
        var settingsOn = ImageCodec.DecodePng(Encode(settingsWrong));
        Paste(settingsOn, Load(Assets + "medal_settings_100_selected"), 1558, 177);
        var frames = new Dictionary<string, byte[]> {
            ["story"] = Encode(story), ["supply"] = Encode(supply), ["settings"] = Encode(Scene("settings")),
            ["settings-wrong"] = Encode(settingsWrong), ["settings-on"] = Encode(settingsOn),
            ["result"] = Encode(Scene("result")), ["reset"] = Encode(Scene("reset")),
            ["full"] = PoolFrame("pool-full-key", "pool-full-general", false),
            ["checked"] = PoolFrame("pool-full-key", "pool-full-general", true),
            ["new"] = PoolFrame("pool-next-key", "pool-next-general", false),
            ["partial"] = PoolFrame("reward-status-0", "reward-status-1", false),
            ["early"] = PoolFrame("pool-full-key", "exchange100-after-general", false)
        };
        var device = new FakeAdbService(frames[state]);
        // 弹窗的两类标题通常分属不同视口。只有按正确方向拖动，才交付另一分类的像素。
        var views = new Dictionary<(string, bool), byte[]>();
        foreach (string modal in new[] { "partial", "early", "full", "new" })
        foreach (bool lower in new[] { false, true })
        {
            var view = ImageCodec.DecodePng(frames[modal]);
            Array.Clear(view.Bgra, (lower ? 391 : 520) * view.Width * 4, 90 * view.Width * 4);
            views[(modal, lower)] = Encode(view);
        }
        device.ScreenshotProvider = () => views.TryGetValue((state, device.Swipes.LastOrDefault().Y2 == 395), out var view)
            ? view : frames[state];
        bool reset = false, submitted = false;
        device.TapFailure = (x, y) =>
        {
            state = (state, x, y) switch {
                ("story", 1656, 720) => "supply",
                ("supply", > 670 and < 949, > 815 and < 920) => startFull ? "settings" : "settings-wrong",
                ("settings-wrong", > 1555 and < 1648, > 169 and < 268) => "settings-on",
                ("settings-on", > 1550 and < 1810, > 298 and < 431) => "settings",
                ("settings", > 825 and < 1092, > 929 and < 1055) => "supply",
                ("supply", > 1100 and < 1427, > 683 and < 782) => reset ? "new" : startFull ? "full" : "partial",
                ("partial" or "new", > 825 and < 1092, > 929 and < 1055) => "supply",
                ("supply", > 1510 and < 1830, > 800 and < 906) when !startFull => "early",
                ("early", > 825 and < 1092, > 929 and < 1055) => "result",
                ("result", > 1500 and < 1820, > 937 and < 1057) => "finished",
                ("full", > 479 and < 573, > 778 and < 874) => "checked",
                ("checked", > 1044 and < 1451, > 778 and < 877) => "reset",
                ("reset", > 825 and < 1092, > 792 and < 923) => "supply",
                _ => throw new InvalidOperationException($"意外输入 {state}: {x}, {y}")
            };
            if (state == "reset") reset = true;
            if (state == "finished") { submitted = true; owner.Cancel(); }
            return null;
        };
        var log = new TestLog();
        using var automation = new AutomationService(device, new TemplateMatcher(), log);
        using var ocr = new OnnxTextRecognizer();
        var service = new MedalExchangeService(automation, ocr, log, "serial");
        if (startFull)
        {
            await service.RunAsync(owner.Token);
            Assert.True(reset);
            Assert.Equal("supply", state);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(owner.Token));
            Assert.True(submitted);
            Assert.False(reset);
        }
    }

    [Fact]
    public async Task UnaffordableBatchUsesOneExchangeAndChecksFinalBalance()
    {
        var story = Scene("supply");
        Paste(story, Load(Assets + "story"), 210, 3);
        byte[] SupplyWithDigit(int left, int right)
        {
            var frame = Scene("supply");
            var strip = Load(Fixtures + "balance");
            for (var y = 0; y < strip.Height; y++)
            for (var x = 0; x < strip.Width; x++)
                if (x < left || x > right) Array.Clear(strip.Bgra, (y * strip.Width + x) * 4, 3);
            Paste(frame, strip, 1762, 33);
            return Encode(frame);
        }
        var status = PoolFrame("pool-full-key", "exchange100-after-general", false);
        var after = ImageCodec.DecodePng(status);
        // 回放单抽：把实机 685/793 的末位 5 换为同一行的 6，模拟获得一份一般报酬。
        var row = Load(Fixtures + "count-exchange100-after-general");
        for (int y = 0; y < row.Height; y++)
            Buffer.BlockCopy(row.Bgra, (y * row.Width + 65) * 4, row.Bgra, (y * row.Width + 111) * 4, 23 * 4);
        Paste(after, row, 1185, 534 + Load(Assets + "medal_general_rewards").Height / 2 - 19);
        var supply = SupplyWithDigit(115, 130);
        var settings = Encode(Scene("settings"));
        var result = Encode(Scene("result"));
        byte[][] screens = [Encode(story), supply, settings, supply, status, supply, result, Encode(after), result,
            SupplyWithDigit(102, 114)];
        var device = new FakeAdbService(screens[0]);
        device.ScreenshotProvider = () => screens[device.Taps.Count];
        var log = new TestLog();
        using var automation = new AutomationService(device, new TemplateMatcher(), log);
        using var ocr = new OnnxTextRecognizer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await new MedalExchangeService(automation, ocr, log, "serial").RunAsync(timeout.Token);
        Assert.Equal(9, device.Taps.Count);
        Assert.InRange(device.Taps[5].X, 1180, 1390);
        Assert.InRange(device.Taps[8].X, 1230, 1360);
    }

    /// <summary>结果页关闭所在位置被清空，右侧仍保留蓝色再次交换；重置按钮置灰时不能命中亮态。</summary>
    [Fact]
    public async Task NeighborAndDisabledResetCannotAuthorizeTap()
    {
        var frame = Scene("result");
        for (var y = 902; y < 1080; y++)
            Array.Clear(frame.Bgra, (y * frame.Width + 1060) * 4, 460 * 4);
        var log = new TestLog();
        using var automation = new AutomationService(new FakeAdbService(Encode(frame)), new TemplateMatcher(), log);
        Assert.Null(await automation.MatchImageAsync(frame, ConfigService.ResolvePath("resources/templates/common/close.png"),
            new WaitOptions { Roi = new(1060, 902, 460, 175), Threshold = .89 }));
        Assert.Null(await automation.MatchImageAsync(Scene("status"), ConfigService.ResolvePath(Assets + "medal_reset_enabled.png"),
            new WaitOptions { Roi = new(1015, 766, 454, 130), Threshold = .94 }));
    }

    [Theory]
    [InlineData("result", "common/close", 1060, 902, 460, 175, 1294, 985, .89)]
    [InlineData("reset", "dialog/button_ok", 727, 751, 465, 185, 960, 851, .89)]
    public async Task SharedButtonsTolerateTenPixelOffsets(string scene, string logicalPath,
        int x, int y, int width, int height, int centerX, int centerY, double threshold)
    {
        var frame = Scene(scene);
        var area = new Roi(x, y, width, height);
        var log = new TestLog();
        using var automation = new AutomationService(new FakeAdbService(Encode(frame)), new TemplateMatcher(), log);
        foreach (int shift in new[] { -10, 0, 10 })
        {
            var shifted = Scene(scene);
            Array.Clear(shifted.Bgra);
            for (var row = y; row < Math.Min(1080, y + height); row++)
                if (row + shift < 1080)
                    Buffer.BlockCopy(frame.Bgra, (row * 1920 + x) * 4, shifted.Bgra,
                        ((row + shift) * 1920 + x + shift) * 4, width * 4);
            var button = await automation.MatchImageAsync(shifted, ConfigService.ResolvePath($"resources/templates/{logicalPath}.png"),
                new WaitOptions { Roi = area, Threshold = threshold });
            Assert.NotNull(button);
            Assert.InRange(button.CenterX, centerX + shift - 5, centerX + shift + 5);
            Assert.InRange(button.CenterY, centerY + shift - 5, centerY + shift + 5);
        }
    }
}

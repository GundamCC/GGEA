using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.UI;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.Tests;

public class MissionEntryResolutionTests
{
    // 使用真实主页按钮裁剪与使命页，全程只替换 ADB 进程 I/O。
    [Theory]
    [InlineData(1280, 720, 76, 467, false)]
    [InlineData(1280, 720, 76, 467, true)]
    [InlineData(1920, 1080, 114, 700, false)]
    [InlineData(1600, 900, 95, 583, false)]
    [InlineData(2560, 1440, 152, 934, false)]
    public async Task Open_ConfirmsHomeAndMission(int width, int height, int x, int y, bool fromStage)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        using var home = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var strip = Cv2.ImRead(Path.Combine(fixture, "Sortie", "series-complete-home-mumu5556.png"));
        using (var roi = home[new Rect(0, 975, strip.Width, strip.Height)]) strip.CopyTo(roi);
        using var gift = Cv2.ImRead(Path.Combine(fixture, "Mission", "home-gift-720.png"));
        using (var giftRoi = home[new Rect(30, 503, 158, 120)])
            Cv2.Resize(gift, giftRoi, giftRoi.Size());
        using var mission = Cv2.ImRead(Path.Combine(fixture, "Mission", "claim-all-ldplayer.png"));
        byte[] Resize(Mat image)
        {
            using var output = new Mat();
            Cv2.Resize(image, output, new Size(width, height), interpolation: InterpolationFlags.Area);
            return output.ToBytes(".png");
        }
        var before = Resize(home);
        var after = Resize(mission);
        var taps = new List<string>();
        using var stage = new Mat(720, 1280, MatType.CV_8UC3, Scalar.Black);
        Paste(stage, "stage-title-720.png", 0, 0);
        Paste(stage, "stage-home-720.png", 0, 650);
        var stagePng = stage.ToBytes(".png");
        var frames = 0;
        var log = new TestLog();
        var adb = new AdbService("adb.exe", log)
        {
            DeviceBytesAsync = (_, _, _) =>
            {
                frames++;
                if (fromStage && taps.Count == 0) return Task.FromResult(stagePng);
                return Task.FromResult(taps.Count == (fromStage ? 1 : 0) ? before : after);
            },
            DeviceTextAsync = (args, _, _) =>
            {
                if (args.Contains("input")) taps.Add(string.Join(" ", args));
                return Task.FromResult("");
            }
        };
        var resources = new ResourceResolver(AppContext.BaseDirectory, "zh-CN");
        var layouts = new UiLayoutService(log);
        var config = new AppConfig { MainScreenConfirmationIntervalMs = 0 };
        using var automation = new AutomationService(adb, new TemplateMatcher(), log, resources: resources);
        var result = await new MissionEntryProbe(new AutomationDevice(adb), automation).OpenAsync("serial", config,
            layouts.Load(resources.Resolve(config.MainUiLayout)), layouts.Load(resources.Resolve(config.MissionLayout)), default);
        Assert.Equal("已确认使命页", result);
        Assert.Equal(fromStage ? 2 : 1, taps.Count);
        Assert.Equal($"-s serial shell input tap {x} {y}", taps[^1]);
        if (fromStage)
        {
            var point = taps[0].Split(' ');
            Assert.InRange(int.Parse(point[^2]), 20, 175);
            Assert.InRange(int.Parse(point[^1]), 650, 719);
        }
        Assert.Equal(config.MainScreenConfirmationFrames + (fromStage ? 4 : 2), frames);
    }

    // 雷电原生 720p 的标题、图标和按钮按原位置回放，避免只验证由 1080p 缩小的素材。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeMission_RetainsPageAndUnknownScreenGetsNoInput(bool missionVisible)
    {
        using var frame = new Mat(720, 1280, MatType.CV_8UC3, Scalar.Black);
        if (missionVisible)
        {
            Paste(frame, "mission-category-720.png", 65, 307);
            Paste(frame, "mission-claim-720.png", 1020, 580);
        }
        var png = frame.ToBytes(".png");
        var log = new TestLog();
        var adb = new AdbService("adb.exe", log)
        {
            DeviceBytesAsync = (_, _, _) => Task.FromResult(png),
            DeviceTextAsync = (args, _, _) => { Assert.DoesNotContain("input", args); return Task.FromResult(""); },
        };
        var config = new AppConfig();
        var layouts = new UiLayoutService(log);
        using var automation = new AutomationService(adb, new TemplateMatcher(), log);
        var result = await new MissionEntryProbe(new AutomationDevice(adb), automation).OpenAsync("device", config,
            layouts.Load(ConfigService.ResolvePath(config.MainUiLayout)), layouts.Load(ConfigService.ResolvePath(config.MissionLayout)), default);
        Assert.Equal(missionVisible ? "已确认使命页" : "未确认游戏主页", result);
    }

    [Theory]
    [InlineData(1280, 720, 240)]
    [InlineData(1600, 900, 240)]
    [InlineData(1920, 1080, 280)]
    [InlineData(2560, 1440, 320)]
    public async Task SwipeMapsBothCornersAndRejectsChangedFrame(int width, int height, int dpi)
    {
        byte[] Frame(int w, int h)
        {
            using var frame = new Mat(h, w, MatType.CV_8UC3, Scalar.Black);
            return frame.ToBytes(".png");
        }

        var png = Frame(width, height);
        var inputs = new List<string>();
        var transport = new AdbService("adb.exe", new TestLog())
        {
            DeviceBytesAsync = (_, _, _) => Task.FromResult(png),
            DeviceTextAsync = (args, _, _) =>
            {
                if (args.Contains("input")) inputs.Add(string.Join(" ", args));
                return Task.FromResult("");
            }
        };
        var device = new AutomationDevice(transport);
        Assert.Equal(dpi, new GameResolution(width, height).PresetDpi);
        Assert.Equal(new GameResolution(1920, 1080), GameResolution.ReadPng(await device.ScreenshotAsync("device")));
        await device.SwipeAsync("device", 0, 0, 1919, 1079, 300);
        Assert.Equal($"-s device shell input swipe 0 0 {width - 1} {height - 1} 300", Assert.Single(inputs));
        png = width == 1280 ? Frame(1920, 1080) : Frame(1280, 720);
        await Assert.ThrowsAsync<AdbException>(() => device.TapAsync("device", 10, 20));
        Assert.Single(inputs);
    }

    private static void Paste(Mat frame, string name, int x, int y)
    {
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mission", name));
        using var roi = frame[new Rect(x, y, crop.Width, crop.Height)];
        crop.CopyTo(roi);
    }

    [Theory]
    [InlineData(720, 1280)]
    [InlineData(1280, 800)]
    public void RejectsUnsupportedShape(int width, int height)
    {
        var png = ImageCodec.EncodePng(TestImageFactory.CreateBgra(width, height, 0), width, height);
        Assert.Throws<InvalidOperationException>(() => GameResolution.ReadPng(png).Normalize(png));
    }
}

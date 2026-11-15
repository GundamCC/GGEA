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
    [InlineData(1280, 720, 76, 467)]
    [InlineData(1920, 1080, 114, 700)]
    public async Task Open_ConfirmsMissionAfterOneMappedTap(int width, int height, int x, int y)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        using var home = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var strip = Cv2.ImRead(Path.Combine(fixture, "Sortie", "series-complete-home-mumu5556.png"));
        using (var roi = home[new Rect(0, 975, strip.Width, strip.Height)]) strip.CopyTo(roi);
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
        var frames = 0;
        var log = new TestLog();
        var adb = new AdbService("adb.exe", log)
        {
            DeviceBytesAsync = (_, _, _) => { frames++; return Task.FromResult(taps.Count == 0 ? before : after); },
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
        var result = await new MissionEntryProbe(adb, automation).OpenAsync("serial", config,
            layouts.Load(resources.Resolve(config.MainUiLayout)), layouts.Load(resources.Resolve(config.MissionLayout)), default);
        Assert.Equal("已确认使命页", result);
        Assert.Equal(new[] { $"-s serial shell input tap {x} {y}" }, taps);
        Assert.Equal(config.MainScreenConfirmationFrames + 1, frames);
    }

    [Theory]
    [InlineData(720, 1280)]
    [InlineData(1280, 800)]
    public void RejectsUnsupportedShape(int width, int height)
    {
        var png = ImageCodec.EncodePng(TestImageFactory.CreateBgra(width, height, 0), width, height);
        Assert.Throws<InvalidOperationException>(() => LandscapeFrame.FromPng(png));
    }
}

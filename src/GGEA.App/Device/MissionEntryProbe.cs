using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Device;

/// <summary>从已打开的主页进入使命页；本次操作只授权一次入口点击。</summary>
internal sealed class MissionEntryProbe(AdbService adb, AutomationService automation)
{
    internal async Task<string> OpenAsync(string serial, AppConfig config, UiLayout home, UiLayout mission,
        CancellationToken ct)
    {
        var entry = home.Find("navMission") ?? throw new InvalidOperationException("缺少 navMission 锚点");
        var claim = mission.Find("claimAll") ?? throw new InvalidOperationException("缺少 claimAll 锚点");
        if (home.ScreenWidth != LandscapeFrame.BaseWidth || home.ScreenHeight != LandscapeFrame.BaseHeight ||
            mission.ScreenWidth != LandscapeFrame.BaseWidth || mission.ScreenHeight != LandscapeFrame.BaseHeight)
            throw new InvalidOperationException("使命入口布局需要使用 1920×1080 基准");

        (int Display, ulong? Physical, int Width, int Height)? route = null;
        async Task<LandscapeFrame> CaptureAsync()
        {
            var (png, display, physical) = await adb.CaptureScreenshotAsync(serial, ct);
            if (adb.ExecutionObserver is { } observer) await observer.OnScreenshotAsync(serial, png, ct);
            var frame = await Task.Run(() => LandscapeFrame.FromPng(png), ct);
            var current = (display, physical, frame.Width, frame.Height);
            if (route.HasValue && route.Value != current)
                throw new AdbException("游戏画面尺寸或显示屏已变化，请重新确认主页");
            route = current;
            return frame;
        }

        var roi = config.MainScreenButtonRoi;
        var homeWait = new WaitOptions { Roi = new Roi(roi.X, roi.Y, roi.Width, roi.Height), Threshold = config.MainScreenThreshold };
        var homePath = ConfigService.ResolvePath(config.MainScreenActivatedTemplate);
        var frame = await CaptureAsync();
        for (var i = 1; ; i++)
        {
            if (await automation.MatchImageAsync(frame.Screen, homePath, homeWait, ct) is null)
                return L.Text("未确认游戏主页");
            if (i >= config.MainScreenConfirmationFrames) break;
            await Task.Delay(Math.Max(0, config.MainScreenConfirmationIntervalMs), ct);
            frame = await CaptureAsync();
        }
        var point = frame.ToDevice(entry.CenterX, entry.CenterY);
        var target = route!.Value;
        await adb.TapFrameAsync(serial, target.Display, target.Physical, point.X, point.Y, ct);

        var enabled = ConfigService.ResolvePath(config.MissionClaimAllTemplate);
        var disabled = ConfigService.ResolvePath(config.MissionClaimAllDisabledTemplate);
        var wait = new WaitOptions { Roi = claim.Roi, Threshold = config.DefaultMatchThreshold };
        var dark = wait with { Threshold = Math.Clamp(wait.Threshold + .05, .5, .99) };
        var deadline = Environment.TickCount64 + GameTiming.RecognitionTimeoutMs;
        do
        {
            frame = await CaptureAsync();
            if (await automation.MatchImageAsync(frame.Screen, enabled, wait, ct) is not null ||
                await automation.MatchImageAsync(frame.Screen, disabled, dark, ct) is not null)
                return L.Text("已确认使命页");
            await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
        } while (Environment.TickCount64 < deadline);
        return L.Text("未确认使命页");
    }
}

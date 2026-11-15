using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.UI;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.App.Device;

/// <summary>从主页或已确认的关卡菜单进入使命页；每个导航动作只投递一次。</summary>
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
        var enabled = ConfigService.ResolvePath(config.MissionClaimAllTemplate);
        var disabled = ConfigService.ResolvePath(config.MissionClaimAllDisabledTemplate);
        var wait = new WaitOptions { Roi = claim.Roi, Threshold = config.DefaultMatchThreshold };
        var dark = wait with { Threshold = Math.Clamp(wait.Threshold + .05, .5, .99) };
        async Task<bool> HasClaimAsync(LandscapeFrame frame) =>
            await automation.MatchImageAsync(frame.Screen, enabled, wait, ct) is not null ||
            await automation.MatchImageAsync(frame.Screen, disabled, dark, ct) is not null;

        async Task TapAsync(LandscapeFrame frame, int x, int y)
        {
            var point = frame.ToDevice(x, y);
            var target = route!.Value;
            await adb.TapFrameAsync(serial, target.Display, target.Physical, point.X, point.Y, ct);
        }

        var frame = await CaptureAsync();
        // 使命页也会点亮底部主页。左栏分类图标和领取按钮同时出现才认作已到达。
        var category = new WaitOptions { Roi = new Roi(80, 450, 125, 130), Threshold = .90 };
        if (await automation.MatchImageAsync(frame.Screen,
                ConfigService.ResolvePath("resources/templates/mission/special_mission_icon.png"), category, ct) is not null
            && await HasClaimAsync(frame))
            return L.Text("已确认使命页");

        var inactive = await automation.MatchImageAsync(frame.Screen,
            ConfigService.ResolvePath(config.MainScreenInactiveTemplate), homeWait, ct);
        var stageTitle = new WaitOptions { Region = RecognitionRegion.PageTitle, Threshold = .85 };
        var returningHome = inactive is not null && await automation.MatchImageAsync(frame.Screen,
                ConfigService.ResolvePath(StageMenuEntryService.TitleTemplate), stageTitle, ct) is not null;
        if (returningHome)
        {
            await TapAsync(frame, inactive!.CenterX, inactive.CenterY);
        }

        var gift = home.Find("navGift") ?? throw new InvalidOperationException("主界面布局缺少 navGift 锚点");
        var giftWait = new WaitOptions { Roi = gift.Roi, Threshold = .88 };
        var giftPath = ConfigService.ResolvePath("resources/templates/home/nav_gift_icon.png");
        var homeDeadline = Environment.TickCount64 + GameTiming.ShortRecognitionTimeoutMs;
        var confirmed = 0;
        do
        {
            if (returningHome || confirmed > 0) frame = await CaptureAsync();
            if (await automation.MatchImageAsync(frame.Screen, homePath, homeWait, ct) is not null &&
                await automation.MatchImageAsync(frame.Screen, giftPath, giftWait, ct) is not null)
            {
                if (++confirmed >= Math.Max(1, config.MainScreenConfirmationFrames)) break;
            }
            else
            {
                if (!returningHome) return L.Text("未确认游戏主页");
                confirmed = 0;
            }
            await Task.Delay(Math.Max(1, config.MainScreenConfirmationIntervalMs), ct);
        } while (Environment.TickCount64 < homeDeadline);
        if (confirmed < Math.Max(1, config.MainScreenConfirmationFrames)) return L.Text("未确认游戏主页");
        await TapAsync(frame, entry.CenterX, entry.CenterY);
        var deadline = Environment.TickCount64 + GameTiming.RecognitionTimeoutMs;
        do
        {
            frame = await CaptureAsync();
            if (await HasClaimAsync(frame))
                return L.Text("已确认使命页");
            await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
        } while (Environment.TickCount64 < deadline);
        return L.Text("未确认使命页");
    }
}

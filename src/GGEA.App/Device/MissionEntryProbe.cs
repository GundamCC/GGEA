using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.UI;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.App.Device;

/// <summary>从主页或已确认的关卡菜单进入使命页；每个导航动作只投递一次。</summary>
internal sealed class MissionEntryProbe(IAdbService adb, AutomationService automation)
{
    internal async Task<string> OpenAsync(string serial, AppConfig config, UiLayout home, UiLayout mission,
        CancellationToken ct)
    {
        var entry = home.Find("navMission") ?? throw new InvalidOperationException("缺少 navMission 锚点");
        var claim = mission.Find("claimAll") ?? throw new InvalidOperationException("缺少 claimAll 锚点");
        if (home.ScreenWidth != GameResolution.BaseWidth || home.ScreenHeight != GameResolution.BaseHeight ||
            mission.ScreenWidth != GameResolution.BaseWidth || mission.ScreenHeight != GameResolution.BaseHeight)
            throw new InvalidOperationException("使命入口布局需要使用 1920×1080 基准");

        async Task<ScreenImage> CaptureAsync() =>
            ImageCodec.DecodePng(await adb.ScreenshotAsync(serial, ct));

        var roi = config.MainScreenButtonRoi;
        var homeWait = new WaitOptions { Roi = new Roi(roi.X, roi.Y, roi.Width, roi.Height), Threshold = config.MainScreenThreshold };
        var homePath = ConfigService.ResolvePath(config.MainScreenActivatedTemplate);
        var enabled = ConfigService.ResolvePath(config.MissionClaimAllTemplate);
        var disabled = ConfigService.ResolvePath(config.MissionClaimAllDisabledTemplate);
        var wait = new WaitOptions { Roi = claim.Roi, Threshold = config.DefaultMatchThreshold };
        var dark = wait with { Threshold = Math.Clamp(wait.Threshold + .05, .5, .99) };
        async Task<bool> HasClaimAsync(ScreenImage frame) =>
            await automation.MatchImageAsync(frame, enabled, wait, ct) is not null ||
            await automation.MatchImageAsync(frame, disabled, dark, ct) is not null;

        var frame = await CaptureAsync();
        // 使命页也会点亮底部主页。左栏分类图标和领取按钮同时出现才认作已到达。
        var category = new WaitOptions { Roi = new Roi(80, 450, 125, 130), Threshold = .90 };
        if (await automation.MatchImageAsync(frame,
                ConfigService.ResolvePath("resources/templates/mission/special_mission_icon.png"), category, ct) is not null
            && await HasClaimAsync(frame))
            return L.Text("已确认使命页");

        var inactive = await automation.MatchImageAsync(frame,
            ConfigService.ResolvePath(config.MainScreenInactiveTemplate), homeWait, ct);
        var stageTitle = new WaitOptions { Region = RecognitionRegion.PageTitle, Threshold = .85 };
        var returningHome = inactive is not null && await automation.MatchImageAsync(frame,
                ConfigService.ResolvePath(StageMenuEntryService.TitleTemplate), stageTitle, ct) is not null;
        if (returningHome)
        {
            await adb.TapAsync(serial, inactive!.CenterX, inactive.CenterY, ct);
        }

        var gift = home.Find("navGift") ?? throw new InvalidOperationException("主界面布局缺少 navGift 锚点");
        var giftWait = new WaitOptions { Roi = gift.Roi, Threshold = .88 };
        var giftPath = ConfigService.ResolvePath("resources/templates/home/nav_gift_icon.png");
        var homeDeadline = Environment.TickCount64 + GameTiming.ShortRecognitionTimeoutMs;
        var confirmed = 0;
        do
        {
            if (returningHome || confirmed > 0) frame = await CaptureAsync();
            if (await automation.MatchImageAsync(frame, homePath, homeWait, ct) is not null &&
                await automation.MatchImageAsync(frame, giftPath, giftWait, ct) is not null)
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
        await adb.TapAsync(serial, entry.CenterX, entry.CenterY, ct);
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

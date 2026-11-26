using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>事件专属导航只负责选关、确认次数与领取；消耗 AP 后交给共用 HARD 略过结算流程。</summary>
internal sealed class EventStagesService(AutomationService automation, string serial)
{
    internal const string Directory = "stage/event/limited_time_event/";
    private static readonly Roi Header = new(190, 0, 470, 105), SkipArea = new(1505, 645, 345, 140),
        ClaimArea = new(1505, 821, 390, 150), DailyTabArea = new(896, 94, 165, 75), TotalTabArea = new(1492, 94, 162, 75);
    private sealed record Target(string Key, Roi Area, double Threshold, bool? BlueTab = null);
    private static Target E(string name, Roi area, double threshold = .92) => new(Directory + name, area, threshold);
    private static readonly Target Story = E("story", Header, .90), Stage = E("stage_title", Header, .90),
        Mission = E("event_mission_title", Header, .90), Back = new("stage/history_back", new(0, 0, 225, 112), .90),
        Complete = E("event_stage_complete", new(1290, 510, 155, 90), .97),
        Skip = new("stage/event/button_skip", SkipArea, .90), Limit = new("stage/skip/count_limit", SkipArea, .90),
        ApDialog = E("event_skip_ap", new(750, 135, 420, 110)),
        Daily = E("mission_daily_tab", DailyTabArea) with { BlueTab = true }, Total = E("mission_total_tab", TotalTabArea) with { BlueTab = true },
        TenRow = E("mission_ten_row", new(850, 363, 215, 98)), TenDone = E("mission_daily_complete", new(1092, 465, 147, 71), .982),
        Claim = new("mission/button_claim_all", ClaimArea, .89), Claimed = new("mission/button_claim_all_disabled", ClaimArea, .91);

    internal static string PathFor(string name) => ConfigService.ResolvePath($"resources/templates/{Directory}{name}.png");
    private Task<TemplateMatchResult?> Match(ScreenImage frame, Target target, CancellationToken ct)
    {
        // 灰度接近的每日/累计页签不能互相授权。文字命中前用同帧蓝色底纹核对激活态。
        if (target.BlueTab is bool selected)
        {
            int blue = 0;
            for (int y = 104; y < 118; y++)
            for (int x = target.Area.X + 18; x < target.Area.X + 48; x++)
            {
                int offset = (y * frame.Width + x) * 4;
                if (frame.Bgra[offset] - frame.Bgra[offset + 2] > 80) blue++;
            }
            if ((blue > 210) != selected) return Task.FromResult<TemplateMatchResult?>(null);
        }
        return automation.MatchImageAsync(frame, ConfigService.ResolvePath($"resources/templates/{target.Key}.png"),
            new WaitOptions { Roi = target.Area, Threshold = target.Threshold }, ct);
    }

    private sealed record Seen(Target Target, TemplateMatchResult Match);
    private async Task<Seen?> Observe(Target? page, Target[] choices, CancellationToken ct, int budget = GameTiming.RecognitionTimeoutMs)
    {
        return await StableObservation.WaitAsync<Seen>(async () =>
        {
            var frame = await automation.CaptureScreenAsync(serial, ct);
            if (page is null || await Match(frame, page, ct) is not null)
                foreach (var t in choices)
                    if (await Match(frame, t, ct) is { } hit) return new(t, hit);
            return null;
        }, (a,b) => a.Target == b.Target && Math.Abs(a.Match.CenterX-b.Match.CenterX) <= 4 &&
            Math.Abs(a.Match.CenterY-b.Match.CenterY) <= 4, budget, ct);
    }
    private async Task<Seen> Require(Target? page, CancellationToken ct, params Target[] choices) =>
        await Observe(page, choices, ct) ?? throw new InvalidOperationException($"事件关卡：未确认 {string.Join(" / ", choices.Select(t => t.Key))}。");
    private Task Tap(Seen seen, CancellationToken ct) => automation.TapAsync(serial, seen.Match.CenterX, seen.Match.CenterY, ct);
    private async Task Click(Target? page, Target t, CancellationToken ct) => await Tap(await Require(page, ct, t), ct);

    internal async Task<bool> EnterAsync(bool checkCompletion, CancellationToken ct)
    {
        var start = await Require(null, ct, Story, Stage);
        if (checkCompletion && start.Target == Stage)
        {
            await Click(Stage, Back, ct);
            start = await Require(null, ct, Story);
        }
        if (start.Target == Story)
        {
            if (checkCompletion && await Observe(Story, [Complete], ct, 2000) is not null) return false;
            await automation.TapAsync(serial, 1240, 407, ct);
        }
        await Require(null, ct, Stage);
        return true;
    }

    internal async Task OpenFromMenuAsync(CancellationToken ct)
    {
        await FierceEnemyAssaultService.OpenEventListAsync(automation, serial, ct);
        var options = new WaitOptions { Region = RecognitionRegion.PageTitle, Threshold = .85,
            TimeoutMs = GameTiming.RecognitionTimeoutMs, AllowGlobalContinuation = false };
        if (!await FierceEnemyAssaultService.WaitEventListAsync(automation, serial,
            ConfigService.ResolvePath(FierceEnemyAssaultService.TitleTemplate), options, ct))
            throw new InvalidOperationException("未确认事件列表标题。");
        var banner = E("event_banner", new(0, 105, 398, 871));
        if (!await FierceEnemyAssaultService.SelectEventBannerAsync(automation, serial,
            ConfigService.ResolvePath(FierceEnemyAssaultService.TitleTemplate), options,
            (frame, token) => Match(frame, banner, token), ct))
            throw new InvalidOperationException("事件列表未确认当前活动横幅。");
        var poster = E("event_poster", new(740, 230, 635, 370));
        await Require(null, ct, poster);
        if (!await FierceEnemyAssaultEntryService.EnterSelectedAsync(automation, serial,
            options with { Region = null, Roi = FierceEnemyAssaultEntryService.ButtonRegion },
            async (screen, token) => await Match(screen, poster, token) is not null, ct))
            throw new InvalidOperationException("未确认当前故事事件的前往按钮。");
        await Require(null, ct, Story);
    }

    internal async Task ReturnFromBossAsync(CancellationToken ct)
    {
        await Click(E("boss", Header, .90), Back, ct);
        await Require(null, ct, Story);
    }

    private async Task ConfigureQuantity(bool maximum, CancellationToken ct)
    {
        var arrow = maximum ? "develop/button/set_max_button" : "develop/button/set_min_button";
        var area = maximum ? new Roi(1190, 310, 230, 175) : new Roi(533, 310, 210, 165);
        var ready = new Target(arrow + "_disabled", area, .93);
        var selection = await Require(ApDialog, ct, ready, new Target(arrow, area, .90));
        if (selection.Target != ready)
        {
            await Tap(selection, ct);
            await Require(ApDialog, ct, ready);
        }
        if (!maximum) await Require(ApDialog, ct, E("event_skip_one", new(922, 329, 101, 115), .98));
    }

    private async Task OpenMission(CancellationToken ct)
    {
        await Click(Stage, Back, ct);
        await OpenMissionFromStory(ct);
    }
    private async Task OpenMissionFromStory(CancellationToken ct)
    {
        await Click(Story, E("mission_entry", new(1110, 820, 280, 118)), ct);
        await SelectTab(Daily, E("mission_daily_inactive", DailyTabArea) with { BlueTab = false }, ct);
    }
    private async Task SelectTab(Target active, Target inactive, CancellationToken ct)
    {
        var tab = await Require(Mission, ct, active, inactive);
        if (tab.Target == inactive) await Tap(tab, ct);
        await Require(Mission, ct, active);
    }
    private async Task ClaimTab(Target tab, CancellationToken ct)
    {
        await Require(Mission, ct, tab);
        var receipt = E("mission_receipt", new(790, 33, 350, 115));
        var ok = new Target("dialog/button_ok", new(720, 905, 480, 175), .88);
        var service = new MissionRewardService(automation, automation.Log);
        await service.ClaimPageAsync(serial, ClaimArea, ok.Area, ConfigService.ResolvePath($"resources/templates/{Claim.Key}.png"),
            ConfigService.ResolvePath($"resources/templates/{Claimed.Key}.png"), ConfigService.ResolvePath($"resources/templates/{ok.Key}.png"),
            0, GameTiming.RecognitionTimeoutMs, Claim.Threshold, ct, async () =>
            {
                var seen = await Require(tab, ct, Claimed, Claim);
                return (seen.Target == Claimed, seen.Match);
            }, async () => (await Require(receipt, ct, ok)).Match);
        await Require(tab, ct, Claimed);
    }

    // 进度条每格对应一次；只接受连续蓝段后接连续空段的十格，未知颜色不能当作零。
    private static int? ReadTenStepBar(ScreenImage frame)
    {
        int filled = 0;
        bool emptySeen = false;
        for (int step = 0; step < 10; step++)
        {
            int x = 1060 + step * 23;
            bool? sample = null;
            for (int y = 527; y <= 531; y += 2)
            {
                int offset = (frame.Width * y + x) * 4;
                var blue = frame.Bgra[offset];
                var green = frame.Bgra[offset + 1];
                var red = frame.Bgra[offset + 2];
                bool active = blue >= 225 && green is >= 145 and <= 205 && red is >= 85 and <= 150;
                bool empty = blue is >= 60 and <= 90 && green is >= 47 and <= 72 && red is >= 32 and <= 57;
                if ((!active && !empty) || (sample.HasValue && sample != active)) return null;
                sample = active;
            }
            if (sample == false) emptySeen = true;
            else if (emptySeen) return null;
            else filled++;
        }
        return filled;
    }

    private sealed record MissionProgress(int Count);
    private async Task<int> ReadMissionProgressAsync(CancellationToken ct)
    {
        var result = await StableObservation.WaitAsync<MissionProgress>(async () =>
        {
            var frame = await automation.CaptureScreenAsync(serial, ct);
            if (await Match(frame, Daily, ct) is null || await Match(frame, TenRow, ct) is null) return null;
            if (await Match(frame, TenDone, ct) is not null) return new(10);
            return ReadTenStepBar(frame) is int value ? new(value) : null;
        }, (a,b) => a == b, GameTiming.RecognitionTimeoutMs, ct);
        if (result is null) throw new InvalidOperationException("未能确认每日十次使命进度，已保留 AP。");
        automation.Log.Info($"限时活动每日使命进度 {result.Count}/10。");
        return result.Count;
    }

    internal static HardStageEntry CreateWork() => new() { Work = "事件关卡", NormalCount = 7, HardCount = 4 };

    internal static UiLayout CreateTrackLayout(UiLayout layout)
    {
        // 与主线共用五槽位规划；活动轨道整体上移，第一槽位（普通末关）靠近左边缘。
        var slots = new[] { (30, 458), (220, 394), (410, 458), (600, 394), (790, 458) };
        var anchors = layout.Anchors.ToDictionary(a => a.Name);
        for (int i = 0; i < slots.Length; i++)
        {
            string key = $"stageSlot{i + 1}";
            anchors[key] = new UiAnchor { Name = key, X = slots[i].Item1 - 10, Y = slots[i].Item2 - 10, Width = 20, Height = 20 };
        }
        anchors["stageTitle"] = new UiAnchor { Name = "stageTitle", X = 1120, Y = 90, Width = 700, Height = 78 };
        anchors["skipDialogCancel"] = new UiAnchor { Name = "skipDialogCancel", X = 590, Y = 935, Width = 360, Height = 115 };
        return new UiLayout { ScreenWidth = layout.ScreenWidth, ScreenHeight = layout.ScreenHeight, Anchors = anchors.Values.ToList() };
    }

    internal async Task<bool> DailyAsync(StageSkipService settlement, UiLayout layout, StageService navigation,
        CancellationToken ct, bool claimRewards = true)
    {
        var work = CreateWork();
        var eventLayout = CreateTrackLayout(layout);
        var start = await Require(null, ct, Story, Stage);
        if (start.Target == Stage) await OpenMission(ct); else await OpenMissionFromStory(ct);
        int progress = await ReadMissionProgressAsync(ct);
        for (int hard = 1; progress < 10 && hard <= 3; hard++)
        {
            await Click(Mission, Back, ct);
            await EnterAsync(false, ct);
            var selected = await navigation.SelectHardStageAsync(serial, eventLayout, [work], work.Id, hard, ct);
            if (!selected.Success) throw new InvalidOperationException(selected.Message);
            bool maximum = progress <= 7;
            var result = await SkipSelected(maximum);
            if (result.IsApInsufficient) return await FinishWithoutAp();
            if (!result.Success && !result.IsUnavailable) throw new InvalidOperationException(result.Message);
            if (result.Success)
            {
                var confirmed = await navigation.ConfirmCurrentHardStageAsync(serial, eventLayout, work, hard, ct);
                if (!confirmed.Success) throw new InvalidOperationException(confirmed.Message);
            }
            await OpenMission(ct);
            int updated = await ReadMissionProgressAsync(ct);
            if (result.Success && updated <= progress) throw new InvalidOperationException("事件使命进度未增加，已保留 AP。");
            if (!maximum && updated > progress && updated < 10) hard--;
            progress = updated;
        }
        if (progress < 10)
        {
            if (progress != 9) throw new InvalidOperationException($"每日使命为 {progress}/10，当前 HARD 次数不足。");
            await Click(Mission, Back, ct);
            await EnterAsync(false, ct);
            var first = await navigation.SelectHardStageAsync(serial, eventLayout, [work], work.Id, 1, ct);
            if (!first.Success) throw new InvalidOperationException(first.Message);
            await navigation.SelectBeforeHardAsync(serial, eventLayout, work, ct);
            var result = await SkipSelected(false);
            if (result.IsApInsufficient) return await FinishWithoutAp();
            if (!result.Success) throw new InvalidOperationException(result.Message);
            await navigation.ConfirmBeforeHardAsync(serial, work, ct);
            await OpenMission(ct);
            if (await ReadMissionProgressAsync(ct) != 10) throw new InvalidOperationException("未确认每日使命完成。");
        }
        await FinishMissionAsync(claimRewards, ct);
        return true;

        Task<StageSkipResult> SkipSelected(bool maximum) => settlement.SkipCurrentAsync(serial, eventLayout, ct,
            token => ConfigureQuantity(maximum, token),
            async token => (await Require(Stage, token, Limit, Skip)).Target == Skip);

        async Task<bool> FinishWithoutAp()
        {
            // 公共结算已关闭回复与确认弹窗，返回故事事件后仍可执行领取、BOSS 和补给。
            await OpenMission(ct);
            await FinishMissionAsync(claimRewards, ct);
            return false;
        }
    }

    private async Task FinishMissionAsync(bool claimRewards, CancellationToken ct)
    {
        if (claimRewards) await ClaimBothTabs(ct);
        await Click(Mission, Back, ct);
        await Require(null, ct, Story);
    }

    internal async Task ClaimFromStoryAsync(CancellationToken ct)
    {
        await OpenMissionFromStory(ct);
        await FinishMissionAsync(true, ct);
    }

    private async Task ClaimBothTabs(CancellationToken ct)
    {
        await ClaimTab(Daily, ct);
        await SelectTab(Total, E("mission_total_inactive", TotalTabArea) with { BlueTab = false }, ct);
        await ClaimTab(Total, ct);
    }
}

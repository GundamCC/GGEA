using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>事件专属导航只负责选关、确认次数与领取；消耗 AP 后交给共用 HARD 略过结算流程。</summary>
internal sealed class EventStagesService(AutomationService automation, string serial)
{
    internal const string Directory = "stage/event/limited_time_event/";
    private static readonly Roi Header = new(190, 0, 470, 105), Nodes = new(0, 285, 1000, 265),
        Detail = new(1240, 86, 465, 85), SkipArea = new(1505, 645, 345, 140),
        ClaimArea = new(1505, 821, 390, 150), DailyTabArea = new(896, 94, 165, 75), TotalTabArea = new(1492, 94, 162, 75);
    private sealed record Target(string Key, Roi Area, double Threshold, bool? BlueTab = null);
    private static Target E(string name, Roi area, double threshold = .92) => new(Directory + name, area, threshold);
    private static readonly Target Story = E("story", Header, .90), Stage = E("stage_title", Header, .90),
        Mission = E("event_mission_title", Header, .90), Back = new("stage/history_back", new(0, 0, 225, 112), .90),
        Complete = E("event_stage_complete", new(1290, 510, 155, 90), .97),
        Skip = new("stage/event/button_skip", SkipArea, .90), Limit = new("stage/skip/count_limit", SkipArea, .90),
        ApDialog = E("event_skip_ap", new(750, 135, 420, 110)), Execute = new("dialog/execute", new(945, 912, 425, 160), .88),
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
        Seen? last = null;
        var end = Environment.TickCount64 + budget;
        do
        {
            var frame = await automation.CaptureScreenAsync(serial, ct);
            Seen? now = null;
            if (page is null || await Match(frame, page, ct) is not null)
                foreach (var t in choices)
                    if (await Match(frame, t, ct) is { } hit) { now = new(t, hit); break; }
            if (now is not null && last?.Target == now.Target && Math.Abs(last.Match.CenterX-now.Match.CenterX) <= 4 &&
                Math.Abs(last.Match.CenterY-now.Match.CenterY) <= 4) return now;
            last = now;
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        } while (Environment.TickCount64 < end);
        return null;
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
        await automation.TapAsync(serial, 1165, 400, ct);
        var events = new Target("stage/event/title_event", new(155, 0, 450, 108), .88);
        await Click(events, E("event_banner", new(0, 105, 398, 871)), ct);
        var forward = new Target("stage/event/button_to_event", FierceEnemyAssaultEntryService.ButtonRegion, .85);
        await Click(E("event_poster", new(740, 230, 635, 370)), forward, ct);
        await Require(null, ct, Story);
    }

    internal async Task ReturnFromBossAsync(CancellationToken ct)
    {
        await Click(E("boss", Header, .90), Back, ct);
        await Require(null, ct, Story);
    }

    private async Task<Seen> FindNode(int index, CancellationToken ct)
    {
        var node = E($"node_hard_{index}", Nodes, .94);
        for (int i = 0; i < 8; i++)
        {
            if (await Observe(Stage, [node], ct, 1900) is { } seen) return seen;
            await Require(null, ct, Stage);
            await automation.SwipeAsync(serial, 830, 538, 225, 538, 370, ct);
        }
        return await Require(Stage, ct, node);
    }

    private async Task RunSkip(StageSkipService settlement, UiLayout layout, bool hard, CancellationToken ct)
    {
        var arrow = hard ? "develop/button/set_max_button" : "develop/button/set_min_button";
        var area = hard ? new Roi(1190, 310, 230, 175) : new Roi(533, 310, 210, 165);
        var ready = new Target(arrow + "_disabled", area, .93);
        if (hard) await Require(ApDialog, ct, E("event_daily_limit", new(775, 437, 240, 108)));
        var selection = await Require(ApDialog, ct, ready, new Target(arrow, area, .90));
        if (selection.Target != ready)
        {
            await Tap(selection, ct);
            await Require(ApDialog, ct, ready);
        }
        if (!hard) await Require(ApDialog, ct, E("event_skip_one", new(922, 329, 101, 115), .98));
        await Click(ApDialog, Execute, ct);
        // 事件弹窗的取消按钮位于底部。只替换此锚点，结果与升级仍沿用公共布局。
        var eventLayout = new UiLayout { ScreenWidth = layout.ScreenWidth, ScreenHeight = layout.ScreenHeight,
            Anchors = layout.Anchors.Where(a => a.Name != "skipDialogCancel").Append(
                new UiAnchor { Name = "skipDialogCancel", X = 590, Y = 935, Width = 360, Height = 115 }).ToList() };
        var result = await settlement.CompleteExecutedSkipAsync(serial, eventLayout, ct);
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }

    private async Task OpenMission(CancellationToken ct)
    {
        await Click(Stage, Back, ct);
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
        var state = await Require(Mission, ct, Claimed, Claim);
        if (state.Target == Claimed) return;
        await Tap(state, ct);
        var receipt = E("mission_receipt", new(790, 33, 350, 115));
        await Click(receipt, new("dialog/button_ok", new(720, 905, 480, 175), .88), ct);
        await Require(tab, ct, Claimed);
    }

    // 三个 HARD 均已耗尽时，只有明确读到 9/10 才补普通关，缺帧或文字被遮挡时继续观察。
    private async Task ConfirmNineAsync(OnnxTextRecognizer ocr, CancellationToken ct)
    {
        int consecutive = 0;
        var deadline = Environment.TickCount64 + GameTiming.RecognitionTimeoutMs;
        do
        {
            var frame = await automation.CaptureScreenAsync(serial, ct);
            string progress = "";
            if (await Match(frame, Daily, ct) is not null && await Match(frame, TenRow, ct) is not null)
            {
                const int width = 128, height = 32;
                var ink = new byte[width * height];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int offset = ((483 + y) * frame.Width + 1096 + x) * 4;
                    ink[y * width + x] = (byte)Math.Clamp((167 - frame.Bgra[offset + 2]) * 3, 0, 255);
                }
                var result = ocr.Recognize(ink, width, new(0, 0, width, height), ct);
                if (result.Confidence >= .9) progress = result.Text.Replace(" ", "");
            }
            consecutive = progress == "9/10" ? consecutive + 1 : 0;
            if (consecutive == 2) return;
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        } while (Environment.TickCount64 < deadline);
        throw new InvalidOperationException("未能确认每日使命进度为 9/10，已保留 AP。");
    }

    internal async Task DailyAsync(StageSkipService settlement, UiLayout layout, OnnxTextRecognizer ocr, CancellationToken ct)
    {
        await EnterAsync(false, ct);
        var ranHard = false;
        for (int i = 1; i <= 3; i++)
        {
            await Tap(await FindNode(i, ct), ct);
            var detail = E($"selected_hard_{i}", Detail, .97);
            await Require(Stage, ct, detail);
            var state = await Require(detail, ct, Limit, Skip);
            if (state.Target == Limit) continue;
            await Tap(state, ct);
            await RunSkip(settlement, layout, true, ct);
            await Require(detail, ct, Limit);
            ranHard = true;
        }

        // 重启时先读使命进度；已经满十次时不能重复消耗普通关卡的 AP。
        var needsOne = ranHard;
        if (!ranHard)
        {
            await OpenMission(ct);
            await Require(Daily, ct, TenRow);
            needsOne = await Observe(Daily, [TenDone], ct, 3000) is null;
            if (needsOne)
            {
                await ConfirmNineAsync(ocr, ct);
                await Click(Mission, Back, ct);
                await EnterAsync(false, ct);
            }
        }
        if (needsOne)
        {
            var first = await FindNode(1, ct);
            var second = await FindNode(2, ct);
            // 相邻节点等距、上下交替，以同屏的 HARD1 和 HARD2 反推紧邻前一关的位置。
            first = await Require(Stage, ct, first.Target);
            second = await Require(Stage, ct, second.Target);
            if (first.Match.CenterX < second.Match.CenterX - first.Match.CenterX + 8)
            {
                await automation.SwipeAsync(serial, 300, 540, 570, 540, 400, ct);
                first = await Require(Stage, ct, first.Target);
                second = await Require(Stage, ct, second.Target);
            }
            var pitch = second.Match.CenterX - first.Match.CenterX;
            var x = first.Match.CenterX - pitch;
            if (pitch is < 170 or > 210 || x < 0) throw new InvalidOperationException("未确认 HARD1 前一关的位置。");
            await automation.TapAsync(serial, x, second.Match.CenterY, ct);
            await Require(Stage, ct, E("previous_stage_title", new(1180, 88, 560, 100), .94));
            await Click(Stage, Skip, ct);
            await RunSkip(settlement, layout, false, ct);
            await OpenMission(ct);
        }
        await Require(Daily, ct, TenDone);
        await ClaimTab(Daily, ct);
        await SelectTab(Total, E("mission_total_inactive", TotalTabArea) with { BlueTab = false }, ct);
        await ClaimTab(Total, ct);
        await Click(Mission, Back, ct);
        await Require(null, ct, Story);
    }
}

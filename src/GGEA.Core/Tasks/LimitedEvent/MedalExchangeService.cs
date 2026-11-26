using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>以两类报酬的实读数量推进奖池，每次兑换和重置只提交一次。</summary>
internal sealed class MedalExchangeService(AutomationService automation, OnnxTextRecognizer ocr, IGgeaLog log, string serial)
{
    private const int MedalCost = 5;
    private const string Directory = "stage/event/limited_time_event/";
    private sealed record Target(string Key, Roi Area, double Threshold = .94);
    private static Target Event(string name, Roi area) => new(Directory + name, area);
    private static readonly Roi PageTitle = new(190, 0, 500, 110), ModalTitle = new(700, 30, 520, 105);
    private static readonly Target Story = Event("story", PageTitle), Supply = Event("medal_supply_title", PageTitle),
        Settings = Event("medal_settings_title", ModalTitle), Status = Event("medal_reward_status_title", ModalTitle),
        OpenSettings = Event("medal_settings_button", new(625, 795, 360, 130)),
        SupplyStatus = Event("medal_reward_status_button", new(1030, 670, 460, 120)),
        Hundred = Event("medal_settings_100_selected", new(1525, 155, 300, 135)),
        Radio = Event("medal_settings_radio_unselected", new(1545, 166, 128, 111)),
        Off = Event("medal_settings_animation_off", new(1510, 298, 340, 135)),
        On = Event("medal_settings_animation_on", Off.Area),
        Bulk = Event("medal_exchange_100_button", new(1480, 795, 365, 106)),
        One = Event("medal_exchange_one", new(1120, 790, 350, 115)),
        Results = Event("medal_exchange_results_title", new(40, 90, 280, 125)),
        Again = Event("medal_results_exchange_again", new(1485, 936, 345, 138)),
        ResultStatus = Event("medal_results_status_button", new(23, 946, 380, 130)),
        Key = Event("medal_reset_key_rewards", new(478, 138, 300, 601)),
        General = Event("medal_general_rewards", Key.Area),
        Unchecked = Event("medal_reset_unchecked", new(475, 782, 100, 90)),
        Checked = Event("medal_reset_checked", Unchecked.Area),
        Reset = Event("medal_reset_enabled", new(1015, 766, 454, 130)),
        Complete = Event("medal_reset_complete_title", new(817, 175, 310, 115)),
        ResetMessage = Event("medal_reset_complete_message", new(760, 457, 406, 140));
    // 共享模板包含整个按钮，因此搜索区必须容纳其外框。
    private static readonly Target ModalClose = new("common/close", new(733, 904, 460, 175), .91),
        ResultClose = new("common/close", new(1060, 902, 460, 175), .89),
        Ok = new("dialog/button_ok", new(727, 751, 465, 185), .89);

    private readonly MedalPoolReader reader = new(ocr);

    public async Task RunAsync(CancellationToken ct)
    {
        if (!System.IO.File.Exists(ConfigService.ResolvePath($"resources/templates/{Supply.Key}.png")))
            throw new InvalidOperationException(L.Text("当前游戏语言缺少故事事件识别素材。"));
        ocr.Validate();
        var frame = await Capture(ct);
        if (await Match(frame, Story, ct) is null) throw new InvalidOperationException(L.Text("请先手动进入故事事件界面。"));
        await automation.TapAsync(serial, 1656, 720, ct);
        await Click(Supply, OpenSettings, ct);
        await Configure(ct);
        await Click(Settings, ModalClose, ct);
        var balance = await ReadBalance(ct);
        await Click(Supply, SupplyStatus, ct);
        var pool = await ReadPool(ct);
        var parent = Supply;
        var previousBatch = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (pool.Remaining == 0)
            {
                // 重置前独立复读两类分数。仅凭可勾选的重置选项不能证明一般报酬领满。
                if (await ReadPool(ct) != pool) throw Uncertain();
                parent = await ResetPool(ct);
                await OpenStatus(parent, ct);
                pool = await ReadPool(ct);
                if (!pool.IsNew) throw Uncertain();
                log.Info(L.Text("已确认新奖池，两类报酬均从零开始。"));
            }
            await Click(Status, ModalClose, ct);
            if (balance < MedalCost)
            {
                if (parent == Results) await Click(Results, ResultClose, ct);
                balance = await ReadBalance(ct);
                if (balance < MedalCost) return;
                parent = Supply;
            }
            // 常规每份 5 枚。能支付剩余整批则用 100 次，否则以单次用尽不足一批的余额。
            var batch = balance >= MedalCost * Math.Min(100, pool.Remaining) ? 100 : 1;
            if (parent == Results && batch != previousBatch)
            {
                await Click(Results, ResultClose, ct);
                parent = Supply;
            }
            if (parent == Supply)
            {
                int actual = await ReadBalance(ct);
                if (actual != balance) throw Uncertain();
                // 池尾仍需按当前帧确认可用按钮。只点击已识别的批量按钮或单次按钮。
                frame = await WaitPair(Supply, SupplyStatus, ct);
                var button = batch == 100 ? await Match(frame, Bulk, ct) : null;
                if (button is null)
                {
                    batch = 1;
                    button = await Match(frame, One, ct) ?? throw Uncertain();
                }
                await Tap(button, ct);
            }
            else await Click(Results, Again, ct);

            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
            await OpenStatus(Results, ct);
            var after = await ReadPool(ct);
            var gained = after.GainSince(pool, batch);
            balance -= MedalCost * gained;
            if (balance < 0) throw Uncertain();
            log.Info($"奖章机体补给：本批获得 {gained} 份，关键 {after.Key.Obtained}/{after.Key.Total}，一般 {after.General.Obtained}/{after.General.Total}。");
            pool = after;
            previousBatch = batch;
            parent = Results;
        }
    }

    private async Task Configure(CancellationToken ct)
    {
        var frame = await WaitPage(Settings, ct);
        if (await Match(frame, Hundred, ct) is null) await Click(Settings, Radio, ct);
        frame = await WaitPair(Settings, Hundred, ct);
        if (await Match(frame, Off, ct) is null) await Click(Settings, On, ct);
        frame = await WaitPair(Settings, Off, ct);
        if (await Match(frame, Hundred, ct) is null) throw Uncertain();
    }

    /// <summary>兼容自动弹出的状况窗口，以及动画结束后手动展开状况的结果页。</summary>
    private async Task OpenStatus(Target parent, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 90000;
        do
        {
            var frame = await Capture(ct);
            if (await Match(frame, Status, ct) is not null) return;
            if (await Match(frame, parent, ct) is not null &&
                await Match(frame, parent == Supply ? SupplyStatus : ResultStatus, ct) is { } button)
            {
                await Tap(button, ct);
                await WaitPage(Status, ct);
                return;
            }
            await Task.Delay(400, ct);
        } while (Environment.TickCount64 < deadline);
        throw Uncertain();
    }

    private async Task<Target> ResetPool(CancellationToken ct)
    {
        var frame = await WaitPage(Status, ct);
        if (await Match(frame, Checked, ct) is null) await Click(Status, Unchecked, ct);
        frame = await WaitPair(Status, Checked, ct);
        await Tap(await Match(frame, Reset, ct) ?? throw Uncertain(), ct);
        frame = await WaitPair(Complete, ResetMessage, ct);
        await Tap(await Match(frame, Ok, ct) ?? throw Uncertain(), ct);
        // OK 后会短暂重绘旧状况；先等底层页面恢复，再重新打开新池。
        var deadline = Environment.TickCount64 + 20000;
        do
        {
            frame = await Capture(ct);
            if (await Match(frame, Status, ct) is null)
            {
                if (await Match(frame, Supply, ct) is not null) return Supply;
                if (await Match(frame, Results, ct) is not null) return Results;
            }
            await Task.Delay(400, ct);
        } while (Environment.TickCount64 < deadline);
        throw Uncertain();
    }

    private async Task<int> ReadBalance(CancellationToken ct)
    {
        var frame = await WaitPair(Supply, SupplyStatus, ct);
        var balance = reader.Balance(frame, ct);
        await Task.Delay(250, ct);
        frame = await WaitPair(Supply, SupplyStatus, ct);
        if (balance is null || reader.Balance(frame, ct) != balance) throw Uncertain();
        return balance.Value;
    }

    private async Task<MedalPool> ReadPool(CancellationToken ct) =>
        new(await ReadCategory(Key, true, ct), await ReadCategory(General, false, ct));

    private async Task<RewardCount> ReadCategory(Target header, bool backToTop, CancellationToken ct)
    {
        for (var i = 0; i < 24; i++)
        {
            var frame = await WaitPage(Status, ct);
            if (await Match(frame, header, ct) is { } label)
            {
                var first = reader.Count(frame, label.CenterY, ct);
                await Task.Delay(250, ct);
                var next = await WaitPage(Status, ct);
                if (await Match(next, header, ct) is { } again && first is { } count &&
                    reader.Count(next, again.CenterY, ct) == count) return count;
                if (i < 2) continue;
                throw Uncertain();
            }
            if (i == 23) break;
            int from = backToTop ? 395 : 684, to = backToTop ? 684 : 395;
            await automation.SwipeAsync(serial, 1230, from, 1230, to, 650, ct);
            await Task.Delay(650, ct);
        }
        throw Uncertain();
    }

    private async Task Click(Target page, Target control, CancellationToken ct)
    {
        var frame = await WaitPair(page, control, ct);
        await Tap(await Match(frame, control, ct) ?? throw Uncertain(), ct);
    }

    private Task<ScreenImage> WaitPage(Target page, CancellationToken ct) => WaitPair(page, null, ct);
    private async Task<ScreenImage> WaitPair(Target page, Target? control, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + GameTiming.RecognitionTimeoutMs;
        do
        {
            var frame = await Capture(ct);
            if (await Match(frame, page, ct) is not null &&
                (control is null || await Match(frame, control, ct) is not null)) return frame;
            await Task.Delay(350, ct);
        } while (Environment.TickCount64 < deadline);
        log.Warn($"奖章兑换等待超时：{page.Key} / {control?.Key}，搜索区 {control?.Area ?? page.Area}。");
        throw Uncertain();
    }

    private async Task<ScreenImage> Capture(CancellationToken ct)
    {
        var frame = await automation.CaptureScreenAsync(serial, ct);
        if (frame.Width != 1920 || frame.Height != 1080) throw Uncertain();
        return frame;
    }

    private Task<TemplateMatchResult?> Match(ScreenImage frame, Target target, CancellationToken ct) =>
        automation.MatchImageAsync(frame, ConfigService.ResolvePath($"resources/templates/{target.Key}.png"),
            new WaitOptions { Roi = target.Area, Threshold = target.Threshold }, ct);

    private Task Tap(TemplateMatchResult match, CancellationToken ct) =>
        automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
    private static InvalidOperationException Uncertain() => new(L.Text("未能确认奖章余额、报酬数量或界面状态，已停止兑换。"));
}

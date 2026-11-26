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
    private static Target Event(string name, Roi area, double threshold = .94) => new(Directory + name, area, threshold);
    private static readonly Roi PageTitle = new(190, 0, 500, 110), ModalTitle = new(700, 30, 520, 105);
    private static readonly Target Story = Event("story", PageTitle, .90), Supply = Event("medal_supply_title", PageTitle, .90),
        Settings = Event("medal_settings_title", ModalTitle), Status = Event("medal_reward_status_title", ModalTitle),
        OpenSettings = Event("medal_settings_button", new(625, 795, 360, 130)),
        SupplyStatus = Event("medal_reward_status_button", new(1030, 670, 460, 120)),
        Hundred = Event("medal_settings_100_selected", new(1525, 155, 300, 135)),
        Radio = Event("medal_settings_radio_unselected", new(1545, 166, 128, 111)),
        Off = Event("medal_settings_animation_off", new(1510, 298, 340, 135)),
        On = Event("medal_settings_animation_on", Off.Area),
        Bulk = Event("medal_bulk_enabled", new(1450, 779, 405, 165)),
        DarkBulk = Event("medal_bulk_disabled", Bulk.Area),
        Results = Event("medal_exchange_results_title", new(40, 90, 280, 125)),
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

    private readonly StageEntryButtonRecognizer buttons = new();
    private readonly MedalPoolReader reader = new(ocr);

    public async Task RunAsync(CancellationToken ct)
    {
        if (!System.IO.File.Exists(ConfigService.ResolvePath($"resources/templates/{Supply.Key}.png")))
            throw new InvalidOperationException(L.Text("当前游戏语言缺少故事事件识别素材。"));
        ocr.Validate();
        var entry = await StableObservation.WaitAsync<Target>(async () =>
        {
            var frame = await Capture(ct);
            if (await IsResetReceipt(frame, ct)) return Complete;
            if (await Match(frame, Supply, ct) is not null) return Supply;
            return await Match(frame, Story, ct) is not null ? Story : null;
        }, (a,b) => a == b, GameTiming.RecognitionTimeoutMs, ct) ?? throw Uncertain();
        if (entry == Complete)
        {
            var underlying = await CloseResetReceipt(ct);
            if (underlying == Results) await Click(Results, ResultClose, ct);
        }
        if (entry == Story) await automation.TapAsync(serial, 1656, 720, ct);
        await Click(Supply, OpenSettings, ct);
        await Configure(ct);
        await Click(Settings, ModalClose, ct);
        var balance = await ReadBalance(ct);
        await OpenStatus(Supply, ct);
        var pool = await ReadPool(ct);
        if (entry == Complete && !pool.IsNew) throw Uncertain();
        var parent = Supply;

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
            parent = await WaitParent(ct);
            if (parent == Results) await Click(Results, ResultClose, ct);
            var batch = await SubmitFromSupply(balance, pool.Remaining, ct);
            if (batch == 0) return;

            await OpenStatus(Results, ct, balance);
            var after = await ReadPool(ct);
            var gained = after.GainSince(pool, batch);
            balance -= MedalCost * gained;
            if (balance < 0) throw Uncertain();
            log.Info($"奖章机体补给：本批获得 {gained} 份，关键 {after.Key.Obtained}/{after.Key.Total}，一般 {after.General.Obtained}/{after.General.Total}。");
            pool = after;
        }
    }

    /// <summary>每轮关闭结果后从补给页重新报价。两帧的余额、按钮状态与费用一致才提交一次。</summary>
    private async Task<int> SubmitFromSupply(int expectedBalance, int remaining, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + GameTiming.RecognitionTimeoutMs;
        int? previous = null;
        do
        {
            var frame = await Capture(ct);
            int? count = null;
            TemplateMatchResult? action = null;
            if (await Match(frame, Supply, ct) is not null && reader.Balance(frame, ct) == expectedBalance)
            {
                if (await Match(frame, DarkBulk, ct) is not null)
                {
                    count = 0;
                }
                else if (await Match(frame, Bulk, ct) is { } bulk &&
                    reader.BulkCost(frame, bulk, ct) is int cost && cost <= expectedBalance)
                {
                    count = cost / MedalCost;
                    action = bulk;
                }
            }
            if (count is int quantity && count == previous)
            {
                if (quantity == 0) return 0;
                log.Info($"奖章机体补给：余额 {expectedBalance}，补给页本批 {quantity} 次。");
                await Tap(action!, ct);
                return Math.Min(quantity, remaining);
            }
            previous = count;
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        } while (Environment.TickCount64 < deadline);
        throw Uncertain();
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

    /// <summary>兑换已提交。只读重识别当前阶段；唯一允许补点的是打开状况的按钮。</summary>
    private async Task OpenStatus(Target parent, CancellationToken ct, int? previousBalance = null)
    {
        var deadline = Environment.TickCount64 + 90000;
        var attempts = 0;
        long nextAttempt = 0;
        TemplateMatchResult? lastButton = null;
        int? lastBalance = null;
        do
        {
            var frame = await Capture(ct);
            if (await Match(frame, Status, ct) is not null) return;

            var visible = parent;
            var page = await Match(frame, parent, ct);
            if (page is null)
            {
                // 结算后也可能回到补给页，只通过状况窗口核对已到账份数。
                if (previousBalance.HasValue && parent == Results)
                {
                    visible = Supply;
                    page = await Match(frame, Supply, ct);
                }
            }
            var button = page is null ? null : await Match(frame, visible == Supply ? SupplyStatus : ResultStatus, ct);
            // 已经确认离开上轮结果并从补给页提交；结果余额可读时，再检查本批的实际扣款。
            int? current = null;
            bool fresh = true;
            if (button is not null && previousBalance is int before)
            {
                current = visible == Supply ? reader.Balance(frame, ct) : reader.ResultBalance(frame, ct);
                fresh = current is int n
                    ? before - n is >= MedalCost and <= 100 * MedalCost && (before - n) % MedalCost == 0
                    : visible == Results;
            }
            if (button is not null && fresh && lastButton is not null && current == lastBalance &&
                Math.Abs(button.CenterX - lastButton.CenterX) <= 3 && Math.Abs(button.CenterY - lastButton.CenterY) <= 3 &&
                Environment.TickCount64 >= nextAttempt && attempts < 3)
            {
                attempts++;
                log.Info($"奖章兑换：已连续确认{(visible == Results ? "交换结果" : "补给页")}与状况按钮，打开目前获得状况（{attempts}/3）。");
                await Tap(button, ct);
                nextAttempt = Environment.TickCount64 + 3500;
                lastButton = null;
            }
            else lastButton = fresh ? button : null;
            lastBalance = current;
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        } while (Environment.TickCount64 < deadline);
        log.Warn($"奖章兑换：未确认目前获得状况，打开尝试 {attempts}/3，最后读取余额 {lastBalance}。");
        throw Uncertain();
    }

    private async Task<Target> ResetPool(CancellationToken ct)
    {
        var frame = await WaitPage(Status, ct);
        if (await Match(frame, Checked, ct) is null) await Click(Status, Unchecked, ct);
        frame = await WaitPair(Status, Checked, ct);
        await Tap(await Match(frame, Reset, ct) ?? throw Uncertain(), ct);
        return await CloseResetReceipt(ct);
    }

    private async Task<bool> IsResetReceipt(ScreenImage frame, CancellationToken ct) =>
        await Match(frame, Complete, ct) is not null && await Match(frame, ResetMessage, ct) is not null;

    private async Task<Target> CloseResetReceipt(CancellationToken ct)
    {
        // 完成提示的蓝色按钮会发光。先核对标题和正文，再用同一通用 OK 素材的字形与蓝色亮态复核。
        var ok = await StableObservation.WaitAsync(async () =>
        {
            var frame = await Capture(ct);
            if (!await IsResetReceipt(frame, ct)) return null;
            return await Match(frame, Ok, ct) ?? buttons.MatchBlueLabel(frame,
                ConfigService.ResolvePath($"resources/templates/{Ok.Key}.png"), Ok.Area, .90, ct);
        }, (a,b) => Math.Abs(a.CenterX-b.CenterX) <= 4 && Math.Abs(a.CenterY-b.CenterY) <= 4,
            GameTiming.RecognitionTimeoutMs, ct);
        if (ok is null) throw new InvalidOperationException("奖章兑换：未连续确认重置完成提示与 OK 按钮。");
        log.Info($"奖章兑换：已确认重置完成，点击 OK ({ok.CenterX},{ok.CenterY})。");
        await Tap(ok, ct);
        return await WaitParent(ct);
    }

    /// <summary>弹窗关闭后重新确认实际底层页面，避免沿用上一次打开弹窗时的父页。</summary>
    private async Task<Target> WaitParent(CancellationToken ct)
    {
        return await StableObservation.WaitAsync<Target>(async () =>
        {
            var frame = await Capture(ct);
            if (await Match(frame, Status, ct) is not null) return null;
            if (await Match(frame, Supply, ct) is not null) return Supply;
            return await Match(frame, Results, ct) is not null ? Results : null;
        }, (a,b) => a == b, 20000, ct) ?? throw Uncertain();
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

    private async Task<bool> IsGeneralOnly(ScreenImage frame, CancellationToken ct)
        => await Match(frame, Event("medal_status_top_note", new(480, 141, 225, 89)), ct) is not null &&
           await Match(frame, Event("medal_general_rewards", new(478, 388, 300, 85)), ct) is not null;

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
            // 后续奖池可能只有一般报酬。顶部注意事项和首个分类同帧成立才确认关键报酬为空。
            if (backToTop && await IsGeneralOnly(frame, ct))
            {
                await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
                if (await IsGeneralOnly(await WaitPage(Status, ct), ct)) return new RewardCount(0, 0);
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

    private sealed record MatchedFrame(ScreenImage Frame, TemplateMatchResult Match);

    private Task<ScreenImage> WaitPage(Target page, CancellationToken ct) => WaitPair(page, null, ct);
    private async Task<ScreenImage> WaitPair(Target page, Target? control, CancellationToken ct)
    {
        var stable = await StableObservation.WaitAsync(async () =>
        {
            var frame = await Capture(ct);
            var hit = await Match(frame, page, ct);
            if (hit is null) return null;
            if (control is not null) hit = await Match(frame, control, ct);
            return hit is null ? null : new MatchedFrame(frame, hit);
        }, (a,b) => Math.Abs(a.Match.CenterX-b.Match.CenterX) <= 4 &&
            Math.Abs(a.Match.CenterY-b.Match.CenterY) <= 4, GameTiming.RecognitionTimeoutMs, ct);
        if (stable is not null) return stable.Frame;
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

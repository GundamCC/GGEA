using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>仅表示奖章流程的画面未确认，供每日队列单独记录并继续。</summary>
internal sealed class MedalRecognitionException(string message) : InvalidOperationException(message);

/// <summary>以两类报酬的实读数量推进奖池，每次兑换和重置只提交一次。</summary>
internal sealed class MedalExchangeService(AutomationService automation, OnnxTextRecognizer ocr, IGgeaLog log, string serial, RecognitionEvidence? evidence = null)
{
    private const int MedalCost = 5;
    private const string Directory = "stage/event/limited_time_event/";
    private sealed record Target(string Key, Roi Area, double Threshold = .90);
    private static Target Event(string name, Roi area, double threshold = .90) => new(Directory + name, area, threshold);
    private static readonly Roi PageTitle = new(190, 0, 500, 110), ModalTitle = new(700, 30, 520, 105);
    private static readonly Target Story = Event("story", PageTitle, .90), Supply = Event("medal_supply_title", PageTitle, .90),
        Settings = Event("medal_settings_title", ModalTitle), Status = Event("medal_reward_status_title", ModalTitle),
        OpenSettings = Event("medal_settings_button", new(625, 795, 360, 130)),
        SupplyStatus = Event("medal_reward_status_button", new(1030, 670, 460, 120)),
        Hundred = Event("medal_settings_100_selected", new(1525, 155, 300, 135)),
        Radio = Event("medal_settings_radio_unselected", new(1545, 166, 128, 111)),
        Off = Event("medal_settings_animation_off", new(1510, 298, 340, 135)),
        On = Event("medal_settings_animation_on", Off.Area),
        Bulk = Event("medal_bulk_enabled", new(1450, 779, 405, 165), .90),
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

    private ScreenImage? lastFrame;
    private bool readingPool;
    private readonly Dictionary<string,string> readings = new();
    private string submission = "尚未提交";
    private string Diagnostics => string.Join("，", readings.Select(p => $"{p.Key}={p.Value}"));

    public async Task RunAsync(CancellationToken ct)
    {
        readings.Clear();
        foreach (var key in new[] { "补给余额", "上一兑换结果页余额", "当前按钮报价", "关键报酬", "一般报酬" }) readings[key] = "尚未读取";
        submission = "尚未提交";
        lastFrame = null;
        readingPool = false;
        try { await ExchangeAsync(ct); }
        catch (MedalRecognitionException ex)
        {
            log.Warn(ex.Message);
            if (evidence is not null && lastFrame is { } frame)
                await evidence.SaveContextAsync("medal-exchange", serial, frame, new { reason = ex.Message, submission, readings });
            // 保存原失败帧后再退出已知状况弹窗；返回结果页时只操作关闭按钮。
            if (readingPool || (lastFrame is { } failed && await Match(failed, Status, ct) is not null))
            {
                try
                {
                    await ReturnFromStatusFailure(ct);
                    log.Info("奖章兑换：已退出目前获得状况并确认补给页。");
                }
                catch (MedalRecognitionException recovery)
                {
                    log.Warn($"奖章兑换：退出状况页未确认，交由每日导航重新识别。{recovery.Message}");
                }
            }
            throw;
        }
    }

    private sealed record ExitPage(Target Page, TemplateMatchResult Button);

    private async Task ReturnFromStatusFailure(CancellationToken ct)
    {
        // 每次补点前重新确认弹窗身份和关闭按钮；结果页只允许关闭，补给页只读确认。
        var attempts = new Dictionary<Target,int>();
        while (true)
        {
            var state = await StableObservation.WaitAsync<ExitPage>(async () =>
            {
                var frame = await Capture(ct);
                foreach (var (page, button) in new[] { (Status, ModalClose), (Results, ResultClose), (Supply, SupplyStatus) })
                {
                    if (await Match(frame, page, ct) is null) continue;
                    var hit = await Match(frame, button, ct);
                    return hit is null ? null : new ExitPage(page, hit);
                }
                return null;
            }, (a,b) => a.Page == b.Page && Math.Abs(a.Button.CenterX-b.Button.CenterX) <= 4 &&
                Math.Abs(a.Button.CenterY-b.Button.CenterY) <= 4, GameTiming.RecognitionTimeoutMs, ct);
            if (state is null) throw Failure("退出状况后确认当前界面");
            if (state.Page == Supply) return;
            int count = attempts.GetValueOrDefault(state.Page);
            if (count >= 3) throw Failure($"退出 {state.Page.Key} 后仍在原页");
            attempts[state.Page] = count + 1;
            await Tap(state.Button, ct);
            await Task.Delay(1500, ct);
        }
    }

    private async Task ExchangeAsync(CancellationToken ct)
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
        }, (a,b) => a == b, GameTiming.RecognitionTimeoutMs, ct) ?? throw Failure("确认启动页面（故事事件、补给或重置完成）");
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
        if (entry == Complete && !pool.IsNew) throw Failure("复核重置后新池，两类已获得数应为零");
        var parent = Supply;
        int? lastSettledCost = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (pool.Remaining == 0)
            {
                // 重置前独立复读两类分数。仅凭可勾选的重置选项不能证明一般报酬领满。
                if (await ReadPool(ct) != pool) throw Failure($"重置前复核已清空奖池，与前次 {pool} 不一致");
                parent = await ResetPool(ct);
                await OpenStatus(parent, ct);
                pool = await ReadPool(ct);
                if (!pool.IsNew) throw Failure("复核重置后新池，两类已获得数应为零");
                log.Info(L.Text("已确认新奖池，两类报酬均从零开始。"));
            }
            await Click(Status, ModalClose, ct);
            parent = await WaitParent(ct);
            if (parent == Results) await Click(Results, ResultClose, ct);
            var batch = await SubmitFromSupply(balance, pool.Remaining, ct, lastSettledCost);
            if (batch == 0) return;

            await OpenStatus(Results, ct, balance);
            var after = await ReadPool(ct);
            // 已提交的批次只复读奖池，直到到账数量与本批一致。
            int? gain = after.TryGainSince(pool, batch);
            for (int retry = 0; gain is null && retry < 2; retry++)
            {
                after = await ReadPool(ct);
                gain = after.TryGainSince(pool, batch);
            }
            var gained = gain ?? throw Failure($"核对本批到账，最多 {batch} 份，兑换前 {pool}，当前 {after}");
            lastSettledCost = MedalCost * gained;
            balance -= lastSettledCost.Value;
            if (balance < 0) throw Failure($"核对扣款，推算余额 {balance}");
            log.Info($"奖章机体补给：本批获得 {gained} 份，关键 {after.Key.Obtained}/{after.Key.Total}，一般 {after.General.Obtained}/{after.General.Total}。");
            submission = $"上一批已核对到账 {gained} 份，实际费用 {lastSettledCost} 枚，扣款后推算余额 {balance} 枚";
            pool = after;
        }
    }

    /// <summary>每轮关闭结果后从补给页重新报价。两帧的余额、按钮状态与费用一致才提交一次。</summary>
    internal async Task<int> SubmitFromSupply(int expectedBalance, int remaining, CancellationToken ct, int? lastSettledCost = null)
    {
        var deadline = Environment.TickCount64 + GameTiming.RecognitionTimeoutMs;
        int? previous = null;
        do
        {
            var frame = await Capture(ct);
            int? count = null;
            TemplateMatchResult? action = null;
            readings["批量按钮"] = "未确认";
            readings["当前按钮报价"] = "尚无当前报价";
            readings["补给余额"] = "尚未确认补给页";
            if (await Match(frame, Supply, ct) is not null)
            {
                var observed = ReadSupplyBalance(frame, ct);
                // 上一批由奖池增量证明实际扣款。仅在不足一份时，允许灰态双帧补足余额漏读。
                bool settledBelowMinimum = lastSettledCost.HasValue && expectedBalance is >= 0 and < MedalCost;
                if ((observed == expectedBalance || (observed is null && settledBelowMinimum)) &&
                    await Match(frame, DarkBulk, ct) is not null)
                {
                    readings["批量按钮"] = "置灰";
                    count = 0;
                }
                else if (observed == expectedBalance && await Match(frame, Bulk, ct) is { } bulk)
                {
                    readings["批量按钮"] = $"亮态 {bulk.Confidence:P1}";
                    if (reader.BulkCost(frame, bulk, ct, text => readings["当前按钮报价"] = text) is int cost && cost <= expectedBalance)
                    {
                        count = cost / MedalCost;
                        action = bulk;
                    }
                }
            }
            if (count is int quantity && count == previous)
            {
                if (quantity == 0)
                {
                    string previousCost = lastSettledCost is int paid ? $"上一批实际费用 {paid} 枚；" : "";
                    log.Info($"奖章兑换正常完成：{previousCost}当前预计余额 {expectedBalance} 枚，补给页余额 OCR：{readings["补给余额"]}。已连续两帧确认批量交换按钮置灰。");
                    return 0;
                }
                log.Info($"奖章机体补给：余额 {expectedBalance}，补给页本批 {quantity} 次。");
                submission = $"已发出本批兑换指令（{quantity} 次，提交前余额 {expectedBalance}）";
                await Tap(action!, ct);
                return Math.Min(quantity, remaining);
            }
            previous = count;
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        } while (Environment.TickCount64 < deadline);
        throw Failure($"核对补给页余额、批量按钮与费用，预期余额 {expectedBalance}");
    }

    private async Task Configure(CancellationToken ct)
    {
        await SetOption(Hundred, Radio);
        await SetOption(Off, On);

        async Task SetOption(Target desired, Target other)
        {
            // 两种状态共享按钮文字和外框，同时比较，避免较低阈值下两者同时命中。
            async Task<SettingMatch?> Observe()
            {
                var frame = await Capture(ct);
                if (await Match(frame, Settings, ct) is null) return null;
                var yes = await Match(frame, desired, ct);
                var no = await Match(frame, other, ct);
                if (yes is not null && no is not null && Math.Abs(yes.Confidence - no.Confidence) < .01) return null;
                if (yes is not null && (no is null || yes.Confidence > no.Confidence)) return new(true, yes);
                return no is null ? null : new(false, no);
            }
            var state = await StableObservation.WaitAsync(Observe,
                (a,b) => a.Desired == b.Desired && Math.Abs(a.Button.CenterX-b.Button.CenterX) <= 4 &&
                    Math.Abs(a.Button.CenterY-b.Button.CenterY) <= 4, GameTiming.RecognitionTimeoutMs, ct)
                ?? throw Failure($"辨别交换设定 {desired.Key} / {other.Key}");
            if (state.Desired) return;
            await Tap(state.Button, ct);
            var confirmed = await StableObservation.WaitAsync(async () => await Observe() is { Desired: true } value ? value : null,
                (a,b) => a.Desired == b.Desired, GameTiming.RecognitionTimeoutMs, ct);
            if (confirmed is null) throw Failure($"点击后复核交换设定 {desired.Key}");
        }
    }

    private sealed record SettingMatch(bool Desired, TemplateMatchResult Button);

    /// <summary>兑换已提交。只读重识别当前阶段；唯一允许补点的是打开状况的按钮。</summary>
    private async Task OpenStatus(Target parent, CancellationToken ct, int? previousBalance = null)
    {
        var deadline = Environment.TickCount64 + 90000;
        var attempts = 0;
        long nextAttempt = 0;
        TemplateMatchResult? lastButton = null;
        int? lastBalance = null;
        TemplateMatchResult? lastReward = null;
        long nextRewardTap = 0;
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
            // 只有已提交本批兑换才会出现奖励展示。连续确认底部完整文字后推进，再重新识别当前页。
            var reward = previousBalance.HasValue && page is null ? automation.TapToNext.Recognize(frame, ct) : null;
            if (reward is not null && lastReward is not null && Math.Abs(reward.CenterX - lastReward.CenterX) <= 4 &&
                Math.Abs(reward.CenterY - lastReward.CenterY) <= 4 && Environment.TickCount64 >= nextRewardTap)
            {
                log.Info("奖章兑换：已连续确认奖励展示的 TAP TO NEXT。");
                await Tap(reward, ct);
                nextRewardTap = Environment.TickCount64 + 1500;
                reward = null;
            }
            lastReward = reward;
            var button = page is null ? null : await Match(frame, visible == Supply ? SupplyStatus : ResultStatus, ct);
            // 已经确认离开上轮结果并从补给页提交；结果余额可读时，再检查本批的实际扣款。
            int? current = null;
            bool fresh = true;
            if (button is not null && previousBalance is int before)
            {
                current = visible == Supply ? ReadSupplyBalance(frame, ct) : reader.ResultBalance(frame, ct, text => readings["上一兑换结果页余额"] = text);
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
        throw Failure($"从 {parent.Key} 打开目前获得状况，按钮点击 {attempts}/3 次");
    }

    private async Task<Target> ResetPool(CancellationToken ct)
    {
        var frame = await WaitPage(Status, ct);
        if (await Match(frame, Checked, ct) is null) await Click(Status, Unchecked, ct);
        frame = await WaitPair(Status, Checked, ct);
        await Tap(await Match(frame, Reset, ct) ?? throw Failure("确认勾选后的重置按钮"), ct);
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
        if (ok is null) throw Failure("连续确认重置完成提示与 OK 按钮");
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
        }, (a,b) => a == b, 20000, ct) ?? throw Failure("关闭弹窗后确认补给页或结果页");
    }

    internal async Task<int> ReadBalance(CancellationToken ct)
    {
        var stable = await StableObservation.WaitAsync(async () =>
        {
            var frame = await Capture(ct);
            return await Match(frame, Supply, ct) is not null && await Match(frame, SupplyStatus, ct) is not null &&
                ReadSupplyBalance(frame, ct) is int balance ? new BalanceReading(balance) : null;
        }, (a,b) => a == b, GameTiming.RecognitionTimeoutMs, ct);
        if (stable is null) throw Failure("连续读取补给页初始余额");
        log.Info($"奖章兑换：确认初始余额 {stable.Value}。");
        return stable.Value;
    }

    private sealed record BalanceReading(int Value);
    private int? ReadSupplyBalance(ScreenImage frame, CancellationToken ct) =>
        reader.Balance(frame, ct, text => readings["补给余额"] = text);

    private async Task<MedalPool> ReadPool(CancellationToken ct)
    {
        readingPool = true;
        var pool = new MedalPool(await ReadCategory(Key, true, ct), await ReadCategory(General, false, ct));
        log.Info($"奖章兑换：关键报酬 {pool.Key.Obtained}/{pool.Key.Total}，一般报酬 {pool.General.Obtained}/{pool.General.Total}。");
        readingPool = false;
        return pool;
    }

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
                var category = backToTop ? "关键报酬" : "一般报酬";
                RewardCount? previous = null;
                int consecutive = 0;
                var deadline = Environment.TickCount64 + GameTiming.RecognitionTimeoutMs;
                do
                {
                    // MuMu 的细字 7/7 有单字符约 77%；关键报酬要求三个独立画面的完整分数一致。
                    var count = reader.Count(frame, label.CenterY, ct, text => readings[category] = text, backToTop ? .75 : .80);
                    consecutive = count.HasValue && count == previous ? consecutive + 1 : 1;
                    if (count.HasValue && consecutive >= (backToTop ? 3 : 2)) return count.Value;
                    previous = count;
                    await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
                    frame = await WaitPage(Status, ct);
                    if (await Match(frame, header, ct) is not { } current) break;
                    label = current;
                    if (Environment.TickCount64 >= deadline) throw Failure($"连续读取{category}同行数量");
                } while (true);
                continue;
            }
            // 后续奖池可能只有一般报酬。顶部注意事项和首个分类同帧成立才确认关键报酬为空。
            if (backToTop && await IsGeneralOnly(frame, ct))
            {
                await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
                if (await IsGeneralOnly(await WaitPage(Status, ct), ct)) { readings["关键报酬"] = "0/0"; return new RewardCount(0, 0); }
            }
            if (i == 23) break;
            int from = backToTop ? 395 : 684, to = backToTop ? 684 : 395;
            await automation.SwipeAsync(serial, 1230, from, 1230, to, 650, ct);
            await Task.Delay(650, ct);
        }
        throw Failure($"在目前获得状况中定位分类 {header.Key}");
    }

    private async Task Click(Target page, Target control, CancellationToken ct)
    {
        var frame = await WaitPair(page, control, ct);
        await Tap(await Match(frame, control, ct) ?? throw Failure($"点击 {page.Key} 上的 {control.Key}"), ct);
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
        throw Failure($"等待 {page.Key} / {control?.Key ?? "标题"}，搜索区 {control?.Area ?? page.Area}");
    }

    private async Task<ScreenImage> Capture(CancellationToken ct)
    {
        var frame = await automation.CaptureScreenAsync(serial, ct);
        lastFrame = frame;
        if (frame.Width != 1920 || frame.Height != 1080) throw Failure($"检查归一化截图尺寸，实际 {frame.Width}×{frame.Height}");
        return frame;
    }

    private Task<TemplateMatchResult?> Match(ScreenImage frame, Target target, CancellationToken ct) =>
        automation.MatchImageAsync(frame, ConfigService.ResolvePath($"resources/templates/{target.Key}.png"),
            new WaitOptions { Roi = target.Area, Threshold = target.Threshold }, ct);

    private Task Tap(TemplateMatchResult match, CancellationToken ct) =>
        automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
    private MedalRecognitionException Failure(string step) => new($"奖章兑换未完成：{step}。{submission}。{Diagnostics}。");
}

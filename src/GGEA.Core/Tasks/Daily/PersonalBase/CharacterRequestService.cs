using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed record CharacterRequestResult(int Accepted, int Reported, IReadOnlyList<string> FailedRoles);

/// <summary>按本轮解锁栏位逐人承接。按委托类型核对交付、强化或开发条件，结算后再处理下一栏。</summary>
public sealed class CharacterRequestService(AutomationService automation, IGgeaLog log, OnnxTextRecognizer? ocr = null, DeliveryProtection? protection = null, DevelopRequest? development = null)
{
    internal Func<string, DeliveryTarget, Func<bool>, CancellationToken, Task<bool>>? RestockFromList { get; init; }
    internal SortieRequestService? Sortie { get; init; }
    private const string Root = "resources/templates/base/character_requests/";
    private static readonly Roi ActionLabelArea = new(1540, 800, 170, 110);
    private readonly CharacterRequestPage _page = new();
    private readonly TemplateMatcher _matcher = new();
    private readonly CharacterRequestCatalog? _catalog = ocr is null ? null : new(ocr);
    private readonly UnitUpgradeService? _upgrade = ocr is null ? null : new(automation, ocr);
    private readonly ScreenImage _acceptLabel = LoadAcceptLabel();
    private readonly TemplateImages _challenge = new(Root + "button_challenge.png");
    private readonly TemplateImages _report = new(Root + "button_report.png");
    private readonly TemplateImages _dialogFrame = new(Root + "dialog_com_frame.png");
    private readonly TemplateImages _panelCorner = new(Root + "dialog_panel_corner.png");
    private readonly TemplateImages _skip = new(Root + "dialog_skip.png");
    private readonly TemplateImages _dialogAdvance = new(Root + "dialog_advance.png");
    private readonly TemplateImages _complete = new(Root + "title_report_complete.png");
    private readonly TemplateImages _reward = new(Root + "title_claim_result.png");
    private readonly TemplateImages _ok = new("resources/templates/dialog/button_ok.png");
    private readonly TemplateImages _next = new(Root + "button_next_character.png");
    private readonly TemplateImages _info = new(Root + "button_request_info.png");
    private readonly TemplateImages _deliver = new(Root + "button_deliver.png");
    private readonly TemplateImages _obtain = new(Root + "button_obtain.png");
    private readonly TemplateImages _deliveryComplete = new(Root + "title_delivery_complete.png");
    private readonly TemplateImages _developmentTitle = new(Root + "title_development.png");
    private readonly TemplateImages _sortieTitle = new(Root + "title_sortie_request.png");
    private readonly TemplateImages _strengthTitle = new(Root + "title_strengthen_troops.png");
    private readonly TemplateImages _unitTitle = new(Root + "title_deliver_unit.png");
    private readonly TemplateImages _fundingTitle = new(Root + "title_funding.png");
    private readonly TemplateImages _weeklyReward = new(Root + "title_weekly_reward.png");
    private readonly TemplateImages _weeklyNext = new(Root + "weekly_tap_to_next.png");
    private readonly FundingRequest? _funding = ocr is null ? null : new(ocr);
    private readonly TemplateImages _back = new("resources/templates/common/back.png");

    private enum Control { Accept, Challenge, Report, Deliver, Obtain }
    private enum ArrivalKind { List, Dialogue, Detail, Complete, Reward, WeeklyReward }
    private enum RequestKind { Unknown, Funding, Strength, UnitDelivery, Development, Sortie }
    private readonly record struct Title(RequestKind Kind, Roi Region);
    private sealed record Detail(Control Action, Roi Back, Roi Button, Title Title, int? Amount);
    private sealed record Arrival(ArrivalKind Kind, Detail? Detail = null, Roi Button = default);
    private sealed record OfferScreen(Detail Detail, StrengthRequest? Request);
    private sealed record DevelopmentScreen(Detail Detail, DevelopmentOrder? Order);
    private sealed record SortieScreen(Detail Detail, SortieOrder? Order);
    private sealed record UnitOfferScreen(Detail Detail, DeliveryOffer? Offer);

    private static ScreenImage LoadAcceptLabel()
    {
        var source = new TemplateImages(Root + "button_accept.png").Primary;
        const int width = 78, height = 41;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(source.Bgra, ((39 + y) * source.Width + 142) * 4, pixels, y * width * 4, width * 4);
        return new ScreenImage { Width = width, Height = height, Bgra = pixels };
    }

    public async Task<int> AcceptFromListAsync(string serial, CancellationToken ct = default) =>
        (await RunFromListAsync(serial, ct)).Accepted;

    public Task<CharacterRequestResult> RunFromListAsync(string serial, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var state = await WaitForListAsync(serial, ct);
            var limit = state.Count;
            log.Info(L.Format($"角色要求列表已确认，本轮最多处理 {limit} 名角色。"));
            var accepted = 0;
            var reported = 0;
            var failures = new List<string>();
            for (var slot = 0; slot < limit; slot++)
            {
                if (slot > 0) state = await WaitForListAsync(serial, ct);
                limit = Math.Min(limit, state.Count);
                if (slot >= limit || (state.ActiveMask & (1 << slot)) == 0) continue;

                await automation.TapAsync(serial, 427 + 535 * slot, 410, ct);
                var detail = await WaitAsync(serial, f => ReadDetail(f, ct), ct) ?? throw Unknown();
                if (detail.Action == Control.Accept)
                {
                    await TapAsync(serial, detail.Button, ct);
                    var arrival = await FollowDialogueAsync(serial, reporting: false, ct);
                    accepted++;
                    var initialKind = detail.Title.Kind;
                    if (arrival.Kind == ArrivalKind.List)
                    {
                        if (initialKind is not (RequestKind.Funding or RequestKind.Development or RequestKind.UnitDelivery or RequestKind.Sortie)) continue;
                        var list = await WaitForListAsync(serial, ct);
                        if ((list.ActiveMask & (1 << slot)) == 0) continue;
                        await automation.TapAsync(serial, 427 + 535 * slot, 410, ct);
                        detail = await WaitAsync(serial, f => ReadDetail(f, ct), ct) ?? throw Unknown();
                    }
                    else detail = arrival.Detail!;
                    if (initialKind is (RequestKind.Development or RequestKind.UnitDelivery or RequestKind.Sortie) && detail.Title.Kind != initialKind) throw Unknown();
                    if (initialKind == RequestKind.Funding &&
                        (detail.Title.Kind != initialKind || detail.Action != Control.Deliver)) throw Unknown();
                }

                if (detail.Title.Kind == RequestKind.Funding && detail.Action == Control.Deliver)
                {
                    if (detail.Amount is not { } amount) throw FundingUnknown();
                    await TapAsync(serial, detail.Button, ct);
                    var confirmation = await WaitAsync(serial, frame => _funding!.ReadConfirmation(frame, amount, ct), ct)
                        ?? throw FundingUnknown();
                    await TapAsync(serial, confirmation.Button, ct);
                    await FinishReportAsync(serial, slot, ct, delivering: true);
                    reported++;
                    continue;
                }
                if (detail.Title.Kind == RequestKind.UnitDelivery && detail.Action is Control.Deliver or Control.Obtain)
                {
                    var evidence = await WaitAsync(serial, frame =>
                    {
                        if (ReadDetail(frame, ct) is not { Action: Control.Deliver or Control.Obtain, Title.Kind: RequestKind.UnitDelivery } current)
                            return null;
                        return new UnitOfferScreen(current, _catalog!.ReadDelivery(frame, current.Title.Region, ct));
                    }, ct) ?? throw Unknown();
                    detail = evidence.Detail;
                    if (evidence.Offer is { } delivery)
                    {
                        var remaining = await DeliverOrRestockAsync(serial, slot, detail, delivery, ct);
                        if (remaining is null)
                        {
                            await FinishReportAsync(serial, slot, ct, delivering: true);
                            reported++;
                            continue;
                        }
                        detail = remaining;
                        failures.Add(L.Format($"第 {slot + 1} 栏没有可安全交付的{delivery.Unit}"));
                    }
                    else
                        failures.Add(L.Format($"第 {slot + 1} 栏交付委托未能与表格核对"));
                }
                else if (detail.Title.Kind == RequestKind.Development && development is not null)
                {
                    var screen = await WaitForDevelopmentAsync(serial, ct);
                    detail = screen.Detail;
                    if (screen.Order is not { } order)
                        failures.Add(L.Format($"第 {slot + 1} 栏开发委托未能与表格核对"));
                    else
                    {
                        if (detail.Action == Control.Challenge && order.Progress < order.Offer.Amount)
                        {
                            await TapAsync(serial, detail.Button, ct);
                            await new DevelopService(automation, log, fixedUnitScales: true)
                                .DevelopForCharacterAsync(serial, order, development, ocr!, ct);
                            screen = await WaitForDevelopmentAsync(serial, ct);
                            if (screen.Order?.Offer != order.Offer) throw Unknown();
                            detail = screen.Detail;
                            order = screen.Order;
                        }
                        if (detail.Action != Control.Report || order.Progress != order.Offer.Amount) throw Unknown();
                        await TapAsync(serial, detail.Button, ct);
                        await FinishReportAsync(serial, slot, ct);
                        reported++;
                        continue;
                    }
                }
                else if (detail.Title.Kind == RequestKind.Sortie && Sortie is not null)
                {
                    var screen = await WaitSortieAsync(serial, ct);
                    detail = screen.Detail;
                    if (screen.Order is {} order)
                    {
                        var completed = order.Progress;
                        if (detail.Action == Control.Challenge && completed < order.Offer.Amount)
                        {
                            await TapAsync(serial, detail.Button, ct);
                            completed += await Sortie.RunAsync(serial, order, ct);
                            screen = await WaitSortieAsync(serial, ct);
                            if (screen.Order?.Offer != order.Offer || screen.Order.Progress != completed) throw Unknown();
                            detail = screen.Detail;
                        }
                        if (detail.Action == Control.Report && completed == order.Offer.Amount)
                        {
                            await TapAsync(serial, detail.Button, ct);
                            await FinishReportAsync(serial, slot, ct);
                            reported++;
                            continue;
                        }
                    }
                    failures.Add(L.Format($"第 {slot + 1} 栏请求出击未完成。"));
                }
                else if (detail.Title.Kind == RequestKind.Strength)
                {
                    var offer = await WaitOfferAsync(serial, ct);
                    detail = offer.Detail;
                    if (offer.Request is null)
                        failures.Add(L.Format($"第 {slot + 1} 栏强化委托未能与表格核对"));
                    if (offer.Request is { } request)
                    {
                        if (detail.Action == Control.Challenge && request.Progress < request.Offer.Amount)
                        {
                            await TapAsync(serial, detail.Button, ct);
                            var success = await _upgrade!.ApplyFromFilteredListAsync(serial, request.Offer.Amount - request.Progress, ct);
                            var returned = await WaitOfferAsync(serial, ct);
                            if (returned.Request?.Offer.Id != request.Offer.Id) throw Unknown();
                            detail = returned.Detail;
                            request = returned.Request;
                            if (!success)
                            {
                                failures.Add(L.Format($"{request.Name}：可用强化材料不足"));
                                log.Warn(failures[^1]);
                            }
                            else if (detail.Action != Control.Report || request.Progress != request.Offer.Amount) throw Unknown();
                        }
                        if (detail.Action == Control.Report && request.Progress == request.Offer.Amount)
                        {
                            await TapAsync(serial, detail.Button, ct);
                            await FinishReportAsync(serial, slot, ct);
                            reported++;
                            continue;
                        }
                    }
                }
                await TapAsync(serial, detail.Back, ct);
            }
            await WaitForListAsync(serial, ct);
            return new CharacterRequestResult(accepted, reported, failures);
        }, ct);

    // 返回详情表示本栏仍未完成。只有确认交付已发送才进入领奖，补开发本身不计为完成。
    private async Task<Detail?> DeliverOrRestockAsync(string serial, int slot, Detail detail, DeliveryOffer offer, CancellationToken ct)
    {
        var attempt = DeliveryProductionAttempt.Start(serial, slot, offer);
        var guards = protection ?? new DeliveryProtection();
        var inventory = new UnitDeliveryService(automation, ocr!, guards, offer.MinimumLevel);
        // 「获取途径」表示当前不能打开交付库存，直接复用按角色和编号核对过的补开发流程。
        if (detail.Action == Control.Deliver)
        {
            await TapAsync(serial, detail.Button, ct);
            if (await inventory.SubmitAsync(serial, ct)) return null;
            detail = await WaitSameDeliveryAsync(serial, offer, ct);
        }
        var target = DevelopmentRequestIndex.ForDelivery(offer, guards);
        if (target is null || RestockFromList is null) return detail;
        await TapAsync(serial, detail.Back, ct);
        await RequireSlotAsync();
        var made = await RestockFromList(serial, target, attempt.Reserve, ct);
        await RequireSlotAsync();
        await automation.TapAsync(serial, 427 + 535 * slot, 410, ct);
        detail = await WaitSameDeliveryAsync(serial, offer, ct);
        if (!made || detail.Action != Control.Deliver) return detail;

        // 重开库存，只重选一次，仍然执行全部等级与突破保护。
        await TapAsync(serial, detail.Button, ct);
        return await inventory.SubmitAsync(serial, ct) ? null : await WaitSameDeliveryAsync(serial, offer, ct);

        async Task RequireSlotAsync()
        {
            var list = await WaitForListAsync(serial, ct);
            if ((list.ActiveMask & (1 << slot)) == 0) throw Unknown();
        }
    }

    private async Task<Detail> WaitSameDeliveryAsync(string serial, DeliveryOffer expected, CancellationToken ct) =>
        await WaitAsync(serial, frame =>
        {
            var detail = ReadDetail(frame, ct);
            return detail is { Action: Control.Deliver or Control.Obtain, Title.Kind: RequestKind.UnitDelivery } &&
                _catalog!.ReadDelivery(frame, detail.Title.Region, ct) == expected ? detail : null;
        }, ct) ?? throw Unknown();

    private async Task<SortieScreen> WaitSortieAsync(string serial, CancellationToken ct) =>
        await WaitAsync(serial, f => ReadDetail(f, ct) is { Title.Kind: RequestKind.Sortie, Action: Control.Challenge or Control.Report } detail
            ? new SortieScreen(detail, _catalog!.ReadSortie(f, detail.Title.Region, ct)) : null, ct) ?? throw Unknown();

    private async Task<DevelopmentScreen> WaitForDevelopmentAsync(string serial, CancellationToken ct) =>
        await WaitAsync(serial, frame =>
        {
            var detail = ReadDetail(frame, ct);
            if (detail is not { Title.Kind: RequestKind.Development, Action: Control.Challenge or Control.Report }) return null;
            return new DevelopmentScreen(detail, _catalog!.ReadDevelopment(frame, detail.Title.Region, ct));
        }, ct) ?? throw Unknown();

    private async Task<OfferScreen> WaitOfferAsync(string serial, CancellationToken ct) =>
        await WaitAsync(serial, frame =>
        {
            var detail = ReadDetail(frame, ct);
            if (detail is null || detail.Action == Control.Accept || detail.Title.Kind != RequestKind.Strength) return null;
            return new OfferScreen(detail, _catalog!.Read(frame, detail.Title.Region, ct));
        }, ct) ?? throw Unknown();

    private async Task FinishReportAsync(string serial, int slot, CancellationToken ct, bool delivering = false)
    {
        await FollowDialogueAsync(serial, reporting: true, ct, delivering);
        var list = await WaitForListAsync(serial, ct);
        if ((list.ActiveMask & (1 << slot)) != 0) throw Unknown();
    }

    // 报告结算遮罩后面仍可能看见上一句台词；先识别结算，确认新页面后才继续。
    private async Task<Arrival> FollowDialogueAsync(string serial, bool reporting, CancellationToken ct, bool delivering = false)
    {
        var deadline = Environment.TickCount64 + 2 * GameTiming.RecognitionTimeoutMs;
        var closedComplete = false;
        var closedReward = false;
        var closedWeekly = false;
        while (Environment.TickCount64 < deadline)
        {
            var budget = (int)Math.Min(GameTiming.RecognitionTimeoutMs, deadline - Environment.TickCount64);
            var state = await WaitAsync(serial, frame =>
            {
                if (reporting)
                {
                    if (closedReward && _weeklyReward.Match(_matcher, frame, .94, new(801, 329, 314, 103), ct) is not null &&
                        _weeklyNext.Match(_matcher, frame, .94, new(806, 916, 307, 82), ct) is not null)
                        return closedWeekly ? null : new Arrival(ArrivalKind.WeeklyReward);
                    if (_reward.Match(_matcher, frame, .93, new Roi(823, 29, 275, 122), ct) is not null)
                    {
                        var ok = _ok.Match(_matcher, frame, .87, new Roi(744, 908, 440, 152), ct);
                        return !closedReward && ok is not null ? new Arrival(ArrivalKind.Reward, Button: ok.Region) : null;
                    }
                    if ((delivering ? _deliveryComplete : _complete).Match(_matcher, frame, .93, new Roi(765, 287, 390, 168), ct) is not null)
                        return closedComplete ? null : new Arrival(ArrivalKind.Complete);
                }
                if (_page.HasTitle(frame, ct))
                {
                    if (_page.ReadList(frame, ct) is not null)
                        return !reporting || closedComplete || closedReward ? new Arrival(ArrivalKind.List) : null;
                    var detail = ReadDetail(frame, ct);
                    return !reporting && detail is { Action: not Control.Accept } ? new Arrival(ArrivalKind.Detail, detail) : null;
                }
                if (closedComplete || closedReward) return null;
                var skip = _skip.Match(_matcher, frame, .96, new(1686, 26, 153, 80), ct) is not null;
                if (skip && _panelCorner.Match(_matcher, frame, .96, new(244, 886, 41, 59), ct) is not null)
                    return new Arrival(ArrivalKind.Dialogue);
                return _dialogFrame.Match(_matcher, frame, .92, new(245, 126, 185, 98), ct) is not null &&
                    (skip || _dialogAdvance.Match(_matcher, frame, .93, new(415, 729, 1230, 195), ct) is not null)
                    ? new Arrival(ArrivalKind.Dialogue) : null;
            }, ct, budget);
            if (state is null || Environment.TickCount64 >= deadline) break;
            if (state.Kind is ArrivalKind.List or ArrivalKind.Detail) return state;
            if (state.Kind == ArrivalKind.Reward)
            {
                await TapAsync(serial, state.Button, ct);
                closedReward = true;
            }
            else if (state.Kind == ArrivalKind.WeeklyReward)
            {
                await automation.TapAsync(serial, 960, 950, ct);
                closedWeekly = true;
            }
            else
            {
                await automation.TapAsync(serial, 960, 540, ct);
                if (state.Kind == ArrivalKind.Complete) closedComplete = true;
            }
        }
        throw Unknown();
    }

    private Detail? ReadDetail(ScreenImage frame, CancellationToken ct)
    {
        if (!_page.HasTitle(frame, ct) ||
            (_next.Match(_matcher, frame, .93, new(1830, 463, 90, 157), ct) is null &&
             _info.Match(_matcher, frame, .93, new(1610, 10, 310, 113), ct) is null)) return null;
        var back = _back.Match(_matcher, frame, .90, new Roi(0, 0, 220, 130), ct);
        if (back is null) return null;
        var report = _report.Match(_matcher, frame, .93, ActionLabelArea, ct);
        var challenge = _challenge.Match(_matcher, frame, .93, ActionLabelArea, ct);
        var deliver = _deliver.Match(_matcher, frame, .95, ActionLabelArea, ct);
        var accept = _matcher.Match(frame.Bgra, frame.Width, frame.Height, _acceptLabel.Bgra,
            _acceptLabel.Width, _acceptLabel.Height, .93, ActionLabelArea, ct: ct);
        var obtain = _obtain.Match(_matcher, frame, .95, ActionLabelArea, ct);
        if ((obtain is null ? 0 : 1) + (accept is null ? 0 : 1) + (report is null ? 0 : 1) + (challenge is null ? 0 : 1) + (deliver is null ? 0 : 1) != 1) return null;
        var title = _catalog is null ? default : ReadTitle(frame, ct);
        if (obtain is not null)
            return title.Kind == RequestKind.UnitDelivery ? new(Control.Obtain, back.Region, obtain.Region, title, null) : null;
        if (deliver is not null)
            return new(Control.Deliver, back.Region, deliver.Region, title,
                title.Kind == RequestKind.Funding ? _funding!.ReadAmount(frame, ct) : null);
        if (report is not null) return new(Control.Report, back.Region, report.Region, title, null);
        return challenge is not null ? new(Control.Challenge, back.Region, challenge.Region, title, null) :
            new(Control.Accept, back.Region, accept!.Region, title, null);
    }

    // 同帧标题决定后续流程。调度资金跳过立绘；强化、交付机体和开发按角色查表。
    private Title ReadTitle(ScreenImage frame, CancellationToken ct)
    {
        var area = new Roi(1340, 225, 300, 80);
        if (_fundingTitle.Match(_matcher, frame, .96, area, ct) is { } funds) return new(RequestKind.Funding, funds.Region);
        if (_unitTitle.Match(_matcher, frame, .96, area, ct) is { } unit) return new(RequestKind.UnitDelivery, unit.Region);
        if (_sortieTitle.Match(_matcher, frame, .96, area, ct) is { } sortie) return new(RequestKind.Sortie, sortie.Region);
        if (_developmentTitle.Match(_matcher, frame, .96, area, ct) is { } dev) return new(RequestKind.Development, dev.Region);
        if (_strengthTitle.Match(_matcher, frame, .93, area, ct) is { } strength) return new(RequestKind.Strength, strength.Region);
        return default;
    }

    private static InvalidOperationException FundingUnknown() =>
        new(L.Text("无法核对调度资金的 CAPITAL 交付金额或确认按钮，已停止本轮。"));

    private Task TapAsync(string serial, Roi button, CancellationToken ct) =>
        automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
    private async Task<CharacterRequestPage.ListState> WaitForListAsync(string serial, CancellationToken ct) =>
        await WaitAsync(serial, frame => _page.ReadList(frame, ct), ct) ?? throw Unknown();
    private Task<T?> WaitAsync<T>(string serial, Func<ScreenImage, T?> observe, CancellationToken ct,
        int timeoutMs = GameTiming.RecognitionTimeoutMs) where T : class =>
        StableObservation.WaitAsync(async () => observe(await automation.CaptureScreenAsync(serial, ct)),
            (first, second) => first.Equals(second), timeoutMs, ct);
    private static InvalidOperationException Unknown() => new(L.Text("未能核对角色要求或报告后的页面，已停止本轮。"));
}

using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed record CharacterRequestResult(int Accepted, int Reported, IReadOnlyList<string> FailedRoles);

/// <summary>按本轮解锁栏位逐人承接。强化委托按角色、编号与进度执行，报告后再处理下一栏。</summary>
public sealed class CharacterRequestService(AutomationService automation, IGgeaLog log, OnnxTextRecognizer? ocr = null, DeliveryProtection? protection = null)
{
    private const string Root = "resources/templates/base/character_requests/";
    private static readonly Roi ActionLabelArea = new(1540, 800, 170, 110);
    private readonly CharacterRequestPage _page = new();
    private readonly TemplateMatcher _matcher = new();
    private readonly StrengthRequestCatalog? _catalog = ocr is null ? null : new(ocr);
    private readonly UnitUpgradeService? _upgrade = ocr is null ? null : new(automation, ocr);
    private readonly ScreenImage _acceptLabel = LoadAcceptLabel();
    private readonly TemplateImages _challenge = new(Root + "button_challenge.png");
    private readonly TemplateImages _report = new(Root + "button_report.png");
    private readonly TemplateImages _dialogFrame = new(Root + "dialog_com_frame.png");
    private readonly TemplateImages _skip = new(Root + "dialog_skip.png");
    private readonly TemplateImages _dialogAdvance = new(Root + "dialog_advance.png");
    private readonly TemplateImages _complete = new(Root + "title_report_complete.png");
    private readonly TemplateImages _reward = new(Root + "title_claim_result.png");
    private readonly TemplateImages _ok = new("resources/templates/dialog/button_ok.png");
    private readonly TemplateImages _next = new(Root + "button_next_character.png");
    private readonly TemplateImages _info = new(Root + "button_request_info.png");
    private readonly TemplateImages _deliver = new(Root + "button_deliver.png");
    private readonly TemplateImages _deliveryComplete = new(Root + "title_delivery_complete.png");
    private readonly TemplateImages _back = new("resources/templates/common/back.png");

    private enum Control { Accept, Challenge, Report, Deliver }
    private enum ArrivalKind { List, Dialogue, Detail, Complete, Reward }
    private sealed record Detail(Control Action, Roi Back, Roi Button);
    private sealed record Arrival(ArrivalKind Kind, Detail? Detail = null, Roi Button = default);
    private sealed record OfferScreen(Detail Detail, bool Strength, StrengthRequest? Request);

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
                    if (arrival.Kind == ArrivalKind.List) continue;
                    detail = arrival.Detail!;
                }

                if (detail.Action == Control.Deliver && _catalog is not null)
                {
                    var evidence = await WaitAsync(serial, frame =>
                    {
                        if (ReadDetail(frame, ct)?.Action != Control.Deliver) return null;
                        var offer = _catalog.ReadDelivery(frame, ct, out var isDelivery);
                        return Tuple.Create(isDelivery, offer);
                    }, ct) ?? throw Unknown();
                    if (evidence.Item2 is { } delivery)
                    {
                        await TapAsync(serial, detail.Button, ct);
                        var service = new UnitDeliveryService(automation, ocr!, protection ?? new(), delivery.MinimumLevel);
                        if (await service.SubmitAsync(serial, ct))
                        {
                            await FollowDialogueAsync(serial, reporting: true, ct, delivering: true);
                            var list = await WaitForListAsync(serial, ct);
                            if ((list.ActiveMask & (1 << slot)) != 0) throw Unknown();
                            reported++;
                            continue;
                        }
                        failures.Add(L.Format($"第 {slot + 1} 栏没有可安全交付的{delivery.Unit}"));
                        detail = await WaitAsync(serial, frame => ReadDetail(frame, ct) is { Action: Control.Deliver } d ? d : null, ct)
                            ?? throw Unknown();
                    }
                    else if (evidence.Item1)
                        failures.Add(L.Format($"第 {slot + 1} 栏交付委托未能与表格核对"));
                }
                else if (_catalog is not null)
                {
                    var offer = await WaitOfferAsync(serial, ct);
                    detail = offer.Detail;
                    if (offer.Strength && offer.Request is null)
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
                            await FollowDialogueAsync(serial, reporting: true, ct);
                            var list = await WaitForListAsync(serial, ct);
                            if ((list.ActiveMask & (1 << slot)) != 0) throw Unknown();
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

    private async Task<OfferScreen> WaitOfferAsync(string serial, CancellationToken ct) =>
        await WaitAsync(serial, frame =>
        {
            var detail = ReadDetail(frame, ct);
            if (detail is null || detail.Action == Control.Accept) return null;
            var request = _catalog!.Read(frame, ct, out var strength);
            return new OfferScreen(detail, strength, request);
        }, ct) ?? throw Unknown();

    // 报告结算遮罩后面仍可能看见上一句台词；先识别结算，确认新页面后才继续。
    private async Task<Arrival> FollowDialogueAsync(string serial, bool reporting, CancellationToken ct, bool delivering = false)
    {
        var deadline = Environment.TickCount64 + 2 * GameTiming.RecognitionTimeoutMs;
        var closedComplete = false;
        var closedReward = false;
        while (Environment.TickCount64 < deadline)
        {
            var budget = (int)Math.Min(GameTiming.RecognitionTimeoutMs, deadline - Environment.TickCount64);
            var state = await WaitAsync(serial, frame =>
            {
                if (reporting)
                {
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
                return !closedComplete && !closedReward &&
                    _dialogFrame.Match(_matcher, frame, .92, new Roi(245, 126, 185, 98), ct) is not null &&
                    (_dialogAdvance.Match(_matcher, frame, .93, new Roi(415, 729, 1230, 195), ct) is not null ||
                     _skip.Match(_matcher, frame, .96, new Roi(1686, 26, 153, 80), ct) is not null)
                    ? new Arrival(ArrivalKind.Dialogue) : null;
            }, ct, budget);
            if (state is null || Environment.TickCount64 >= deadline) break;
            if (state.Kind is ArrivalKind.List or ArrivalKind.Detail) return state;
            if (state.Kind == ArrivalKind.Reward)
            {
                await TapAsync(serial, state.Button, ct);
                closedReward = true;
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
        if ((accept is null ? 0 : 1) + (report is null ? 0 : 1) + (challenge is null ? 0 : 1) + (deliver is null ? 0 : 1) != 1) return null;
        if (deliver is not null) return new(Control.Deliver, back.Region, deliver.Region);
        if (report is not null) return new(Control.Report, back.Region, report.Region);
        return challenge is not null ? new(Control.Challenge, back.Region, challenge.Region) :
            new(Control.Accept, back.Region, accept!.Region);
    }

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

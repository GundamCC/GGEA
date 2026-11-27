using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>从角色要求列表逐栏进入详情。本轮可处理栏数只会缩小，详情翻页不会越过锁定栏位。</summary>
public sealed class CharacterRequestService(AutomationService automation, IGgeaLog log)
{
    private readonly CharacterRequestPage _page = new();
    private readonly TemplateMatcher _matcher = new();
    private const string Root = "resources/templates/base/character_requests/";
    private readonly TemplateImages _accept = new(Root + "button_accept.png");
    private readonly TemplateImages _arrow = new(Root + "button_next_character.png");
    private readonly TemplateImages _back = new("resources/templates/common/back.png");

    private sealed record ListState(int Count);
    private sealed record Detail(bool CanAccept, Roi Back, Roi Button);
    private sealed record Arrival(bool OnList, Detail? Detail);

    /// <summary>调用方已进入列表。已锁定的角色没有点击路径，每次返回列表都重新确认剩余栏位。</summary>
    public Task<int> AcceptFromListAsync(string serial, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var state = await WaitForListAsync(serial, ct);
            var limit = state.Count;
            log.Info(L.Format($"角色要求列表已确认，本轮最多处理 {limit} 名角色。"));
            var submitted = 0;
            for (var slot = 0; slot < limit; slot++)
            {
                if (slot > 0)
                {
                    state = await WaitForListAsync(serial, ct);
                    limit = Math.Min(limit, state.Count);
                    if (slot >= limit) break;
                }

                await automation.TapAsync(serial, 427 + 535 * slot, 410, ct);
                var detail = await WaitAsync(serial, frame => ReadDetail(frame, ct), ct)
                    ?? throw new InvalidOperationException(L.Text("未能确认角色要求详情，已停止本轮承接。"));
                if (detail.CanAccept)
                {
                    await automation.TapAsync(serial, detail.Button.CenterX, detail.Button.CenterY, ct);
                    // 点击后只观察，不重复发送承接，也不确认交付请求。
                    var arrival = await WaitAsync(serial, frame =>
                    {
                        if (_page.ReadSlotCount(frame, ct) is not null) return new Arrival(true, null);
                        var next = ReadDetail(frame, ct);
                        return next is { CanAccept: false } ? new Arrival(false, next) : null;
                    }, ct) ?? throw new InvalidOperationException(L.Text("已点击承接，未能确认后续页面，已停止本轮承接。"));
                    submitted++;
                    if (arrival.OnList) continue;
                    detail = arrival.Detail!;
                }

                await automation.TapAsync(serial, detail.Back.CenterX, detail.Back.CenterY, ct);
            }
            await WaitForListAsync(serial, ct);
            return submitted;
        }, ct);

    private Detail? ReadDetail(ScreenImage frame, CancellationToken ct)
    {
        if (!_page.HasTitle(frame, ct) ||
            _arrow.Match(_matcher, frame, .93, new Roi(1830, 463, 90, 157), ct) is null) return null;
        var back = _back.Match(_matcher, frame, .90, new Roi(0, 0, 220, 130), ct);
        if (back is null) return null;
        var accept = _accept.Match(_matcher, frame, .93, new Roi(1380, 740, 465, 208), ct);
        return new Detail(accept is not null, back.Region, accept?.Region ?? default);
    }

    private async Task<ListState> WaitForListAsync(string serial, CancellationToken ct) =>
        await WaitAsync(serial, frame => _page.ReadSlotCount(frame, ct) is { } count ? new ListState(count) : null, ct)
        ?? throw new InvalidOperationException(L.Text("未能确认角色要求列表及栏位状态，已停止本轮承接。"));

    private Task<T?> WaitAsync<T>(string serial, Func<ScreenImage, T?> observe, CancellationToken ct) where T : class =>
        StableObservation.WaitAsync(async () => observe(await automation.CaptureScreenAsync(serial, ct)),
            (first, second) => first.Equals(second), GameTiming.RecognitionTimeoutMs, ct);
}

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
    private static readonly Roi ActionLabelArea = new(1540, 800, 170, 110);
    private readonly ScreenImage _acceptLabel = LoadAcceptLabel();
    private readonly TemplateImages _challenge = new(Root + "button_challenge.png");
    private readonly TemplateImages _dialogFrame = new(Root + "dialog_com_frame.png");
    private readonly TemplateImages _dialogAdvance = new(Root + "dialog_advance.png");
    private readonly TemplateImages _arrow = new(Root + "button_next_character.png");
    private readonly TemplateImages _back = new("resources/templates/common/back.png");

    private sealed record ListState(int Count);
    private enum ArrivalKind { List, Dialogue, Challenge }
    private sealed record Detail(bool Challenge, Roi Back, Roi Button);
    private sealed record Arrival(ArrivalKind Kind, Detail? Detail = null);

    // 两种按钮共用外框，仅用原「承接」素材的中心文字与「挑战」比较。
    private static ScreenImage LoadAcceptLabel()
    {
        var source = new TemplateImages(Root + "button_accept.png").Primary;
        const int width = 78, height = 41;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(source.Bgra, ((39 + y) * source.Width + 142) * 4, pixels, y * width * 4, width * 4);
        return new ScreenImage { Width = width, Height = height, Bgra = pixels };
    }

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
                if (!detail.Challenge)
                {
                    await automation.TapAsync(serial, detail.Button.CenterX, detail.Button.CenterY, ct);
                    var arrival = await FollowAcceptanceAsync(serial, ct);
                    submitted++;
                    if (arrival.Kind == ArrivalKind.List) continue;
                    detail = arrival.Detail!;
                }

                await automation.TapAsync(serial, detail.Back.CenterX, detail.Back.CenterY, ct);
            }
            await WaitForListAsync(serial, ct);
            return submitted;
        }, ct);

    /// <summary>所有委托共用承接对话。每次只推进一句，下一次动作由新截图授权。</summary>
    private async Task<Arrival> FollowAcceptanceAsync(string serial, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 2 * GameTiming.RecognitionTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var budget = (int)Math.Min(GameTiming.RecognitionTimeoutMs, deadline - Environment.TickCount64);
            var state = await WaitAsync(serial, frame => ReadArrival(frame, ct), ct, budget);
            if (state is null) break;
            if (state.Kind != ArrivalKind.Dialogue) return state;
            if (Environment.TickCount64 >= deadline) break;
            await automation.TapAsync(serial, 960, 540, ct);
        }
        throw new InvalidOperationException(L.Text("已点击承接，未能确认后续页面，已停止本轮承接。"));
    }

    private Arrival? ReadArrival(ScreenImage frame, CancellationToken ct)
    {
        if (_page.HasTitle(frame, ct))
        {
            if (_page.ReadSlotCount(frame, ct) is not null) return new Arrival(ArrivalKind.List);
            var detail = ReadDetail(frame, ct);
            return detail is { Challenge: true } ? new Arrival(ArrivalKind.Challenge, detail) : null;
        }
        if (_dialogFrame.Match(_matcher, frame, .92, new Roi(245, 126, 185, 98), ct) is null ||
            _dialogAdvance.Match(_matcher, frame, .93, new Roi(415, 729, 1230, 195), ct) is null) return null;
        return new Arrival(ArrivalKind.Dialogue);
    }

    private Detail? ReadDetail(ScreenImage frame, CancellationToken ct)
    {
        if (!_page.HasTitle(frame, ct) ||
            _arrow.Match(_matcher, frame, .93, new Roi(1830, 463, 90, 157), ct) is null) return null;
        var back = _back.Match(_matcher, frame, .90, new Roi(0, 0, 220, 130), ct);
        if (back is null) return null;
        var challenge = _challenge.Match(_matcher, frame, .93, ActionLabelArea, ct);
        var accept = _matcher.Match(frame.Bgra, frame.Width, frame.Height, _acceptLabel.Bgra,
            _acceptLabel.Width, _acceptLabel.Height, .93, ActionLabelArea, ct: ct);
        if ((challenge is null) == (accept is null)) return null;
        return new Detail(challenge is not null, back.Region, (challenge ?? accept!).Region);
    }

    private async Task<ListState> WaitForListAsync(string serial, CancellationToken ct) =>
        await WaitAsync(serial, frame => _page.ReadSlotCount(frame, ct) is { } count ? new ListState(count) : null, ct)
        ?? throw new InvalidOperationException(L.Text("未能确认角色要求列表及栏位状态，已停止本轮承接。"));

    private Task<T?> WaitAsync<T>(string serial, Func<ScreenImage, T?> observe, CancellationToken ct,
        int timeoutMs = GameTiming.RecognitionTimeoutMs) where T : class =>
        StableObservation.WaitAsync(async () => observe(await automation.CaptureScreenAsync(serial, ct)),
            (first, second) => first.Equals(second), timeoutMs, ct);
}

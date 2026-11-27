using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>只处理交付数为一的弹窗。选择可撤销，最终交付只发送一次。</summary>
internal sealed class UnitDeliveryService(AutomationService automation, OnnxTextRecognizer ocr, DeliveryProtection protection, int minimumLevel)
{
    private readonly DeliveryPage _page = new(ocr, protection, minimumLevel);

    internal async Task<bool> SubmitAsync(string serial, CancellationToken ct)
    {
        var view = await ObserveAsync(serial, ct);
        // 已满时点击别的单位不会切换。先找到原标记并取消，再从底部重新按当前策略选择。
        if (view.Count == 1 && view.Badge is null) view = await ScrollToBottomAsync(serial, view, ct);
        while (view.Count == 1 && view.Badge is null && !view.AtTop)
            view = await ScrollAsync(serial, view, towardBottom: false, ct);
        if (view.Count == 1)
        {
            if (view.Badge is not { } old) return await CancelAsync(serial, view, ct);
            await automation.TapAsync(serial, old.CenterX, old.CenterY, ct);
            view = await ObserveAsync(serial, ct, expectedCount: 0);
        }

        view = await ScrollToBottomAsync(serial, view, ct);
        while (view.Candidate is null && !view.AtTop)
            view = await ScrollAsync(serial, view, towardBottom: false, ct);
        if (view.Candidate is not { } card) return await CancelAsync(serial, view, ct);

        await automation.TapAsync(serial, card.X, card.ArtY + 37, ct);
        view = await ObserveAsync(serial, ct, expectedCount: 1);
        if (view.Badge is not { } badge || Math.Abs(badge.X - (card.X + 25)) > 5 ||
            Math.Abs(badge.Y - (card.ArtY - 32)) > 5 || view.Submit is not { } submit)
            return await CancelAsync(serial, view, ct);
        await automation.TapAsync(serial, submit.CenterX, submit.CenterY, ct);
        return true;
    }

    private async Task<DeliveryPage.View> ScrollToBottomAsync(string serial, DeliveryPage.View view, CancellationToken ct)
    {
        while (!view.AtBottom) view = await ScrollAsync(serial, view, towardBottom: true, ct);
        return view;
    }

    private async Task<DeliveryPage.View> ScrollAsync(string serial, DeliveryPage.View before, bool towardBottom, CancellationToken ct)
    {
        // 左侧避开锁定开关与数量浮层。向上查找时保留重叠行，以读取之前被裁掉的星级和等级。
        if (towardBottom) await automation.SwipeAsync(serial, 630, 757, 630, 516, 280, ct);
        else await automation.DragAsync(serial, 630, 556, 630, 646, 480, ct);
        return await ObserveAsync(serial, ct, before.Count, v => towardBottom
            ? v.ThumbTop > before.ThumbTop || v.AtBottom : v.ThumbTop < before.ThumbTop || v.AtTop);
    }

    private async Task<DeliveryPage.View> ObserveAsync(string serial, CancellationToken ct, int? expectedCount = null,
        Func<DeliveryPage.View, bool>? accept = null) =>
        await StableObservation.WaitAsync(async () =>
        {
            var view = _page.Read(await automation.CaptureScreenAsync(serial, ct), ct);
            return view is not null && (!expectedCount.HasValue || view.Count == expectedCount) &&
                (accept is null || accept(view)) ? view : null;
        }, (a, b) => a == b, GameTiming.RecognitionTimeoutMs, ct)
        ?? throw new InvalidOperationException(L.Text("无法确认交付列表或选中状态，已停止本轮。"));

    private async Task<bool> CancelAsync(string serial, DeliveryPage.View view, CancellationToken ct)
    {
        await automation.TapAsync(serial, view.Cancel.CenterX, view.Cancel.CenterY, ct);
        return false;
    }
}

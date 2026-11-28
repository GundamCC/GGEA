using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.App.Tasks;

/// <summary>从挑战直达的作品关卡开始，按委托剩余量略过。消费和奖励结算与每日 AP 共用。</summary>
public sealed class SortieRequestService(AutomationService automation, StageService stages, StageSkipService skip,
    UiLayout layout, IReadOnlyList<HardStageEntry> works, OnnxTextRecognizer ocr, IGgeaLog log)
{
    private readonly StageSkipBudget _budget = new(ocr, automation.Resources);
    private readonly TemplateImages _back = new("resources/templates/stage/history_back.png");
    private readonly TemplateMatcher _matcher = new();

    public int CaptureApLimit { get; init; } = AppConfig.DefaultCaptureApLimit;

    internal Task<int> CaptureAsync(string serial, SortieOrder order, long refresh, CancellationToken ct) =>
        RunAsync(serial, order, ct, new CaptureRequestBudget(refresh, CaptureApLimit));

    internal async Task<int> RunAsync(string serial, SortieOrder order, CancellationToken ct, CaptureRequestBudget? capture = null)
    {
        var key = int.Parse(order.Offer.SeriesId, System.Globalization.CultureInfo.InvariantCulture);
        var file = FormattableString.Invariant($"logo_l_series_{key:D4}.webp");
        var work = works.SingleOrDefault(w => w.Screenshot == file) ?? throw Unknown();
        var logo = ConfigService.ResolvePath("resources/templates/logo/" + file);
        var needed = order.Offer.Amount - order.Progress;
        stages.Context.Invalidate();
        try
        {
            await ConfirmWorkAsync();
            int done = capture is null
                ? await RunSortieStagesAsync(serial, work, needed, ct)
                : await CaptureStagesAsync(serial, work, needed, capture, ct);
            var back = await ConfirmWorkAsync();
            await automation.TapAsync(serial, back.CenterX, back.CenterY, ct);
            if (capture is null) log.Info(L.Format($"请求出击本轮完成 {done} 次。"));
            return done;
        }
        finally { stages.Context.Invalidate(); }

        async Task<Roi> ConfirmWorkAsync()
        {
            var result = await StableObservation.WaitAsync(async () =>
            {
                var f = await automation.CaptureScreenAsync(serial, ct);
                return _budget.OnStage(f, ct) && MatchesWork(f, order.Offer.SeriesId, logo, ct)
                    ? _back.Match(_matcher, f, .90, new(0, 0, 220, 130), ct) : null;
            }, (a, b) => a.Region == b.Region, GameTiming.RecognitionTimeoutMs, ct);
            return result?.Region ?? throw Unknown();
        }
    }

    private async Task<int> RunSortieStagesAsync(string serial, HardStageEntry work, int needed, CancellationToken ct)
    {
        int done = 0;
        var node = await stages.ReadRequestNodeAsync(serial, work, ct);
        // 已经解锁的较低编号 HARD 可以分摊超过单关日限的委托。
        while (node is { Kind: WorkStageKind.Hard, Number: int hard, Position: not null } && done < needed)
        {
            stages.Context.SetWorkStageList(serial, StageMapMode.Main, work.Id, hard);
            if (node.HasPassed)
            {
                var batch = await skip.SkipWithinBudgetAsync(serial, layout, _budget, needed - done, true, ct);
                if (!batch.Success && !batch.IsUnavailable) throw new InvalidOperationException(batch.Message);
                done += batch.ExecutedCount;
                if (done == needed || batch.IsApInsufficient || batch.RemainingAp == 0) break;
            }
            if (hard == 1)
            {
                // 所有 HARD 次数用完时，选中紧邻的普通关卡并核对其编号和已通关状态。
                var start = StageSlotPlanner.GetViewportStart(work.NormalCount, work.HardCount, hard, work.SideStories);
                int target = StageSlotPlanner.GetOverallIndex(work.NormalCount, 1, work.SideStories) - 1;
                if (!StageSlotPlanner.TryGetVisibleSlot(start, target, out var index)) break;
                var slot = layout.Require($"stageSlot{index}", "关卡布局缺少普通关卡槽位");
                await automation.TapAsync(serial, slot.CenterX, slot.CenterY, ct);
                await stages.ConfirmBeforeHardAsync(serial, work, ct);
                node = await stages.ReadRequestNodeAsync(serial, work, ct);
                if (node?.Position?.OverallIndex != target) throw Unknown();
                break;
            }
            var selected = await stages.SelectHardStageAsync(serial, layout, works, work.Id, hard - 1, ct);
            if (!selected.Success) throw new InvalidOperationException(selected.Message);
            node = await stages.ReadRequestNodeAsync(serial, work, ct);
            if (node?.Kind != WorkStageKind.Hard || node.Number != hard - 1) throw Unknown();
        }
        if (done < needed && node is { HasPassed: true, Kind: WorkStageKind.Normal or WorkStageKind.Boss or WorkStageKind.SideStory })
        {
            var batch = await skip.SkipWithinBudgetAsync(serial, layout, _budget, needed - done, false, ct);
            if (!batch.Success && !batch.IsUnavailable) throw new InvalidOperationException(batch.Message);
            done += batch.ExecutedCount;
        }
        return done;
    }

    // 当前关卡次数用完或没有夺取奖励时才向左移动。两种难度共用同一预算和奖励计数。
    private async Task<int> CaptureStagesAsync(string serial, HardStageEntry work, int needed,
        CaptureRequestBudget capture, CancellationToken ct)
    {
        var node = await stages.ReadRequestNodeAsync(serial, work, ct);
        using var numbers = new OnnxStageNumberRecognizer(ocr);
        int units = 0;
        while (units < needed && capture.RemainingAp > 0)
        {
            if (node?.Position is null) throw UnconfirmedNeighbor();
            if (node.HasPassed)
            {
                var reward = await StableObservation.WaitAsync(async () =>
                {
                    var frame = await automation.CaptureScreenAsync(serial, ct);
                    return _budget.OnStage(frame, ct) ? new StageReward(capture.OffersCapture(frame, ct)) : null;
                }, (a, b) => a == b, GameTiming.RecognitionTimeoutMs, ct) ?? throw Unknown();
                if (reward.HasCapture)
                {
                    var batch = await skip.SkipWithinBudgetAsync(serial, layout, _budget, needed - units,
                        node.Kind == WorkStageKind.Hard, ct, capture);
                    if (batch.Success)
                    {
                        var gained = batch.CapturedUnits ?? throw Unknown();
                        units += gained;
                        log.Info(L.Format($"本批略过 {batch.ExecutedCount} 次，奖励页确认夺取 {gained} 台。"));
                        if (units >= needed || capture.RemainingAp == 0 || batch.RemainingAp == 0) break;
                        // 结算转场后须确认仍选中同一个节点，才能继续消费或向左换关。
                        node = await ConfirmNodeAsync(serial, work, node.Position.OverallIndex, ct);
                        continue;
                    }
                    if (!batch.IsUnavailable) throw new InvalidOperationException(batch.Message);
                }
            }
            if (node.Position.OverallIndex == 1) break;
            node = await SelectPreviousAsync(serial, work, node, numbers, ct);
        }
        return units;
    }

    private async Task<StageNodeRecognition> SelectPreviousAsync(string serial, HardStageEntry work,
        StageNodeRecognition current, OnnxStageNumberRecognizer numbers, CancellationToken ct)
    {
        int target = current.Position!.OverallIndex - 1;
        // 轨道可以偏移。以双帧确认的光圈为锚点，只有实际读到左邻编号才点击。
        if (current.CenterX < 220)
        {
            await automation.SwipeAsync(serial, 180, 540, 585, 540, 400, ct);
            current = await ConfirmNodeAsync(serial, work, target + 1, ct, minCenterX: 210);
        }
        int x = current.CenterX - 190;
        int left = Math.Max(0, x - 65);
        int expected = target <= work.NormalCount ? target : target <= work.NormalCount + work.SideStories
            ? target - work.NormalCount : target - work.NormalCount - work.SideStories;
        var digits = await StableObservation.WaitAsync(async () =>
        {
            var frame = await automation.CaptureScreenAsync(serial, ct);
            if (!_budget.OnStage(frame, ct)) return null;
            var number = numbers.Recognize(frame, new(left, 620, x + 65 - left, 170), x, ct);
            return number.Number == expected && number.Region.HasValue ? number : null;
        }, (a, b) => Math.Abs(a.Region!.Value.CenterX - b.Region!.Value.CenterX) <= 8 &&
            Math.Abs(a.Region.Value.CenterY - b.Region.Value.CenterY) <= 12, GameTiming.RecognitionTimeoutMs, ct)
            ?? throw UnconfirmedNeighbor();
        await automation.TapAsync(serial, digits.Region!.Value.CenterX, digits.Region.Value.CenterY, ct);
        return await ConfirmNodeAsync(serial, work, target, ct);
    }

    private async Task<StageNodeRecognition> ConfirmNodeAsync(string serial, HardStageEntry work, int expected, CancellationToken ct, int minCenterX = 0)
    {
        var end = Environment.TickCount64 + GameTiming.RecognitionTimeoutMs;
        do
        {
            var selected = await stages.ReadRequestNodeAsync(serial, work, ct);
            if (selected?.Position?.OverallIndex == expected && selected.CenterX >= minCenterX) return selected;
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        } while (Environment.TickCount64 < end);
        throw UnconfirmedNeighbor();
    }

    private static InvalidOperationException UnconfirmedNeighbor() => new(L.Text("未能确认夺取委托的相邻关卡。"));
    private sealed record StageReward(bool HasCapture);

    // 选关页的 Logo 直接叠在动态作品插图上，只比对完全不透明的原图笔画。
    internal static bool MatchesWork(ScreenImage frame, string series, string logo, CancellationToken ct)
    {
        using var raw = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var region = raw[new Rect(0, 68, 540, 346)];
        using var gray = new Mat();
        Cv2.CvtColor(region, gray, ColorConversionCodes.BGRA2GRAY);
        using var shape = new Mat();
        using var mask = new Mat();
        using var texture = new Mat();
        using var scores = new Mat();
        // NT 选关页使用原生小 Logo 的字形，和作品列表的大 Logo 有差异。
        if (series == "1440")
        {
            var path = ConfigService.ResolvePath(DevelopmentMaps.Get(series).Logo);
            if (File.Exists(path))
            {
                using var native = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Unchanged);
                if (MatchAtScale(native, 1.3125)) return true;
            }
        }
        using var art = Cv2.ImDecode(File.ReadAllBytes(logo), ImreadModes.Unchanged);
        if (art.Empty()) return false;
        int maxPercent = Math.Min(108, gray.Width * 100 / art.Width);
        for (int percent = 45; percent <= maxPercent; percent++)
            if (MatchAtScale(art, percent / 100d)) return true;
        return false;

        bool MatchAtScale(Mat template, double scale)
        {
            ct.ThrowIfCancellationRequested();
            if (template.Channels() != 4) return false;
            Cv2.Resize(template, shape, default, scale, scale, InterpolationFlags.Area);
            if (shape.Width > gray.Width || shape.Height > gray.Height) return false;
            Cv2.ExtractChannel(shape, mask, 3);
            Cv2.Threshold(mask, mask, 254, 255, ThresholdTypes.Binary);
            if (Cv2.CountNonZero(mask) < 200) return false;
            Cv2.CvtColor(shape, texture, ColorConversionCodes.BGRA2GRAY);
            Cv2.MatchTemplate(gray, texture, scores, TemplateMatchModes.CCoeffNormed, mask);
            Cv2.PatchNaNs(scores, -1);
            Cv2.Threshold(scores, scores, 1.001, 0, ThresholdTypes.TozeroInv);
            Cv2.MinMaxLoc(scores, out _, out var score, out _, out _);
            return score is >= .81 and <= 1.001;
        }
    }

    private static InvalidOperationException Unknown() => new(L.Text("无法核对请求出击的作品或关卡。"));
}

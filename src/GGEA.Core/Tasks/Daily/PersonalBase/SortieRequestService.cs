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

    internal async Task<int> RunAsync(string serial, SortieOrder order, CancellationToken ct, CaptureRequestBudget? capture = null)
    {
        var key = int.Parse(order.Offer.SeriesId, System.Globalization.CultureInfo.InvariantCulture);
        var file = FormattableString.Invariant($"logo_l_series_{key:D4}.webp");
        var work = works.SingleOrDefault(w => w.Screenshot == file) ?? throw Unknown();
        var logo = ConfigService.ResolvePath("resources/templates/logo/" + file);
        var needed = order.Offer.Amount - order.Progress;
        int done = 0;
        stages.Context.Invalidate();
        try
        {
            await ConfirmWorkAsync();
            var node = await stages.ReadRequestNodeAsync(serial, work, ct);
            // 已经解锁的较低编号 HARD 可以分摊超过单关日限的委托。
            while (node is { Kind: WorkStageKind.Hard, Number: int hard, Position: not null } && done < needed)
            {
                stages.Context.SetWorkStageList(serial, StageMapMode.Main, work.Id, hard);
                if (capture is not null && hard > 3)
                {
                    await SelectHardAsync(3);
                    continue;
                }
                if (node.HasPassed && (capture is null || await HasCaptureAsync()))
                {
                    var batch = await skip.SkipWithinBudgetAsync(serial, layout, _budget, needed - done, true, ct, capture);
                    if (!batch.Success && !batch.IsUnavailable) throw new InvalidOperationException(batch.Message);
                    // 夺取可能一台也不掉，只有结算卡片计入本轮进度。单关耗尽后再换关。
                    if (batch.Success && capture is not null)
                    {
                        var units = batch.CapturedUnits ?? throw Unknown();
                        done += units;
                        log.Info(L.Format($"本批略过 {batch.ExecutedCount} 次，奖励页确认夺取 {units} 台。"));
                    }
                    else done += batch.ExecutedCount;
                    if (done >= needed || batch.IsApInsufficient || batch.RemainingAp == 0 || capture?.RemainingAp == 0) break;
                    if (capture is not null && batch.Success) continue;
                }
                if (hard == 1)
                {
                    if (capture is not null) break;
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
                await SelectHardAsync(hard - 1);
            }
            if (capture is null && done < needed && node is { HasPassed: true, Kind: WorkStageKind.Normal or WorkStageKind.Boss or WorkStageKind.SideStory })
            {
                var batch = await skip.SkipWithinBudgetAsync(serial, layout, _budget, needed - done, false, ct);
                if (!batch.Success && !batch.IsUnavailable) throw new InvalidOperationException(batch.Message);
                done += batch.ExecutedCount;
            }
            var back = await ConfirmWorkAsync();
            await automation.TapAsync(serial, back.CenterX, back.CenterY, ct);
            if (capture is null) log.Info(L.Format($"请求出击本轮完成 {done} 次。"));
            return done;

            async Task SelectHardAsync(int number)
            {
                var selected = await stages.SelectHardStageAsync(serial, layout, works, work.Id, number, ct);
                if (!selected.Success) throw new InvalidOperationException(selected.Message);
                node = await stages.ReadRequestNodeAsync(serial, work, ct);
                if (node?.Kind != WorkStageKind.Hard || node.Number != number) throw Unknown();
            }
        }
        finally { stages.Context.Invalidate(); }

        async Task<bool> HasCaptureAsync()
        {
            var seen = await StableObservation.WaitAsync(async () =>
            {
                var frame = await automation.CaptureScreenAsync(serial, ct);
                return _budget.OnStage(frame, ct) ? new StageReward(capture!.OffersCapture(frame, ct)) : null;
            }, (a, b) => a == b, GameTiming.RecognitionTimeoutMs, ct);
            return seen?.HasCapture ?? throw Unknown();
        }

        async Task<Roi> ConfirmWorkAsync()
        {
            var result = await StableObservation.WaitAsync(async () =>
            {
                var f = await automation.CaptureScreenAsync(serial, ct);
                return _budget.OnStage(f, ct) && MatchesWork(f, logo, ct)
                    ? _back.Match(_matcher, f, .90, new(0, 0, 220, 130), ct) : null;
            }, (a, b) => a.Region == b.Region, GameTiming.RecognitionTimeoutMs, ct);
            return result?.Region ?? throw Unknown();
        }
    }

    private sealed record StageReward(bool HasCapture);

    // 选关页的 Logo 直接叠在动态作品插图上，只比对完全不透明的原图笔画。
    internal static bool MatchesWork(ScreenImage frame, string logo, CancellationToken ct)
    {
        using var raw = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Bgra);
        using var region = raw[new Rect(0, 68, 540, 346)];
        using var gray = new Mat();
        Cv2.CvtColor(region, gray, ColorConversionCodes.BGRA2GRAY);
        using var art = Cv2.ImDecode(File.ReadAllBytes(logo), ImreadModes.Unchanged);
        if (art.Channels() != 4) return false;
        using var shape = new Mat();
        using var mask = new Mat();
        using var texture = new Mat();
        using var scores = new Mat();
        for (int percent = 45; percent <= 108; percent++)
        {
            ct.ThrowIfCancellationRequested();
            Cv2.Resize(art, shape, default, percent / 100d, percent / 100d, InterpolationFlags.Area);
            if (shape.Width > gray.Width || shape.Height > gray.Height) break;
            Cv2.ExtractChannel(shape, mask, 3);
            Cv2.Threshold(mask, mask, 254, 255, ThresholdTypes.Binary);
            if (Cv2.CountNonZero(mask) < 200) continue;
            Cv2.CvtColor(shape, texture, ColorConversionCodes.BGRA2GRAY);
            Cv2.MatchTemplate(gray, texture, scores, TemplateMatchModes.CCoeffNormed, mask);
            Cv2.PatchNaNs(scores, -1);
            Cv2.MinMaxLoc(scores, out _, out var score, out _, out _);
            if (double.IsFinite(score) && score is >= .81 and <= 1.001) return true;
        }
        return false;
    }

    private static InvalidOperationException Unknown() => new(L.Text("无法核对请求出击的作品或关卡。"));
}

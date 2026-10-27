using GGEA.App.Localization;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>强敌略过编排：素材选单位、详情复核、OCR 难度、按钮复核，最后复用公共略过流程。</summary>
public sealed class FierceEnemySkipService(AutomationService automation, IGgeaLog log,
    OnnxTextRecognizer ocr, Func<Func<ScreenImage, CancellationToken, bool>, StageSkipService> createSkip)
{
    public const string SelectStageTemplate = FierceEnemyNavigationService.SelectStageTemplate;
    public const string SkipTemplate = "resources/templates/stage/event/button_skip.png";
    public const string DarkSkipTemplate = "resources/templates/stage/event/button_skip_disabled.png";
    private static readonly Roi DetailPortrait = new(0, 110, 970, 700);

    /// <summary>从主界面执行一次选定单位与难度；零次正常结束，未知次数、单位或按钮均停止。</summary>
    public async Task<StageSkipResult> ExecuteAsync(string serial, UiLayout mainLayout, UiLayout stageLayout,
        FierceEnemySkipSelection selection, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(selection.Difficulty)) throw new ArgumentOutOfRangeException(nameof(selection));
        // “最新”不创建素材识别器，覆盖导航、详情及返回确认的整个生命周期。
        using var unit = selection.Unit.IsLatest ? null : await Task.Run(() => new FierceEnemyUnitRecognizer(selection.Unit.TemplatePath), ct);
        var entry = new FierceEnemyAssaultEntryService(automation, log, new FierceEnemyAssaultSkipCountRecognizer(ocr));
        entry.Validate();
        var open = await new FierceEnemyAssaultService(automation, log).OpenFromHomeAsync(serial, mainLayout, ct: ct);
        if (!open.Success) return StageSkipResult.Fail(open.Message);
        var count = await entry.EnterAndReadAsync(serial, ct);
        if (!count.CanSkip) return StageSkipResult.Fail(count.Message, isUnavailable: count.Remaining == 0);
        if (!await NavigateAsync(serial, unit, ct))
            return StageSkipResult.Fail(L.Format($"未确认 {selection.Unit.Name} 的关卡详情，已停止强敌略过。"));
        // 进入详情后再核对共享次数，导航前读数不能作为消费资源的最终依据。
        count = await entry.ReadCountAsync(serial, ct);
        if (!count.CanSkip) return StageSkipResult.Fail(count.Message, isUnavailable: count.Remaining == 0);
        return await SkipDetailAsync(serial, stageLayout, selection, unit, ct);
    }

    /// <summary>检查当前页，再向下浏览一次、反向向上查找一次；每次滚动后重新截图识别，不循环盲滑。</summary>
    internal async Task<StageSkipResult> SkipDetailAsync(string serial, UiLayout stageLayout,
        FierceEnemySkipSelection selection, FierceEnemyUnitRecognizer? unit, CancellationToken ct)
    {
        var difficulty = new FierceEnemyDifficultyRecognizer(ocr);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var screen = await automation.CaptureScreenAsync(serial, ct);
            if (unit is not null && await Task.Run(() => unit.Find(screen, DetailPortrait, ct), ct) is null)
                return StageSkipResult.Fail(L.Text("详情单位复核失败，未点击略过。"));
            var label = await Task.Run(() => difficulty.Find(screen, selection.Difficulty, ct), ct);
            var region = label is { } found ? FierceEnemyDifficultyRecognizer.GetSkipRegion(found) : null;
            if (region is not null)
            {
                // 暗态先判定；次数耗尽或尚未通关属于当前任务不可用，不阻断后续每日任务。
                var state = await FindSkipButtonAsync(screen, region.Value, ct);
                if (state.IsUnavailable)
                    return StageSkipResult.Fail(L.Format($"{selection.Unit.Name} / {selection.Difficulty} 略过按钮变暗，不可略过，继续后续任务。"), isUnavailable: true);
                var button = state.Button;
                if (button is not null)
                {
                    var layout = WithSkipAnchor(stageLayout, button);
                    log.Info($"强敌略过：{selection.Unit.Name} / {selection.Difficulty}，已确认关卡、难度及略过按钮。");
                    return await createSkip((frame, token) => IsDetail(frame, unit, token) &&
                        new FierceEnemyAssaultSkipCountRecognizer(ocr).Recognize(frame, token).Remaining is not null)
                        .SkipCurrentAsync(serial, layout, ct);
                }
            }
            if (attempt == 0)
            {
                log.Info("目标难度或完整略过按钮尚未出现，向下浏览一次后重新识别。");
                await automation.SwipeAsync(serial, 1500, 870, 1500, 340, 650, ct);
                await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
            }
            else if (attempt == 1)
            {
                log.Info("向下浏览后仍未确认目标，向下拖动一次以查找上方难度。");
                await automation.SwipeAsync(serial, 1500, 340, 1500, 870, 650, ct);
                await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
            }
        }
        return StageSkipResult.Fail(L.Format($"未确认 {selection.Difficulty} 的可用略过按钮，已停止强敌略过。"));
    }

    /// <summary>同帧优先判断暗态，防止未通关或零次时点击不可用按钮。</summary>
    public async Task<(TemplateMatchResult? Button, bool IsUnavailable)> FindSkipButtonAsync(
        ScreenImage screen, Roi region, CancellationToken ct = default)
    {
        var options = new WaitOptions { Roi = region, Threshold = .92 };
        var dark = await automation.MatchImageAsync(screen, ConfigService.ResolvePath(DarkSkipTemplate), options, ct);
        if (dark is not null) return (null, true);
        return (await automation.MatchImageAsync(screen, ConfigService.ResolvePath(SkipTemplate), options, ct), false);
    }

    /// <summary>两种任务共用单位定位，防止历史列表与详情判定发生分叉。</summary>
    internal Task<bool> NavigateAsync(string serial, FierceEnemyUnitRecognizer? unit, CancellationToken ct)
        => new FierceEnemyNavigationService(automation, log, ocr).NavigateAsync(serial, unit, ct);

    /// <summary>略过返回确认沿用同一详情证据。</summary>
    internal bool IsDetail(ScreenImage screen, FierceEnemyUnitRecognizer? unit, CancellationToken ct)
        => new FierceEnemyNavigationService(automation, log, ocr).IsDetail(screen, unit, ct);

    /// <summary>仅替换当前调用的按钮锚点，不改动 AP 任务共享布局。</summary>
    public static UiLayout WithSkipAnchor(UiLayout original, TemplateMatchResult button)
        => new() { ScreenWidth = original.ScreenWidth, ScreenHeight = original.ScreenHeight,
            Anchors = original.Anchors.Where(a => a.Name != "stageSkip").Append(new UiAnchor {
                Name = "stageSkip", X = button.X, Y = button.Y, Width = button.Width, Height = button.Height }).ToList() };
}

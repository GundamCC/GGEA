using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>支持故事事件与 BOSS 战斗列表启动。每次结果返回后丢弃上一轮卡片位置。</summary>
public sealed class LimitedEventBossService(AutomationService automation, OnnxTextRecognizer ocr, IGgeaLog log, string serial)
{
    private const string Event = "stage/event/limited_time_event/";
    private const double BossTitleThreshold = 0.90;
    private const string Max = "develop/button/set_max_button";
    private readonly FierceEnemyDifficultyRecognizer labels = new(ocr, new(1035, 128, 125, 776));
    private static readonly Roi Title = new(190, 0, 375, 96);
    private static readonly Roi Cancel = new(550, 762, 435, 161);
    private static readonly Roi Execute = new(945, 762, 450, 161);
    private static readonly Roi RemainingTickets = new(1280, 607, 165, 43);
    private static readonly Roi Quantity = new(1210, 385, 200, 160);

    public async Task RunAsync(FierceEnemyDifficulty difficulty, CancellationToken ct)
    {
        if (!System.IO.File.Exists(ConfigService.ResolvePath("resources/templates/" + Event + "story.png")))
            throw new InvalidOperationException(L.Text("当前游戏语言缺少故事事件识别素材。"));
        ocr.Validate();
        var entry = await WaitStable(async frame =>
        {
            if (await Find(frame, Event + "boss", Title, ct, BossTitleThreshold) is { } boss) return new Observation("boss", boss);
            return await Find(frame, Event + "story", Title, ct) is { } story ? new Observation("story", story) : null;
        }, ct) ?? throw new InvalidOperationException(L.Text("请进入故事事件或 BOSS 战斗界面。"));
        if (entry.Kind == "story") await automation.TapAsync(serial, 1650, 314, ct);
        await WaitBoss(ct);
        while (true)
        {
            var (button, rare) = await Locate(difficulty, ct);
            log.Info(rare ? L.Text("略过稀有 BOSS 一次。") : $"BOSS / {difficulty}");
            await Tap(button, ct);
            var choice = await WaitStable(frame => DialogAction(frame, rare, ct), ct) ?? throw Unknown();
            await Tap(choice.Button, ct);
            if (choice.Kind == "cancel")
            {
                await WaitBoss(ct);
                return;
            }
            if (choice.Kind == "max")
            {
                var execute = await WaitStable(async frame =>
                {
                    var next = await DialogAction(frame, rare, ct);
                    return next?.Kind == "execute" ? next : null;
                }, ct) ?? throw Unknown();
                await Tap(execute.Button, ct);
            }
            // 活动略过直接交回列表。等待动画结束并复核新帧，整轮不依赖普通略过结果页。
            await WaitBoss(ct, 90);
        }
    }

    /// <summary>先复核列表顶部的稀有标记，再向下浏览玩家所选难度，每次手势后重新截图。</summary>
    private async Task<(TemplateMatchResult, bool)> Locate(FierceEnemyDifficulty difficulty, CancellationToken ct)
    {
        async Task<Observation?> Card(ScreenImage frame, bool rareOnly)
        {
            if (await Find(frame, Event + "boss", Title, ct, BossTitleThreshold) is null) return null;
            var rare = await FindRare(frame, ct);
            if (rareOnly && rare is null) return null;
            Roi? label = rare is null
                ? await Task.Run(() => labels.Find(frame, difficulty, ct), ct)
                : new Roi(1040, rare.Y + 55, 100, 30);
            if (label is not { } found || FierceEnemyDifficultyRecognizer.GetSkipRegion(found) is not { } region) return null;
            var button = await Find(frame, "stage/event/button_skip", new Roi(region.X - 25, region.Y - 16, region.Width + 50, region.Height + 32), ct, .88);
            return button is null ? null : new Observation(rare is null ? "regular" : "rare", button);
        }
        for (int i = 0; i < 2; i++)
        {
            if (await WaitStable(f => Card(f, true), ct, 1500) is { } rare) return (rare.Button, true);
            await Scroll(420, ct);
            await WaitBoss(ct);
        }
        for (int i = 0; i < 6; i++)
        {
            if (await WaitStable(f => Card(f, false), ct, 2000) is { } card) return (card.Button, card.Kind == "rare");
            if (i == 5) break;
            await Scroll(-245, ct);
            await WaitBoss(ct);
        }
        throw Unknown();
    }

    private async Task<Observation?> DialogAction(ScreenImage frame, bool rare, CancellationToken ct)
    {
        if (await Find(frame, Event + "ticket_dialog", new(725, 287, 480, 92), ct) is null ||
            await Find(frame, "dialog/cancel", Cancel, ct) is not { } cancel) return null;
        if (DiamondSafetyChecker.CountRedPixels(frame.Bgra, frame.Width, RemainingTickets) >= DiamondSafetyChecker.DefaultRedPixelThreshold &&
            await Find(frame, Event + "insufficient_execute", Execute, ct, .96) is not null) return new("cancel", cancel);
        if (!rare && await Find(frame, Max, Quantity, ct) is { } max) return new("max", max);
        if (await Find(frame, Max + "_disabled", Quantity, ct) is null) return null;
        return await Find(frame, "dialog/execute", Execute, ct) is { } execute ? new("execute", execute) : null;
    }

    private Task<TemplateMatchResult?> FindRare(ScreenImage frame, CancellationToken ct) =>
        Find(frame, Event + "rare", new(977, 122, 392, 755), ct, .94);

    private Task Scroll(int movement, CancellationToken ct) =>
        HistoryBossProgressService.ScrollListAsync(automation, serial, movement, log, ct);

    private async Task<ScreenImage> Capture(CancellationToken ct)
    {
        var frame = await automation.CaptureScreenAsync(serial, ct);
        if (frame.Width != 1920 || frame.Height != 1080) throw Unknown();
        return frame;
    }
    private Task Tap(TemplateMatchResult button, CancellationToken ct) =>
        automation.TapAsync(serial, button.CenterX, button.CenterY, ct);
    private Task<TemplateMatchResult?> Find(ScreenImage frame, string key, Roi roi, CancellationToken ct, double threshold = .92) =>
        automation.MatchImageAsync(frame, ConfigService.ResolvePath($"resources/templates/{key}.png"),
            new WaitOptions { Roi = roi, Threshold = threshold }, ct);
    private static InvalidOperationException Unknown() => new(L.Text("未确认限时活动画面或可用按钮，已停止略过。"));

    private sealed record Observation(string Kind, TemplateMatchResult Button);

    // 动画中控件可能短暂出现或移动。只读轮询，每次操作前须在相邻两帧确认同一状态和位置。
    private async Task<Observation?> WaitStable(Func<ScreenImage, Task<Observation?>> recognize,
        CancellationToken ct, int timeoutMs = GameTiming.ShortRecognitionTimeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        Observation? previous = null;
        do
        {
            var current = await recognize(await Capture(ct));
            if (current is not null && previous?.Kind == current.Kind &&
                Math.Abs(previous.Button.CenterX - current.Button.CenterX) <= 3 &&
                Math.Abs(previous.Button.CenterY - current.Button.CenterY) <= 3) return current;
            previous = current;
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        } while (Environment.TickCount64 < deadline);
        return null;
    }

    private async Task WaitBoss(CancellationToken ct, int seconds = 20)
    {
        _ = await WaitStable(async frame => await Find(frame, Event + "boss", Title, ct, BossTitleThreshold) is { } boss
            ? new Observation("boss", boss) : null, ct, seconds * 1000) ?? throw Unknown();
    }
}

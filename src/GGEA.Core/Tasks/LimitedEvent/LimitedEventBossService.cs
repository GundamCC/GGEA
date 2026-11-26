using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Localization;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>只接管玩家打开的故事事件。每次结果返回后丢弃上一轮卡片位置。</summary>
public sealed class LimitedEventBossService(AutomationService automation, OnnxTextRecognizer ocr, IGgeaLog log, string serial)
{
    private const string Event = "stage/event/limited_time_event/";
    private const string Max = "develop/button/set_max_button";
    private readonly FierceEnemyDifficultyRecognizer labels = new(ocr, new(1035, 128, 125, 776));
    private static readonly Roi Title = new(190, 0, 375, 96);
    private static readonly Roi Cancel = new(550, 762, 435, 161);
    private static readonly Roi Execute = new(945, 762, 450, 161);
    private static readonly Roi Quantity = new(1210, 385, 200, 160);

    public async Task RunAsync(FierceEnemyDifficulty difficulty, CancellationToken ct)
    {
        if (!System.IO.File.Exists(ConfigService.ResolvePath("resources/templates/" + Event + "story.png")))
            throw new InvalidOperationException(L.Text("当前游戏语言缺少故事事件识别素材。"));
        ocr.Validate();
        var frame = await Capture(ct);
        if (await Find(frame, Event + "story", Title, ct) is null)
            throw new InvalidOperationException(L.Text("请先手动进入故事事件界面。"));
        await automation.TapAsync(serial, 1650, 314, ct);
        frame = await WaitBoss(ct);
        while (true)
        {
            var (button, rare) = await Locate(frame, difficulty, ct);
            log.Info(rare ? L.Text("略过稀有 BOSS 一次。") : $"BOSS / {difficulty}");
            await Tap(button, ct);
            await WaitFor(Event + "ticket_dialog", new(725, 287, 480, 92), ct);
            frame = await Capture(ct);
            var cancel = await Find(frame, "dialog/cancel", Cancel, ct);
            if (cancel is null) throw Unknown();
            // 只接受同一挑战券对话框中的负数红字与暗态执行，余额数字本身随玩家而变。
            if (await Find(frame, Event + "insufficient_execute", Execute, ct, .96) is not null &&
                HasDeficit(frame))
            {
                await Tap(cancel, ct);
                await WaitBoss(ct);
                return;
            }
            if (!rare)
            {
                var max = await Find(frame, Max, Quantity, ct);
                if (max is not null)
                {
                    await Tap(max, ct);
                    await WaitFor(Max + "_disabled", Quantity, ct);
                }
            }
            frame = await Capture(ct);
            if (await Find(frame, Event + "ticket_dialog", new(725, 287, 480, 92), ct) is null ||
                await Find(frame, Max + "_disabled", Quantity, ct) is null) throw Unknown();
            await Tap(await Find(frame, "dialog/execute", Execute, ct) ?? throw Unknown(), ct);
            // 活动略过直接交回列表。等待动画结束并复核新帧，整轮不依赖普通略过结果页。
            await WaitBoss(ct, 90);
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
            frame = await WaitBoss(ct);
        }
    }

    /// <summary>先复核列表顶部的稀有标记，再向下浏览玩家所选难度，每次手势后重新截图。</summary>
    private async Task<(TemplateMatchResult, bool)> Locate(ScreenImage frame, FierceEnemyDifficulty difficulty, CancellationToken ct)
    {
        // 稀有卡的标识在同一卡片内。可见时直接定位，避免结算后重复滚动。
        var rare = await FindRare(frame, ct);
        if (rare is null)
        {
            // 最多四张卡片，溢出高度小于两段手势。先回顶检查被视口遮挡的稀有卡。
            for (var i = 0; i < 2; i++)
            {
                await Scroll(420, ct);
                frame = await WaitBoss(ct);
                rare = await FindRare(frame, ct);
                if (rare is not null) break;
            }
        }
        for (var i = 0; i < 6; i++)
        {
            Roi? label = rare is null
                ? await Task.Run(() => labels.Find(frame, difficulty, ct), ct)
                : new Roi(1040, rare.Y + 55, 100, 30);
            if (label is { } found && FierceEnemyDifficultyRecognizer.GetSkipRegion(found) is { } region)
            {
                var button = await Find(frame, "stage/event/button_skip", new Roi(region.X - 25, region.Y - 16, region.Width + 50, region.Height + 32), ct, .88);
                if (button is null) throw Unknown();
                return (button, rare is not null);
            }
            if (i == 5) break;
            await Scroll(-245, ct);
            frame = await WaitBoss(ct);
            rare = await FindRare(frame, ct);
        }
        throw Unknown();
    }

    private Task<TemplateMatchResult?> FindRare(ScreenImage frame, CancellationToken ct) =>
        Find(frame, Event + "rare", new(977, 122, 392, 755), ct, .94);

    private Task Scroll(int movement, CancellationToken ct) =>
        HistoryBossProgressService.ScrollListAsync(automation, serial, movement, log, ct);

    private static bool HasDeficit(ScreenImage frame)
    {
        // 余额不足时右侧显示红色向下图标和负余额。充足时执行按钮亮起。
        var red = 0;
        for (var y = 607; y < 650; y++)
        for (var x = 1280; x < 1445; x++)
        {
            var i = (y * frame.Width + x) * 4;
            if (frame.Bgra[i + 2] > frame.Bgra[i + 1] + 55 && frame.Bgra[i + 2] > frame.Bgra[i] + 55) red++;
        }
        return red >= 30;
    }

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

    private async Task<ScreenImage> WaitBoss(CancellationToken ct, int seconds = 20)
    {
        var deadline = Environment.TickCount64 + seconds * 1000;
        do
        {
            var frame = await Capture(ct);
            if (await Find(frame, Event + "boss", Title, ct, .94) is not null) return frame;
            await Task.Delay(350, ct);
        } while (Environment.TickCount64 < deadline);
        throw Unknown();
    }

    private async Task<TemplateMatchResult> WaitFor(string key, Roi roi, CancellationToken ct) =>
        await automation.WaitForImageAsync(serial, ConfigService.ResolvePath($"resources/templates/{key}.png"),
            new WaitOptions { Roi = roi, Threshold = .92, PollIntervalMs = GameTiming.ConfirmationIntervalMs, TimeoutMs = GameTiming.ShortRecognitionTimeoutMs, AllowGlobalContinuation = false }, ct)
            ?? throw Unknown();
}

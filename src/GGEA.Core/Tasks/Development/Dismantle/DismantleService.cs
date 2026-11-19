using GGEA.App.Localization;
using System.IO;
using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>从解体页面循环自动选择和解体；只有明确的无推荐单位提示才能正常结束。</summary>
public sealed class DismantleService(AutomationService automation, int timeoutMs = GameTiming.RecognitionTimeoutMs, int settleMs = GameTiming.RecognitionIntervalMs)
{
    private const string Upgrade = "resources/templates/upgrade/";
    private const string Empty = "resources/templates/dialog/no_recommended_units.png";
    private const string Ok = "resources/templates/dialog/button_ok.png";
    private const string Confirm = "resources/templates/common/button_confirm.png";

    /// <summary>开始前一次性检查素材；缺失时不进入任何可能解体的操作。</summary>
    public static void ValidateTemplates()
    {
        foreach (var path in new[] { Empty, Ok, Confirm }.Concat(new[] { "title_dismantle.png", "auto_dismantle.png",
                     "button_auto_dismantle.png", "button_reset.png", "button_dismantle.png", "dismantle_confirm.png",
                     "button_execute_dismantle.png", "option/option_dev_unit_off.png", "option/option_dev_unit_on.png",
                     "filter_on.png", "filter_off.png", "sort.png" }.Select(x => Upgrade + x)))
            if (!File.Exists(ConfigService.ResolvePath(path))) throw new FileNotFoundException(L.Text("缺少解体识别素材。"), path);
    }

    /// <summary>返回已确认结果并关闭的解体轮数；上限保护、超时和未知状态全部抛出，不冒充清空。</summary>
    public async Task<int> RunUntilEmptyAsync(string serial, Action<string> report, CancellationToken ct = default)
    {
        ValidateTemplates();
        var recognizer = new DismantlePageRecognizer(automation);
        await RequireAsync(serial, Upgrade + "title_dismantle.png", RecognitionRegion.PageTitle, ct);
        var page = await recognizer.RecognizeAsync(await automation.ScreenshotAsync(serial, ct), ct);
        if (page?.FilterState == DismantleFilterState.On)
        {
            if (await recognizer.OpenSortAndFindResetAsync(serial, ct, timeoutMs) is null ||
                !await recognizer.ResetAndConfirmAsync(serial, ct, timeoutMs)) throw new InvalidOperationException(L.Text("解体筛选重置未完成。"));
            await RequireAsync(serial, Upgrade + "title_dismantle.png", RecognitionRegion.PageTitle, ct);
            page = await recognizer.RecognizeAsync(await automation.ScreenshotAsync(serial, ct), ct);
        }
        if (page?.FilterState != DismantleFilterState.Off) throw new InvalidOperationException(L.Text("未确认解体筛选为 OFF，停止解体。"));

        for (var rounds = 0; rounds < 1000; rounds++)
        {
            ct.ThrowIfCancellationRequested();
            report(L.Format($"正在自动选择第 {rounds + 1} 轮解体单位（每轮最多 100 台）…"));
            await RequireAsync(serial, Upgrade + "title_dismantle.png", RecognitionRegion.PageTitle, ct);
            await ClickAsync(serial, Upgrade + "button_auto_dismantle.png", RecognitionRegion.AutoDismantle, ct);
            await RequireAsync(serial, Upgrade + "auto_dismantle.png", RecognitionRegion.SortTitle, ct, .95);
            await ClickAsync(serial, Upgrade + "button_reset.png", RecognitionRegion.SortReset, ct, .90);
            await SelectDevelopOptionAsync(serial, ct);
            await ClickAsync(serial, Confirm, RecognitionRegion.BottomButtons, ct);

            var selected = await WaitSelectionAsync(serial, ct);
            if (selected is null)
            {
                await ClickAsync(serial, Ok, RecognitionRegion.DialogButtons, ct);
                await RequireAsync(serial, Upgrade + "title_dismantle.png", RecognitionRegion.PageTitle, ct);
                report(L.Format($"已识别没有推荐单位，本次解体共完成 {rounds} 轮。"));
                return rounds;
            }
            await automation.TapAsync(serial, selected.CenterX, selected.CenterY, ct);
            await RequireAsync(serial, Upgrade + "dismantle_confirm.png", RecognitionRegion.SortTitle, ct, .95);
            await ClickAsync(serial, Upgrade + "button_execute_dismantle.png", RecognitionRegion.BottomButtons, ct);
            // 执行后不得重试消费性点击。先确认确认框已消失，防止相似蓝色执行按钮被当成结果 OK。
            await WaitResultAsync(serial, ct);
            await RequireAsync(serial, Upgrade + "title_dismantle.png", RecognitionRegion.PageTitle, ct);
            report(L.Format($"第 {rounds + 1} 轮解体完成，继续检查可解体单位。"));
        }
        throw new InvalidOperationException(L.Text("解体达到 1000 轮保护上限，未确认无推荐单位，任务停止。"));
    }

    /// <summary>每次自动选择后同帧优先检查空提示；按钮缺失和超时不能视为没有单位。</summary>
    public async Task<TemplateMatchResult?> WaitSelectionAsync(string serial, CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        do
        {
            ct.ThrowIfCancellationRequested();
            var png = await automation.ScreenshotAsync(serial, ct);
            if (await MatchAsync(png, Empty, RecognitionRegion.DialogPrompt, ct, .95) is not null) return null;
            if (await MatchAsync(png, Upgrade + "title_dismantle.png", RecognitionRegion.PageTitle, ct) is not null &&
                await MatchAsync(png, Upgrade + "button_dismantle.png", RecognitionRegion.DismantleButton, ct) is { } selected) return selected;
            await Task.Delay(settleMs, ct);
        } while (Environment.TickCount64 < deadline);
        throw new InvalidOperationException(L.Text("未识别到无推荐单位提示或可解体按钮，停止而不推断解体完成。"));
    }

    /// <summary>有界向上滑动内容至已验证的底部选项区域，再通过亮蓝色勾选区确认状态。</summary>
    private async Task SelectDevelopOptionAsync(string serial, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await automation.SwipeAsync(serial, 1200, 780, 1200, 220, 600, ct);
            await Task.Delay(settleMs, ct);
            var png = await automation.ScreenshotAsync(serial, ct);
            if (await MatchAsync(png, Upgrade + "auto_dismantle.png", RecognitionRegion.SortTitle, ct) is null)
                throw new InvalidOperationException(L.Text("滚动后自动解体标题未识别，停止选择。"));
            var off = await MatchAsync(png, Upgrade + "option/option_dev_unit_off.png", RecognitionRegion.DismantleDevelopOption, ct, .90);
            var on = await MatchAsync(png, Upgrade + "option/option_dev_unit_on.png", RecognitionRegion.DismantleDevelopOption, ct, .90);
            if (on is not null && CountOptionBluePixels(png, on) >= 100) return;
            if (off is null) continue;
            if (CountOptionBluePixels(png, off) > 20) throw new InvalidOperationException(L.Text("开发单位选项处于不确定状态，停止选择。"));
            await automation.TapAsync(serial, off.X + 38, off.Y + 36, ct);
            await Task.Delay(settleMs, ct);
            png = await automation.ScreenshotAsync(serial, ct);
            on = await MatchAsync(png, Upgrade + "option/option_dev_unit_on.png", RecognitionRegion.DismantleDevelopOption, ct, .90);
            if (on is not null && CountOptionBluePixels(png, on) >= 100) return;
            throw new InvalidOperationException(L.Text("未确认开发单位选项已勾选，停止而不点击确定。"));
        }
        throw new InvalidOperationException(L.Text("滚动后未找到开发单位选项，停止解体。"));
    }

    /// <summary>只统计左侧复选框内的亮青蓝色，不把蓝色文字或背景计为勾选。</summary>
    public static int CountOptionBluePixels(byte[] png, TemplateMatchResult match)
    {
        var image = ImageCodec.DecodePng(png);
        var count = 0;
        for (var y = Math.Max(0, match.Y + 8); y < Math.Min(image.Height, match.Y + 64); y++)
        for (var x = Math.Max(0, match.X + 8); x < Math.Min(image.Width, match.X + 70); x++)
        {
            var i = (y * image.Width + x) * 4;
            int b = image.Bgra[i], g = image.Bgra[i + 1], r = image.Bgra[i + 2];
            if (b >= 180 && g >= 150 && b - r >= 45 && g - r >= 35) count++;
        }
        return count;
    }

    /// <summary>结果 OK 只点击一次，必须确认离开确认解体弹窗。</summary>
    private async Task WaitResultAsync(string serial, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        do
        {
            var png = await automation.ScreenshotAsync(serial, ct);
            // “解体结果”对“确认解体”模板实测也有 89.7%，标题必须使用 95%。
            // 同时要求结果 OK 居中，排除标题淡入淡出时把右侧执行按钮（OK 模板可达 87.5%）再点一次。
            if (await MatchAsync(png, Upgrade + "dismantle_confirm.png", RecognitionRegion.SortTitle, ct, .95) is null &&
                await MatchAsync(png, Ok, RecognitionRegion.BottomButtons, ct) is { } ok &&
                Math.Abs(ok.CenterX - ImageCodec.DecodePng(png).Width / 2) <= 80)
            {
                await automation.TapAsync(serial, ok.CenterX, ok.CenterY, ct);
                return;
            }
            await Task.Delay(settleMs, ct);
        } while (Environment.TickCount64 < deadline);
        throw new InvalidOperationException(L.Text("执行解体后未确认结果 OK，任务停止，不重复执行解体。"));
    }

    /// <summary>所有解体步骤禁用全局继续点击，避免等待期间发生额外操作。</summary>
    private Task<TemplateMatchResult?> MatchAsync(byte[] png, string path, RecognitionRegion region, CancellationToken ct, double threshold = .85)
        => automation.MatchImageAsync(png, ConfigService.ResolvePath(path), new WaitOptions { Region = region, Threshold = threshold, AllowGlobalContinuation = false }, ct);

    /// <summary>有限等待指定模板；失败由调用链统一停止。</summary>
    private async Task<TemplateMatchResult> RequireAsync(string serial, string path, RecognitionRegion region, CancellationToken ct, double threshold = .85)
        => await automation.WaitForImageAsync(serial, ConfigService.ResolvePath(path),
            new WaitOptions { Region = region, Threshold = threshold, TimeoutMs = timeoutMs, PollIntervalMs = Math.Max(1, settleMs), AllowGlobalContinuation = false }, ct)
            ?? throw new InvalidOperationException(L.Format($"未识别到 {Path.GetFileName(path)}，已停止。"));

    /// <summary>仅点击本次识别位置，不复用上一轮坐标。</summary>
    private async Task ClickAsync(string serial, string path, RecognitionRegion region, CancellationToken ct, double threshold = .85)
    {
        var match = await RequireAsync(serial, path, region, ct, threshold);
        await automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
    }
}

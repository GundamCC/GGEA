using GGEA.App.Automation;
using GGEA.App.Localization;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>只在主要获得方式的右侧列表中寻找开发卡。移动按钮必须与开发标题同属一行。</summary>
internal sealed class ObtainMethodsNavigator(AutomationService automation)
{
    private const string Root = "resources/templates/base/character_requests/";
    private readonly TemplateImages _title = new(Root + "title_obtain_methods.png");
    private readonly TemplateImages _develop = new(Root + "source_develop.png");
    private readonly TemplateImages _move = new(Root + "source_move.png");
    private readonly TemplateImages _close = new("resources/templates/common/close.png");
    private readonly TemplateMatcher _matcher = new();
    internal sealed record Panel(Roi Close, Roi? Move, ScreenImage Frame);

    internal Panel? Read(ScreenImage frame, CancellationToken ct)
    {
        if (frame.Width != 1920 || frame.Height != 1080 ||
            _title.Match(_matcher, frame, .94, new(780, 48, 364, 90), ct) is null) return null;
        var close = _close.Match(_matcher, frame, .90, new(700, 910, 520, 164), ct);
        if (close is null) return null;
        var heading = _develop.Match(_matcher, frame, .95, new(752, 156, 144, 748), ct);
        Roi? move = null;
        if (heading is not null)
        {
            // 1920×1080 实测按钮上沿比标题上沿低 15px。完整按钮须落在右侧可见窗口内。
            int top = Math.Max(156, heading.Region.Y + 3);
            int height = Math.Min(905, heading.Region.Y + 111) - top;
            if (height >= _move.Primary.Height)
                move = _move.Match(_matcher, frame, .94, new(1504, top, 304, height), ct)?.Region;
        }
        return new(close.Region, move, frame);
    }

    // 用列表左半部判断是否停止滚动，避开蓝色按钮的扫光和窗口外动画。
    private static bool SameRows(ScreenImage a, ScreenImage b)
    {
        int changed = 0;
        for (int y = 164; y < 896; y += 5)
            for (int x = 756; x < 1450; x += 7)
            {
                int index = (y * a.Width + x) * 4;
                if (Math.Abs(a.Bgra[index] - b.Bgra[index]) > 12 && ++changed > 22) return false;
            }
        return true;
    }

    internal async Task<bool> OpenDevelopmentAsync(string serial, CancellationToken ct)
    {
        Panel? previous = null;
        int stationary = 0;
        for (int pass = 0; ; pass++)
        {
            var panel = await StableObservation.WaitAsync(async () => Read(await automation.CaptureScreenAsync(serial, ct), ct),
                (a, b) => a.Close == b.Close && a.Move == b.Move && SameRows(a.Frame, b.Frame),
                GameTiming.RecognitionTimeoutMs, ct) ?? throw new InvalidOperationException(L.Text("未能确认主要获得方式页面。"));
            if (panel.Move is {} move)
            {
                await automation.TapAsync(serial, move.CenterX, move.CenterY, ct);
                return true;
            }
            stationary = previous is not null && SameRows(previous.Frame, panel.Frame) ? stationary + 1 : 0;
            if (stationary >= 2 || pass == 24)
            {
                await automation.TapAsync(serial, panel.Close.CenterX, panel.Close.CenterY, ct);
                return false;
            }
            previous = panel;
            await automation.DragAsync(serial, 1310, 797, 1310, 355, 650, ct);
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
        }
    }
}

using GGEA.App.Config;

namespace GGEA.App.Vision;

/// <summary>标准 HARD 略过确认页的当前证据；原始帧只在单次略过期间持有，不跨关卡复用。</summary>
public sealed record SkipExecutionDialog(ScreenImage Screen, TemplateMatchResult Execute, TemplateMatchResult Cancel);

/// <summary>验证执行与取消双按钮，并复核原弹窗标题、次数和 AP 数值，防止恢复时发起另一笔略过。</summary>
public sealed class SkipExecutionDialogRecognizer
{
    private readonly StageEntryButtonRecognizer _buttons = new();
    private readonly string _execute = ConfigService.ResolvePath("resources/templates/dialog/execute.png");
    private readonly string _cancel = ConfigService.ResolvePath("resources/templates/dialog/cancel.png");
    private static readonly Roi ExecuteRegion = new(950, 770, 410, 160);
    private static readonly Roi CancelRegion = new(560, 770, 410, 160);
    // 只比较不动的字形和数字，不把按钮发光及弹窗外的动态背景当成选择变化。
    private static readonly Roi[] IdentityRegions =
    [new(850, 210, 220, 60), new(900, 440, 170, 70), new(940, 590, 120, 55), new(1330, 590, 120, 55)];

    /// <summary>匹配当前语言的完整按钮与中央文字；蓝色亮态是执行授权的额外条件。</summary>
    public SkipExecutionDialog? Recognize(ScreenImage screen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (screen.Width != 1920 || screen.Height != 1080) return null;
        var execute = _buttons.Match(screen, _execute, ExecuteRegion, .90, ct);
        if (execute is null || !IsEnabled(screen, execute)) return null;
        var cancel = _buttons.Match(screen, _cancel, CancelRegion, .90, ct);
        return cancel is null ? null : new(screen, execute, cancel);
    }

    /// <summary>固定字形区域逐像素比较，AP 或次数的一处变化就拒绝，不能被大片相同底色稀释。</summary>
    public bool IsSameSelection(SkipExecutionDialog original, SkipExecutionDialog current, CancellationToken ct)
    {
        foreach (var region in IdentityRegions)
        {
            // 无字形的空白区域不能建立资源未变的证据。
            var minimum = 255;
            var maximum = 0;
            var changedPixels = 0;
            for (var y = region.Y; y < region.Y + region.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (var x = region.X; x < region.X + region.Width; x++)
                {
                    var i = (y * 1920 + x) * 4;
                    minimum = Math.Min(minimum, original.Screen.Bgra[i]);
                    maximum = Math.Max(maximum, original.Screen.Bgra[i]);
                    // 原生 PNG 的静止内容稳定；只容忍轻微色差和少量孤立噪点。
                    // 不用整块相关系数，否则浅色 AP 数字变化会被大面积相同背景淹没。
                    if ((Math.Abs(original.Screen.Bgra[i] - current.Screen.Bgra[i]) > 8 ||
                         Math.Abs(original.Screen.Bgra[i + 1] - current.Screen.Bgra[i + 1]) > 8 ||
                         Math.Abs(original.Screen.Bgra[i + 2] - current.Screen.Bgra[i + 2]) > 8) &&
                        ++changedPixels > 8) return false;
                }
            }
            if (maximum - minimum < 25) return false;
        }
        return true;
    }

    private static bool IsEnabled(ScreenImage screen, TemplateMatchResult button)
    {
        var blue = 0;
        for (var y = button.Y; y < button.Y + button.Height; y++)
        for (var x = button.X; x < button.X + button.Width; x++)
        {
            var i = (y * screen.Width + x) * 4;
            if (screen.Bgra[i] >= 180 && screen.Bgra[i] > screen.Bgra[i + 2] + 50 &&
                screen.Bgra[i + 1] > screen.Bgra[i + 2] + 20) blue++;
        }
        return blue >= button.Width * button.Height * .20;
    }
}

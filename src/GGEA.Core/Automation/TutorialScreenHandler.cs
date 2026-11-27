using GGEA.App.Config;
using GGEA.App.Logging;
using GGEA.App.Vision;

namespace GGEA.App.Automation;

/// <summary>在截图交付给业务前处理全屏教程，使用通用按钮与教程底栏两端的对称分隔线区分普通弹窗。</summary>
internal sealed class TutorialScreenHandler(
    Func<string, CancellationToken, Task<byte[]>> capture,
    Func<string, int, int, CancellationToken, Task> tap,
    Func<ScreenImage, CancellationToken, bool> errorVisible, IGgeaLog log)
{
    private readonly StageEntryButtonRecognizer _buttons = new();

    internal (bool IsOk, TemplateMatchResult Button)? Identify(ScreenImage screen, CancellationToken ct)
    {
        if (!HasTutorialFooter(screen)) return null;
        var area = RecognitionRegions.Resolve(RecognitionRegion.BottomButtons, screen.Width, screen.Height);
        foreach (var (ok, name) in new[] { (true, "dialog/button_ok"), (false, "stage/sortie/continue") })
        {
            var path = ConfigService.ResolvePath($"resources/templates/{name}.png");
            if (_buttons.MatchBlueLabel(screen, path, area, .90, ct) is { } button) return (ok, button);
        }
        return null;
    }

    private static bool HasTutorialFooter(ScreenImage screen)
    {
        if (screen.Width != 1920 || screen.Height != 1080) return false;
        // 两侧银灰色装饰线的上下均为深蓝背景，保留归一化和抗锯齿的颜色容差。
        foreach (var x in new[] { 40, 88, 1850, 1880 })
        {
            var line = (910 * screen.Width + x) * 4;
            var above = line - 5 * screen.Width * 4;
            if (screen.Bgra[line + 2] - screen.Bgra[above + 2] < 38 ||
                Math.Abs(screen.Bgra[line] - screen.Bgra[line + 2]) > 30) return false;
        }
        return true;
    }

    internal async Task<byte[]> HandleAsync(string serial, byte[] png, CancellationToken ct)
    {
        var screen = ImageCodec.DecodePng(png);
        var candidate = Identify(screen, ct);
        if (candidate is null) return png;
        bool clickedOk = false;
        int misses = 0;
        while (true)
        {
            await Task.Delay(300, ct);
            png = await capture(serial, ct);
            screen = ImageCodec.DecodePng(png);
            if (errorVisible(screen, ct)) throw new ConnectionErrorException();
            var current = Identify(screen, ct);
            if (current is null)
            {
                // 翻页过渡会短暂隐藏底栏；结束后交付重新截取的业务画面。
                if (HasTutorialFooter(screen)) misses = 0;
                else if (++misses >= (clickedOk ? 2 : 5)) return png;
            }
            else
            {
                misses = 0;
                if (current.Value.IsOk == candidate?.IsOk)
                {
                    var (ok, button) = current.Value;
                    log.Info(ok ? "教程检测：点击 OK，等待返回原流程。" : "教程检测：点击继续，等待下一页。");
                    await tap(serial, button.CenterX, button.CenterY, ct);
                    clickedOk = ok;
                    candidate = null;
                    await Task.Delay(550, ct);
                    continue;
                }
            }
            candidate = current;
        }
    }
}

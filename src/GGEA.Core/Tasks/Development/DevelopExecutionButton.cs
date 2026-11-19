using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>共享执行开发按钮识别；同帧优先排除暗态，数量按钮禁用不代表资源不足。</summary>
public static class DevelopExecutionButton
{
    public const string DisabledTemplate = "resources/templates/develop/button/button_execute_develop_disabled.png";

    /// <summary>1920×1080 下金币数字一行；避开左侧金币图标及数量按钮，不扫描其它红色内容。</summary>
    public static Roi CoinAmountRegion => new(1280, 660, 560, 55);

    /// <summary>以至少 20 个严格红色像素排除零散噪点；只在暗态执行按钮已经命中时参与判定。</summary>
    public const int CoinRedPixelThreshold = 20;

    /// <summary>统计当前金币行红色像素；不支持的尺寸不推断金币状态。</summary>
    public static int CountCoinRedPixels(ScreenImage screen) => screen.Width == 1920 && screen.Height == 1080
        ? DiamondSafetyChecker.CountRedPixels(screen.Bgra, screen.Width, CoinAmountRegion) : 0;

    /// <summary>只观察底部按钮，不点击；暗态优先，未知状态不能授权开发。</summary>
    public static async Task<(DevelopButtonState State, TemplateMatchResult? Match)> ObserveAsync(
        AutomationService automation, byte[] png, string enabledTemplate, CancellationToken ct = default)
    {
        var wait = new WaitOptions { Region = RecognitionRegion.BottomButtons, Threshold = RecognitionRegions.BottomButtonThreshold };
        var disabled = await automation.MatchImageAsync(png, ConfigService.ResolvePath(DisabledTemplate), wait, ct);
        if (disabled is not null)
        {
            // 暗态和红字必须来自同一截图，不能用数量调整前或上一轮的金币状态。
            var red = CountCoinRedPixels(ImageCodec.DecodePng(png));
            return (red >= CoinRedPixelThreshold ? DevelopButtonState.InsufficientCoins : DevelopButtonState.UnitLimitReached, disabled);
        }
        var enabled = await automation.MatchImageAsync(png, enabledTemplate, wait, ct);
        return (enabled is null ? DevelopButtonState.Unknown : DevelopButtonState.Ready, enabled);
    }

    /// <summary>等待亮态或暗态出现；超时交由任务失败入口诊断，不在正常截图中扫描错误弹窗。</summary>
    public static async Task<(DevelopButtonState State, TemplateMatchResult? Match)> WaitAsync(
        AutomationService automation, string serial, string enabledTemplate, int timeoutMs, CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        do
        {
            ct.ThrowIfCancellationRequested();
            var png = await automation.ScreenshotAsync(serial, ct);
            var result = await ObserveAsync(automation, png, enabledTemplate, ct);
            if (result.Match is not null) return result;
            if (Environment.TickCount64 >= deadline) break;
            await Task.Delay(GameTiming.RecognitionIntervalMs, ct);
        } while (Environment.TickCount64 < deadline);
        return (DevelopButtonState.Unknown, null);
    }
}

/// <summary>仅描述按钮观察结果，不将亮态等同于开发已完成。</summary>
public enum DevelopButtonState
{
    /// <summary>两种状态均未命中，不能点击。</summary>
    Unknown,
    /// <summary>亮态按钮可用，尚未执行开发。</summary>
    Ready,
    /// <summary>暗态按钮命中且金币行无红字，持有单位达到上限。</summary>
    UnitLimitReached,
    /// <summary>同帧暗态按钮及金币行红字均命中，持有金币不足。</summary>
    InsufficientCoins,
}

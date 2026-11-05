using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

/// <summary>只准备开发数量；数量按钮是本地同步操作，按确定起点计数，不做逐次数值识别。</summary>
public sealed class DevelopmentQuantityService(AutomationService automation)
{
    /// <summary>连续数量操作的间隔；不等待网络，不影响 ADB 自身的指令耗时。</summary>
    public const int ClickIntervalMs = 80;

    /// <summary>先确认页面并定位所需按钮，再快速执行最短路径；绝不点击执行开发。</summary>
    public async Task<int> SetAsync(string serial, int target, string executeTemplate,
        Action<string> report, CancellationToken ct = default, int? current = null)
    {
        var actions = BallDevelopmentPlan.Adjust(current, target);
        // 所有按钮共享十五秒预算；每轮清空坐标，只允许使用同一张完整就绪画面执行数量操作。
        var deadline = Environment.TickCount64 + GameTiming.ShortRecognitionTimeoutMs;
        var locations = new Dictionary<QuantityAction, (int X, int Y)>();
        var required = actions.Distinct().ToArray();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            locations.Clear();
            var missing = new List<string>();
            var png = await automation.ScreenshotAsync(serial, ct);
            var screen = ImageCodec.DecodePng(png);
            if (screen.Width != 1920 || screen.Height != 1080) throw new InvalidOperationException("开发数量调整仅支持 1920×1080。");
            if ((await DevelopExecutionButton.ObserveAsync(automation, png, executeTemplate, ct)).Match is null)
                missing.Add("执行开发");
            else
            {
                // 初始端点的数量按钮可能变暗；正常与禁用模板都只匹配当前帧，不猜测位置。
                foreach (var action in required)
                {
                    var (name, label) = action switch
                    {
                        QuantityAction.Minimum => ("set_min_button.png", "最低"),
                        QuantityAction.Decrement => ("decrement_button.png", "减号"),
                        QuantityAction.Increment => ("increment_button.png", "加号"),
                        QuantityAction.Maximum => ("set_max_button.png", "最大"),
                        _ => throw new ArgumentOutOfRangeException(nameof(action)),
                    };
                    var wait = new WaitOptions { Region = RecognitionRegion.DevelopmentQuantity, Threshold = .90 };
                    var match = await automation.MatchImageAsync(png,
                        ConfigService.ResolvePath("resources/templates/develop/button/" + name), wait, ct);
                    if (match is null)
                        match = await automation.MatchImageAsync(png,
                            ConfigService.ResolvePath("resources/templates/develop/button/" + name.Replace(".png", "_disabled.png")), wait, ct);
                    if (match is null) missing.Add($"数量按钮“{label}”（{name}，含禁用态）");
                    else locations[action] = (match.CenterX, match.CenterY);
                }
            }
            if (missing.Count == 0) break;
            var reason = string.Join("、", missing);
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
                throw new InvalidOperationException($"等待10秒仍未识别到{reason}，停止调整数量。");
            report($"未识别到{reason}，等待重试（共用最多10秒）…");
            await Task.Delay((int)Math.Min(GameTiming.RecognitionIntervalMs, remaining), ct);
            if (Environment.TickCount64 >= deadline)
                throw new InvalidOperationException($"等待10秒仍未识别到{reason}，停止调整数量。");
        }
        report($"目标 {target} 台，计划点击 {actions.Count} 次，间隔 {ClickIntervalMs} 毫秒。");
        for (var i = 0; i < actions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var point = locations[actions[i]];
            await automation.TapAsync(serial, point.X, point.Y, ct);
            if (i + 1 < actions.Count) await Task.Delay(ClickIntervalMs, ct);
        }
        report($"已按计划设置开发单位数为 {target}。");
        return target;
    }
}

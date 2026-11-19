using GGEA.App.Automation;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class StageService
{
    /// <summary>
    /// 点击作品节点，再在作品详情页识别“选择”按钮进入具体关卡列表。
    /// 先等待入场特效，再用选择按钮本身确认详情页状态；这比全屏像素差异
    /// 更能抵御背景动画、网络加载占位画面及轻微特效造成的误判。
    /// </summary>
    private async Task<StageNavigationResult> OpenWorkStageListAsync(
        string serial,
        int workX,
        int workY,
        UiAnchor workSelect,
        CancellationToken ct)
    {
        ScreenImage? canvasBeforeTap = null;
        if (HasWorkSelectTemplates())
        {
            canvasBeforeTap = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
        }

        await _automation.TapAsync(serial, workX, workY, ct);
        await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);

        // 详情页可能有轻微入场动画，识别“选择”前再固定等待 0.5 秒，避免点早进入失败。
        await GameTiming.DelayIfPositiveAsync(_options.SelectTapExtraDelayMs, ct);
        if (canvasBeforeTap is not null)
        {
            var afterTap = ImageCodec.DecodePng(await _automation.ScreenshotAsync(serial, ct));
            if (!HasWorkOverviewTransition(canvasBeforeTap, afterTap))
            {
                _log.Warn("点击作品后画面未进入详情页，停止识别选择按钮");
                return StageNavigationResult.Fail(
                    StageNavigationFailure.InvalidContext,
                    "点击作品后未确认详情页切换，已停止自动操作");
            }
        }

        var selectResult = await ConfirmAndClickWorkSelectAsync(serial, workSelect, ct);
        if (!selectResult.Success)
        {
            return selectResult;
        }

        await GameTiming.DelayIfPositiveAsync(_options.PageSettleMs, ct);
        return StageNavigationResult.Ok("已识别并点击作品详情页的可用选择按钮");
    }

    /// <summary>只要配置了任一种选择按钮模板，就必须先确认已从画布切入作品详情页。</summary>
    private bool HasWorkSelectTemplates()
        => !string.IsNullOrWhiteSpace(_options.WorkSelectTemplatePath) ||
           !string.IsNullOrWhiteSpace(_options.WorkSelectClosedTemplatePath);

    /// <summary>
    /// 对比点击前后主视觉区域的稀疏像素。作品详情页会替换大面积内容；
    /// 采样可避免逐像素比较的开销，也不会受顶部固定栏和底部导航栏影响。
    /// </summary>
    private bool HasWorkOverviewTransition(ScreenImage before, ScreenImage after)
    {
        if (before.Width != after.Width || before.Height != after.Height)
        {
            return false;
        }

        var changed = 0;
        var sampled = 0;
        var left = Math.Clamp(180, 0, before.Width);
        var right = Math.Clamp(before.Width - 180, left, before.Width);
        var top = Math.Clamp(100, 0, before.Height);
        var bottom = Math.Clamp(before.Height - 160, top, before.Height);
        for (var y = top; y < bottom; y += 16)
        {
            for (var x = left; x < right; x += 16)
            {
                var offset = (y * before.Width + x) * 4;
                var delta = Math.Abs(before.Bgra[offset] - after.Bgra[offset]) +
                            Math.Abs(before.Bgra[offset + 1] - after.Bgra[offset + 1]) +
                            Math.Abs(before.Bgra[offset + 2] - after.Bgra[offset + 2]);
                sampled++;
                if (delta >= 90)
                {
                    changed++;
                }
            }
        }

        return sampled > 0 && (double)changed / sampled >= _options.WorkOverviewChangeRatio;
    }

    /// <summary>
    /// 在作品详情页的右下角区分可用和暗态“选择”按钮。暗态优先检查，
    /// 因为它表示作品尚未解锁，禁止继续点击坐标或进入略过流程。
    /// </summary>
    private async Task<StageNavigationResult> ConfirmAndClickWorkSelectAsync(
        string serial,
        UiAnchor workSelect,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.WorkSelectTemplatePath) &&
            string.IsNullOrWhiteSpace(_options.WorkSelectClosedTemplatePath))
        {
            // 未配置模板时保持旧版行为，方便已有测试和自定义布局渐进迁移。
            await _automation.TapAsync(serial, workSelect.CenterX, workSelect.CenterY, ct);
            return StageNavigationResult.Ok("未配置选择按钮模板，已按布局坐标点击");
        }

        var wait = new WaitOptions
        {
            Threshold = _options.WorkSelectThreshold,
            Roi = WorkSelectSearchRoi,
        };
        var deadline = Environment.TickCount64 + Math.Max(0, _options.WorkSelectWaitTimeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(_options.WorkSelectClosedTemplatePath))
            {
                var closed = await _automation.FindImageAsync(
                    serial,
                    _options.WorkSelectClosedTemplatePath,
                    wait,
                    ct);
                if (closed is not null)
                {
                    _log.Warn("作品选择按钮为暗态，判定该作品尚未解锁");
                    return StageNavigationResult.Fail(
                        StageNavigationFailure.WorkStageLocked,
                        "识别到选择按钮暗态，该作品尚未解锁");
                }
            }

            if (!string.IsNullOrWhiteSpace(_options.WorkSelectTemplatePath))
            {
                var enabled = await _automation.FindImageAsync(
                    serial,
                    _options.WorkSelectTemplatePath,
                    wait,
                    ct);
                if (enabled is not null)
                {
                    _log.Info($"识别到可用选择按钮: ({enabled.CenterX},{enabled.CenterY})");
                    await _automation.TapAsync(serial, enabled.CenterX, enabled.CenterY, ct);
                    return StageNavigationResult.Ok("已点击可用选择按钮");
                }
            }

            await Task.Delay(Math.Max(1, _options.WorkSelectPollIntervalMs), ct);
        }

        _log.Warn(
            $"未在选择按钮区域 {WorkSelectSearchRoi} 内识别到可用或暗态按钮，停止自动操作");
        return StageNavigationResult.Fail(
            StageNavigationFailure.InvalidContext,
            "未识别到选择按钮可用或暗态，已停止自动操作");
    }
}

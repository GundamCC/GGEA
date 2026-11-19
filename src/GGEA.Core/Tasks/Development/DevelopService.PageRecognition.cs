using GGEA.App.Automation;
using GGEA.App.UI;

namespace GGEA.App.Tasks;

public sealed partial class DevelopService
{
    /// <summary>
    /// 识别开发页左上角页面标题，确认已进入开发页。
    /// 标题模板未配置时跳过（返回 true，不阻断流程）。
    /// </summary>
    private async Task<bool> WaitForDevelopTitleAsync(
        string serial,
        string developTitleTemplatePath,
        UiLayout developLayout,
        double titleThreshold,
        int titleWaitMs,
        string roadmapDisplaySettingsTitleTemplatePath,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(developTitleTemplatePath))
        {
            _log.Info("未配置开发页标题模板，跳过标题识别");
            return true;
        }

        var titleAnchor = developLayout.Find("developTitle");
        var titleWait = new WaitOptions
        {
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = titleThreshold,
            Roi = titleAnchor?.Roi,
        };

        var settingsTitleAnchor = developLayout.Find("roadmapDisplaySettingsTitle");
        var settingsConfirm = developLayout.Find("roadmapDisplaySettingsConfirm");
        var settingsWait = new WaitOptions
        {
            PollIntervalMs = titleWait.PollIntervalMs,
            Threshold = titleThreshold,
            Roi = settingsTitleAnchor?.Roi,
        };
        var deadline = Environment.TickCount64 + titleWaitMs;
        var settingsHandled = false;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var match = await _automation.FindImageAsync(serial, developTitleTemplatePath, titleWait, ct);
            if (match is not null)
            {
                _log.Info($"已识别开发页标题: {match}");
                return true;
            }

            // 首次进入具体作品的路线图时，游戏会用全屏设置页阻挡真实路线图。
            // 只在专用标题模板命中后点击固定的底部确认锚点，避免误触其它弹窗。
            if (!settingsHandled && !string.IsNullOrEmpty(roadmapDisplaySettingsTitleTemplatePath))
            {
                var settingsMatch = await _automation.FindImageAsync(
                    serial,
                    roadmapDisplaySettingsTitleTemplatePath,
                    settingsWait,
                    ct);
                if (settingsMatch is not null)
                {
                    if (settingsConfirm is null)
                    {
                        throw new InvalidOperationException(
                            "开发布局缺少 roadmapDisplaySettingsConfirm 锚点");
                    }

                    _log.Info($"检测到开发路线图显示设置: {settingsMatch}");
                    await _automation.TapAsync(serial, settingsConfirm.CenterX, settingsConfirm.CenterY, ct);
                    _log.Info($"已点击开发路线图显示设置的确定按钮: ({settingsConfirm.CenterX},{settingsConfirm.CenterY})");
                    settingsHandled = true;
                    await Task.Delay(RoadmapDisplaySettingsSettleDelayMs, ct);
                    // 设置页占用了原等待时间；关闭后重新给真实路线图完整的加载窗口。
                    deadline = Environment.TickCount64 + titleWaitMs;
                    continue;
                }
            }

            await Task.Delay(titleWait.PollIntervalMs, ct);
        }

        _log.Warn($"未识别到开发页左上角标题（{titleWaitMs}ms），可能未进入开发页");
        return false;
    }

    /// <summary>
    /// 失败退出前识别一次当前页面左上角标题并记入日志，便于判断失败时停留在哪个页面。
    /// 标题模板未配置时跳过。
    /// </summary>
    private async Task RecognizePageTitleOnceAsync(
        string serial,
        string developTitleTemplatePath,
        double titleThreshold,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(developTitleTemplatePath))
        {
            _log.Warn("未配置开发页标题模板，失败时跳过标题识别");
            return;
        }

        var match = await _automation.FindImageAsync(
            serial,
            developTitleTemplatePath,
            new WaitOptions { Threshold = titleThreshold },
            ct);
        _log.Info(match is not null
            ? $"失败时页面标题仍可识别: {match}"
            : "失败时页面标题未识别到（可能已离开开发页）");
    }
}

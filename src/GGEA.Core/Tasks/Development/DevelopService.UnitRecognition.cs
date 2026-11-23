using GGEA.App.Automation;
using GGEA.App.Config;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class DevelopService
{
    /// <summary>
    /// 进入开发页 → 点击第一个路线图 → 查找并点击目标机体。
    /// </summary>
    /// <param name="serial">设备序列号。</param>
    /// <param name="mainLayout">主界面布局（用于开发入口 navDevelop）。</param>
    /// <param name="developLayout">开发页布局（第一个路线图位置）。</param>
    /// <param name="roadmapFirstTemplatePath">第一个路线图模板（用于确认已进入开发页）。</param>
    /// <param name="msTemplatePath">目标机体模板。</param>
    /// <param name="msLargeTemplatePath">目标机体大图模板（与小图共同覆盖不同缩放比例）；为空表示不启用。</param>
    /// <param name="developTitleTemplatePath">开发页左上角页面标题模板；为空表示不识别标题。</param>
    /// <param name="maxDragsPerDirection">显式限制每方向步数；0 表示按实际边界遍历。</param>
    /// <param name="dragDurationMs">单次拖动时长。</param>
    /// <param name="threshold">机体匹配置信度阈值。</param>
    /// <param name="stepDelayMs">点击后的页面切换等待时长。</param>
    /// <param name="networkSettleDelayMs">点击机体后等待网络加载完成的稳定时长。</param>
    /// <param name="titleThreshold">开发页标题匹配置信度阈值。</param>
    /// <param name="titleWaitMs">进入开发页后等待标题出现的总超时。</param>
    /// <param name="roadmapDisplaySettingsTitleTemplatePath">首次进入路线图时“开发路线图显示设置”标题模板；为空表示不处理。</param>
    /// <returns>找到并点击后返回匹配结果；未找到返回 null。</returns>
    public async Task<TemplateMatchResult?> FindAndTapMsAsync(
        string serial,
        UiLayout mainLayout,
        UiLayout developLayout,
        string roadmapFirstTemplatePath,
        string msTemplatePath,
        string msLargeTemplatePath,
        string developTitleTemplatePath,
        int maxDragsPerDirection = 0,
        int dragDurationMs = 700,
        double threshold = 0.86,
        int stepDelayMs = 2500,
        int networkSettleDelayMs = 3000,
        double titleThreshold = 0.85,
        int titleWaitMs = GameTiming.ShortRecognitionTimeoutMs,
        string roadmapDisplaySettingsTitleTemplatePath = "",
        CancellationToken ct = default)
    {
        var navDevelop = mainLayout.Require("navDevelop", "主界面布局缺少 navDevelop 锚点");
        var firstRoadmap = developLayout.Require("firstRoadmap", "开发布局缺少 firstRoadmap 锚点");

        // 1. 从主界面进入开发页
        _log.Info($"进入开发页: ({navDevelop.CenterX}, {navDevelop.CenterY})");
        await _automation.TapAsync(serial, navDevelop.CenterX, navDevelop.CenterY, ct);
        await Task.Delay(stepDelayMs, ct);

        // 2. 确认开发页并点击第一个路线图
        var roadmapWait = new WaitOptions
        {
            TimeoutMs = GameTiming.ShortRecognitionTimeoutMs,
            PollIntervalMs = GameTiming.RecognitionIntervalMs,
            Threshold = 0.85,
            Roi = firstRoadmap.Roi,
        };
        var roadmapFound = await _automation.WaitForImageAsync(
            serial,
            roadmapFirstTemplatePath,
            roadmapWait,
            ct,
            matchTarget: (screen, token) => MatchFirstRoadmapAsync(screen, roadmapFirstTemplatePath, roadmapWait, token));
        if (roadmapFound is null)
        {
            // 入口偶发停留在未完整载入的中间页面；恢复点击会直接进入路线图页，
            // 因此随后必须用路线图标题确认页面，而不能再次等待入口卡片模板。
            _log.Warn(
                $"未检测到开发路线图，点击 ({RoadmapRecoveryTapX},{RoadmapRecoveryTapY}) 后检测路线图标题");
            await _automation.TapAsync(serial, RoadmapRecoveryTapX, RoadmapRecoveryTapY, ct);
            await Task.Delay(stepDelayMs, ct);
        }
        else
        {
            _log.Info($"点击第一个路线图: ({firstRoadmap.CenterX}, {firstRoadmap.CenterY})");
            await _automation.TapAsync(serial, firstRoadmap.CenterX, firstRoadmap.CenterY, ct);
            await Task.Delay(stepDelayMs, ct);
        }

        // 3. 识别路线图页左上角标题，确认已进入开发路线图（标题模板为空时跳过）
        if (!await WaitForDevelopTitleAsync(
                serial,
                developTitleTemplatePath,
                developLayout,
                titleThreshold,
                titleWaitMs,
                roadmapDisplaySettingsTitleTemplatePath,
                ct))
        {
            _log.Warn("未确认开发路线图页标题，流程中断");
            return null;
        }

        var match = await FindOnRoadmapAsync(serial, msTemplatePath, msLargeTemplatePath,
            maxDragsPerDirection, dragDurationMs, threshold, developTitleTemplatePath, titleThreshold, ct);
        if (match is not null)
        {
            await _automation.TapAsync(serial, match.CenterX, match.CenterY, ct);
            if (networkSettleDelayMs <= 5000) await Task.Delay(Math.Max(0, networkSettleDelayMs), ct);
        }
        return match;
    }

    /// <summary>同帧优先识别原路线图，仅在未命中时补试未完成状态；共用入口 ROI、阈值与等待预算。</summary>
    internal async Task<TemplateMatchResult?> MatchFirstRoadmapAsync(ScreenImage screen, string templatePath,
        WaitOptions wait, CancellationToken ct)
    {
        var primary = await _automation.MatchImageAsync(screen, templatePath, wait, ct);
        if (primary is not null || !Path.GetFileName(templatePath).Equals("dev_0079.png", StringComparison.OrdinalIgnoreCase))
            return primary;

        // 只扩展 0079 卡片；自定义目录沿用同目录素材，不让其他路线图误用内置替补。
        var unfinished = ConfigService.ResolvePath(Path.Combine(Path.GetDirectoryName(templatePath) ?? "", "dev_0079_unfinished.png"));
        if (!File.Exists(unfinished)) return null;
        return await _automation.MatchImageAsync(screen, unfinished, wait, ct);
    }

    /// <summary>
    /// 默认以单张原生素材匹配大小两档，返回命中结果与来源，供下一帧重复确认。
    /// 仅在用户另配了辅助模板时先检查辅助项，不再依赖随包的旧大小截图。
    /// </summary>
    private async Task<(string TemplatePath, TemplateMatchResult Match)?> FindMsMatchAsync(
        byte[] frame,
        string msLargeTemplatePath,
        string msTemplatePath,
        WaitOptions wait,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(msLargeTemplatePath))
        {
            var large = await _automation.MatchRoadmapImageAsync(frame, msLargeTemplatePath, wait, ct);
            if (large is not null)
            {
                _log.Info($"命中目标机体大图模板: {large}");
                return (msLargeTemplatePath, large);
            }
        }

        var target = await _automation.MatchRoadmapImageAsync(frame, msTemplatePath, wait, ct);
        if (target is not null)
        {
            _log.Info($"命中目标机体素材: {target}");
            return (msTemplatePath, target);
        }

        return null;
    }

    /// <summary>目标机体确认帧数：连续多帧同一位置命中才点击。</summary>
    private const int MsConfirmFrames = 2;

    /// <summary>目标机体确认帧允许的中心坐标漂移（像素），超过该距离视为不同目标。</summary>
    private const int MsConfirmCenterTolerancePx = 12;

    /// <summary>
    /// 确认目标机体匹配是否稳定：在后续帧中连续命中且中心位置基本一致。
    /// 拖动动画或页面切换过程中可能出现瞬时边界匹配，稳定确认可避免误点其他机体。
    /// </summary>
    private async Task<bool> IsMatchStableAsync(
        Func<Task<byte[]>> captureFrame,
        string msTemplatePath,
        TemplateMatchResult first,
        WaitOptions wait,
        CancellationToken ct)
    {
        var current = first;
        for (var i = 1; i < MsConfirmFrames; i++)
        {
            await Task.Delay(GameTiming.ConfirmationIntervalMs, ct);
            // 确认帧也归调用方所有；失败时用最新帧比较位移，避免把消失的瞬时目标当成拖动。
            var next = await _automation.MatchRoadmapImageAsync(await captureFrame(), msTemplatePath, wait, ct);
            if (next is null ||
                Math.Abs(next.CenterX - current.CenterX) > MsConfirmCenterTolerancePx ||
                Math.Abs(next.CenterY - current.CenterY) > MsConfirmCenterTolerancePx ||
                Math.Abs(next.Width - current.Width) > MsConfirmCenterTolerancePx ||
                Math.Abs(next.Height - current.Height) > MsConfirmCenterTolerancePx)
            {
                _log.Warn($"目标机体确认帧不稳定（第 {i + 1}/{MsConfirmFrames} 帧），继续搜索");
                return false;
            }

            current = next;
        }

        return true;
    }
}

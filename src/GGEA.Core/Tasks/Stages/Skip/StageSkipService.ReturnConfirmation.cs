using GGEA.App.Automation;
using GGEA.App.UI;
using OpenCvSharp;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class StageSkipService
{
    /// <summary>结果 OK 后按任务确认关卡页：强化培育检查专用标题，AP 任务处理升级并确认次数上限。</summary>
    public Task<bool> ConfirmPostSkipAsync(string serial, UiLayout stageLayout, CancellationToken ct = default)
        => ConfirmPostSkipAsync(serial, stageLayout, 0, ct);

    private async Task<bool> ConfirmPostSkipAsync(string serial, UiLayout stageLayout, int acquisitionContinueCount, CancellationToken ct)
    {
        // 结果加载和返回确认分别计时；生产入口保留原 10 秒返回预算，旧调用方的短预算仍兼容。
        var timeoutMs = _options.PostSkipTimeoutMs ?? Math.Min(20000, _options.SkipResultTimeoutMs);
        if (!string.IsNullOrWhiteSpace(_options.PostSkipPageTemplatePath))
        {
            // 强化培育只确认返回标题；使用单次识别轮询，不触发全局继续提示或玩家升级识别。
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            while (Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var match = await _automation.FindImageAsync(serial, _options.PostSkipPageTemplatePath,
                    new WaitOptions { Roi = new Roi(0, 0, 737, 104), Threshold = 0.92 }, ct);
                if (match is not null)
                {
                    _log.Info("略过结果 OK 后已确认返回强化培育关卡");
                    return true;
                }
                await DelayWithinBudgetAsync(deadline, GameTiming.RecognitionIntervalMs, ct);
            }
            _log.Warn("略过结果 OK 后未识别到强化培育关卡标题");
            return false;
        }
        if (string.IsNullOrWhiteSpace(_options.SkipCountLimitTemplatePath) && _options.PostSkipConfirmation is null) return true;
        var countLimit = _options.PostSkipConfirmation is null ? stageLayout.Require("skipCountLimit", "关卡布局缺少 skipCountLimit 锚点") : null;
        var detectRank = !string.IsNullOrWhiteSpace(_options.PlayerRankUpTemplatePath) &&
            !string.IsNullOrWhiteSpace(_options.PlayerRankUpNextTemplatePath);
        if (detectRank || _options.ContinueUnitAcquisition)
        {
            var count = _options.PostSkipConfirmation is null ? LoadPostSkipTemplate(_options.SkipCountLimitTemplatePath) : null;
            var matcher = new TemplateMatcher();
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            var nextRankCheck = Environment.TickCount64 + Math.Max(0, _options.PlayerRankUpCheckDelayMs);
            // 返回确认不能比配置的升级轮询更慢，强敌连续展示按确认帧间隔采样。
            var pollMs = _options.ContinueUnitAcquisition ? GameTiming.ConfirmationIntervalMs
                : Math.Clamp(_options.PlayerRankUpPollIntervalMs, GameTiming.ConfirmationIntervalMs, GameTiming.RecognitionIntervalMs);
            var rankUpSeen = false;
            var recovering = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Environment.TickCount64 >= deadline)
                {
                    if (!detectRank || recovering || timeoutMs <= 0) break;
                    // 已结算的略过只恢复返回确认，保留奖励点击额度，绝不重发消耗 AP 的执行。
                    recovering = true;
                    deadline = Environment.TickCount64 + Math.Min(timeoutMs, 10000);
                    _log.Warn("略过结果 OK 后返回确认超时，开始一次恢复检查，重新识别升级提示和目标关卡。");
                }
                var screen = await _automation.CaptureScreenAsync(serial, ct);
                var checkRank = rankUpSeen || recovering || _options.ContinueUnitAcquisition || Environment.TickCount64 >= nextRankCheck;
                var state = await Task.Run(() =>
                {
                    // SSD 对暗色模板可能误命中纯色过渡画面，再核对纹理，防止提前报告成功。
                    TemplateMatchResult? Match(TemplateImages templates, Roi roi, double threshold)
                        => templates.Match(template => MatchSingle(template, roi, threshold), ct);
                    // 同份素材同时通过像素与纹理，不混合语言和通用证据。
                    TemplateMatchResult? MatchSingle(ScreenImage template, Roi roi, double threshold)
                    {
                        var candidate = matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
                            template.Width, template.Height, threshold, roi);
                        if (candidate is null) return null;
                        return TemplateMatcher.MatchesFixedTexture(screen, template, candidate.Region, .80) ? candidate : null;
                    }
                    // 高置信度正常按钮直接完成；较弱命中仍保留原阈值，但需等升级检查排除遮罩。
                    var countMatch = count is not null ? Match(count, countLimit!.Roi, _options.SkipCountLimitThreshold) : null;
                    var complete = _options.PostSkipConfirmation?.Invoke(screen, ct) ?? (countMatch is not null);
                    if (!detectRank)
                        return (RankUp: false, Next: (TemplateMatchResult?)null, Complete: complete);
                    if (countMatch is { Confidence: >= 0.98 } && !checkRank)
                        return (RankUp: false, Next: (TemplateMatchResult?)null, Complete: true);
                    if (!checkRank)
                        return (RankUp: false, Next: (TemplateMatchResult?)null, Complete: false);
                    var title = LoadPostSkipTemplate(_options.PlayerRankUpTemplatePath);
                    var rankUp = MatchRankText(screen, title, new Roi(620, 190, 720, 145), ct) is not null;
                    // 第一次只确认升级，动画等待后再从新截图读取继续提示。
                    return (RankUp: rankUp,
                        Next: rankUp && rankUpSeen ? MatchRankText(screen, LoadPostSkipTemplate(_options.PlayerRankUpNextTemplatePath),
                            new Roi(715, 900, 520, 175), ct) : null,
                        Complete: !rankUp && complete);
                }, ct);
                ct.ThrowIfCancellationRequested();
                if (state.Complete) return true;
                if (checkRank)
                    nextRankCheck = Environment.TickCount64 + Math.Max(0, _options.PlayerRankUpPollIntervalMs);
                if (state.RankUp && !rankUpSeen)
                {
                    rankUpSeen = true;
                    var animationWait = Math.Max(0, _options.PlayerRankUpAnimationWaitMs);
                    // 只补偿一次动画等待，不因反复命中无限延长总超时。
                    if (!recovering) deadline += animationWait;
                    _log.Info("检测到玩家升级，正在确认继续提示…");
                    await DelayWithinBudgetAsync(deadline, animationWait <= 5000 ? animationWait : GameTiming.ConfirmationIntervalMs, ct);
                    nextRankCheck = Environment.TickCount64;
                    continue;
                }
                // 强敌奖励可能晚于结果 OK。与结果前共用点击额度，只有新帧确认提示才续期。
                if (!state.RankUp && _options.ContinueUnitAcquisition && acquisitionContinueCount < 6)
                {
                    var reward = await Task.Run(() => _automation.TapToNext.Recognize(screen, ct), ct);
                    if (reward is not null)
                    {
                        ct.ThrowIfCancellationRequested();
                        await _automation.TapAsync(serial, reward.CenterX, reward.CenterY, ct);
                        acquisitionContinueCount++;
                        _log.Info($"结果 OK 后已点击机体奖励 TAP TO NEXT（{acquisitionContinueCount}/6），继续确认返回关卡。");
                        if (!recovering) deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
                        await DelayWithinBudgetAsync(deadline, GameTiming.ConfirmationIntervalMs, ct);
                        continue;
                    }
                }
                if (state.Next is not null)
                {
                    _log.Info("检测到 PLAYER RANK UP，点击继续并等待返回关卡页");
                    await _automation.TapAsync(serial, state.Next.CenterX, state.Next.CenterY, ct);
                }
                // 无论本帧等待还是点击，下一次判断都必须重新截图。
                await DelayWithinBudgetAsync(deadline, rankUpSeen || recovering ? GameTiming.ConfirmationIntervalMs : pollMs, ct);
            }
            _log.Warn("结果 OK 后未能确认返回关卡页（升级提示或网络等待超时）");
            return false;
        }

        if (_options.PostSkipConfirmation is not null)
        {
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            while (Environment.TickCount64 < deadline)
            {
                var screen = await _automation.CaptureScreenAsync(serial, ct);
                if (await Task.Run(() => _options.PostSkipConfirmation(screen, ct), ct)) return true;
                await DelayWithinBudgetAsync(deadline, GameTiming.RecognitionIntervalMs, ct);
            }
            return false;
        }

        // 未启用升级素材的旧调用保留原有等待行为。
        {
            var wait = new WaitOptions
            {
                TimeoutMs = timeoutMs,
                PollIntervalMs = GameTiming.RecognitionIntervalMs,
                Threshold = _options.SkipCountLimitThreshold,
                Roi = countLimit!.Roi,
            };
            var countMatch = await _automation.WaitForImageAsync(
                serial,
                _options.SkipCountLimitTemplatePath,
                wait,
                ct);
            if (countMatch is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>升级页使用完整固定字形；局部灰度相关允许蓝色背景亮度和文字比例变化。</summary>
    private static TemplateMatchResult? MatchRankText(ScreenImage screen, TemplateImages templates, Roi roi, CancellationToken ct)
    {
        if (roi.X + roi.Width > screen.Width || roi.Y + roi.Height > screen.Height) return null;
        using var frame = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
        using var crop = frame[new Rect(roi.X, roi.Y, roi.Width, roi.Height)];
        using var observed = new Mat();
        Cv2.CvtColor(crop, observed, ColorConversionCodes.BGRA2GRAY);
        return templates.Match(template =>
        {
            using var pixels = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC4, template.Bgra);
            using var reference = new Mat();
            Cv2.CvtColor(pixels, reference, ColorConversionCodes.BGRA2GRAY);
            using var scaled = new Mat();
            using var scores = new Mat();
            foreach (var scale in new[] { 1.0, 1.05, 1.025 })
            {
                ct.ThrowIfCancellationRequested();
                Cv2.Resize(reference, scaled, new Size(), scale, scale);
                if (scaled.Width > roi.Width || scaled.Height > roi.Height) continue;
                Cv2.MatchTemplate(observed, scaled, scores, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(scores, out _, out double score, out _, out Point position);
                if (double.IsFinite(score) && score >= .92)
                    return new TemplateMatchResult(new Roi(roi.X + position.X, roi.Y + position.Y,
                        scaled.Width, scaled.Height), score);
            }
            return null;
        }, ct);
    }

    /// <summary>返回确认及超时取证按需加载素材，避免正常略过重复 PNG 解码。</summary>
    private TemplateImages LoadPostSkipTemplate(string path)
    {
        if (!_postSkipTemplates.TryGetValue(path, out var template))
            _postSkipTemplates[path] = template = new TemplateImages(path);
        return template;
    }
}

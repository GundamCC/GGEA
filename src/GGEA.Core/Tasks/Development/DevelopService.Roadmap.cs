using GGEA.App.Automation;
using GGEA.App.IO;
using GGEA.App.Config;
using GGEA.App.Vision;

namespace GGEA.App.Tasks;

public sealed partial class DevelopService
{
    /// <summary>
    /// 在已打开的路线图上搜索，返回连续两帧稳定目标但不点击，供实际画面验证复用。
    /// 官方单位先同帧检查两张放大立绘，另一单位命中时最多定向拖动两次，每次识别；失败后归位并逐行或逐列扫描。
    /// 固定工具栏不参与识别或边界比较。步长保留重叠，避免大比例机体跨屏漏检。
    /// </summary>
    public async Task<TemplateMatchResult?> FindOnRoadmapAsync(string serial,
        string msTemplatePath, string msLargeTemplatePath, int maxDragsPerDirection = 0,
        int dragDurationMs = 700, double threshold = 0.86, string titleTemplatePath = "",
        double titleThreshold = 0.85, CancellationToken ct = default)
    {
        if (maxDragsPerDirection is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(maxDragsPerDirection));
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (dragDurationMs is < 100 or > 5000) dragDurationMs = Math.Clamp(dragDurationMs, 100, 5000);
        // 透明立绘使用 RGB 相关性，恢复原有 0.86 下限；同时拒绝分数接近的其它位置。
        var wait = new WaitOptions { Threshold = Math.Max(0.86, threshold), Roi = new Roi(360, 240, 1520, 700), RoadmapFixedScales = _fixedUnitScales };
        // 五分钟看门狗限制无进展的等待；有效位移会续期，宽图或高图不因正常扫描耗时被截断。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        byte[]? previous = null;
        byte[]? inspectedFrame = null;
        TemplateMatchResult? result = null;

        TemplateMatchResult? lastTitle = null;
        SelectStageTitleRecognizer? titleGlyphs = null;

        async Task CaptureAsync(bool confirmingTarget = false)
        {
            token.ThrowIfCancellationRequested();
            async Task<byte[]> Capture()
            {
                var png = await _automation.ScreenshotAsync(serial, token);
                var image = GameResolution.ReadPng(png);
                if (image.Width != GameResolution.BaseWidth || image.Height != GameResolution.BaseHeight)
                    throw new InvalidOperationException("开发路线图需要 1920×1080 游戏画面，请先调整模拟器分辨率");
                return png;
            }
            previous = await Capture();
            if (string.IsNullOrEmpty(titleTemplatePath)) return;
            var options = new WaitOptions { Threshold = titleThreshold, Roi = new Roi(200, 0, 650, 150) };
            var check = await PageObservation.ConfirmAsync(previous, Capture,
                async (frame, _) =>
                {
                    var screen = ImageCodec.DecodePng(frame);
                    var match = await _automation.MatchImageAsync(screen, titleTemplatePath, options, token);
                    if (match is not null)
                    {
                        lastTitle = match;
                        return true;
                    }
                    // 仅在已找到目标后，复核刚刚确认过的固定标题位置。拖图、入场与自定义素材沿用原校验。
                    if (!confirmingTarget || lastTitle is null || titleThreshold > .90 ||
                        !Path.GetFileName(titleTemplatePath).Equals("title_develop.png", StringComparison.OrdinalIgnoreCase))
                        return false;
                    titleGlyphs ??= new SelectStageTitleRecognizer(titleTemplatePath);
                    var title = lastTitle.Region;
                    var pixels = new byte[title.Width * title.Height * 4];
                    for (var y = 0; y < title.Height; y++)
                        Buffer.BlockCopy(screen.Bgra, ((title.Y + y) * screen.Width + title.X) * 4,
                            pixels, y * title.Width * 4, title.Width * 4);
                    // 使用同一帧完整白字与字间空隙，忽略画布底色；机体仍须在此帧通过位置和尺寸复核。
                    return titleGlyphs.Recognize(new ScreenImage { Width = title.Width, Height = title.Height, Bgra = pixels }, token);
                },
                GameTiming.ConfirmationIntervalMs, token);
            previous = check.Frame;
            if (!check.Confirmed) throw new InvalidOperationException("路线图标题已消失，停止拖动与点击");
        }

        async Task<bool> InspectAsync()
        {
            // 确认帧若发生比例切换，复用新帧再搜索一次，不能把尚未检索的新比例拖出视野。
            // 正常未命中立即返回；仅不稳定候选触发这一次有限重试。
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // 只复用字节完全一致且未确认目标的已检索帧，不用宽松的位移容差跳过新节点。
                if (inspectedFrame is not null && previous!.AsSpan().SequenceEqual(inspectedFrame)) return false;
                inspectedFrame = previous;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var found = await FindMsMatchAsync(previous!, msLargeTemplatePath, msTemplatePath, wait, token);
                _log.Info($"路线图单帧检索耗时 {elapsed.Elapsed.TotalMilliseconds:F0}ms，{(found is null ? "未命中" : "发现候选")}");
                if (found is null) return false;
                if (!await IsMatchStableAsync(async () => { await CaptureAsync(confirmingTarget: true); return previous!; },
                        found.Value.TemplatePath, found.Value.Match, wait, token)) continue;
                result = found.Value.Match;
                _log.Info($"目标机体已稳定匹配: {result}");
                return true;
            }
            return false;
        }

        // 截图相似只说明未观察到位移，不能用一次未生效的手势直接授权换行或结束扫描。
        async Task<(bool Moved, bool Found)> MoveAsync(int dx, int dy)
        {
            var staticGestures = 0;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var before = previous!;
                // 两次仍缺少对应点时改用短步，让稀疏节点在前后帧中保留更多重叠。
                var stepX = attempt == 2 ? dx / 2 : dx;
                var stepY = attempt == 2 ? dy / 2 : dy;
                // 换一个画布内的起点复核同一方向，避开原起点可能覆盖的节点/控件；不改变游戏缩放。
                var sx = dx == 0 ? 1550 - attempt * 80 : 1140 - stepX / 2;
                var sy = dy == 0 ? 840 - attempt * 50 : 590 - stepY / 2;
                _log.Info($"路线图精确拖动: ({sx},{sy}) → ({sx + stepX},{sy + stepY})");
                await _automation.DragAsync(serial, sx, sy, sx + stepX, sy + stepY, dragDurationMs, token);
                // DragAsync 已等待抬手。短暂等待后按新帧确认页面和移动，不在每个网格额外等五秒。
                await Task.Delay(GameTiming.ConfirmationIntervalMs, token);
                await CaptureAsync();
                var displacement = RoadmapMotion.Measure(before, previous!, stepX, stepY, out var stationary);
                if (displacement != 0)
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    _log.Info($"路线图拖动后已观察到画布位移 {displacement}px（第 {attempt + 1} 次手势）");
                    return (true, false);
                }
                // 稀疏节点、加载或粒子容差可能掩盖真实变化；本帧必须识别后才能继续拖动。
                if (await InspectAsync()) return (false, true);
                if (stationary && ++staticGestures >= 2)
                {
                    _log.Info("路线图已在两个手势起点复核画布静止，按当前边界换向或结束搜索");
                    return (false, false);
                }
                if (attempt < 2) _log.Info(stationary ? "路线图静止，换起点复核当前边界" : "路线图位移证据不足，复核同一方向");
            }
            throw new InvalidOperationException("路线图无法确认位移或边界，停止输入；本次未完成画布遍历");
        }

        await CaptureAsync();
        // 两张大图共用首次画面，不增加截图或改变游戏缩放；自定义目标不推断相邻关系。
        var shortcut = GetRoadmapShortcut(msTemplatePath, msLargeTemplatePath);
        var largeWait = wait with { RoadmapFixedScales = true, RoadmapLargeOnly = true };
        TemplateMatchResult? initialTarget = null;
        TemplateMatchResult? initialOther = null;
        if (shortcut is { } hint)
        {
            initialTarget = await _automation.MatchRoadmapImageAsync(previous!, msTemplatePath, largeWait, token);
            if (File.Exists(hint.OtherTemplate))
                initialOther = await _automation.MatchRoadmapImageAsync(previous!, hint.OtherTemplate, largeWait, token);
            else
                _log.Warn("路线图快捷方向素材缺失，保留原目标搜索流程");
        }
        // 保留首次大小两档目标识别：小图命中也直接确认，不能因辅助大图存在而拖走目标。
        if (await InspectAsync()) return result;
        if (shortcut is { } direction && initialTarget is null && initialOther is not null)
        {
            var otherStable = await IsMatchStableAsync(async () => { await CaptureAsync(); return previous!; },
                direction.OtherTemplate, initialOther, largeWait, token);
            // 确认帧可能已出现目标；辅助单位不稳定时也检查最新帧，再回退归位。
            if (await InspectAsync()) return result;
            if (otherStable)
            {
                // 快捷预算按实际手势计数，不复用带两次边界复核的 MoveAsync，避免扩成四次拖动。
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    var sx = 1140 - direction.Dx / 2;
                    var sy = 840 - attempt * 50;
                    _log.Info($"放大辅助单位已确认，向{(direction.Dx > 0 ? "右" : "左")}快捷拖动（{attempt + 1}/2）并识别目标");
                    await _automation.DragAsync(serial, sx, sy, sx + direction.Dx, sy, dragDurationMs, token);
                    await Task.Delay(GameTiming.RecognitionIntervalMs, token);
                    await CaptureAsync();
                    // 每次手势后都识别，即使像素未变也不沿用上一轮未命中结论。
                    inspectedFrame = null;
                    if (await InspectAsync()) return result;
                }
                _log.Info("两次快捷拖动后均未确认目标，继续归位并按实际边界扫描");
            }
        }
        // 实机上 850px 手势可推进约 1300px，缩短扫描步幅以保留整台机体与下一帧的重叠区。
        const int scanStepX = 490;
        var lastMoveWasHorizontal = false;
        // 任意起点到角落沿途也属于搜索范围。记住最后实际推进的轴，抵达角落后沿另一轴展开。
        _log.Info("路线图定位左上角：检查页面与位移，疑似边界先识别目标再复核");
        foreach (var (dx, dy) in new[] { (0, 250), (scanStepX, 0) })
        {
            var edge = false;
            for (var i = 0; maxDragsPerDirection == 0 || i < maxDragsPerDirection; i++)
            {
                var move = await MoveAsync(dx, dy);
                if (move.Found) return result;
                if (!move.Moved) { edge = true; break; }
                lastMoveWasHorizontal = dx != 0;
                if (await InspectAsync()) return result;
            }
            if (!edge)
            {
                // 预算内最后一帧仍有机会命中，但不能在边界未确认时声称覆盖全图。
                if (await InspectAsync()) return result;
                _log.Warn("路线图归位达到拖动上限，未确认边界，停止搜索"); return null;
            }
        }
        var alongColumns = lastMoveWasHorizontal;
        var stripName = alongColumns ? "列" : "行";
        var sweep = alongColumns ? (X: 0, Y: -250) : (X: -scanStepX, Y: 0);
        var advance = alongColumns ? (X: -scanStepX, Y: 0) : (X: 0, Y: -250);
        _log.Info($"路线图已归位，开始逐{stripName}扫描");
        for (var strip = 0; maxDragsPerDirection == 0 || strip < maxDragsPerDirection; strip++)
        {
            var edge = false;
            for (var step = 0; maxDragsPerDirection == 0 || step < maxDragsPerDirection; step++)
            {
                var move = await MoveAsync(sweep.X, sweep.Y);
                if (move.Found) return result;
                if (!move.Moved) { edge = true; break; }
                if (await InspectAsync()) return result;
            }
            if (!edge) { _log.Warn("路线图扫描达到指定步数上限，停止搜索"); return null; }
            var advanced = await MoveAsync(advance.X, advance.Y);
            if (advanced.Found) return result;
            if (!advanced.Moved) { _log.Warn("路线图扫描结束，未找到稳定目标机体"); return null; }
            if (await InspectAsync()) return result;
            sweep = (-sweep.X, -sweep.Y);
            _log.Info($"路线图换至第 {strip + 2} {stripName}后反向扫描");
        }
        _log.Warn("路线图搜索达到指定范围上限，未找到目标机体");
        return null;
    }

    /// <summary>右/左指 ADB 手势方向：找原型高达向左，找铁球向右；仅官方路径使用该相邻关系。</summary>
    private static (string OtherTemplate, int Dx)? GetRoadmapShortcut(string target, string customLarge)
    {
        if (!string.IsNullOrEmpty(customLarge)) return null;
        var prototype = ConfigService.ResolvePath("resources/templates/develop/dev_unit/ogk_g0070u00100.png");
        var ball = ConfigService.ResolvePath("resources/templates/develop/dev_unit/ogk_g0010u00700.png");
        var resolved = Path.GetFullPath(ConfigService.ResolvePath(target));
        if (string.Equals(resolved, Path.GetFullPath(prototype), FileSystemPaths.Comparison)) return (ball, -850);
        if (string.Equals(resolved, Path.GetFullPath(ball), FileSystemPaths.Comparison)) return (prototype, 850);
        return null;
    }
}

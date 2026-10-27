using GGEA.App.Automation;
using System.IO;
using GGEA.App.Config;
using GGEA.App.Device;
using GGEA.App.Logging;
using GGEA.App.Tasks;

namespace GGEA.App.Vision;

/// <summary>关卡节点识别结果。只有黄色选中效果、类型和编号都可信时 Position 才非空。</summary>
public sealed record StageNodeRecognition(
    int CenterX,
    int CenterY,
    WorkStageKind Kind,
    int? Number,
    WorkStageNodeState State,
    WorkStagePosition? Position,
    string Message,
    Roi? NumberRegion = null)
{
    /// <summary>CLEAR 与 COMPLETE 都表示通关；略过仍由业务流程复核实际按钮、次数和 AP。</summary>
    public bool HasPassed => State is WorkStageNodeState.Completed or WorkStageNodeState.Cleared;

    /// <summary>只表明编号观察缺失，不伪造节点、坐标、未通关状态或作品进度。</summary>
    public static StageNodeRecognition Unidentified { get; } = new(0, 0, WorkStageKind.Unknown,
        null, WorkStageNodeState.Unknown, null, "当前关卡编号未识别，沿用游戏当前选择。");
}

/// <summary>左侧具体关卡节点识别接口，便于导航流程在标题识别失败时安全回退。</summary>
public interface IStageNodeRecognizer
{
    /// <summary>识别当前黄色光圈选中的关卡节点。</summary>
    Task<StageNodeRecognition?> RecognizeSelectedAsync(
        string serial,
        HardStageEntry work,
        CancellationToken ct = default);
}

/// <summary>NEXT 标记和其正下方完整编号授权的待选节点；坐标落在实际编号上，不猜测台座位置。</summary>
public sealed record StageNextNodeRecognition(int CenterX, int CenterY, WorkStagePosition Position,
    Roi MarkerRegion, Roi NumberRegion);

/// <summary>已校验截图的 NEXT 观察边界；截图安全检查、两帧确认和点击后选中复核由导航流程负责。</summary>
public interface IStageNextNodeRecognizer
{
    /// <summary>只返回唯一、编号有效且未完成未锁定的 NEXT 目标。</summary>
    StageNextNodeRecognition? RecognizeNext(ScreenImage screen, HardStageEntry work, CancellationToken ct = default);
}

/// <summary>关卡轨道的页面几何；历史关卡整体上移，不能靠放宽光圈或字形阈值兼容。</summary>
public sealed record StageNodeLayout(Roi Nodes, Roi NextMarkers, int HaloTop)
{
    public static StageNodeLayout Main { get; } = new(new(0, 600, 1015, 380), new(0, 400, 1015, 380), 800);
    public static StageNodeLayout History { get; } = new(new(0, 300, 1015, 400), new(0, 160, 1015, 440), 480);
}

/// <summary>
/// 作品具体关卡识别器。
/// 黄色光圈在左下轨道确定选中节点，再读取其类型、数字和状态；
/// NEXT 单独向上扩展搜索并关联下方节点，两者均排除右侧详情内容。
/// </summary>
public sealed class StageNodeRecognizer : IStageNodeRecognizer, IStageNextNodeRecognizer, IDisposable
{
    /// <summary>用户校准的关卡轨道区域。</summary>
    public static readonly Roi StageNodesRoi = new(0, 600, 1015, 380);

    // NEXT 悬浮于编号上方，BOSS 的标记可高至 y=456，不能只在原来的下方轨道中搜索。
    private const double NextMarkerThreshold = 0.92;

    private const double LabelThreshold = 0.80;
    private const double StateThreshold = 0.80;
    // 相邻关卡节点的横向距离约为一个槽位；标签必须以黄色光圈中心为准，
    // 不能因宽搜索区命中相邻 HARD 标签而将普通关卡错误升级为 HARD。
    private const int LabelCenterTolerancePx = 40;
    // 同色像素必须覆盖大部分字形，且主要落在字形内；分别排除零散装饰与整块同色背景。
    private const double MinimumLabelColorCoverage = 0.70;
    private const double MinimumLabelColorPrecision = 0.70;

    private readonly IAdbService _adb;
    private readonly TemplateMatcher _matcher;
    private readonly IGgeaLog _log;
    private readonly string _templateDirectory;
    private IReadOnlyDictionary<string, NodeTemplate>? _templates;
    private readonly IStageNumberRecognizer _numberRecognizer;
    private readonly bool _ownsNumberRecognizer;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly RecognitionEvidence? _evidence;
    private readonly StageNodeLayout _layout;
    // 同一识别器由设备任务串行调用；只保留本次观察的失败帧，成功或下一次观察时清除。
    private RecognitionFrame? _lastFailure;

    /// <summary>复用现有 ADB/模板服务；帧间延时可替换，以确定性验证动画过渡与取消。</summary>
    public StageNodeRecognizer(
        IAdbService adb,
        TemplateMatcher matcher,
        IGgeaLog log,
        string templateDirectory, Func<int, CancellationToken, Task>? delay = null,
        IStageNumberRecognizer? numberRecognizer = null, RecognitionEvidence? evidence = null,
        StageNodeLayout? layout = null)
    {
        _adb = adb;
        _matcher = matcher;
        _log = log;
        _templateDirectory = templateDirectory;
        _delay = delay ?? Task.Delay;
        _ownsNumberRecognizer = numberRecognizer is null;
        _numberRecognizer = numberRecognizer ?? new OnnxStageNumberRecognizer();
        _evidence = evidence;
        _layout = layout ?? StageNodeLayout.Main;
    }

    /// <summary>推图会实际出击，必须完整加载类型、编号和状态素材，不能把素材缺失当作未通关。</summary>
    public void ValidateForProgress()
    {
        var templates = LoadTemplates();
        var required = new[] { "label_hard", "label_boss", "label_side_story", "locked_normal", "locked_hard", "locked_boss", "complete_badge", "clear_badge", "next_marker" };
        foreach (var name in required)
            if (!templates.TryGetValue(name, out var template) || !template.Mask.Any(pixel => pixel != 0))
                throw new InvalidOperationException($"自动推图所需关卡模板或遮罩不可用：{name}");
        _numberRecognizer.Validate();
    }

    /// <summary>截取当前页面并识别被黄色光圈选中的具体关卡。</summary>
    public async Task<StageNodeRecognition?> RecognizeSelectedAsync(
        string serial,
        HardStageEntry work,
        CancellationToken ct = default)
    {
        _lastFailure = null;
        var png = await _adb.ScreenshotAsync(serial, ct);
        var screen = ImageCodec.DecodePng(png);
        var first = RecognizeSelected(screen, work, ct);
        if (first?.Position is null)
        {
            _lastFailure = new(serial, png, screen, []);
            return null;
        }
        // 两帧同意才交给出击流程；页面滚动和动画过渡不能凭单帧产生可信编号。
        await _delay(GameTiming.ConfirmationIntervalMs, ct);
        png = await _adb.ScreenshotAsync(serial, ct);
        screen = ImageCodec.DecodePng(png);
        var next = RecognizeSelected(screen, work, ct);
        if (next?.Position is not null && first.Number == next.Number && first.Kind == next.Kind &&
            first.State == next.State && Math.Abs(first.CenterX - next.CenterX) <= 8 &&
            HasStableNumberPosition(first, next)) return next;
        _log.Info("关卡编号或选中状态尚未稳定，等待重新识别。");
        _lastFailure = new(serial, png, screen, []);
        return null;
    }

    /// <summary>由导航流程在需要诊断时取证；只保存同设备实际失败帧，不另行截图或写入成功帧。</summary>
    public Task SaveLastFailureAsync(string serial)
        => _evidence is not null && _lastFailure is { } frame && frame.Serial == serial
            ? _evidence.SaveAsync("stage-selected-node", frame) : Task.CompletedTask;

    /// <summary>光圈呼吸动画会改变外接框中心；以完整编号笔画验证位置，保持 8/12 像素容差。</summary>
    private static bool HasStableNumberPosition(StageNodeRecognition first, StageNodeRecognition next)
        => first.NumberRegion is Roi a && next.NumberRegion is Roi b
            ? Math.Abs(a.CenterX - b.CenterX) <= 8 && Math.Abs(a.CenterY - b.CenterY) <= 12
            : Math.Abs(first.CenterY - next.CenterY) <= 12;

    /// <summary>读取唯一 NEXT 下的实际类型、编号和状态；无标记或歧义时不按上一关编号加一。</summary>
    public StageNextNodeRecognition? RecognizeNext(ScreenImage screen, HardStageEntry work, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var templates = LoadTemplates();
        var search = Clip(_layout.NextMarkers, screen.Width, screen.Height);
        var marker = Match(screen, search, templates, "next_marker", NextMarkerThreshold, ct);
        if (marker is null) return null;
        // 排除首个标记所在矩形后再搜索，多个 NEXT 不能靠最高分擅自决定点击目标。
        var region = marker.Region;
        var remaining = new[]
        {
            new Roi(search.X, search.Y, region.X - search.X, search.Height),
            new Roi(region.X + region.Width, search.Y, search.X + search.Width - region.X - region.Width, search.Height),
            new Roi(region.X, search.Y, region.Width, region.Y - search.Y),
            new Roi(region.X, region.Y + region.Height, region.Width, search.Y + search.Height - region.Y - region.Height),
        };
        if (remaining.Any(roi => roi.Width > 0 && roi.Height > 0 &&
            Match(screen, roi, templates, "next_marker", NextMarkerThreshold, ct) is not null))
        {
            _log.Info("当前选关页出现多个 NEXT 标记，等待唯一目标。");
            return null;
        }

        var top = Math.Max(_layout.Nodes.Y, region.Y + region.Height + 45);
        var bottom = Math.Min(_layout.Nodes.Y + _layout.Nodes.Height - 100, region.Y + region.Height + 280);
        if (bottom <= top) return null;
        var number = RecognizeNumber(screen, Clip(new Roi(marker.CenterX - 85, top, 170, bottom - top),
            screen.Width, screen.Height), marker.CenterX, ct);
        if (number.Number is not int value || number.Region is not Roi digits) return null;
        var labelTop = region.Y + region.Height;
        var kind = RecognizeKind(screen, Clip(new Roi(marker.CenterX - 140, labelTop, 280,
            Math.Max(1, digits.Y - labelTop)), screen.Width, screen.Height), marker.CenterX, templates, ct);
        if (kind == WorkStageKind.Normal && value == work.NormalCount) kind = WorkStageKind.Boss;
        var state = RecognizeState(screen, Clip(new Roi(marker.CenterX - 110, digits.Y + digits.Height + 8,
            220, _layout.Nodes.Y + _layout.Nodes.Height - digits.Y - digits.Height - 8), screen.Width, screen.Height), kind, templates);
        if (state != WorkStageNodeState.Incomplete || !WorkStageSequencePlanner.TryGetPosition(
            work.NormalCount, work.SideStories, work.HardCount, kind, value, out var position) || position is null)
            return null;
        return new(digits.CenterX, digits.CenterY, position, region, digits);
    }

    /// <summary>供测试和调用方对已获取截图进行分析，避免同一状态重复截图。</summary>
    public StageNodeRecognition? RecognizeSelected(ScreenImage screen, HardStageEntry work, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var roi = Clip(_layout.Nodes, screen.Width, screen.Height);
        var candidates = FindSelectedHalos(screen, roi);
        var selected = SelectUniqueHalo(candidates);
        if (selected is null)
        {
            _log.Info(candidates.Count == 0
                ? "关卡轨道暂未找到完整黄色选中效果，等待新截图。"
                : "关卡轨道存在多个黄色选中候选，等待唯一目标。");
            return null;
        }

        var templates = LoadTemplates();
        var numberRoi = CreateNumberRoi(selected.Value.CenterX, selected.Value.CenterY, roi, screen);
        var numberResult = RecognizeNumber(screen, numberRoi, selected.Value.CenterX, ct);
        var number = numberResult.Number;
        // 轨道上下交错，较高节点的 HARD/BOSS 标签位于 y=600 之上。
        // 以已定位的编号为锚点读其正上方标签，不能用固定轨道顶边裁掉半个字形。
        var labelRoi = CreateLabelRoi(selected.Value.CenterX,
            numberResult.Region?.Y ?? roi.Y + 100, screen);
        var kind = RecognizeKind(screen, labelRoi, selected.Value.CenterX, templates, ct);
        // 未贴 BOSS 标签的最后 NORMAL 关仍按 BOSS 处理，保证配置序列只有一个入口。
        if (kind == WorkStageKind.Normal && number == work.NormalCount)
        {
            kind = WorkStageKind.Boss;
        }
        var stateRoi = CreateStateRoi(selected.Value.CenterX, selected.Value.CenterY, roi, screen);
        var state = RecognizeState(screen, stateRoi, kind, templates);

        WorkStagePosition? position = null;
        if (number is int stageNumber &&
            !WorkStageSequencePlanner.TryGetPosition(
                work.NormalCount,
                work.SideStories,
                work.HardCount,
                kind,
                stageNumber,
                out position))
        {
            _log.Warn($"关卡编号与作品配置不一致：{work.Work} {kind} {stageNumber}");
        }

        var numberText = number?.ToString() ?? "未知编号";
        var positionText = position is null ? "未能映射至作品序列" : $"序号 {position.OverallIndex}";
        var message = $"当前选中 {GetKindText(kind)} {numberText}，状态 {GetStateText(state)}，{positionText}";
        _log.Info(message);
        return new StageNodeRecognition(
            selected.Value.CenterX,
            selected.Value.CenterY,
            kind,
            number,
            state,
            position,
            message,
            numberResult.Region);
    }

    /// <summary>返回页身份只需唯一选中光圈与完整可信编号；不映射作品序列、不决定通关或发起点击。</summary>
    public bool HasSelectedNode(ScreenImage screen, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var roi = Clip(_layout.Nodes, screen.Width, screen.Height);
        var selected = SelectUniqueHalo(FindSelectedHalos(screen, roi));
        if (selected is null) return false;
        var number = _numberRecognizer.Recognize(screen,
            CreateNumberRoi(selected.Value.CenterX, selected.Value.CenterY, roi, screen), selected.Value.CenterX, ct);
        return number.Number is > 0 && number.Region is not null;
    }

    /// <summary>
    /// 定位台座上的亮黄色环边，不把整块半透明光柱当成环。
    /// 实机呼吸动画会让光柱与台座连成超过 100px 高的区域；亮边则保持稳定的左右弧结构。
    /// 保留原来的宽高、左右弧及唯一性限制，星级行和单侧黄弧不能授权选中。
    /// </summary>
    private List<Roi> FindSelectedHalos(ScreenImage screen, Roi roi)
    {
        var width = roi.Width;
        var height = roi.Height;
        var pixels = new bool[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((roi.Y + y) * screen.Width + roi.X + x) * 4;
                var b = screen.Bgra[offset];
                var g = screen.Bgra[offset + 1];
                var r = screen.Bgra[offset + 2];
                // 光圈必须在当前页面的台座区域；主线与历史轨道上方的星级不能授权选中节点。
                // 环边明亮且偏黄，呼吸光柱较暗；排除白色高光与橙红台座，避免黏连改变外接框。
                pixels[y * width + x] = roi.Y + y >= _layout.HaloTop && r >= 235 && g >= 200 && b <= 180 && r - b >= 55;
            }
        }

        // 光圈存在细小断点，局部膨胀后再做连通域，避免把同一圆环拆成数段。
        var expanded = new bool[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!pixels[y * width + x])
                {
                    continue;
                }

                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                        {
                            expanded[ny * width + nx] = true;
                        }
                    }
                }
            }
        }

        var visited = new bool[expanded.Length];
        var components = new List<Roi>();
        var queue = new Queue<int>();
        for (var start = 0; start < expanded.Length; start++)
        {
            if (!expanded[start] || visited[start])
            {
                continue;
            }

            visited[start] = true;
            queue.Enqueue(start);
            var minX = width;
            var maxX = 0;
            var minY = height;
            var maxY = 0;
            var count = 0;
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width;
                var y = index / width;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                count++;

                foreach (var neighbor in GetNeighbors(x, y, width, height))
                {
                    if (expanded[neighbor] && !visited[neighbor])
                    {
                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            var candidateWidth = maxX - minX + 1;
            var candidateHeight = maxY - minY + 1;
            if (candidateWidth < 40 || candidateWidth > 260 || candidateHeight < 10 || candidateHeight > 100 || count < 120)
            {
                continue;
            }

            components.Add(new Roi(minX, minY, candidateWidth, candidateHeight));
        }

        var rings = new List<Roi>();
        // 完整环必须覆盖台座宽度；单侧短弧不能独立作为选中证据。
        void Consider(Roi ring)
        {
            rings.Add(new Roi(roi.X + ring.X, roi.Y + ring.Y, ring.Width, ring.Height));
        }
        foreach (var ring in components.Where(component => component.Width >= 140)) Consider(ring);

        // 已通关的 COMPLETE 标记遮住环的中部：同高度、相近宽度的左右黄弧需合起来定位台座。
        // 外接宽高仍受原光圈范围限制，避免把远处分离的黄色装饰拼成选中效果。
        var arcs = components.Where(component => component.Width < 140).OrderBy(component => component.X).ToArray();
        for (var leftIndex = 0; leftIndex < arcs.Length; leftIndex++)
            for (var rightIndex = leftIndex + 1; rightIndex < arcs.Length; rightIndex++)
            {
                var left = arcs[leftIndex]; var right = arcs[rightIndex];
                var gap = right.X - left.X - left.Width;
                if (gap is < 40 or > 180 || Math.Abs(left.Width - right.Width) > 24 ||
                    Math.Abs(left.CenterY - right.CenterY) > 12) continue;
                var top = Math.Min(left.Y, right.Y);
                var ring = new Roi(left.X, top, right.X + right.Width - left.X,
                    Math.Max(left.Y + left.Height, right.Y + right.Height) - top);
                if (ring.Width is >= 160 and <= 260 && ring.Height <= 100) Consider(ring);
            }
        return rings;
    }

    /// <summary>同一台座的内外环允许重复候选，分属不同台座的黄环必须拒绝，不能按面积排名。</summary>
    private static (int CenterX, int CenterY)? SelectUniqueHalo(IReadOnlyList<Roi> rings)
    {
        if (rings.Count == 0) return null;
        var largest = rings.OrderByDescending(ring => ring.Width * ring.Height).First();
        return rings.All(ring => Math.Abs(ring.CenterX - largest.CenterX) <= 24 &&
                                 Math.Abs(ring.CenterY - largest.CenterY) <= 40)
            ? (largest.CenterX, largest.CenterY) : null;
    }

    /// <summary>枚举四连通邻居，避免对角相邻星级图标误连为选中光圈。</summary>
    private static IEnumerable<int> GetNeighbors(int x, int y, int width, int height)
    {
        if (x > 0) yield return y * width + x - 1;
        if (x + 1 < width) yield return y * width + x + 1;
        if (y > 0) yield return (y - 1) * width + x;
        if (y + 1 < height) yield return (y + 1) * width + x;
    }

    /// <summary>根据 HARD、BOSS、SIDE STORY 标签判定类型；无标签即 NORMAL。</summary>
    private WorkStageKind RecognizeKind(
        ScreenImage screen,
        Roi roi,
        int selectedCenterX,
        IReadOnlyDictionary<string, NodeTemplate> templates, CancellationToken ct)
    {
        TemplateMatchResult? MatchLabel(string name)
        {
            if (!templates.TryGetValue(name, out var template)) return null;
            foreach (var candidate in template.Candidates)
            {
                var result = MatchLabelSingle(name, candidate);
                if (result is not null) return result;
            }
            return null;
        }

        // 文字、遮罩、中心与颜色必须来自同一份素材，再决定是否补试通用版本。
        TemplateMatchResult? MatchLabelSingle(string name, NodeTemplate template)
        {
            // 在搜索前限制候选中心，避免区域边缘的背景高分遮住节点正上方的真实标签。
            var left = Math.Max(roi.X, selectedCenterX - LabelCenterTolerancePx - template.Width / 2);
            var right = Math.Min(roi.X + roi.Width,
                selectedCenterX + LabelCenterTolerancePx - template.Width / 2 + template.Width);
            var search = new Roi(left, roi.Y, Math.Max(1, right - left), roi.Height);
            var match = MatchSingle(screen, search, template, LabelThreshold, ct);
            if (match is not null &&
                   Math.Abs(match.CenterX - selectedCenterX) <= LabelCenterTolerancePx &&
                   HasExpectedLabelColor(screen, match.Region, name, template)) return match;

            // 灰度最优点可能是同亮度的背景，色彩校验失败不能代表真实标签不存在。
            // 在已限定的小区域内按相同字形覆盖率/精度筛选，再以原灰度阈值复核；不降阈值兜底。
            // 色彩只转换一次，用积分图先排除颜色总量不可能达标的位置，避免逐候选扫描整张字形。
            var stride = search.Width + 1;
            var colors = new bool[search.Width * search.Height];
            var sums = new int[stride * (search.Height + 1)];
            for (var y = 0; y < search.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (var x = 0; x < search.Width; x++)
                {
                    var offset = ((search.Y + y) * screen.Width + search.X + x) * 4;
                    var colored = IsExpectedLabelColor(screen.Bgra[offset + 2], screen.Bgra[offset + 1], screen.Bgra[offset], name);
                    colors[y * search.Width + x] = colored;
                    sums[(y + 1) * stride + x + 1] = (colored ? 1 : 0) + sums[y * stride + x + 1] +
                        sums[(y + 1) * stride + x] - sums[y * stride + x];
                }
            }
            var glyphs = Enumerable.Range(0, template.Mask.Length).Where(index => template.Mask[index] != 0)
                .Select(index => index / template.Width * search.Width + index % template.Width).ToArray();
            if (glyphs.Length == 0) return null;
            TemplateMatchResult? verified = null;
            for (var y = 0; y <= search.Height - template.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (var x = 0; x <= search.Width - template.Width; x++)
                {
                    var candidateRight = x + template.Width; var bottom = y + template.Height;
                    var colored = sums[bottom * stride + candidateRight] - sums[y * stride + candidateRight] -
                        sums[bottom * stride + x] + sums[y * stride + x];
                    var required = (int)Math.Ceiling(Math.Max(glyphs.Length * MinimumLabelColorCoverage,
                        colored * MinimumLabelColorPrecision));
                    if (colored < required || required > glyphs.Length) continue;
                    var overlap = 0;
                    var origin = y * search.Width + x;
                    for (var i = 0; i < glyphs.Length; i++)
                    {
                        if (colors[origin + glyphs[i]]) overlap++;
                        if (overlap >= required || overlap + glyphs.Length - i - 1 < required) break;
                    }
                    if (overlap < required) continue;
                    var candidate = new Roi(search.X + x, search.Y + y, template.Width, template.Height);
                    var found = MatchSingle(screen, candidate, template, LabelThreshold, ct);
                    if (found is not null && (verified is null || found.Confidence > verified.Confidence)) verified = found;
                }
            }
            return verified;
        }

        var hard = MatchLabel("label_hard");
        var boss = MatchLabel("label_boss");
        var sideStory = MatchLabel("label_side_story");
        var best = new[]
        {
            (Kind: WorkStageKind.Hard, Match: hard),
            (Kind: WorkStageKind.Boss, Match: boss),
            (Kind: WorkStageKind.SideStory, Match: sideStory),
        }
        .Where(item => item.Match is not null)
        .OrderByDescending(item => item.Match!.Confidence)
        .FirstOrDefault();

        return best.Match is null ? WorkStageKind.Normal : best.Kind;
    }

    /// <summary>
    /// 三种特殊关卡标签均有稳定的主题色。普通关卡上方的橙色 NEXT 装饰与字形轮廓相近，
    /// 仅靠灰度模板会产生假阳性；颜色必须与模板遮罩字形重合，不能用矩形内少量同色像素授权。
    /// </summary>
    private static bool HasExpectedLabelColor(ScreenImage screen, Roi region, string labelName, NodeTemplate template)
    {
        var coloredPixels = 0;
        var glyphPixels = 0;
        var coloredGlyphPixels = 0;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var offset = (y * screen.Width + x) * 4;
                var glyph = template.Mask[(y - region.Y) * template.Width + x - region.X] != 0;
                if (glyph) glyphPixels++;
                var b = screen.Bgra[offset];
                var g = screen.Bgra[offset + 1];
                var r = screen.Bgra[offset + 2];
                var isExpectedColor = IsExpectedLabelColor(r, g, b, labelName);
                if (isExpectedColor)
                {
                    coloredPixels++;
                    if (glyph) coloredGlyphPixels++;
                }
            }
        }

        return glyphPixels > 0 && coloredPixels > 0 &&
               (double)coloredGlyphPixels / glyphPixels >= MinimumLabelColorCoverage &&
               (double)coloredGlyphPixels / coloredPixels >= MinimumLabelColorPrecision;
    }

    /// <summary>初次匹配与背景干扰复查共用同一主题色判据，不因回退改变识别门槛。</summary>
    private static bool IsExpectedLabelColor(byte r, byte g, byte b, string labelName) => labelName switch
    {
        "label_hard" => r >= 150 && r - g >= 70 && r - b >= 70,
        "label_boss" => r >= 150 && b >= 120 && r - g >= 60 && b - g >= 25,
        "label_side_story" => g >= 120 && g - r >= 30 && g - b >= 10,
        _ => false,
    };

    /// <summary>整行 OCR 的可信数字才可作为编号；失败由导航流程有限重试，不回退猜测旧模板结果。</summary>
    private StageNumberRecognition RecognizeNumber(ScreenImage screen, Roi roi, int selectedCenterX, CancellationToken ct)
    {
        var result = _numberRecognizer.Recognize(screen, roi, selectedCenterX, ct);
        _log.Info(result.Message);
        return result;
    }

    /// <summary>仅释放自身创建的 OCR 会话；窗口注入的共享会话由窗口在业务收尾后统一释放。</summary>
    public void Dispose()
    {
        if (_ownsNumberRecognizer && _numberRecognizer is IDisposable disposable) disposable.Dispose();
    }

    /// <summary>锁定、COMPLETE 与 CLEAR 分别保留真实状态，不把未满目标的 CLEAR 当作未通关。</summary>
    private WorkStageNodeState RecognizeState(
        ScreenImage screen,
        Roi roi,
        WorkStageKind kind,
        IReadOnlyDictionary<string, NodeTemplate> templates)
    {
        // 各类台座造型不同，跨类型搜索会把未锁定 HARD 台座误命中为 locked_boss。
        // 类型已由当前编号上方的标签确认，只允许对应类型的锁定证据参与状态判断。
        var lockedTemplate = kind switch
        {
            WorkStageKind.Hard => "locked_hard",
            WorkStageKind.Boss => "locked_boss",
            _ => "locked_normal",
        };
        if (Match(screen, roi, templates, lockedTemplate, StateThreshold) is not null)
        {
            return WorkStageNodeState.Locked;
        }

        var complete = Match(screen, roi, templates, "complete_badge", StateThreshold,
            accept: (match, template) => HasCompleteBadgeColors(screen, match.Region, template));
        // 黄色光柱也可能达到 COMPLETE 的灰度门槛；必须同时具有原标记的青色图案，
        // 否则会把尚未通关的最终 HARD 当作作品完成，不能仅凭亮度宣布完成。
        if (complete is not null) return WorkStageNodeState.Completed;

        // CLEAR 的蓝色边框与 COMPLETE 相近，使用独立原像素字形及更严格的匹配门槛，仍复核青色分布。
        var clear = Match(screen, roi, templates, "clear_badge", .90,
            accept: (match, template) => HasCompleteBadgeColors(screen, match.Region, template));
        return clear is not null
            ? WorkStageNodeState.Cleared : WorkStageNodeState.Incomplete;
    }

    /// <summary>青色标记既要覆盖模板的有效图案，也不能只是同色背景；与标签校验使用相同覆盖/精度要求。</summary>
    private static bool HasCompleteBadgeColors(ScreenImage screen, Roi region, NodeTemplate template)
    {
        static bool IsCyan(byte r, byte g, byte b) => g >= 130 && b >= 150 && g - r >= 25 && b - r >= 25;
        var expected = 0; var actual = 0; var overlap = 0;
        for (var y = 0; y < template.Height; y++)
            for (var x = 0; x < template.Width; x++)
            {
                var index = y * template.Width + x;
                var offset = index * 4;
                var target = template.Mask[index] != 0 && IsCyan(template.Bgra[offset + 2], template.Bgra[offset + 1], template.Bgra[offset]);
                offset = ((region.Y + y) * screen.Width + region.X + x) * 4;
                var observed = IsCyan(screen.Bgra[offset + 2], screen.Bgra[offset + 1], screen.Bgra[offset]);
                if (target) expected++;
                if (observed) actual++;
                if (target && observed) overlap++;
            }
        return expected > 0 && actual > 0 &&
               (double)overlap / expected >= MinimumLabelColorCoverage &&
               (double)overlap / actual >= MinimumLabelColorPrecision;
    }

    /// <summary>对同一截图使用模板及其遮罩匹配，透明背景不参与评分。</summary>
    private TemplateMatchResult? Match(ScreenImage screen, Roi roi,
        IReadOnlyDictionary<string, NodeTemplate> templates, string name, double threshold,
        CancellationToken ct = default, Func<TemplateMatchResult, NodeTemplate, bool>? accept = null)
    {
        if (!templates.TryGetValue(name, out var template)) return null;
        foreach (var candidate in template.Candidates)
        {
            var result = MatchSingle(screen, roi, candidate, threshold, ct);
            if (result is not null && (accept is null || accept(result, candidate))) return result;
        }
        return null;
    }

    /// <summary>当前模板与自身遮罩成对匹配，不重新执行语言解析。</summary>
    private TemplateMatchResult? MatchSingle(ScreenImage screen, Roi roi, NodeTemplate template,
        double threshold, CancellationToken ct)
        => _matcher.Match(screen.Bgra, screen.Width, screen.Height, template.Bgra,
            template.Width, template.Height, threshold, roi, template.Mask, ct);

    /// <summary>加载透明模板与同名遮罩；素材缺失时只跳过对应特征，不影响其他识别。</summary>
    private IReadOnlyDictionary<string, NodeTemplate> LoadTemplates()
    {
        if (_templates is not null)
        {
            return _templates;
        }

        var loaded = new Dictionary<string, NodeTemplate>(StringComparer.OrdinalIgnoreCase);
        if (!ConfigService.Resources.DirectoryExists(_templateDirectory))
        {
            _log.Warn($"具体关卡模板目录不存在: {_templateDirectory}");
            _templates = loaded;
            return loaded;
        }

        foreach (var path in ConfigService.Resources.GetFiles(_templateDirectory, "*.png"))
        {
            if (path.EndsWith(".mask.png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // 兼容旧安装目录可能残留的数字图片；编号只由 OCR 读取，不能重新加载旧数字模板。
            if (Path.GetFileName(path).StartsWith("digit_", StringComparison.OrdinalIgnoreCase)) continue;

            var candidates = ConfigService.Resources.TemplateCandidates(path).ToArray();
            var primary = LoadSingle(candidates[0]);
            if (primary is null) continue;
            if (candidates.Length > 1) primary.Fallback = new(() => LoadSingle(candidates[1]));
            loaded[Path.GetFileNameWithoutExtension(path)] = primary;
        }

        _templates = loaded;
        _log.Info($"已加载 {loaded.Count} 个具体关卡识别模板");
        return loaded;
    }

    /// <summary>遮罩跟随物理模板；缺失或尺寸不匹配延续原有不可用语义。</summary>
    private NodeTemplate? LoadSingle(string path)
    {
        var maskPath = ConfigService.Resources.ResolveTemplateCompanion(path,
            Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".mask.png"));
        if (!File.Exists(maskPath)) return null;
        var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
        var maskImage = ImageCodec.DecodePng(File.ReadAllBytes(maskPath));
        if (image.Width != maskImage.Width || image.Height != maskImage.Height)
        {
            _log.Warn($"具体关卡模板与遮罩尺寸不一致: {Path.GetFileName(path)}");
            return null;
        }
        var mask = new byte[image.Width * image.Height];
        for (var index = 0; index < mask.Length; index++) mask[index] = maskImage.Bgra[index * 4];
        return new(image.Bgra, image.Width, image.Height, mask);
    }

    /// <summary>标签与编号绑定，只读取编号正上方的完整字形，排除更高处 NEXT 与背景装饰。</summary>
    private static Roi CreateLabelRoi(int centerX, int numberTop, ScreenImage screen)
        => Clip(new Roi(centerX - 140, numberTop - 100, 280, 100), screen.Width, screen.Height);

    /// <summary>数字位于台座上方；局部窗口排除远处天空与高处难度标签，仍覆盖呼吸动画造成的中心漂移。</summary>
    private static Roi CreateNumberRoi(int centerX, int centerY, Roi stageRoi, ScreenImage screen)
    {
        var top = Math.Max(stageRoi.Y, centerY - 230);
        return Clip(new Roi(centerX - 85, top, 170, Math.Max(1, centerY - 85 - top)), screen.Width, screen.Height);
    }

    private static Roi CreateStateRoi(int centerX, int centerY, Roi stageRoi, ScreenImage screen)
        => Clip(new Roi(centerX - 120, centerY - 85, 240, 175), screen.Width, screen.Height);

    private static Roi Clip(Roi roi, int width, int height)
    {
        var x = Math.Clamp(roi.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(roi.Y, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(roi.X + roi.Width, x + 1, width);
        var bottom = Math.Clamp(roi.Y + roi.Height, y + 1, height);
        return new Roi(x, y, right - x, bottom - y);
    }

    private static string GetKindText(WorkStageKind kind) => kind switch
    {
        WorkStageKind.Hard => "HARD",
        WorkStageKind.Boss => "BOSS",
        WorkStageKind.SideStory => "SIDE STORY",
        _ => "NORMAL",
    };

    private static string GetStateText(WorkStageNodeState state) => state switch
    {
        WorkStageNodeState.Locked => "未解锁",
        WorkStageNodeState.Completed => "已通过",
        WorkStageNodeState.Cleared => "已通关（CLEAR，未完全完成）",
        WorkStageNodeState.Incomplete => "未通过",
        _ => "未知",
    };

    private sealed record NodeTemplate(byte[] Bgra, int Width, int Height, byte[] Mask)
    {
        public Lazy<NodeTemplate?>? Fallback { get; set; }
        public IEnumerable<NodeTemplate> Candidates
        {
            get
            {
                yield return this;
                if (Fallback?.Value is { } fallback) yield return fallback;
            }
        }
    }
}

using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>OCR 观察结果；不可信时编号为空，不能将候选文字用于出击。</summary>
public sealed record StageNumberRecognition(int? Number, string Message, Roi? Region = null);

/// <summary>关卡编号识别边界，便于替换推理实现并验证流程的失败行为。</summary>
public interface IStageNumberRecognizer
{
    /// <summary>出击前检查所需离线模型及其运行环境。</summary>
    void Validate();
    /// <summary>读取与选中节点对齐的一整行编号。</summary>
    StageNumberRecognition Recognize(ScreenImage screen, Roi roi, int selectedCenterX, CancellationToken ct = default);
}

/// <summary>定位选中关卡的完整数字行，由共用 OCR 模型决定编号，不按进度猜测。</summary>
public sealed class OnnxStageNumberRecognizer : IStageNumberRecognizer, IDisposable
{
    public const string ModelFileName = OnnxTextRecognizer.ModelFileName;
    public const string ModelSha256 = OnnxTextRecognizer.ModelSha256;
    private readonly OnnxTextRecognizer _ocr;
    private readonly bool _ownsOcr;
    private bool _disposed;

    /// <summary>独立使用时拥有会话，供测试与非窗口调用释放。</summary>
    public OnnxStageNumberRecognizer(string? modelPath = null)
    { _ocr = new OnnxTextRecognizer(modelPath); _ownsOcr = true; }

    /// <summary>窗口注入共享会话时只借用，不提前释放标题识别仍需的模型。</summary>
    public OnnxStageNumberRecognizer(OnnxTextRecognizer ocr) => _ocr = ocr;

    /// <summary>出击前验证模型及原生运行环境。</summary>
    public void Validate()
    { ObjectDisposedException.ThrowIf(_disposed, this); _ocr.Validate(); }

    /// <summary>保留整行边界、纯数字格式、置信度与完整字形数量校验。</summary>
    public StageNumberRecognition Recognize(ScreenImage screen, Roi roi, int selectedCenterX, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 两条固定路径分别保留绝对白色与相对背景反差，避免为每张浅色背景追加亮度阈值。
        var white = OnnxTextRecognizer.CreateWhiteMask(screen, roi, ct);
        var contrast = CreateLocalContrast(screen, roi, ct);
        return ResolveCandidates([
            ReadCandidate(white, white, "白色笔画"),
            ReadCandidate(contrast.Mask, contrast.Gray, "局部背景抑制")]);

        StageNumberRecognition ReadCandidate(byte[] mask, byte[] gray, string source)
        {
            ct.ThrowIfCancellationRequested();
            var bounds = LocateNumberLine(mask, roi, selectedCenterX, out var digitCount, out var reason);
            if (bounds is null) return new(null, reason);
            var decoded = OcrNumberDecoder.Decode(_ocr.Recognize(gray, roi.Width, bounds.Value, ct));
            // 高置信度也不能把完整两位编号读成一位或额外补字。
            if (decoded.Number is not null && decoded.Number.Value.ToString(CultureInfo.InvariantCulture).Length != digitCount)
                return new(null, $"OCR 读取 {decoded.Number}，文字长度与完整编号字形数量 {digitCount} 不符，等待重新识别。");
            return decoded.Number is null ? decoded : decoded with
            {
                Message = $"{source}：{decoded.Message}",
                Region = new Roi(roi.X + bounds.Value.X, roi.Y + bounds.Value.Y, bounds.Value.Width, bounds.Value.Height)
            };
        }
    }

    /// <summary>只合并可信且位置一致的结果；不同编号或不同文字行均不能靠最高分/预期进度裁决。</summary>
    internal static StageNumberRecognition ResolveCandidates(IReadOnlyList<StageNumberRecognition> candidates)
    {
        var accepted = candidates.Where(candidate => candidate.Number is not null && candidate.Region is not null).ToArray();
        if (accepted.Length == 0) return candidates[0];
        var first = accepted[0];
        if (accepted.Any(candidate => candidate.Number != first.Number ||
            Math.Abs(candidate.Region!.Value.CenterX - first.Region!.Value.CenterX) > 8 ||
            Math.Abs(candidate.Region.Value.CenterY - first.Region.Value.CenterY) > 12))
            return new(null, "编号预处理结果或字形位置存在冲突，保留未知编号，不猜测当前关卡。");
        return first;
    }

    /// <summary>用局部开运算估计缓变背景，保留亮笔画的灰度反差；掩码只负责几何定位，不代替 OCR 输入。</summary>
    private static (byte[] Mask, byte[] Gray) CreateLocalContrast(ScreenImage screen, Roi roi, CancellationToken ct)
    {
        using var full = Mat.FromPixelData(screen.Height, screen.Width, MatType.CV_8UC4, screen.Bgra);
        using var crop = new Mat(full, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGRA2GRAY);
        // 21px 大于数字笔画宽度、小于完整字高，移除缓变天空/山体而保留原字形，禁止扩大或补造笔画。
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(21, 21));
        using var background = new Mat();
        Cv2.MorphologyEx(gray, background, MorphTypes.Open, kernel);
        using var contrast = new Mat();
        Cv2.Subtract(gray, background, contrast);
        var residual = new byte[roi.Width * roi.Height];
        Marshal.Copy(contrast.Data, residual, 0, residual.Length);
        var mask = new byte[residual.Length];
        var foreground = new byte[residual.Length];
        for (var y = 0; y < roi.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < roi.Width; x++)
            {
                var index = y * roi.Width + x;
                var offset = ((roi.Y + y) * screen.Width + roi.X + x) * 4;
                var min = Math.Min(screen.Bgra[offset], Math.Min(screen.Bgra[offset + 1], screen.Bgra[offset + 2]));
                var max = Math.Max(screen.Bgra[offset], Math.Max(screen.Bgra[offset + 1], screen.Bgra[offset + 2]));
                if (min < 140 || max - min > 50) continue;
                foreground[index] = (byte)Math.Min(255, residual[index] * 6);
                if (residual[index] >= 16) mask[index] = 255;
            }
        }
        return (mask, foreground);
    }

    /// <summary>定位唯一完整编号行；拒绝截断、其他行与相邻节点，避免 OCR 读到局部或无关文字。</summary>
    private static Roi? LocateNumberLine(byte[] binary, Roi roi, int selectedCenterX, out int digitCount, out string reason)
    {
        digitCount = 0;
        using var source = Mat.FromPixelData(roi.Height, roi.Width, MatType.CV_8UC1, binary);
        using var labels = new Mat(); using var stats = new Mat(); using var centers = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(source, labels, stats, centers);
        var components = new List<Roi>();
        for (var i = 1; i < count; i++)
        {
            var b = new Roi(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
            var area = stats.At<int>(i, 4);
            if (b.Height is >= 32 and <= 68 && area >= 60 && b.Width <= b.Height * .8 &&
                (b.X == 0 || b.Y == 0 || b.X + b.Width == roi.Width || b.Y + b.Height == roi.Height))
            { reason = "OCR 编号区域边缘存在截断字形，等待重新识别。"; return null; }
            var density = (double)area / (b.Width * b.Height);
            if (b.Height is >= 36 and <= 64 && b.Width >= 8 && b.Width <= b.Height * .7 && area >= 100 &&
                density is >= .15 and <= .95 && b.X > 0 && b.Y > 0 && b.X + b.Width < roi.Width && b.Y + b.Height < roi.Height)
                components.Add(b);
        }
        var lines = new List<List<Roi>>();
        foreach (var b in components.OrderBy(b => b.Y))
        {
            var line = lines.FirstOrDefault(items => Math.Abs(items[0].Y - b.Y) <= 6 && Math.Abs(items[0].Height - b.Height) <= 8);
            if (line is null) lines.Add([b]); else line.Add(b);
        }
        var centered = lines.Where(items => Math.Abs(roi.X + (items.Min(b => b.X) + items.Max(b => b.X + b.Width)) / 2 - selectedCenterX) <= 26).ToList();
        reason = "OCR 未找到唯一的完整白色编号行，等待重新识别。";
        if (centered.Count != 1) return null;
        var digits = centered[0].OrderBy(b => b.X).ToList();
        digitCount = digits.Count;
        if (digits.Count is < 1 or > 2) { reason = "OCR 编号行不符合一至两位完整数字，等待重新识别。"; return null; }
        if (digits.Count == 2)
        {
            var gap = digits[1].X - digits[0].X - digits[0].Width;
            if (gap is < 2 or > 18) { reason = "OCR 编号字形间距异常，等待重新识别。"; return null; }
        }
        var x = digits.Min(b => b.X); var y = digits.Min(b => b.Y);
        return new Roi(x, y, digits.Max(b => b.X + b.Width) - x, digits.Max(b => b.Y + b.Height) - y);
    }

    /// <summary>仅释放自己创建的会话，共用模型由窗口生命周期管理。</summary>
    public void Dispose()
    { _disposed = true; if (_ownsOcr) _ocr.Dispose(); }
}

/// <summary>编号格式与置信度约束，不将字母强行纠正为数字。</summary>
public static class OcrNumberDecoder
{
    /// <summary>解码接口保留用于测试 CTC 重复数字行为。</summary>
    public static StageNumberRecognition Decode(Tensor<float> output, IReadOnlyList<string> characters)
        => Decode(OcrTextDecoder.Decode(output, characters));

    /// <summary>只接受一至两位、非零开头的高置信度数字。</summary>
    public static StageNumberRecognition Decode(OcrTextRecognition result)
    {
        var text = result.Text;
        var diagnostic = result.Diagnostic;
        if (text.Length is < 1 or > 2 || text[0] == '0' || text.Any(c => c is < '0' or > '9'))
            return new(null, $"{diagnostic}；不符合关卡数字格式，等待重新识别。");
        if (result.Confidence < .90 || result.MinimumConfidence < .85)
            return new(null, $"{diagnostic}；置信度不足，等待重新识别。");
        return new(int.Parse(text, CultureInfo.InvariantCulture), $"{diagnostic}；确认编号 {text}。");
    }
}

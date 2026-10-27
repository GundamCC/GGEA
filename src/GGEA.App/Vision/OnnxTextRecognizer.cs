using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>CTC 文本及可信度；格式与范围由识别场景负责校验。</summary>
public sealed record OcrTextRecognition(string Text, double Confidence, double MinimumConfidence,
    double? MinimumContentConfidence = null)
{
    public string Diagnostic => string.Create(CultureInfo.InvariantCulture,
        $"OCR 文字“{Text}”，平均置信度 {Confidence:P1}，最低字符 {MinimumConfidence:P1}");
}

/// <summary>数字、HARD 标题与 TAP TO NEXT 共用的离线 CPU 推理会话；窗口拥有并在任务退出后释放。</summary>
public sealed class OnnxTextRecognizer : IDisposable
{
    public const string ModelFileName = "en_PP-OCRv5_rec_mobile.onnx";
    public const string ModelSha256 = "c3461add59bb4323ecba96a492ab75e06dda42467c9e3d0c18db5d1d21924be8";
    private readonly string _modelPath;
    private readonly object _gate = new();
    private InferenceSession? _session;
    private string[] _characters = [];
    private string _inputName = "";
    private bool _disposed;

    /// <summary>模型随包分发，不在识别时联网下载。</summary>
    public OnnxTextRecognizer(string? modelPath = null)
        => _modelPath = GGEA.App.Config.ConfigService.ResolvePath(modelPath ?? $"resources/ocr/{ModelFileName}");

    /// <summary>首次使用校验固定模型身份，防止字典、模型或发布文件混用。</summary>
    public void Validate()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null) return;
            if (!File.Exists(_modelPath)) throw new InvalidOperationException($"离线 OCR 模型缺失：{ModelFileName}");
            using (var stream = File.OpenRead(_modelPath))
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("离线 OCR 模型摘要不符，请使用完整的新版本程序与资源。");
            // SessionOptions 已会触发 NativeMethods 静态初始化，诊断边界必须覆盖它而非只包会话构造。
            var session = OnnxRuntimeDiagnostics.Initialize(() =>
            {
                using var options = new SessionOptions
                {
                    IntraOpNumThreads = 2, InterOpNumThreads = 1,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                options.AppendExecutionProvider_CPU(0);
                return new InferenceSession(_modelPath, options);
            });
            try
            {
                if (!session.ModelMetadata.CustomMetadataMap.TryGetValue("character", out var dictionary))
                    throw new InvalidOperationException("离线 OCR 模型未携带对应字符字典。");
                var characters = dictionary.TrimEnd('\r', '\n').Split('\n').Select(value => value.TrimEnd('\r')).ToList();
                if (!characters.Contains(" ")) characters.Add(" ");
                characters.Insert(0, ""); // CTC 的零号是 blank，不能当成数字 0。
                if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1 ||
                    session.OutputMetadata.First().Value.Dimensions[^1] != characters.Count)
                    throw new InvalidOperationException("离线 OCR 模型接口或字符字典规格不符。");
                _characters = characters.ToArray();
                _inputName = session.InputMetadata.Keys.Single();
                _session = session;
            }
            catch { session.Dispose(); throw; }
        }
    }

    /// <summary>读取定位后的亮字灰度行（兼容二值掩码）；串行使用会话，取消通过原生 RunOptions 传递。</summary>
    public OcrTextRecognition Recognize(byte[] binary, int stride, Roi bounds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (stride <= 0 || binary.Length == 0 || binary.Length % stride != 0 ||
            bounds.Width <= 0 || bounds.Height <= 0 || bounds.X < 0 || bounds.Y < 0 ||
            (long)bounds.X + bounds.Width > stride || (long)bounds.Y + bounds.Height > binary.Length / stride)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        var input = PrepareInput(binary, stride, bounds);
        lock (_gate)
        {
            Validate();
            ct.ThrowIfCancellationRequested();
            using var runOptions = new RunOptions();
            using var registration = ct.Register(() => runOptions.Terminate = true);
            try
            {
                using var output = _session!.Run([NamedOnnxValue.CreateFromTensor(_inputName, input)],
                    _session.OutputNames, runOptions);
                ct.ThrowIfCancellationRequested();
                return OcrTextDecoder.Decode(output.Single().AsTensor<float>(), _characters);
            }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        }
    }

    /// <summary>提取低饱和白色笔画，排除黄色光圈、橙色箭头和背景颜色。</summary>
    internal static byte[] CreateWhiteMask(ScreenImage screen, Roi roi, CancellationToken ct)
    {
        if (roi.Width <= 0 || roi.Height <= 0 || roi.X < 0 || roi.Y < 0 ||
            (long)roi.X + roi.Width > screen.Width || (long)roi.Y + roi.Height > screen.Height)
            throw new ArgumentOutOfRangeException(nameof(roi));
        var binary = new byte[roi.Width * roi.Height];
        for (var y = 0; y < roi.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < roi.Width; x++)
            {
                var offset = ((roi.Y + y) * screen.Width + roi.X + x) * 4;
                var b = screen.Bgra[offset]; var g = screen.Bgra[offset + 1]; var r = screen.Bgra[offset + 2];
                var minimum = Math.Min(r, Math.Min(g, b));
                if (minimum >= 180 && Math.Max(r, Math.Max(g, b)) - minimum <= 50) binary[y * roi.Width + x] = 255;
            }
        }
        return binary;
    }

    /// <summary>白色笔画转为白底黑字并保留整行比例；留边与线性缩放减少游戏背景及粗体抗锯齿的影响。</summary>
    private static DenseTensor<float> PrepareInput(byte[] binary, int stride, Roi bounds)
    {
        using var source = Mat.FromPixelData(binary.Length / stride, stride, MatType.CV_8UC1, binary);
        using var crop = new Mat(source, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        using var inverted = new Mat(); Cv2.BitwiseNot(crop, inverted);
        using var padded = new Mat(); Cv2.CopyMakeBorder(inverted, padded, 4, 4, 8, 8, BorderTypes.Constant, Scalar.White);
        const int height = 48;
        var contentWidth = (int)Math.Ceiling((double)height * padded.Cols / padded.Rows);
        var width = Math.Max(320, contentWidth); // 英文标题比数字长，动态宽度保留字形比例。
        using var resized = new Mat(); Cv2.Resize(padded, resized, new Size(contentWidth, height), 0, 0, InterpolationFlags.Linear);
        var input = new DenseTensor<float>(new[] { 1, 3, height, width });
        // 右侧按 PP-OCR 约定填零（归一化后的中性值），三通道均使用同一灰度文字行。
        for (var channel = 0; channel < 3; channel++)
            for (var y = 0; y < height; y++)
                for (var x = 0; x < contentWidth; x++) input[0, channel, y, x] = resized.At<byte>(y, x) / 127.5f - 1;
        return input;
    }

    /// <summary>与验证及推理共用同一锁，关闭后不允许重新加载模型。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose(); _session = null;
        }
    }
}

/// <summary>共用 CTC 贪心解码，保留 blank 分隔的重复字符。</summary>
public static class OcrTextDecoder
{
    /// <summary>零号 blank 不输出，相邻重复折叠，不执行字母与数字替换。</summary>
    public static OcrTextRecognition Decode(Tensor<float> output, IReadOnlyList<string> characters)
    {
        if (output.Dimensions.Length != 3 || output.Dimensions[0] != 1 || output.Dimensions[2] != characters.Count)
            throw new InvalidOperationException("离线 OCR 输出规格不符。");
        var text = ""; var scores = new List<float>(); var contentScores = new List<float>(); var previous = -1;
        for (var time = 0; time < output.Dimensions[1]; time++)
        {
            var best = 0;
            for (var index = 1; index < characters.Count; index++)
                if (output[0, time, index] > output[0, time, best]) best = index;
            var score = output[0, time, best];
            if (!float.IsFinite(score) || score is < 0 or > 1) throw new InvalidOperationException("离线 OCR 置信度无效。");
            if (best != 0 && best != previous)
            {
                text += characters[best]; scores.Add(score);
                // 标题允许省略词间空格，空格低分不能代替字母/数字低分；原始平均与最低分仍完整保留。
                if (!string.IsNullOrWhiteSpace(characters[best])) contentScores.Add(score);
            }
            else if (best != 0)
            {
                // 同一 CTC 字符占据多个时间步：边缘首次出现可能只有 52%，核心时间步却为 100%。
                // 使用连续同标签段中模型实际输出的峰值，不丢弃核心证据；blank/其他标签会断开该段。
                scores[^1] = Math.Max(scores[^1], score);
                if (!string.IsNullOrWhiteSpace(characters[best]))
                    contentScores[^1] = Math.Max(contentScores[^1], score);
            }
            previous = best;
        }
        var mean = scores.Count == 0 ? 0 : scores.Average();
        var minimum = scores.Count == 0 ? 0 : scores.Min();
        return new(text, mean, minimum, contentScores.Count == 0 ? 0 : contentScores.Min());
    }
}

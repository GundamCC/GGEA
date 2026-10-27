using GGEA.App.Vision;
using System.IO;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GGEA.Tests;

/// <summary>OCR 解码的实际安全边界与离线资源失败，不以预期下一关纠正文字。</summary>
public sealed class OnnxStageNumberRecognizerTests
{
    private static readonly string[] Characters = ["", "0", "1", "8", "O", "l", "9"];

    /// <summary>真实浅色山体和沙漠光柱均走生产定位及 OCR，不能用当前进度修正为期望数字。</summary>
    [Theory]
    [InlineData("history-08th-hard3-nodes.png", 290, 790, 405, 130, 3, 0)]
    [InlineData("history-cross-dimension-normal6.png", 230, 510, 320, 145, 6, 0)]
    [InlineData("history-cross-dimension-normal6.png", 230, 510, 320, 145, 6, 30)]
    public void BrightBackground_RealPixelsReadCompleteNumber(string fixture, int cropY, int centerX, int top, int height, int expected, int brightnessDelta)
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/Navigation", fixture)));
        // 非零增亮是明确的故障注入，不冒充另一张实机截图；保留同一编号轮廓，模拟背景整体变亮。
        if (brightnessDelta != 0)
            for (var offset = 0; offset < crop.Bgra.Length; offset += 4)
                for (var channel = 0; channel < 3; channel++)
                    crop.Bgra[offset + channel] = (byte)Math.Min(255, crop.Bgra[offset + channel] + brightnessDelta);
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 0);
        for (var y = 0; y < crop.Height; y++)
            Buffer.BlockCopy(crop.Bgra, y * crop.Width * 4, pixels, ((cropY + y) * 1920) * 4, crop.Width * 4);
        var screen = ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1920, 1080));
        using var numbers = new OnnxStageNumberRecognizer(TapToNextOcrSamples.SharedOcr);
        var result = numbers.Recognize(screen, new(centerX - 85, top, 170, height), centerX);
        Assert.Equal(expected, result.Number);
        Assert.NotNull(result.Region);
        Assert.InRange(result.Region.Value.Height, 36, 64);
    }

    /// <summary>不同预处理不能以最高分压过冲突，甚至同数字出现在不同行也不能任意选择。</summary>
    [Theory]
    [InlineData(8, 400)]
    [InlineData(6, 440)]
    public void CandidateConflict_RemainsUnknown(int secondNumber, int secondY)
    {
        var result = OnnxStageNumberRecognizer.ResolveCandidates([
            new(6, "first", new(500, 400, 25, 50)), new(secondNumber, "second", new(500, secondY, 25, 50))]);
        Assert.Null(result.Number);
        Assert.Null(result.Region);
    }

    /// <summary>CTC 相邻重复被折叠，blank 分开的重复数字保留，避免关卡 11 变成 1。</summary>
    [Fact]
    public void Decode_BlankSeparatedRepeats_PreservesEleven()
    {
        var output = Probabilities([2, 2, 0, 2, 2], .99f);
        Assert.Equal(11, OcrNumberDecoder.Decode(output, Characters).Number);
    }

    /// <summary>18 的 8 首次时间步偏低，但连续核心时间步可信；全段都低分时仍拒绝。</summary>
    [Theory]
    [InlineData(.527f, .999f, 18)]
    [InlineData(.70f, .70f, null)]
    public void Decode_RepeatedLabel_UsesActualSpanPeak(float edge, float core, int? expected)
    {
        var output = Probabilities([2, 3, 3], .999f);
        foreach (var (time, score) in new[] { (1, edge), (2, core) })
            for (var index = 0; index < Characters.Length; index++)
                output[0, time, index] = index == 3 ? score : (1 - score) / (Characters.Length - 1);
        var text = OcrTextDecoder.Decode(output, Characters);
        Assert.Equal("18", text.Text);
        Assert.InRange(text.MinimumConfidence, Math.Max(edge, core) - .001, Math.Max(edge, core) + .001);
        Assert.Equal(expected, OcrNumberDecoder.Decode(text).Number);
    }

    /// <summary>高分字母、零、前导零、三位数字均不能授权出击；不得执行 O→0 或 l→1 替换。</summary>
    [Theory]
    [InlineData("O")]
    [InlineData("l1")]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("100")]
    [InlineData("")]
    public void Decode_InvalidNumberText_ReturnsUnknown(string text)
    {
        var indices = text.SelectMany(c => new[] { Array.IndexOf(Characters, c.ToString()), 0 }).ToArray();
        Assert.Null(OcrNumberDecoder.Decode(Probabilities(indices, .99f), Characters).Number);
    }

    /// <summary>平均分不足或任意字符偏低时拒绝，不能由另一位的高分掩盖低分。</summary>
    [Theory]
    [InlineData(.89f, .89f)]
    [InlineData(.99f, .84f)]
    public void Decode_LowConfidence_ReturnsUnknown(float first, float second)
    {
        var output = Probabilities([2, 1], first);
        for (var index = 0; index < Characters.Length; index++) output[0, 1, index] = index == 1 ? second : (1 - second) / (Characters.Length - 1);
        var result = OcrNumberDecoder.Decode(output, Characters);
        Assert.Null(result.Number);
        Assert.Contains("置信度不足", result.Message);
    }

    /// <summary>缺少模型应在流程启动校验时明确失败，不回退到易混淆的旧数字模板。</summary>
    [Fact]
    public void Validate_MissingModel_ReportsMissingResource()
    {
        using var recognizer = new OnnxStageNumberRecognizer(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".onnx"));
        Assert.Contains("模型缺失", Assert.Throws<InvalidOperationException>(recognizer.Validate).Message);
    }

    /// <summary>损坏或替换模型不能通过存在性检查后继续执行原生推理。</summary>
    [Fact]
    public void Validate_CorruptModel_RejectsDigest()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            using var recognizer = new OnnxStageNumberRecognizer(path);
            Assert.Contains("摘要不符", Assert.Throws<InvalidOperationException>(recognizer.Validate).Message);
        }
        finally { File.Delete(path); }
    }

    /// <summary>释放后禁止重新载入，重复释放保持安全，覆盖窗口资源所有权的结束边界。</summary>
    [Fact]
    public void Dispose_PreventsReload()
    {
        var recognizer = new OnnxStageNumberRecognizer();
        recognizer.Dispose(); recognizer.Dispose();
        Assert.Throws<ObjectDisposedException>(recognizer.Validate);
    }

    /// <summary>构造合法 softmax 概率以检验文字约束，不模拟原生模型的图像算法。</summary>
    private static DenseTensor<float> Probabilities(int[] indices, float score)
    {
        var output = new DenseTensor<float>(new[] { 1, indices.Length, Characters.Length });
        for (var time = 0; time < indices.Length; time++)
            for (var index = 0; index < Characters.Length; index++)
                output[0, time, index] = index == indices[time] ? score : (1 - score) / (Characters.Length - 1);
        return output;
    }
}

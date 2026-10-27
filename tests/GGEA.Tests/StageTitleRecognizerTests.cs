using System.IO;
using GGEA.App.Vision;
using OpenCvSharp;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GGEA.Tests;

/// <summary>用五张真实游戏标题图验证 OCR，覆盖缩放、ROI 和完整标题约束。</summary>
public sealed class StageTitleRecognizerTests : IDisposable
{
    private readonly OnnxTextRecognizer _ocr = new();
    private static readonly Roi TitleRoi = new(850, 60, 1070, 140);

    /// <summary>读取图中文字而非模板文件名；缩放和位置变化仍识别正确编号。</summary>
    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 1.0)]
    [InlineData(3, 1.0)]
    [InlineData(4, 1.0)]
    [InlineData(5, 1.0)]
    [InlineData(3, .85)]
    [InlineData(3, 1.10)]
    public async Task RecognizeAsync_RealTitleInRoi_ReturnsHardIndex(int index, double scale)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTitle(pixels, index, 1230, 95, scale);
        var match = await Recognizer(pixels).RecognizeAsync("serial", TitleRoi);
        Assert.NotNull(match);
        Assert.Equal(index, match.HardIndex);
        Assert.True(match.Confidence >= .90);
        Assert.InRange(match.Region.X, TitleRoi.X, TitleRoi.X + TitleRoi.Width);
    }

    /// <summary>没有文字、文字在 ROI 外、ROI 裁断文字均不能读出关卡。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(200)]
    [InlineData(840)]
    public async Task RecognizeAsync_AbsentOutsideOrClippedTitle_ReturnsNull(int x)
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        if (x >= 0) PasteTitle(pixels, 3, x, 95, 1);
        Assert.Null(await Recognizer(pixels).RecognizeAsync("serial", TitleRoi));
    }

    /// <summary>存在两个高分标题时不能取第一个授权导航。</summary>
    [Fact]
    public async Task RecognizeAsync_TwoTitles_ReturnsNull()
    {
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTitle(pixels, 2, 1230, 80, 1);
        PasteTitle(pixels, 3, 1230, 145, 1);
        Assert.Null(await Recognizer(pixels).RecognizeAsync("serial", TitleRoi));
    }

    /// <summary>用户取消后不能继续推理或返回识别成功。</summary>
    [Fact]
    public async Task RecognizeAsync_Canceled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Recognizer(pixels).RecognizeAsync("serial", TitleRoi, ct: cancellation.Token));
    }

    /// <summary>完整关键词、有效范围、平均与单字符阈值共同保护识别结果。</summary>
    [Theory]
    [InlineData("HARD STAGE 3", .99, .98, 3)]
    [InlineData("HARDSTAGE5", .99, .98, 5)]
    [InlineData("STAGE 3", .99, .98, null)]
    [InlineData("HARD STAGE O", .99, .98, null)]
    [InlineData("HARD STAGE 6", .99, .98, null)]
    [InlineData("HARD STAGE 13", .99, .98, null)]
    [InlineData("HARD STAGE 3", .89, .88, null)]
    [InlineData("HARD STAGE 3", .99, .84, null)]
    public void Decode_InvalidOrLowConfidenceTitle_Rejects(string text, double mean, double minimum, int? expected)
        => Assert.Equal(expected, OcrStageTitleDecoder.Decode(new(text, mean, minimum)));

    /// <summary>缩放样本低分来自空格；保留含空格平均分，仍拒绝真正低分的编号字符。</summary>
    [Theory]
    [InlineData(.99f, 3)]
    [InlineData(.84f, null)]
    public void Decode_LowSpaceScore_DoesNotHideLowDigitScore(float digitScore, int? expected)
    {
        const string text = "HARD STAGE 3";
        var characters = new[] { "" }.Concat(text.Select(c => c.ToString()).Distinct()).ToArray();
        var output = new DenseTensor<float>(new[] { 1, text.Length, characters.Length });
        for (var time = 0; time < text.Length; time++)
        {
            var selected = Array.IndexOf(characters, text[time].ToString());
            var score = text[time] == ' ' ? .55f : text[time] == '3' ? digitScore : .99f;
            for (var index = 0; index < characters.Length; index++)
                output[0, time, index] = index == selected ? score : (1 - score) / (characters.Length - 1);
        }
        var result = OcrTextDecoder.Decode(output, characters);
        Assert.Equal(text, result.Text);
        Assert.InRange(result.MinimumConfidence, .549, .551);
        Assert.Equal(expected, OcrStageTitleDecoder.Decode(result));
    }

    /// <summary>借用数字识别器退出不能释放标题识别仍使用的共享会话。</summary>
    [Fact]
    public async Task Dispose_BorrowedNumberRecognizer_PreservesTitleSession()
    {
        using (var number = new OnnxStageNumberRecognizer(_ocr)) number.Validate();
        var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
        PasteTitle(pixels, 3, 1230, 95, 1);
        Assert.Equal(3, (await Recognizer(pixels).RecognizeAsync("serial", TitleRoi))?.HardIndex);
    }

    /// <summary>构造真实截图入口，测试识别器独立于模板目录配置。</summary>
    private OnnxStageTitleRecognizer Recognizer(byte[] pixels)
        => new(new FakeAdbService(ImageCodec.EncodePng(pixels, 1920, 1080)), new TestLog(), _ocr);

    /// <summary>原始标题截图只从测试夹具读取，不再依赖随程序发布的旧模板目录。</summary>
    private static void PasteTitle(byte[] target, int index, int x, int y, double scale)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "StageTitles", $"hard-stage-{index}.png");
        using var source = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size((int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale)),
            0, 0, InterpolationFlags.Linear);
        var height = resized.Height; var width = resized.Width;
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
            {
                var offset = ((y + row) * 1920 + x + col) * 4;
                var color = resized.At<Vec3b>(row, col);
                target[offset] = color.Item0; target[offset + 1] = color.Item1; target[offset + 2] = color.Item2;
                target[offset + 3] = 255;
            }
    }

    /// <summary>测试所有者释放原生会话。</summary>
    public void Dispose() => _ocr.Dispose();
}

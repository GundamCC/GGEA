using System.IO;
using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.Tests;

/// <summary>状态机测试共用离线模型，测试进程退出时释放，避免每个场景重复加载模型。</summary>
internal static class TapToNextOcrSamples
{
    public static OnnxTextRecognizer SharedOcr { get; } = new();
    static TapToNextOcrSamples() => AppDomain.CurrentDomain.ProcessExit += (_, _) => SharedOcr.Dispose();

    /// <summary>从秘密战斗的真实裁剪取完整字母，居中放置，模板参数不决定 OCR 的文字内容。</summary>
    public static byte[] TextPng(int width, int height)
    {
        var crop = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Sortie", "secret-battle-tap-mumu5556.png")));
        using var source = Mat.FromPixelData(crop.Height, crop.Width, MatType.CV_8UC4, crop.Bgra);
        using var word = new Mat(source, new Rect(198, 34, 224, 37));
        var scale = Math.Min(1, Math.Min((width - 4.0) / word.Width, (height - 4.0) / word.Height));
        using var resized = new Mat();
        Cv2.Resize(word, resized, new Size((int)(word.Width * scale), (int)(word.Height * scale)));
        var pixels = TestImageFactory.CreateBgra(width, height, 20);
        var rowCount = resized.Height; var columnCount = resized.Width;
        var x = width / 2 - columnCount / 2; var y = height / 2 - rowCount / 2;
        for (var row = 0; row < rowCount; row++)
            System.Runtime.InteropServices.Marshal.Copy(resized.Ptr(row), pixels,
                ((y + row) * width + x) * 4, columnCount * 4);
        return ImageCodec.EncodePng(pixels, width, height);
    }

    /// <summary>原像素拼接同一文字到明确的测试位置。</summary>
    public static void Paste(byte[] pixels, int stride, int width, int height, int x, int y)
    {
        var text = ImageCodec.DecodePng(TextPng(width, height));
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(text.Bgra, row * width * 4, pixels, ((y + row) * stride + x) * 4, width * 4);
    }
}

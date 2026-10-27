using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>验证真实模板加载及不同 PNG 像素格式统一为 BGRA 的约定。</summary>
public sealed class ImageCodecTests
{
    /// <summary>持续操作必须复用同一个 STA 线程，并允许线程内重入调用。</summary>
    [Fact]
    public void CodecThread_ReusesStaThread()
    {
        var owner = ImageCodecThread.Invoke(() => Environment.CurrentManagedThreadId);
        for (var i = 0; i < 256; i++)
        {
            Assert.Equal(owner, ImageCodecThread.Invoke(() =>
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                return ImageCodecThread.Invoke(() => Environment.CurrentManagedThreadId);
            }));
        }
    }

    /// <summary>多个设备并发编解码时结果不能串帧，单次失败不能破坏共享线程。</summary>
    [Fact]
    public async Task Codec_ConcurrentCalls_KeepIndependentPixels()
    {
        await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            byte[] pixels = [(byte)index, 30, 90, 255];
            var bitmap = ImageCodec.FromBgra(pixels, 1, 1);
            byte[] copied = new byte[4];
            bitmap.CopyPixels(copied, 4, 0);
            Assert.Equal(pixels, copied);
            Assert.True(bitmap.IsFrozen);
            Assert.Throws<InvalidOperationException>(() => ImageCodec.EncodePng([], 1, 1));
            var image = ImageCodec.DecodePng(ImageCodec.EncodePng(pixels, 1, 1));
            Assert.Equal(pixels, image.Bgra);
            Assert.True(image.Bitmap.IsFrozen);
        })));
    }

    /// <summary>直接加载随程序交付的出击模板，覆盖用户报告的失败入口。</summary>
    [Fact]
    public void DecodePng_SortieTemplate_LoadsFrozenBgraImage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "resources", "common", "templates", "stage", "sortie", "sortie.png");
        var image = ImageCodec.DecodePng(File.ReadAllBytes(path));
        Assert.Equal(366, image.Width);
        Assert.Equal(133, image.Height);
        Assert.Equal(PixelFormats.Bgra32, image.Bitmap.Format);
        Assert.True(image.Bitmap.IsFrozen);
        Assert.Equal(image.Width * image.Height * 4, image.Bgra.Length);
    }

    /// <summary>RGB 和高位深 PNG 必须得到正确的通道与行布局，不能直接按四字节复制。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodePng_RgbFormats_NormalizesPixels(bool highBitDepth)
    {
        var format = highBitDepth ? PixelFormats.Rgb48 : PixelFormats.Rgb24;
        byte[] pixels = highBitDepth ? [255, 255, 0, 0, 0, 0, 0, 0, 255, 255, 0, 0] : [255, 0, 0, 0, 255, 0];
        var bitmap = BitmapSource.Create(2, 1, 96, 96, format, null, pixels, pixels.Length);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        var image = ImageCodec.DecodePng(stream.ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, image.Bgra);
    }

    /// <summary>持续截图及失败重试后仍可编解码，输出可在调用线程读取。</summary>
    [Fact]
    public void Codec_RepeatedSuccessAndFailure_RemainsUsable()
    {
        byte[] expected = [10, 20, 30, 255, 40, 50, 60, 255];
        byte[] truncated = [137, 80, 78, 71, 13, 10, 26, 10];
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var error = Assert.Throws<InvalidOperationException>(() => ImageCodec.DecodePng(truncated));
            Assert.NotNull(error.InnerException);
            Assert.Contains(error.InnerException.Message, error.Message);
            Assert.Throws<InvalidOperationException>(() => ImageCodec.EncodePng([], 2, 1));
            var decoded = ImageCodec.DecodePng(ImageCodec.EncodePng(expected, 2, 1));
            var copied = new byte[expected.Length];
            decoded.Bitmap.CopyPixels(copied, 8, 0);
            Assert.Equal(expected, decoded.Bgra);
            Assert.Equal(expected, copied);
        }
    }
}

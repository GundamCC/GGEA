using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GGEA.App.Vision;

/// <summary>
/// 图像编解码辅助：PNG 字节 <-> ScreenImage、裁剪、保存。
/// WPF 图像对象与线程关联，解码统一放在进程共享的 STA 线程上执行以保证可靠。
/// </summary>
public static class ImageCodec
{
    /// <summary>PNG 文件头。</summary>
    private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47 };

    /// <summary>
    /// 解码 PNG 字节为 ScreenImage。
    /// </summary>
    public static ScreenImage DecodePng(byte[] pngBytes)
    {
        if (pngBytes.Length < 8 || !pngBytes.AsSpan(0, 4).SequenceEqual(PngSignature))
        {
            throw new InvalidOperationException("输入不是有效的 PNG 数据");
        }

        try
        {
            return ImageCodecThread.Invoke(() =>
            {
                using var stream = new MemoryStream(pngBytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();

                // 匹配器统一使用非预乘 BGRA32，不能直接复制高位深或 RGB 像素。
                BitmapSource normalized = bitmap.Format == PixelFormats.Bgra32
                    ? bitmap
                    : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                normalized.Freeze();
                var width = normalized.PixelWidth;
                var height = normalized.PixelHeight;
                var pixels = new byte[checked(width * height * 4)];
                normalized.CopyPixels(pixels, checked(width * 4), 0);
                return new ScreenImage
                {
                    Bitmap = normalized,
                    Bgra = pixels,
                    Width = width,
                    Height = height,
                };
            });
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"PNG 解码失败: {error.Message}", error);
        }
    }

    /// <summary>按 ROI 裁剪 BitmapSource。</summary>
    public static BitmapSource Crop(BitmapSource source, Roi roi)
    {
        var rect = new Int32Rect(roi.X, roi.Y, roi.Width, roi.Height);
        var cropped = new CroppedBitmap(source, rect);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>将 BitmapSource 保存为 PNG 文件。</summary>
    public static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// 将 BGRA 像素数组编码为 PNG 字节（在进程共享的 STA 线程上完成，测试与自动化均可安全调用）。
    /// </summary>
    public static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        try
        {
            return ImageCodecThread.Invoke(() =>
            {
                var bitmap = FromBgra(bgra, width, height);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                return stream.ToArray();
            });
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"PNG 编码失败: {error.Message}", error);
        }
    }

    /// <summary>从 BGRA 数组创建冻结的 BitmapSource（用于测试预览）。</summary>
    public static BitmapSource FromBgra(byte[] bgra, int width, int height)
    {
        // 此入口也被识图辅助代码直接调用，不能在调用线程隐式创建新的 Dispatcher。
        return ImageCodecThread.Invoke(() =>
        {
            var bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                bgra,
                checked(width * 4));
            bitmap.Freeze();
            return bitmap;
        });
    }
}

using System.Windows.Media.Imaging;

namespace GGEA.App.Vision;

/// <summary>
/// 一帧屏幕图像：既保留用于显示的 BitmapSource，也保留用于匹配的 BGRA 像素数组。
/// </summary>
public sealed class ScreenImage
{
    /// <summary>冻结的 BitmapSource，可跨线程安全使用。</summary>
    public required BitmapSource Bitmap { get; init; }

    /// <summary>BGRA 像素数组，长度为 Width * Height * 4。</summary>
    public required byte[] Bgra { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
}

namespace GGEA.App.Vision;

/// <summary>
/// 平台无关的一帧屏幕图像；识别只读取 BGRA 像素，界面自行创建显示对象。
/// </summary>
public sealed class ScreenImage
{
    /// <summary>BGRA 像素数组，长度为 Width * Height * 4。</summary>
    public required byte[] Bgra { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
}

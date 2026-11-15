using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>同一帧的设备尺寸与基准像素；输入只能由产生识别证据的帧换算。</summary>
public sealed record LandscapeFrame(int Width, int Height, ScreenImage Screen)
{
    public const int BaseWidth = 1920;
    public const int BaseHeight = 1080;

    public static LandscapeFrame FromPng(byte[] png)
    {
        var raw = ImageCodec.DecodePng(png);
        if ((long)raw.Width * 9 != (long)raw.Height * 16)
            throw new InvalidOperationException($"当前截图为 {raw.Width}×{raw.Height}，使命入口仅支持横屏 16:9");
        if (raw.Width == BaseWidth) return new(raw.Width, raw.Height, raw);

        var pixels = new byte[BaseWidth * BaseHeight * 4];
        using var source = Mat.FromPixelData(raw.Height, raw.Width, MatType.CV_8UC4, raw.Bgra);
        using var target = Mat.FromPixelData(BaseHeight, BaseWidth, MatType.CV_8UC4, pixels);
        Cv2.Resize(source, target, new Size(BaseWidth, BaseHeight), interpolation:
            raw.Width < BaseWidth ? InterpolationFlags.Linear : InterpolationFlags.Area);
        return new(raw.Width, raw.Height, new ScreenImage { Bgra = pixels, Width = BaseWidth, Height = BaseHeight });
    }

    public (int X, int Y) ToDevice(int x, int y)
    {
        if ((uint)x >= BaseWidth || (uint)y >= BaseHeight) throw new ArgumentOutOfRangeException();
        // 将基准像素中心投影到设备上的像素格，右下边缘同样落在有效范围内。
        return ((int)((2L * x + 1) * Width / (2 * BaseWidth)),
            (int)((2L * y + 1) * Height / (2 * BaseHeight)));
    }
}

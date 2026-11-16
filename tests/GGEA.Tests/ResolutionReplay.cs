using GGEA.App.Vision;
using OpenCvSharp;

namespace GGEA.Tests;

/// <summary>从标准回放生成模拟器尺寸的画面，再经过与设备截图相同的生产归一化入口。</summary>
internal static class ResolutionReplay
{
    internal static byte[] Png(byte[] original, int width, int height)
    {
        using var source = Cv2.ImDecode(original, ImreadModes.Color);
        using var device = new Mat();
        Cv2.Resize(source, device, new Size(width, height),
            interpolation: width < source.Width ? InterpolationFlags.Area : InterpolationFlags.Linear);
        return new GameResolution(width, height).Normalize(device.ToBytes(".png"));
    }

    internal static ScreenImage Screen(byte[] original, int width, int height) =>
        ImageCodec.DecodePng(Png(original, width, height));
}

using System.Buffers.Binary;
using GGEA.App.Localization;
using OpenCvSharp;

namespace GGEA.App.Vision;

/// <summary>设备截图的真实尺寸。任务、模板、OCR 和可持久化坐标统一使用 1080p 横屏空间。</summary>
public readonly record struct GameResolution(int Width, int Height)
{
    public const int BaseWidth = 1920;
    public const int BaseHeight = 1080;
    public const string SupportedSizes = "1280×720、1600×900、1920×1080、2560×1440";

    // DPI 供用户配置模拟器；截图与输入均使用物理像素，不能再乘密度系数。
    public int? PresetDpi => (Width, Height) switch
    {
        (1280, 720) or (1600, 900) => 240,
        (1920, 1080) => 280,
        (2560, 1440) => 320,
        _ => null
    };
    public bool IsSupported => PresetDpi.HasValue;
    public string UnsupportedMessage => L.Format($"当前截图为 {Width}×{Height}，支持的横屏尺寸为 {SupportedSizes}");

    public static GameResolution ReadPng(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !png.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidOperationException("截图缺少 PNG 尺寸信息");
        return new(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }

    internal byte[] Normalize(byte[] png)
    {
        if (!IsSupported) throw new InvalidOperationException(UnsupportedMessage);
        if (Width == BaseWidth) return png;
        using var source = Cv2.ImDecode(png, ImreadModes.Color);
        if (source.Width != Width || source.Height != Height) throw new InvalidOperationException("截图尺寸与解码结果不一致");
        using var target = new Mat();
        Cv2.Resize(source, target, new Size(BaseWidth, BaseHeight), interpolation:
            Width < BaseWidth ? InterpolationFlags.Linear : InterpolationFlags.Area);
        return target.ToBytes(".png", new ImageEncodingParam(ImwriteFlags.PngCompression, 1));
    }

    public (int X, int Y) ToDevice(int x, int y)
    {
        if (!IsSupported) throw new InvalidOperationException(UnsupportedMessage);
        if ((uint)x >= BaseWidth || (uint)y >= BaseHeight) throw new ArgumentOutOfRangeException();
        // 投影像素中心，四边均落在有效设备坐标内。
        return ((int)((2L * x + 1) * Width / (2 * BaseWidth)),
            (int)((2L * y + 1) * Height / (2 * BaseHeight)));
    }
}

using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 水平位移估算器测试：用两张只有一条竖条位置不同的合成图验证位移方向与大小。
/// </summary>
public class ImageShiftEstimatorTests
{
    [Fact]
    public void MeasureHorizontalShift_DetectsLeftShift()
    {
        // 原图竖条在 x=[600,700)，第二张图竖条在 x=[500,600) → 内容左移 100px
        var a = CreateScreenWithBar(600);
        var b = CreateScreenWithBar(500);

        var (shift, quality) = ImageShiftEstimator.MeasureHorizontalShift(a, b);

        Assert.InRange(shift, -106, -94);
        Assert.True(quality > 0.9);
    }

    [Fact]
    public void MeasureHorizontalShift_NoShift_ReturnsZero()
    {
        var a = CreateScreenWithBar(600);
        var b = CreateScreenWithBar(600);

        var (shift, _) = ImageShiftEstimator.MeasureHorizontalShift(a, b);

        Assert.InRange(Math.Abs(shift), 0, 4);
    }

    /// <summary>生成 1920x1080 灰色背景、左侧 x=barX 起 100px 宽的白色竖条图像。</summary>
    private static ScreenImage CreateScreenWithBar(int barX)
    {
        const int width = 1920;
        const int height = 1080;
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bright = x >= barX && x < barX + 100;
                var v = (byte)(bright ? 240 : 80);
                var i = (y * width + x) * 4;
                bgra[i] = v;       // B
                bgra[i + 1] = v;   // G
                bgra[i + 2] = v;   // R
                bgra[i + 3] = 255; // A
            }
        }

        return new ScreenImage
        {
            Bitmap = ImageCodec.FromBgra(bgra, width, height),
            Bgra = bgra,
            Width = width,
            Height = height,
        };
    }
}

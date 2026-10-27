using System.IO;
using GGEA.App.Automation;
using GGEA.App.Tasks;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>回归单次手势未生效、稀疏节点被当作边界以及等亮度彩色节点位移。</summary>
public sealed class RoadmapDragBoundaryTests
{
    /// <summary>第一次扫描手势未改变截图时，应在画布另一位置有限重试，而非立即换行。</summary>
    [Fact]
    public async Task FirstScanGestureIgnored_RetriesAtAnotherOrigin()
    {
        await SearchAsync(100, 100, 900, 450, 2);
    }

    /// <summary>新出现的小节点可能落在差异容差内，判断边界前仍必须检查目标。</summary>
    [Fact]
    public async Task SparseTargetOnStationaryFrame_IsInspectedBeforeChangingDirection()
    {
        await SearchAsync(40, 30, 758, 398, 1);
    }

    /// <summary>用手势驱动截图，不用预排帧掩盖实际扫描方向；只清理测试自己的目录。</summary>
    private static async Task SearchAsync(int width, int height, int x, int y, int scansBeforeTarget)
    {
        var directory = Directory.CreateTempSubdirectory("ggea-boundary-");
        try
        {
            var template = Path.Combine(directory.FullName, "unit.png");
            File.WriteAllBytes(template, ImageCodec.EncodePng(TestImageFactory.CreateBgra(width, height, 200), width, height));
            var empty = ImageCodec.EncodePng(TestImageFactory.CreateBgra(1920, 1080, 20), 1920, 1080);
            var pixels = TestImageFactory.CreateBgra(1920, 1080, 20);
            TestImageFactory.PasteBlock(pixels, 1920, width, height, x, y, 200);
            var target = ImageCodec.EncodePng(pixels, 1920, 1080);
            var adb = new FakeAdbService(empty);
            adb.ScreenshotProvider = () => adb.Swipes.Count(s => s.X2 < s.X1) >= scansBeforeTarget ? target : empty;
            var service = new DevelopService(new AutomationService(adb, new TemplateMatcher(), new TestLog()), new TestLog());

            var match = await service.FindOnRoadmapAsync("device", template, "", maxDragsPerDirection: 3, dragDurationMs: 100);

            Assert.NotNull(match);
            Assert.Equal(x + width / 2, match.CenterX);
            Assert.Equal(y + height / 2, match.CenterY);
            var scans = adb.Swipes.Where(s => s.X2 < s.X1).ToArray();
            Assert.Equal(scansBeforeTarget, scans.Length);
            if (scans.Length == 2) Assert.NotEqual(scans[0].Y1, scans[1].Y1);
            Assert.DoesNotContain(adb.Swipes, s => s.Y2 < s.Y1); // 找到目标后不能继续换行。
            Assert.Empty(adb.Taps);
            Assert.Empty(adb.PinchInDurations);
        }
        finally { directory.Delete(true); }
    }

    /// <summary>蓝色节点与深色背景的灰度相近，RGB 明显变化仍应表示画布移动。</summary>
    [Fact]
    public void ColoredNodeMovement_WithSimilarLuminance_IsNotBoundary()
    {
        var before = TestImageFactory.CreateBgra(1920, 1080, 20);
        var after = (byte[])before.Clone();
        PaintBlue(before, 600);
        PaintBlue(after, 1200);
        Assert.False(DevelopService.IsNearlySame(ImageCodec.EncodePng(before, 1920, 1080),
            ImageCodec.EncodePng(after, 1920, 1080)));
    }

    /// <summary>生成有明确色差、却几乎没有亮度差的节点，隔离边界比较的灰度缺陷。</summary>
    private static void PaintBlue(byte[] pixels, int left)
    {
        for (var y = 450; y < 590; y++)
        for (var x = left; x < left + 140; x++)
        {
            var offset = (y * 1920 + x) * 4;
            pixels[offset] = 200;
            pixels[offset + 1] = 0;
            pixels[offset + 2] = 0;
        }
    }
}

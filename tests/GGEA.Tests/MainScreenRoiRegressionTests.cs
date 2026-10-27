using System.IO;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>回放 AP 不足退出后真实关卡页的左下角裁剪，防止搜索右界再次裁掉导航按钮。</summary>
public class MainScreenRoiRegressionTests
{
    /// <summary>旧区域复现漏识别，新区域必须在原阈值下识别为未选中，不能误报已经回到主页。</summary>
    [Fact]
    public async Task StageHomeButton_FullTemplateFitsOnlyExpandedRoi()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var options = MainScreenDetector.DefaultOptions with
        {
            ActivatedTemplatePath = Path.Combine(root, MainScreenDetector.DefaultOptions.ActivatedTemplatePath),
            InactiveTemplatePath = Path.Combine(root, MainScreenDetector.DefaultOptions.InactiveTemplatePath),
            BackTemplatePath = Path.Combine(root, MainScreenDetector.DefaultOptions.BackTemplatePath)
        };
        var strip = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures/Navigation/ap-insufficient-home-strip.png")));
        var screen = TestImageFactory.CreateBgra(1920, 1080, 0);
        for (var y = 0; y < strip.Height; y++)
            Buffer.BlockCopy(strip.Bgra, y * strip.Width * 4, screen, (975 + y) * 1920 * 4, strip.Width * 4);
        var adb = new FakeAdbService(ImageCodec.EncodePng(screen, 1920, 1080));
        var legacy = new MainScreenDetector(adb, new TemplateMatcher(), new TestLog(),
            options with { ButtonRoi = new Roi(0, 975, 282, 105) });
        Assert.Equal(MainScreenButtonState.NotFound, (await legacy.DetectButtonAsync("serial")).State);
        var detector = new MainScreenDetector(adb, new TemplateMatcher(), new TestLog(), options);
        var result = await detector.DetectButtonAsync("serial");
        Assert.Equal(MainScreenButtonState.Inactive, result.State);
        Assert.True(result.Match!.Confidence >= 0.85);
        Assert.InRange(result.Match.CenterX, 150, 160);
        Assert.InRange(result.Match.CenterY, 1020, 1035);
        Assert.False(await detector.IsMainScreenAsync("serial"));
    }
}

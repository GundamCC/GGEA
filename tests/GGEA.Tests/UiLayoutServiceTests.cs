using System.IO;
using GGEA.App.UI;
using GGEA.App.Vision;

namespace GGEA.Tests;

/// <summary>
/// 主界面布局加载测试：正常读取、缺失文件与损坏 JSON 都应安全回退。
/// </summary>
public class UiLayoutServiceTests
{
    [Fact]
    public void Load_ValidJson_ReadsAnchors()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "main.json");
        File.WriteAllText(path, """
        {
          "screenWidth": 1920,
          "screenHeight": 1080,
          "anchors": [
            { "name": "navHome", "x": 74, "y": 900, "width": 220, "height": 155 },
            { "name": "redDotGift", "x": 24, "y": 634, "width": 96, "height": 54, "template": "resources/templates/red_dot_gift.png" }
          ]
        }
        """);

        var layout = new UiLayoutService(new TestLog()).Load(path);

        Assert.Equal(1920, layout.ScreenWidth);
        Assert.Equal(2, layout.Anchors.Count);
        var home = layout.Find("navHome");
        Assert.NotNull(home);
        Assert.Equal(184, home!.CenterX);
        Assert.Equal(977, home.CenterY);
        var redDot = layout.Find("redDotGift");
        Assert.Equal("resources/templates/red_dot_gift.png", redDot!.Template);
    }

    /// <summary>默认开发布局应保留实机标定后的首个作品识别区域与点击中心。</summary>
    [Fact]
    public void Load_DefaultDevelopLayout_UsesCalibratedFirstRoadmapRegion()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "resources", "common", "ui_layout", "develop.json");

        var layout = new UiLayoutService(new TestLog()).Load(path);
        var firstRoadmap = layout.Find("firstRoadmap");

        Assert.NotNull(firstRoadmap);
        var template = ImageCodec.DecodePng(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory, "resources", "common", "templates", "develop", "dev_0079.png")));
        // 实机目标左上角为 (463,257)；ROI 必须容纳完整模板，否则匹配器会直接拒绝。
        Assert.True(firstRoadmap!.X <= 463);
        Assert.True(firstRoadmap.Y <= 257);
        Assert.True(firstRoadmap.X + firstRoadmap.Width >= 463 + template.Width);
        Assert.True(firstRoadmap.Y + firstRoadmap.Height >= 257 + template.Height);
        // 扩大搜索范围后，派生点击中心仍须落在目标卡片内部。
        Assert.InRange(firstRoadmap.CenterX, 464, 463 + template.Width - 2);
        Assert.InRange(firstRoadmap.CenterY, 258, 257 + template.Height - 2);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyLayout()
    {
        using var temp = new TempDirectory();
        var layout = new UiLayoutService(new TestLog()).Load(Path.Combine(temp.Path, "not-exists.json"));
        Assert.Empty(layout.Anchors);
    }

    [Fact]
    public void Load_InvalidJson_ReturnsEmptyLayout()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "main.json");
        File.WriteAllText(path, "{ broken json !!!");
        var layout = new UiLayoutService(new TestLog()).Load(path);
        Assert.Empty(layout.Anchors);
    }

    /// <summary>测试用临时目录，自动清理。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }
    }
}

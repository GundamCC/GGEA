using System.IO;
using GGEA.App.Config;

namespace GGEA.Tests;

/// <summary>
/// Hard 关卡数据加载测试：正常读取、非法条目过滤、文件缺失回退。
/// </summary>
public class HardStagesServiceTests
{
    [Fact]
    public void Load_ValidJson_ReadsEntries()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "hard_stages.json");
        File.WriteAllText(path, """
        {
          "hardStages": [
            { "id": 1, "work": "Mobile Suit Gundam", "normalCount": 14, "sideStories": 2, "hardCount": 3, "screenshot": "mobile_suit_gundam.png" },
            { "work": "Gundam SEED", "hardCount": 5, "screenshot": "seed.png" }
          ]
        }
        """);

        var data = new HardStagesService(new TestLog()).Load(path);

        Assert.Equal(2, data.HardStages.Count);
        Assert.Equal("Mobile Suit Gundam", data.HardStages[0].Work);
        Assert.Equal(3, data.HardStages[0].HardCount);
        Assert.Equal("mobile_suit_gundam.png", data.HardStages[0].Screenshot);
        Assert.Equal(1, data.HardStages[0].Id);
        Assert.Equal(14, data.HardStages[0].NormalCount);
        Assert.Equal(2, data.HardStages[0].SideStories);
    }

    [Fact]
    public void Load_InvalidHardCount_FiltersEntry()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "hard_stages.json");
        File.WriteAllText(path, """
        {
          "hardStages": [
            { "work": "Valid", "hardCount": 2, "screenshot": "valid.png" },
            { "work": "Invalid", "hardCount": 9, "screenshot": "invalid.png" }
          ]
        }
        """);

        var data = new HardStagesService(new TestLog()).Load(path);

        Assert.Single(data.HardStages);
        Assert.Equal("Valid", data.HardStages[0].Work);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        using var temp = new TempDirectory();
        var data = new HardStagesService(new TestLog()).Load(Path.Combine(temp.Path, "not-exists.json"));
        Assert.Empty(data.HardStages);
    }

    /// <summary>发布数据中的作品 ID 必须对应已核对的 Logo，且素材随构建输出分发。</summary>
    [Fact]
    public void Load_BundledCatalog_MapsAllWorksToExistingWebpLogos()
    {
        var resources = Path.Combine(AppContext.BaseDirectory, "resources");
        var data = new HardStagesService(new TestLog()).Load(Path.Combine(resources, "hard_stages.json"));
        // 素材编号不是作品 ID 顺序：Hathaway、F91、Narrative 尤其不能按文件名排序关联。
        string[] seriesByWorkId =
        [
            "0010", "0810", "0950", "1100", "1250", "1500", "1620", "1440", "2000",
            "2100", "2190", "2300", "3000", "3100", "3300", "3700", "4000", "4300", "5010", "1750",
        ];

        Assert.Equal(seriesByWorkId.Length, data.HardStages.Count);
        Assert.Equal(data.HardStages.Count, data.HardStages.Select(work => work.Id).Distinct().Count());
        Assert.Equal(data.HardStages.Count, data.HardStages.Select(work => work.Screenshot).Distinct().Count());
        for (var index = 0; index < seriesByWorkId.Length; index++)
        {
            var work = Assert.Single(data.HardStages, entry => entry.Id == index + 1);
            Assert.Equal($"logo_l_series_{seriesByWorkId[index]}.webp", work.Screenshot);
            var path = Path.Combine(resources, "templates", "stage", "main_stage", work.Screenshot);
            Assert.True(File.Exists(path), $"作品 {work.Id} 的 Logo 未随构建输出分发: {work.Screenshot}");
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length >= 12);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        }
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

using System.IO;
using GGEA.App.Config;

namespace GGEA.Tests;

/// <summary>
/// 作品画布位置配置加载测试：正常读取、非法条目过滤、文件缺失回退。
/// </summary>
public class StageWorkPositionsServiceTests
{
    [Fact]
    public void Load_ValidJson_ReadsPositions()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "stage_work_positions.json");
        File.WriteAllText(path, """
        {
          "positions": [
            { "id": 1, "canvasX": 734, "canvasY": 445, "verified": true },
            { "id": 2, "canvasX": 1365, "canvasY": 439 }
          ]
        }
        """);

        var data = new StageWorkPositionsService(new TestLog()).Load(path);

        Assert.Equal(2, data.Positions.Count);
        Assert.Equal(734, data.Positions[0].CanvasX);
        Assert.Equal(445, data.Positions[0].CanvasY);
        Assert.True(data.Positions[0].Verified);
        var found = data.Find(2);
        Assert.NotNull(found);
        Assert.Equal(1365, found!.CanvasX);
        Assert.Null(data.Find(99));
    }

    [Fact]
    public void Load_InvalidPosition_FiltersEntry()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "stage_work_positions.json");
        File.WriteAllText(path, """
        {
          "positions": [
            { "id": 1, "canvasX": 734, "canvasY": 445 },
            { "id": 0, "canvasX": 0, "canvasY": 0 },
            { "id": 3, "canvasX": 0, "canvasY": 445 }
          ]
        }
        """);

        var data = new StageWorkPositionsService(new TestLog()).Load(path);

        Assert.Single(data.Positions);
        Assert.Equal(1, data.Positions[0].Id);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        using var temp = new TempDirectory();
        var data = new StageWorkPositionsService(new TestLog()).Load(Path.Combine(temp.Path, "not-exists.json"));
        Assert.Empty(data.Positions);
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

using System.IO;
using GGEA.App.Config;
using GGEA.App.UI;

namespace GGEA.Tests;

/// <summary>配置损坏仍可回退启动：保留原回退结果，以警告代替任务终止错误。</summary>
public sealed class ConfigurationFallbackLoggingTests
{
    /// <summary>真实加载损坏 JSON，验证五类加载器均返回默认或空数据，并保留诊断原因。</summary>
    [Theory]
    [InlineData("config")]
    [InlineData("layout")]
    [InlineData("positions")]
    [InlineData("hard_stages")]
    [InlineData("ap_profiles")]
    public void InvalidJson_ReturnsFallbackWithWarning(string kind)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ invalid json");
            var log = new TestLog();
            switch (kind)
            {
                case "config": Assert.Equal("adb", new ConfigService(log).Load(path).AdbPath); break;
                case "layout": Assert.Empty(new UiLayoutService(log).Load(path).Anchors); break;
                case "positions": Assert.Empty(new StageWorkPositionsService(log).Load(path).Positions); break;
                case "hard_stages": Assert.Empty(new HardStagesService(log).Load(path).HardStages); break;
                case "ap_profiles": Assert.Empty(new ApConsumptionProfileService(log).Load(path).Profiles); break;
            }
            var entry = Assert.Single(log.Entries);
            Assert.Equal("WARN", entry.Level);
            Assert.Contains("读取", entry.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

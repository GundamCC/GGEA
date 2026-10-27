using System.IO;
using GGEA.App.Config;

namespace GGEA.Tests;

/// <summary>验证稀疏语言覆盖、旧配置迁移及采集目标，防止跨语言误用和目录整批丢失。</summary>
public sealed class ResourceResolverTests
{
    /// <summary>每个用例独享临时资源树，退出时清理，不触碰真实素材或全局语言。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-resources-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>语言仅覆盖同路径文件，保留通用兄弟文件，绝不借用其他语言。</summary>
    [Fact]
    public void Resolve_SparseOverlayAndDirectoryEnumeration()
    {
        using var temp = new TempDirectory();
        string Add(string relative, string value)
        {
            var path = Path.GetFullPath(Path.Combine(temp.Path, "resources", relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
            return path;
        }
        var common = Add("common/templates/dialog/ok.png", "common");
        var cancel = Add("common/templates/dialog/cancel.png", "cancel");
        var japanese = Add("ja/templates/dialog/ok.png", "japanese");
        var exclusive = Add("ja/templates/extra/new.png", "new");
        Add("zh-CN/templates/dialog/only_chinese.png", "chinese");
        var resolver = new ResourceResolver(temp.Path, "ja");
        Assert.Equal(japanese, resolver.Resolve("resources/templates/dialog/ok.png"));
        Assert.Equal(japanese, resolver.Resolve(common));
        Assert.Equal(cancel, resolver.Resolve(Path.Combine(Path.GetDirectoryName(japanese)!, "cancel.png")));
        Assert.False(File.Exists(resolver.Resolve("resources/templates/dialog/only_chinese.png")));
        Assert.Equal(new[] { cancel, japanese }, resolver.GetFiles("resources/templates/dialog", "*.png"));
        Assert.Contains(exclusive, resolver.GetFiles("resources/templates", "*.png", SearchOption.AllDirectories));
        Assert.True(resolver.DirectoryExists("resources/templates/extra"));
        File.Delete(japanese);
        Assert.Equal(common, resolver.Resolve("resources/templates/dialog/ok.png"));
    }

    /// <summary>中文基准移出通用后只在中文生效；切换视图不遗留上一次的语言。</summary>
    [Fact]
    public void Resolve_LanguageOnlyBaselineAndCustomPaths()
    {
        using var temp = new TempDirectory();
        var baseline = Path.Combine(temp.Path, "resources", "zh-CN", "templates", "ok.png");
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        File.WriteAllText(baseline, "baseline");
        var chinese = new ResourceResolver(temp.Path, "zh-CN");
        var japanese = new ResourceResolver(temp.Path, "ja");
        Assert.Equal(baseline, chinese.Resolve("resources/templates/ok.png"));
        Assert.False(File.Exists(japanese.Resolve("resources/templates/ok.png")));
        Assert.Equal(Path.Combine(temp.Path, "resources", "ja", "templates", "ok.png"),
            japanese.ResolveWritePath("resources/templates/ok.png"));
        var custom = Path.Combine(temp.Path, "custom", "ok.png");
        Assert.Equal(custom, japanese.Resolve(custom));
        Assert.Equal(custom, japanese.ResolveWritePath(custom));
    }

    /// <summary>旧版用户坐标仍有优先权，更新器也保留新旧两个物理位置。</summary>
    [Fact]
    public void Resolve_PreservesLegacyUserPositions()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "resources"));
        var legacy = Path.Combine(temp.Path, "resources", "stage_work_positions.json");
        File.WriteAllText(legacy, "{}");
        Assert.Equal(legacy, new ResourceResolver(temp.Path, "ja").Resolve("resources/stage_work_positions.json"));
        Assert.True(GGEA.Updater.UpdateFiles.IsUserFile("resources/common/stage_work_positions.json"));
        Assert.True(GGEA.Updater.UpdateFiles.IsUserFile("resources/stage_work_positions.json"));
    }

    /// <summary>只保存包名的老配置自动恢复区服与语言，旧自定义包名迁回国际服，语言始终受区服约束。</summary>
    [Theory]
    [InlineData(GameProfiles.JapanPackage, "japan", "ja")]
    [InlineData(GameProfiles.WorldwidePackage, "worldwide", "zh-CN")]
    [InlineData("example.custom", "worldwide", "zh-CN")]
    public void Load_LegacyPackageAndRoundTrip(string package, string server, string language)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { gamePackage = package }));
        var service = new ConfigService(new TestLog());
        var config = service.Load(path);
        Assert.Equal(server, config.GameServer);
        Assert.Equal(language, config.GameLanguage);
        Assert.Equal(server == "japan" ? GameProfiles.JapanPackage : GameProfiles.WorldwidePackage, config.GamePackage);
        config.GameLanguage = "zh-HK";
        service.Save(config, path);
        var saved = service.Load(path);
        Assert.Equal(server == "japan" ? "ja" : "zh-HK", saved.GameLanguage);
        Assert.Equal(config.GamePackage, saved.GamePackage);
    }

    /// <summary>标准区服纠正冲突包名及不支持的语言，旧自定义设置不再保留任意包名。</summary>
    [Fact]
    public void Normalize_ServerOwnsStandardPackage()
    {
        var config = new AppConfig { GameServer = "japan", GameLanguage = "../other" };
        GameProfiles.Normalize(config);
        Assert.Equal(GameProfiles.JapanPackage, config.GamePackage);
        Assert.Equal("ja", config.GameLanguage);
        config.GameServer = "worldwide";
        config.GameLanguage = "en";
        GameProfiles.Normalize(config);
        Assert.Equal(GameProfiles.WorldwidePackage, config.GamePackage);
        Assert.Equal("en", config.GameLanguage);
    }
}

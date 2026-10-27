using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GGEA.App.Config;
using GGEA.App.Localization;

namespace GGEA.Tests;

/// <summary>覆盖首次系统语言、持久化优先级及日文资源的格式安全；不更改测试进程的全局语言。</summary>
public sealed class LocalizationTests
{
    /// <summary>未选择时随 Windows 界面语言，已有选择优先，未知语言安全回退。</summary>
    [Theory]
    [InlineData(null, "ja-JP", "ja")]
    [InlineData(null, "zh-CN", "zh-CN")]
    [InlineData(null, "en-US", "zh-CN")]
    [InlineData("zh-CN", "ja-JP", "zh-CN")]
    [InlineData("ja", "en-US", "ja")]
    [InlineData("ja-JP", "zh-CN", "ja")]
    [InlineData("invalid", "en-US", "zh-CN")]
    public void Resolve_UsesSystemOnlyWithoutSupportedPreference(string? saved, string system, string expected)
        => Assert.Equal(expected, UiLanguages.Resolve(saved, CultureInfo.GetCultureInfo(system)));

    /// <summary>旧配置首次选择后保存，下一次系统语言改变也不能覆盖偏好或游戏识别配置。</summary>
    [Fact]
    public void Preference_RoundTrips_IndependentlyOfGameLanguage()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(path, "{\"gameServer\":\"worldwide\",\"gameLanguage\":\"en\"}");
        var service = new ConfigService(new TestLog());
        var config = service.Load(path);
        Assert.Null(config.UiLanguage);
        config.UiLanguage = UiLanguages.Resolve(config.UiLanguage, CultureInfo.GetCultureInfo("ja-JP"));
        service.Save(config, path);
        var loaded = service.Load(path);
        Assert.Equal("ja", UiLanguages.Resolve(loaded.UiLanguage, CultureInfo.GetCultureInfo("zh-CN")));
        Assert.Equal("worldwide", loaded.GameServer);
        Assert.Equal("en", loaded.GameLanguage);
        Assert.Equal(GameProfiles.WorldwidePackage, loaded.GamePackage);
    }

    /// <summary>下拉名称为本语言自称；中文原文与未知的外部信息保持可读回退。</summary>
    [Fact]
    public void NativeNames_AndFallback_AreStable()
    {
        Assert.Equal(new[] { "简体中文", "日本語" }, UiLanguages.All.Select(option => option.NativeName));
        Assert.Equal("English", GameProfiles.Languages.Single(option => option.Id == "en").Label);
        Assert.Equal("設定", L.Translate("设置", "ja"));
        Assert.Equal("设置", L.Translate("设置", "zh-CN"));
        Assert.Equal("external error 123", L.Translate("external error 123", "ja"));
    }

    /// <summary>每个译文都必须非空并保留占位符及格式，防止只在日文运行时报 FormatException。</summary>
    [Fact]
    public void JapaneseCatalog_PreservesAllFormatArguments()
    {
        foreach (var (source, translated) in ReadCatalog())
        {
            Assert.False(string.IsNullOrWhiteSpace(translated), source);
            var sourceFormat = CompositeFormat.Parse(source);
            var translatedFormat = CompositeFormat.Parse(translated);
            Assert.Equal(sourceFormat.MinimumArgumentCount, translatedFormat.MinimumArgumentCount);
            var sourceArguments = Regex.Matches(source, @"\{\d+[^}]*\}").Select(match => match.Value).Order().ToArray();
            var translatedArguments = Regex.Matches(translated, @"\{\d+[^}]*\}").Select(match => match.Value).Order().ToArray();
            Assert.Equal(sourceArguments, translatedArguments);
            Assert.Equal(translated, L.Translate(source, "ja"));
        }
    }

    /// <summary>三个实际界面的本地化标记必须有译文，同时禁止再加入裸露的中文显示属性。</summary>
    [Fact]
    public void XamlDisplayText_HasJapaneseResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GGEA.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var catalog = ReadCatalog();
        foreach (var file in new[] { "MainWindow.xaml", "SettingsWindow.xaml", "UpdatePanel.xaml" })
        {
            var document = XDocument.Load(Path.Combine(directory.FullName, "src", "GGEA.App", "UI", file));
            foreach (var attribute in document.Descendants().Attributes())
            {
                var match = Regex.Match(attribute.Value, @"^\{loc:Text Source='(.*)'\}$", RegexOptions.Singleline);
                if (match.Success)
                {
                    var source = match.Groups[1].Value.Replace(@"\'", "'").Replace(@"\\", @"\");
                    Assert.True(catalog.ContainsKey(source), $"{file}: {source}");
                }
                else if (attribute.Name.LocalName is "Text" or "Content" or "Title" or "Header" or "ToolTip" or "AutomationProperties.Name")
                    Assert.False(Regex.IsMatch(attribute.Value, @"[\p{IsCJKUnifiedIdeographs}]"), $"{file}: {attribute}");
            }
        }
    }

    /// <summary>读取生产嵌入资源，测试不会依赖构建输出中的外置翻译文件。</summary>
    private static Dictionary<string, string> ReadCatalog()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("GGEA.App.Localization.ja.json");
        Assert.NotNull(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    /// <summary>每个配置用例使用独立目录，避免修改用户或其他测试的配置。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggea-i18n-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

using System.IO;
using GGEA.ReleaseManager;

namespace GGEA.Tests;

/// <summary>验证维护者数据仅写入指定本机目录，损坏配置不被默默重建。</summary>
public sealed class ReleaseManagerSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ggea-release-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Settings_RoundTrip_ContainsOnlyPaths_AndNoTemporaryFiles()
    {
        var store = new ReleaseManagerSettingsStore(_directory);
        Assert.Equal(new ReleaseManagerSettings(), store.Load());
        var settings = new ReleaseManagerSettings { SourceRepository = @"C:\source" };
        store.Save(settings);
        store.Save(settings with { SourceRepository = @"D:\source" });
        Assert.Equal(settings with { SourceRepository = @"D:\source" }, store.Load());
        Assert.Single(Directory.GetFiles(_directory));
        Assert.DoesNotContain("token", File.ReadAllText(store.FilePath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GGEA", "ReleaseManager"),
            ReleaseManagerSettingsStore.DefaultDirectory);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1,\"token\":\"TEST-NOT-A-CREDENTIAL\"}")]
    public void Settings_InvalidData_IsPreserved(string content)
    {
        Directory.CreateDirectory(_directory);
        var store = new ReleaseManagerSettingsStore(_directory);
        File.WriteAllText(store.FilePath, content);
        Assert.ThrowsAny<Exception>(() => store.Load());
        Assert.Equal(content, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Settings_InvalidSave_PreservesPreviousFile()
    {
        var store = new ReleaseManagerSettingsStore(_directory);
        store.Save(new());
        string previous = File.ReadAllText(store.FilePath);
        Assert.Throws<InvalidDataException>(() => store.Save(new() { SourceRepository = "https://example.invalid/source" }));
        Assert.Equal(previous, File.ReadAllText(store.FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

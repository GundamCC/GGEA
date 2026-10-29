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
    [InlineData("{\"schemaVersion\":3}")]
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

    [Fact]
    public void RelativeSource_FollowsExecutable_AfterMovingRepository()
    {
        Assert.Equal(@"D:\moved\GGEA", ReleaseManagerSettingsStore.ResolveSource(".", @"D:\moved\GGEA"));
        Assert.Equal(".", ReleaseManagerSettingsStore.InitialSource(@"C:\old", @"D:\moved\GGEA", @"D:\moved\GGEA"));
        Assert.Equal(@"..\other", ReleaseManagerSettingsStore.InitialSource(@"..\other", @"D:\moved\GGEA", @"D:\moved\GGEA"));
        Assert.Equal(@"C:\other", ReleaseManagerSettingsStore.StoreSource(@"C:\other", @"D:\moved\GGEA"));
        Assert.Equal(@"C:\other", ReleaseManagerSettingsStore.InitialSource(@"C:\other", @"D:\repo", @"D:\repo", migrateAbsolute: false));
        var store = new ReleaseManagerSettingsStore(_directory);
        store.Save(new() { SourceRepository = "." });
        Assert.Equal(".", store.Load().SourceRepository);
    }

    [Theory]
    [InlineData(@"C:relative")]
    [InlineData(@"\rooted")]
    [InlineData("https://example.invalid/source")]
    public void RelativeSource_RejectsAmbiguousPaths(string path)
    {
        Assert.Throws<InvalidDataException>(() => ReleaseManagerSettingsStore.ResolveSource(path, @"C:\repo"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
